using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.LiveTv.Configuration;
using Jellyfin.LiveTv.TunerHosts.HdHomerun;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;
using static Jellyfin.LiveTv.Signal.ChannelSignalStore;

namespace Jellyfin.LiveTv.Signal;

/// <summary>
/// Keeps the latest signal reading of each HDHomeRun channel (Finly). It's seeded from the tuner's lineup, which has the
/// values of its last channel scan, and updated from every read of the tuner's status.json, which has a live reading
/// for each tuned tuner, whoever tuned it. While streams are open, <see cref="ChannelSignalMonitor"/> reads the status
/// regularly.
/// </summary>
public sealed class ChannelSignalService : IChannelSignalService, IDisposable
{
    /// <summary>
    /// The file in the configuration folder the readings are kept in.
    /// </summary>
    public const string FileName = "channel-signal.json";

    /// <summary>
    /// How often the lineup's scan values are read at most.
    /// </summary>
    internal static readonly TimeSpan LineupRefreshInterval = TimeSpan.FromHours(4);

    /// <summary>
    /// How long to wait before trying the lineup again after failing to read it.
    /// </summary>
    internal static readonly TimeSpan LineupRetryInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How old a status may be for a single channel's reading: about fresh, but no more than one read in 2 seconds.
    /// </summary>
    internal static readonly TimeSpan ChannelStatusAge = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How old a status may be for the readings of all channels.
    /// </summary>
    internal static readonly TimeSpan AllStatusAge = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long the channel items' guide numbers are reused.
    /// </summary>
    internal static readonly TimeSpan ChannelMapCacheTime = TimeSpan.FromMinutes(5);

    private const string HdHomerunType = "hdhomerun";
    private const string ChannelIdPrefix = "hdhr_";
    private static readonly TimeSpan LineupTimeout = TimeSpan.FromSeconds(10);

