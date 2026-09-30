#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
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
    public class LiveStreamHelper
    {
        private readonly IMediaEncoder _mediaEncoder;
        private readonly ILogger _logger;
        private readonly IApplicationPaths _appPaths;
        private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;

        /// <summary>
        /// How much of a live stream to read when probing it, in milliseconds.
        /// </summary>
        private const int LiveProbeAnalyzeDurationMs = 1500;

        public LiveStreamHelper(IMediaEncoder mediaEncoder, ILogger logger, IApplicationPaths appPaths)
        {
            _mediaEncoder = mediaEncoder;
            _logger = logger;
            _appPaths = appPaths;
        }

        public async Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string? cacheKey, bool addProbeDelay, CancellationToken cancellationToken)
        {
            var originalRuntime = mediaSource.RunTimeTicks;

            var now = DateTime.UtcNow;

            MediaInfo? mediaInfo = null;
            var cacheFilePath = string.IsNullOrEmpty(cacheKey) ? null : Path.Combine(_appPaths.CachePath, "mediainfo", cacheKey.GetMD5().ToString("N", CultureInfo.InvariantCulture) + ".json");

            if (cacheFilePath is not null)
            {
                try
                {
                    FileStream jsonStream = AsyncFile.OpenRead(cacheFilePath);

                    await using (jsonStream.ConfigureAwait(false))
                    {
                        mediaInfo = await JsonSerializer.DeserializeAsync<MediaInfo>(jsonStream, _jsonOptions, cancellationToken).ConfigureAwait(false);
                    }

                    // Keep channels that are in use out of the cache cleanup, which removes files not written for 30 days
                    File.SetLastWriteTimeUtc(cacheFilePath, now);
                }
                catch (IOException ex)
                {
                    _logger.LogDebug(ex, "Could not open cached media info");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error opening cached media info");
                }
            }

            if (mediaInfo is null)
            {
                if (addProbeDelay)
                {
                    var delayMs = mediaSource.AnalyzeDurationMs ?? 0;
                    delayMs = Math.Max(3000, delayMs);
                    _logger.LogInformation("Waiting {0}ms before probing the live stream", delayMs);
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }

                // SD channels repeat their MPEG-2 sequence header every half second and HD channels send a key frame
                // about every second, so 1.5 seconds of stream is enough to find every stream and its format
                mediaSource.AnalyzeDurationMs = LiveProbeAnalyzeDurationMs;

                mediaInfo = await _mediaEncoder.GetMediaInfo(
                    new MediaInfoRequest
                    {
                        MediaSource = mediaSource,
                        MediaType = isAudio ? DlnaProfileType.Audio : DlnaProfileType.Video,
                        ExtractChapters = false
                    },
                    cancellationToken).ConfigureAwait(false);

                // A video stream without a codec is a stream that sent no data during the probe, for example a channel
                // that is off air and only broadcasting sound. Treat it as missing rather than as unplayable video.
                mediaInfo.MediaStreams = mediaInfo.MediaStreams
                    .Where(i => i.Type != MediaStreamType.Video || !string.IsNullOrEmpty(i.Codec))
                    .ToList();

                // Only cache a complete probe: one that caught the picture size. A short probe that just missed a sequence header
                // is tried again next time instead of being kept for good.
                if (cacheFilePath is not null && mediaInfo.MediaStreams.Any(i => i.Type == MediaStreamType.Video && i.Width > 0))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(cacheFilePath) ?? throw new InvalidOperationException("Path can't be a root directory."));
                    // Create truncates, a shorter result written over a longer one would leave a corrupt file behind
                    FileStream createStream = AsyncFile.Create(cacheFilePath);
                    await using (createStream.ConfigureAwait(false))
                    {
                        await JsonSerializer.SerializeAsync(createStream, mediaInfo, _jsonOptions, cancellationToken).ConfigureAwait(false);
                    }

                    _logger.LogDebug("Saved media info to {0}", cacheFilePath);
                }
            }

            var mediaStreams = mediaInfo.MediaStreams;

            if (!string.IsNullOrEmpty(cacheKey))
            {
                var newList = new List<MediaStream>();
                newList.AddRange(mediaStreams.Where(i => i.Type == MediaStreamType.Video).Take(1));
                newList.AddRange(mediaStreams.Where(i => i.Type == MediaStreamType.Audio).Take(1));

                foreach (var stream in newList)
                {
                    stream.Index = -1;
                    stream.Language = null;
                }

                mediaStreams = newList;
            }

            _logger.LogInformation("Live tv media info probe took {0} seconds", (DateTime.UtcNow - now).TotalSeconds.ToString(CultureInfo.InvariantCulture));

            mediaSource.Bitrate = mediaInfo.Bitrate;
            mediaSource.Container = mediaInfo.Container;
            mediaSource.Formats = mediaInfo.Formats;
            mediaSource.MediaStreams = mediaStreams;
            mediaSource.RunTimeTicks = mediaInfo.RunTimeTicks;
            mediaSource.Size = mediaInfo.Size;
            mediaSource.Timestamp = mediaInfo.Timestamp;
            mediaSource.Video3DFormat = mediaInfo.Video3DFormat;
            mediaSource.VideoType = mediaInfo.VideoType;

            mediaSource.DefaultSubtitleStreamIndex = null;

            // Null this out so that it will be treated like a live stream
            if (!originalRuntime.HasValue)
            {
                mediaSource.RunTimeTicks = null;
            }

            var audioStream = mediaStreams.FirstOrDefault(i => i.Type == MediaStreamType.Audio);

            if (audioStream is null || audioStream.Index == -1)
            {
                mediaSource.DefaultAudioStreamIndex = null;
            }
            else
            {
                mediaSource.DefaultAudioStreamIndex = audioStream.Index;
            }

            var videoStream = mediaStreams.FirstOrDefault(i => i.Type == MediaStreamType.Video);
            if (videoStream is not null)
            {
                if (!videoStream.BitRate.HasValue)
                {
                    var width = videoStream.Width ?? 1920;

                    if (width >= 3000)
                    {
                        videoStream.BitRate = 30000000;
                    }
                    else if (width >= 1900)
                    {
                        videoStream.BitRate = 20000000;
                    }
                    else if (width >= 1200)
                    {
                        videoStream.BitRate = 8000000;
                    }
                    else if (width >= 700)
                    {
                        videoStream.BitRate = 2000000;
                    }
                }

                // This is coming up false and preventing stream copy
                videoStream.IsAVC = null;
            }

            mediaSource.AnalyzeDurationMs = 3000;

            // Try to estimate this
            mediaSource.InferTotalBitrate(true);
        }

        public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, bool addProbeDelay, CancellationToken cancellationToken)
        {
            return AddMediaInfoWithProbe(mediaSource, isAudio, null, addProbeDelay, cancellationToken);
        }
    }
}
