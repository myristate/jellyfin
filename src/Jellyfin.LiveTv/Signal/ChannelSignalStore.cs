using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Extensions.Json;
using Jellyfin.LiveTv.TunerHosts.HdHomerun;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.Signal;

/// <summary>
/// The latest signal reading of each tuner channel, by guide number (Finly). Readings come from the tuner's lineup,
/// which has the values of its last channel scan, and from status.json, which has a live reading for each tuned
/// tuner. A live reading also gives the channel's frequency, and is applied to every channel known to share that
/// frequency, as they're on the same multiplex. Learned frequencies and readings are kept in a small JSON file.
/// </summary>
internal sealed class ChannelSignalStore
{
    /// <summary>
    /// The signal quality, in percent, under which a channel is weak.
    /// </summary>
    internal const int WeakQualityThreshold = 80;

    /// <summary>
    /// The signal strength, in percent, under which a channel whose quality isn't known is weak.
    /// </summary>
    internal const int WeakStrengthThreshold = 40;

    /// <summary>
    /// How long a live reading is preferred to the lineup's scan values.
    /// </summary>
    internal static readonly TimeSpan LiveReadingPreferredFor = TimeSpan.FromDays(7);

    /// <summary>
    /// How often new readings are saved at most. A newly learned frequency is saved at once.
    /// </summary>
    internal static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(1);

    private readonly Lock _lock = new();
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Dictionary<string, Reading> _readings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _frequencies = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;
    private DateTime _lastSaved = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelSignalStore"/> class, loading the saved readings.
    /// </summary>
    /// <param name="path">The file the readings are kept in.</param>
    /// <param name="logger">The logger.</param>
    public ChannelSignalStore(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
        Load();
    }

    /// <summary>
    /// Gets whether a reading is weak: quality under <see cref="WeakQualityThreshold"/>, or, when quality isn't known,
    /// strength under <see cref="WeakStrengthThreshold"/>.
    /// </summary>
    /// <param name="strength">The signal strength, in percent.</param>
    /// <param name="quality">The signal quality, in percent.</param>
    /// <returns>Whether the signal is weak.</returns>
    internal static bool IsWeak(int? strength, int? quality)
        => quality.HasValue
            ? quality.Value < WeakQualityThreshold
            : strength.HasValue && strength.Value < WeakStrengthThreshold;

    /// <summary>
    /// Records the lineup's scan values. They don't replace a live reading younger than
    /// <see cref="LiveReadingPreferredFor"/>.
    /// </summary>
    /// <param name="channels">The lineup's channels.</param>
    /// <param name="now">The time now, in UTC.</param>
    internal void RecordLineup(IEnumerable<LineupSignal> channels, DateTime now)
    {
        var measuredAt = ToSeconds(now);
        lock (_lock)
        {
            foreach (var channel in channels)
            {
                if (string.IsNullOrWhiteSpace(channel.GuideNumber) || (channel.Strength is null && channel.Quality is null))
                {
                    continue;
                }

                var number = channel.GuideNumber.Trim();
                if (_readings.TryGetValue(number, out var current)
                    && current.IsLive
                    && now - current.MeasuredAt < LiveReadingPreferredFor)
                {
                    continue;
                }

                var reading = new Reading(channel.Strength, channel.Quality, null, measuredAt, false);
                if (current is null || !current.SameValues(reading))
                {
                    _readings[number] = reading;
                    _dirty = true;
                }
            }

            SaveIfDue(now, false);
        }
    }

    /// <summary>
    /// Records the live readings of the tuned tuners in a status.json. A tuner on a channel gives that channel its
    /// reading and frequency; the reading goes to every channel known to be on the same frequency.
    /// </summary>
    /// <param name="tuners">The tuners.</param>
    /// <param name="now">The time now, in UTC.</param>
    internal void RecordStatus(IEnumerable<HdHomerunTunerState> tuners, DateTime now)
    {
        var measuredAt = ToSeconds(now);
        lock (_lock)
        {
            var learned = false;
            foreach (var tuner in tuners)
            {
                if (!tuner.HasSignal)
                {
                    continue;
                }

                var number = tuner.ChannelNumber;
                if (number is null && tuner.Frequency is null)
                {
                    continue;
                }

                if (number is not null && tuner.Frequency is long frequency
                    && (!_frequencies.TryGetValue(number, out var known) || known != frequency))
                {
                    _frequencies[number] = frequency;
                    learned = true;
                }

                var reading = new Reading(tuner.SignalStrength, tuner.SignalQuality, tuner.SymbolQuality, measuredAt, true);
                if (number is not null)
                {
                    _readings[number] = reading;
                }

                if (tuner.Frequency is long tunedFrequency)
                {
                    foreach (var sameMultiplex in _frequencies.Where(f => f.Value == tunedFrequency).Select(f => f.Key).ToList())
                    {
                        _readings[sameMultiplex] = reading;
                    }
                }

                _dirty = true;
            }

            SaveIfDue(now, learned);
        }
    }