    private readonly HdHomerunTunerStatus _tunerStatus;
    private readonly IConfigurationManager _config;
    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ChannelSignalService> _logger;
    private readonly ChannelSignalStore _store;
    private readonly SemaphoreSlim _lineupGate = new(1, 1);
    private readonly Lock _channelMapLock = new();
    private DateTime _nextLineupRead = DateTime.MinValue;
    private (DateTime Time, IReadOnlyList<(Guid Id, string GuideNumber)> Channels)? _channelMap;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelSignalService"/> class.
    /// </summary>
    /// <param name="tunerStatus">The shared HDHomeRun status reader.</param>
    /// <param name="config">The configuration manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public ChannelSignalService(
        HdHomerunTunerStatus tunerStatus,
        IConfigurationManager config,
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        ILogger<ChannelSignalService> logger)
    {
        _tunerStatus = tunerStatus;
        _config = config;
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _store = new ChannelSignalStore(Path.Combine(config.CommonApplicationPaths.ConfigurationDirectoryPath, FileName), logger);
        _tunerStatus.StatusRead = (_, tuners) => _store.RecordStatus(tuners, DateTime.UtcNow);
    }

    /// <inheritdoc />
    public int WeakQualityThreshold => ChannelSignalStore.WeakQualityThreshold;

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, ChannelSignal>> GetChannelSignalsAsync(CancellationToken cancellationToken)
    {
        await RefreshLineupIfDueAsync(cancellationToken).ConfigureAwait(false);
        await ReadStatusAsync(GetTunerUrls(), AllStatusAge, cancellationToken).ConfigureAwait(false);

        var readings = _store.GetAll();
        var signals = new Dictionary<Guid, ChannelSignal>();
        foreach (var (id, guideNumber) in GetChannelMap())
        {
            if (readings.TryGetValue(guideNumber, out var signal))
            {
                signals[id] = signal;
            }
        }

        return signals;
    }

    /// <inheritdoc />
    public async Task<ChannelSignal?> GetChannelSignalAsync(Guid channelId, CancellationToken cancellationToken)
    {
        if (_libraryManager.GetItemById(channelId) is not LiveTvChannel channel
            || GetGuideNumber(channel.ExternalId) is not string guideNumber)
        {
            return null;
        }

        await RefreshLineupIfDueAsync(cancellationToken).ConfigureAwait(false);

        // Another server sharing the tuner may be on the channel or its multiplex, so the status is the only way to know
        await ReadStatusAsync(GetTunerUrls(), ChannelStatusAge, cancellationToken).ConfigureAwait(false);

        return _store.Get(guideNumber);
    }

    /// <summary>
    /// Saves any unsaved readings.
    /// </summary>
    public void Dispose()
    {
        _tunerStatus.StatusRead = null;
        _store.Flush();
        _lineupGate.Dispose();
    }

    /// <summary>
    /// Reads the status of each HDHomeRun that one of the open streams is from, recording its live readings.
    /// </summary>
    /// <param name="tunerHostIds">The tuner hosts of the open streams.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of tuners whose status was read.</returns>
    internal async Task<int> PollOpenStreamsAsync(IEnumerable<string?> tunerHostIds, CancellationToken cancellationToken)
    {
        var streaming = tunerHostIds.Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (streaming.Count == 0)
        {
            _store.Flush();
            return 0;
        }

        var urls = GetTunerUrls(host => streaming.Contains(host.Id));
        await ReadStatusAsync(urls, HdHomerunTunerStatus.CacheTime, cancellationToken).ConfigureAwait(false);
        return urls.Count;
    }

    /// <summary>
    /// Reads the scan values from each HDHomeRun's lineup, unless they were read within
    /// <see cref="LineupRefreshInterval"/>.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    internal async Task RefreshLineupIfDueAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow < _nextLineupRead)
        {
            return;
        }

        await _lineupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (DateTime.UtcNow < _nextLineupRead)
            {
                return;
            }

            var allRead = true;
            foreach (var url in GetTunerUrls())
            {
                allRead &= await ReadLineupAsync(url, cancellationToken).ConfigureAwait(false);
            }

            _nextLineupRead = DateTime.UtcNow + (allRead ? LineupRefreshInterval : LineupRetryInterval);
        }
        finally
        {
            _lineupGate.Release();
        }
    }

    /// <summary>
    /// Reads the signal values from a lineup.json, a list with an entry per channel such as
    /// <c>{"GuideNumber":"4","GuideName":"Channel 4","SignalStrength":82,"SignalQuality":100,...}</c>. The values
    /// are missing for some channels.
    /// </summary>
    /// <param name="json">The lineup.json.</param>
    /// <returns>The channels with a guide number.</returns>
    internal static IReadOnlyList<LineupSignal> ParseLineup(string json)
    {
        var channels = new List<LineupSignal>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return channels;
            }

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && HdHomerunTunerState.GetString(entry, "GuideNumber") is string guideNumber)
                {
                    channels.Add(new LineupSignal(
                        guideNumber,
                        HdHomerunTunerState.GetPercent(entry, "SignalStrength"),
                        HdHomerunTunerState.GetPercent(entry, "SignalQuality")));
                }
            }
        }
        catch (JsonException)
        {
            // Nothing to learn from it
        }

        return channels;
    }

    /// <summary>
    /// Gets the guide number from a tuner channel's id, such as hdhr_4.
    /// </summary>
    /// <param name="externalId">The channel's id from the tuner.</param>
    /// <returns>The guide number, or <c>null</c> when it isn't an HDHomeRun channel.</returns>
    internal static string? GetGuideNumber(string? externalId)
        => externalId is not null
            && externalId.StartsWith(ChannelIdPrefix, StringComparison.OrdinalIgnoreCase)
            && externalId.Length > ChannelIdPrefix.Length
                ? externalId[ChannelIdPrefix.Length..]
                : null;

    private async Task ReadStatusAsync(IReadOnlyList<string> urls, TimeSpan maxAge, CancellationToken cancellationToken)
    {
        foreach (var url in urls)
        {
            // Recorded as it's read
            await _tunerStatus.GetTuners(url, maxAge, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> ReadLineupAsync(string baseUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(LineupTimeout);
            var json = await _httpClientFactory.CreateClient(NamedClient.Default)
                .GetStringAsync(baseUrl + "/lineup.json", timeout.Token)
                .ConfigureAwait(false);
            _store.RecordLineup(ParseLineup(json), DateTime.UtcNow);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Couldn't read the lineup signal from {Url}: {Message}", baseUrl, ex.Message);
            return false;
        }
    }

    private List<string> GetTunerUrls(Func<TunerHostInfo, bool>? filter = null)
    {
        var urls = new List<string>();
        foreach (var host in _config.GetLiveTvConfiguration().TunerHosts ?? [])
        {
            if (!string.Equals(host.Type, HdHomerunType, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(host.Url)
                || (filter is not null && !filter(host)))
            {
                continue;
            }

            try
            {
                var url = HdHomerunHost.GetApiUrl(host);
                if (!urls.Contains(url, StringComparer.OrdinalIgnoreCase))
                {
                    urls.Add(url);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or UriFormatException)
            {
                _logger.LogDebug("Skipping the tuner at {Url}: {Message}", host.Url, ex.Message);
            }
        }

        return urls;
    }

    private IReadOnlyList<(Guid Id, string GuideNumber)> GetChannelMap()
    {
        lock (_channelMapLock)
        {
            if (_channelMap is { } cached && DateTime.UtcNow - cached.Time < ChannelMapCacheTime)
            {
                return cached.Channels;
            }
        }

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.LiveTvChannel],
            DtoOptions = new DtoOptions(false) { EnableImages = false }
        });

        var channels = new List<(Guid Id, string GuideNumber)>();
        foreach (var item in items)
        {
            if (item is LiveTvChannel channel && GetGuideNumber(channel.ExternalId) is string guideNumber)
            {
                channels.Add((channel.Id, guideNumber));
            }
        }

        lock (_channelMapLock)
        {
            _channelMap = (DateTime.UtcNow, channels);
        }

        return channels;
    }
}
