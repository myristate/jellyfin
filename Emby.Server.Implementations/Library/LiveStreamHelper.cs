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
    public partial class LiveStreamHelper
    {
        private readonly IMediaEncoder _mediaEncoder;
        private readonly ILogger _logger;
        private readonly IApplicationPaths _appPaths;
        private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;

        /// <summary>
        /// How much of a live stream to read when probing it, in milliseconds.
        /// </summary>
        private const int LiveProbeAnalyzeDurationMs = 1500;

        /// <summary>
        /// The longest a live stream probe may take. A channel that sends almost nothing, such as a data or off air
        /// service, never fills the probe and would otherwise hold it, and the tuner, for many minutes.
        /// </summary>
        private static readonly TimeSpan LiveProbeTimeout = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The longest a probe for a recording may take (Finly). A recording would rather wait than fail.
        /// </summary>
        public static readonly TimeSpan RecordingProbeTimeout = TimeSpan.FromSeconds(20);

        public LiveStreamHelper(IMediaEncoder mediaEncoder, ILogger logger, IApplicationPaths appPaths)
        {
            _mediaEncoder = mediaEncoder;
            _logger = logger;
            _appPaths = appPaths;
        }

        public Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string? cacheKey, bool addProbeDelay, CancellationToken cancellationToken)
            => AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, null, cancellationToken);

        /// <summary>
        /// Probes a live stream, or uses what probing its channel found before.
        /// </summary>
        /// <param name="mediaSource">The stream's media source, which is filled in.</param>
        /// <param name="isAudio">Whether it is a radio station.</param>
        /// <param name="cacheKey">The key under which the probe is cached, or <c>null</c> not to cache it.</param>
        /// <param name="addProbeDelay">Whether to wait before probing.</param>
        /// <param name="probeTimeout">How long probing may take (Finly), or <c>null</c> for <see cref="LiveProbeTimeout"/>.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task.</returns>
        public async Task AddMediaInfoWithProbe(MediaSourceInfo mediaSource, bool isAudio, string? cacheKey, bool addProbeDelay, TimeSpan? probeTimeout, CancellationToken cancellationToken)
        {
            var timeout = probeTimeout ?? LiveProbeTimeout;
            var originalRuntime = mediaSource.RunTimeTicks;

            var now = DateTime.UtcNow;

            MediaInfo? mediaInfo = null;
            var cacheFilePath = string.IsNullOrEmpty(cacheKey) ? null : GetProbeCachePath(_appPaths, cacheKey);

            if (cacheFilePath is not null)
            {
                // (Finly) What probing the channel found before, re-probed in the background once it is a week old
                var cached = await ReadProbeCache(cacheFilePath, cancellationToken).ConfigureAwait(false);
                if (cached?.MediaInfo is not null)
                {
                    mediaInfo = cached.MediaInfo;
                    if (IsStale(cached.ProbeDateUtc, now))
                    {
                        ProbeAgainInBackground(mediaSource, isAudio, cacheFilePath);
                    }
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

                var (probed, isComplete) = await Probe(mediaSource, isAudio, timeout, cancellationToken).ConfigureAwait(false);
                mediaInfo = probed;

                // Only cache a complete probe: one that caught the picture size, or a radio station's sound. A short probe
                // that just missed a sequence header is tried again next time instead of being kept for good.
                if (cacheFilePath is not null && isComplete)
                {
                    await WriteProbeCache(cacheFilePath, mediaInfo, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
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