    /// <summary>
    /// Gets a channel's latest reading.
    /// </summary>
    /// <param name="guideNumber">The channel's guide number.</param>
    /// <returns>The reading, or <c>null</c> when it has none.</returns>
    internal ChannelSignal? Get(string guideNumber)
    {
        lock (_lock)
        {
            return _readings.TryGetValue(guideNumber, out var reading) ? reading.ToSignal() : null;
        }
    }

    /// <summary>
    /// Gets every channel's latest reading.
    /// </summary>
    /// <returns>The readings, by guide number.</returns>
    internal IReadOnlyDictionary<string, ChannelSignal> GetAll()
    {
        lock (_lock)
        {
            return _readings.ToDictionary(r => r.Key, r => r.Value.ToSignal(), StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Gets the frequency a channel was last seen on.
    /// </summary>
    /// <param name="guideNumber">The channel's guide number.</param>
    /// <returns>The frequency, in Hz, or <c>null</c> when it isn't known.</returns>
    internal long? GetFrequency(string guideNumber)
    {
        lock (_lock)
        {
            return _frequencies.TryGetValue(guideNumber, out var frequency) ? frequency : null;
        }
    }

    /// <summary>
    /// Saves the readings when any are unsaved.
    /// </summary>
    internal void Flush()
    {
        lock (_lock)
        {
            SaveIfDue(DateTime.UtcNow, true);
        }
    }

    private static DateTime ToSeconds(DateTime time)
        => new(time.Ticks - (time.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

    private void SaveIfDue(DateTime now, bool force)
    {
        if (!_dirty || (!force && now - _lastSaved < SaveInterval))
        {
            return;
        }

        _lastSaved = now;
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var file = new SignalFile
            {
                Frequencies = new Dictionary<string, long>(_frequencies),
                Readings = new Dictionary<string, Reading>(_readings)
            };

            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, JsonDefaults.Options));
            File.Move(temp, _path, true);
            _dirty = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning("Couldn't save the channel signal readings to {Path}: {Message}", _path, ex.Message);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var file = JsonSerializer.Deserialize<SignalFile>(File.ReadAllText(_path), JsonDefaults.Options);
            foreach (var (number, frequency) in file?.Frequencies ?? [])
            {
                if (!string.IsNullOrWhiteSpace(number) && frequency > 0)
                {
                    _frequencies[number] = frequency;
                }
            }

            foreach (var (number, reading) in file?.Readings ?? [])
            {
                if (!string.IsNullOrWhiteSpace(number) && reading?.IsValid() == true)
                {
                    _readings[number] = reading with { MeasuredAt = DateTime.SpecifyKind(reading.MeasuredAt.ToUniversalTime(), DateTimeKind.Utc) };
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // A damaged file only loses what was learned; start afresh
            _logger.LogWarning("Couldn't read the channel signal readings from {Path}, starting afresh: {Message}", _path, ex.Message);
            _frequencies.Clear();
            _readings.Clear();
        }
    }

    /// <summary>
    /// A channel's signal values in a lineup.json.
    /// </summary>
    /// <param name="GuideNumber">The channel's guide number.</param>
    /// <param name="Strength">The signal strength from the last scan, in percent.</param>
    /// <param name="Quality">The signal quality from the last scan, in percent.</param>
    internal sealed record LineupSignal(string GuideNumber, int? Strength, int? Quality);

    /// <summary>
    /// A reading.
    /// </summary>
    /// <param name="Strength">The signal strength, in percent.</param>
    /// <param name="Quality">The signal quality, in percent.</param>
    /// <param name="SymbolQuality">The symbol quality, in percent.</param>
    /// <param name="MeasuredAt">When it was taken, in UTC.</param>
    /// <param name="IsLive">Whether it came from a tuned tuner rather than the lineup.</param>
    internal sealed record Reading(int? Strength, int? Quality, int? SymbolQuality, DateTime MeasuredAt, bool IsLive)
    {
        /// <summary>
        /// Gets whether the reading has the same values and source as another, whenever taken.
        /// </summary>
        /// <param name="other">The other reading.</param>
        /// <returns>Whether they're the same.</returns>
        public bool SameValues(Reading other)
            => Strength == other.Strength && Quality == other.Quality && SymbolQuality == other.SymbolQuality && IsLive == other.IsLive;

        /// <summary>
        /// Gets whether a loaded reading makes sense.
        /// </summary>
        /// <returns>Whether it has a value and every value is a percentage.</returns>
        public bool IsValid()
            => (Strength.HasValue || Quality.HasValue)
                && IsPercent(Strength) && IsPercent(Quality) && IsPercent(SymbolQuality);

        /// <summary>
        /// Gets the reading as clients see it.
        /// </summary>
        /// <returns>The reading.</returns>
        public ChannelSignal ToSignal() => new()
        {
            Strength = Strength,
            Quality = Quality,
            SymbolQuality = SymbolQuality,
            MeasuredAt = MeasuredAt,
            IsLive = IsLive,
            IsWeak = IsWeak(Strength, Quality)
        };

        private static bool IsPercent(int? value) => value is null or (>= 0 and <= 100);
    }

    /// <summary>
    /// The saved file.
    /// </summary>
    private sealed class SignalFile
    {
        public Dictionary<string, long>? Frequencies { get; set; }

        public Dictionary<string, Reading>? Readings { get; set; }
    }
}
