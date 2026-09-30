using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Library
{
    /// <summary>
    /// The live stream probe cache (Finly). Each channel's probe is kept with the date it was made. A channel that is
    /// played is probed again in the background once its probe is a week old, so a channel that changed format is picked
    /// up; the cache files aren't touched when read, so the date stays true.
    /// </summary>
    public partial class LiveStreamHelper
    {
        /// <summary>
        /// How old a probe may get before the channel is probed again.
        /// </summary>
        internal static readonly TimeSpan ProbeCacheRefreshAge = TimeSpan.FromDays(7);

        // The channels being probed again in the background, by cache file
        private static readonly ConcurrentDictionary<string, byte> _probingAgain = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Gets the last background probe started, for tests.
        /// </summary>
        internal Task ProbeAgainTask { get; private set; } = Task.CompletedTask;

        /// <summary>
        /// Gets where the probe for a channel's open token is cached.
        /// </summary>
        /// <param name="appPaths">The application paths.</param>
        /// <param name="cacheKey">The channel's open token.</param>
        /// <returns>The cache file.</returns>
        public static string GetProbeCachePath(IApplicationPaths appPaths, string cacheKey)
            => Path.Combine(appPaths.CachePath, "mediainfo", cacheKey.GetMD5().ToString("N", CultureInfo.InvariantCulture) + ".json");

        /// <summary>
        /// Gets whether a probe is old enough that the channel should be probed again.
        /// </summary>
        /// <param name="probeDateUtc">When it was probed.</param>
        /// <param name="now">The time now.</param>
        /// <returns>Whether to probe again.</returns>
        internal static bool IsStale(DateTime probeDateUtc, DateTime now)
            => now - probeDateUtc > ProbeCacheRefreshAge;

        /// <summary>
        /// Reads a cached probe. Probes cached before the date was kept take the file's date.
        /// </summary>
        /// <param name="path">The cache file.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The cached probe, or <c>null</c> when there is none or it can't be read.</returns>
        internal async Task<LiveProbeCacheEntry?> ReadProbeCache(string path, CancellationToken cancellationToken)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && (document.RootElement.TryGetProperty(nameof(LiveProbeCacheEntry.MediaInfo), out var inner)
                        || document.RootElement.TryGetProperty("mediaInfo", out inner))
                    && inner.ValueKind == JsonValueKind.Object)
                {
                    return JsonSerializer.Deserialize<LiveProbeCacheEntry>(bytes, _jsonOptions);
                }

                return new LiveProbeCacheEntry
                {
                    MediaInfo = JsonSerializer.Deserialize<MediaInfo>(bytes, _jsonOptions),
                    ProbeDateUtc = File.GetLastWriteTimeUtc(path)
                };
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Could not open cached media info");
                return null;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error reading cached media info {Path}", path);
                return null;
            }
        }

        /// <summary>
        /// Writes a probe to the cache.
        /// </summary>
        /// <param name="path">The cache file.</param>
        /// <param name="mediaInfo">The probe.</param>
        /// <param name="probeDateUtc">When it was probed.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task.</returns>
        internal async Task WriteProbeCache(string path, MediaInfo mediaInfo, DateTime probeDateUtc, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Path can't be a root directory."));

            // Written to a new file and moved over the old one, so a reader never sees half of it
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp";
            var createStream = AsyncFile.Create(temporaryPath);
            await using (createStream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(createStream, new LiveProbeCacheEntry { ProbeDateUtc = probeDateUtc, MediaInfo = mediaInfo }, _jsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, true);
            _logger.LogDebug("Saved media info to {0}", path);
        }

        /// <summary>
        /// Probes a live stream.
        /// </summary>
        /// <returns>The probe, and whether it is complete enough to cache: it caught the picture size, or it is a radio
        /// station's sound.</returns>
        private async Task<(MediaInfo MediaInfo, bool IsComplete)> Probe(MediaSourceInfo mediaSource, bool isAudio, TimeSpan timeout, CancellationToken cancellationToken)
        {
            // SD channels repeat their MPEG-2 sequence header every half second and HD channels send a key frame
            // about every second, so 1.5 seconds of stream is enough to find every stream and its format
            mediaSource.AnalyzeDurationMs = LiveProbeAnalyzeDurationMs;

            MediaInfo mediaInfo;
            using (var probeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                probeCancellation.CancelAfter(timeout);
                try
                {
                    mediaInfo = await _mediaEncoder.GetMediaInfo(
                        new MediaInfoRequest
                        {
                            MediaSource = mediaSource,
                            MediaType = isAudio ? DlnaProfileType.Audio : DlnaProfileType.Video,
                            ExtractChapters = false
                        },
                        probeCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"Probing the live stream took longer than {timeout.TotalSeconds} seconds");
                }
            }

            // A radio station has no video stream at all. A TV channel that is off air still lists its video stream,
            // just without data, so it isn't mistaken for radio and kept without its picture.
            var isRadio = !mediaInfo.MediaStreams.Any(i => i.Type == MediaStreamType.Video)
                && mediaInfo.MediaStreams.Any(i => i.Type == MediaStreamType.Audio && !string.IsNullOrEmpty(i.Codec));

            // A video stream without a codec is a stream that sent no data during the probe, for example a channel
            // that is off air and only broadcasting sound. Treat it as missing rather than as unplayable video.
            mediaInfo.MediaStreams = mediaInfo.MediaStreams
                .Where(i => i.Type != MediaStreamType.Video || !string.IsNullOrEmpty(i.Codec))
                .ToList();

            var isComplete = isRadio || mediaInfo.MediaStreams.Any(i => i.Type == MediaStreamType.Video && i.Width > 0);
            return (mediaInfo, isComplete);
        }

        /// <summary>
        /// Probes a channel again without holding up whoever is playing it, keeping the old probe if it fails.
        /// </summary>
        private void ProbeAgainInBackground(MediaSourceInfo mediaSource, bool isAudio, string cacheFilePath)
        {
            if (!_probingAgain.TryAdd(cacheFilePath, 0))
            {
                return;
            }

            // A copy, the stream's own media source is filled in from the cached probe meanwhile
            var copy = JsonSerializer.Deserialize<MediaSourceInfo>(JsonSerializer.SerializeToUtf8Bytes(mediaSource, _jsonOptions), _jsonOptions)!;
            ProbeAgainTask = Task.Run(async () =>
            {
                try
                {
                    var (mediaInfo, isComplete) = await Probe(copy, isAudio, LiveProbeTimeout, CancellationToken.None).ConfigureAwait(false);
                    if (isComplete)
                    {
                        await WriteProbeCache(cacheFilePath, mediaInfo, DateTime.UtcNow, CancellationToken.None).ConfigureAwait(false);
                        _logger.LogInformation("Probed live stream {Path} again, its probe was over {Days} days old", copy.Path, ProbeCacheRefreshAge.TotalDays);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogInformation("Couldn't probe live stream {Path} again, keeping its old probe: {Message}", copy.Path, ex.Message);
                }
                finally
                {
                    _probingAgain.TryRemove(cacheFilePath, out _);
                }
            });
        }
    }
}
