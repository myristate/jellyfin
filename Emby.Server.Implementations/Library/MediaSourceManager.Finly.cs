#nullable disable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AsyncKeyedLock;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.Library
{
    /// <summary>
    /// Opening and closing live streams (Finly).
    /// </summary>
    /// <remarks>
    /// Talking to the tuner can take many seconds, so it happens outside the lock that guards the open streams: one
    /// stalled tuner doesn't hold up every other channel being opened or closed. Opens of the same channel wait for each
    /// other instead, so the second shares the stream the first opened.
    /// </remarks>
    public partial class MediaSourceManager
    {
        /// <summary>
        /// How long to wait for a tuner that was just freed before trying it again, times the attempt.
        /// </summary>
        private static readonly TimeSpan LiveStreamBusyRetryDelay = TimeSpan.FromSeconds(1);

        // Opens of the same channel (open token) wait for each other
        private readonly AsyncKeyedLocker<string> _liveStreamOpenLocks = new(o =>
        {
            o.PoolSize = 20;
            o.PoolInitialFill = 1;
        });

        // The open token each open live stream was opened with, by live stream id
        private readonly ConcurrentDictionary<string, string> _liveStreamOpenTokens = new(StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc />
        public IReadOnlyCollection<KeyValuePair<string, ILiveStream>> GetOpenLiveStreams()
            => _openStreams.ToArray();

        /// <inheritdoc />
        public async Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(LiveStreamRequest request, LiveStreamOpenOptions options, CancellationToken cancellationToken)
        {
            options ??= new LiveStreamOpenOptions();

            ILiveStream liveStream;
            using (await _liveStreamOpenLocks.LockAsync(request.OpenToken, cancellationToken).ConfigureAwait(false))
            {
                liveStream = await ShareOpenLiveStream(request.OpenToken).ConfigureAwait(false);
                if (liveStream is null)
                {
                    var (provider, keyId) = GetProvider(request.OpenToken);
                    var opened = await OpenWithProvider(provider, keyId, options, cancellationToken).ConfigureAwait(false);

                    var openedSource = opened.MediaSource;

                    // Validate that this is actually possible
                    if (openedSource.SupportsDirectStream)
                    {
                        openedSource.SupportsDirectStream = SupportsDirectStream(openedSource.Path, openedSource.Protocol);
                    }

                    SetKeyProperties(provider, openedSource);

                    liveStream = await RegisterLiveStream(openedSource.LiveStreamId, opened, request.OpenToken).ConfigureAwait(false);
                }
            }

            var mediaSource = liveStream.MediaSource;
            try
            {
                await AddLiveStreamMediaInfo(request, options, mediaSource, cancellationToken).ConfigureAwait(false);

                // TODO: @bond Fix
                var json = JsonSerializer.SerializeToUtf8Bytes(mediaSource, _jsonOptions);
                _logger.LogInformation("Live stream opened: {@MediaSource}", mediaSource);
                var clone = JsonSerializer.Deserialize<MediaSourceInfo>(json, _jsonOptions);

                if (!request.UserId.IsEmpty())
                {
                    var user = _userManager.GetUserById(request.UserId);
                    var item = request.ItemId.IsEmpty()
                        ? null
                        : _libraryManager.GetItemById(request.ItemId);
                    SetDefaultAudioAndSubtitleStreamIndices(item, clone, user);
                }

                return new Tuple<LiveStreamResponse, IDirectStreamProvider>(new LiveStreamResponse(clone), liveStream as IDirectStreamProvider);
            }
            catch (Exception ex) when (ex is not LiveTvChannelUnavailableException)
            {
                // Whoever asked won't use the stream, so give back their share of it
                _logger.LogWarning("Opening live stream {LiveStreamId} failed after it was opened, closing it: {Message}", mediaSource.LiveStreamId, ex.Message);
                await CloseLiveStream(mediaSource.LiveStreamId, true).ConfigureAwait(false);
                throw;
            }
        }

        private async Task AddLiveStreamMediaInfo(LiveStreamRequest request, LiveStreamOpenOptions options, MediaSourceInfo mediaSource, CancellationToken cancellationToken)
        {
            try
            {
                if (mediaSource.MediaStreams.Any(i => i.Index != -1) || !mediaSource.SupportsProbing)
                {
                    AddMediaInfo(mediaSource);
                }
                else
                {
                    // hack - these two values were taken from LiveTVMediaSourceProvider
                    string cacheKey = request.OpenToken;

                    // No fixed wait before probing: the probe reads the live data as it arrives, and the wait added
                    // 3 seconds to every first tune of a channel
                    await new LiveStreamHelper(_mediaEncoder, _logger, _appPaths)
                        .AddMediaInfoWithProbe(mediaSource, false, cacheKey, false, options.ProbeTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (TimeoutException ex)
            {
                // Every broadcasting channel is identified within 2 seconds. One that isn't has sent nothing playable, for
                // example a part time channel that is off air or one that has moved since the tuner last scanned. Free
                // the tuner and say so, rather than letting the client wait for a picture that never comes.
                _logger.LogWarning("Live stream {LiveStreamId}: {Message}, the channel isn't broadcasting", mediaSource.LiveStreamId, ex.Message);
                await CloseLiveStream(mediaSource.LiveStreamId, true).ConfigureAwait(false);
                throw new LiveTvChannelUnavailableException("The channel isn't broadcasting right now");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error probing live tv stream");
                AddMediaInfo(mediaSource);
            }
        }

        /// <summary>
        /// Shares a running stream of the channel, if there is one that can be shared.
        /// </summary>
        /// <param name="openToken">The channel's open token.</param>
        /// <returns>The shared stream, or <c>null</c>.</returns>
        private async Task<ILiveStream> ShareOpenLiveStream(string openToken)
        {
            using (await _liveStreamLocker.LockAsync().ConfigureAwait(false))
            {
                foreach (var (id, token) in _liveStreamOpenTokens)
                {
                    if (string.Equals(token, openToken, StringComparison.Ordinal)
                        && _openStreams.TryGetValue(id, out var liveStream)
                        && liveStream.EnableStreamSharing)
                    {
                        liveStream.ConsumerCount = Math.Max(liveStream.ConsumerCount, 0) + 1;
                        _logger.LogInformation("Sharing live stream {LiveStreamId}, consumer count is now {ConsumerCount}", id, liveStream.ConsumerCount);
                        return liveStream;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Opens a stream with the provider, which talks to the tuner, without holding the open streams' lock. When the
        /// tuner has no free tuner, streams kept open after their last viewer left are closed to make room and it is tried
        /// again. Any other error is passed on at once.
        /// </summary>
        private async Task<ILiveStream> OpenWithProvider(IMediaSourceProvider provider, string keyId, LiveStreamOpenOptions options, CancellationToken cancellationToken)
        {
            var closedAny = false;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await provider.OpenMediaSource(keyId, _openStreams.Values.ToList(), cancellationToken).ConfigureAwait(false);
                }
                catch (LiveTvConflictException) when (attempt < LiveStreamConflictAttempts && options.CloseIdleStreamsWhenBusy)
                {
                    int closed;
                    using (await _liveStreamLocker.LockAsync(cancellationToken).ConfigureAwait(false))
                    {
                        closed = await CloseIdleLiveStreams().ConfigureAwait(false);
                    }

                    closedAny |= closed > 0;
                    if (!closedAny)
                    {
                        // Nothing of ours to free: the tuners are in use, say so now rather than keep the viewer waiting
                        throw;
                    }

                    // A tuner that was just released takes a moment before the tuner hands it out again
                    _logger.LogInformation("No free tuner (attempt {Attempt}), closed {Closed} idle streams, trying again", attempt, closed);
                    await Task.Delay(LiveStreamBusyRetryDelay * attempt, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Adds a newly opened stream to the open streams. Every open of a channel has the same live stream id, so a
        /// stream already there under the id is replaced only when nobody is using it; one in use is shared instead and
        /// the new one closed.
        /// </summary>
        private async Task<ILiveStream> RegisterLiveStream(string id, ILiveStream liveStream, string openToken)
        {
            using (await _liveStreamLocker.LockAsync().ConfigureAwait(false))
            {
                if (_openStreams.TryGetValue(id, out var previous) && !ReferenceEquals(previous, liveStream))
                {
                    if (previous.ConsumerCount <= 0 || !previous.EnableStreamSharing)
                    {
                        _logger.LogInformation("Replacing live stream {LiveStreamId}, closing the previous one nobody is using", id);
                        await previous.Close().ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.LogInformation("Live stream {LiveStreamId} is open and in use, sharing it and closing the new one", id);
                        await liveStream.Close().ConfigureAwait(false);
                        previous.ConsumerCount++;
                        return previous;
                    }
                }

                _openStreams[id] = liveStream;
                _liveStreamOpenTokens[id] = openToken;
                return liveStream;
            }
        }

        /// <inheritdoc />
        public async Task<bool> CloseUnreadLiveStream(string id, ILiveStream liveStream, TimeSpan minimumUnreadTime)
        {
            ArgumentException.ThrowIfNullOrEmpty(id);
            ArgumentNullException.ThrowIfNull(liveStream);

            using (await _liveStreamLocker.LockAsync().ConfigureAwait(false))
            {
                if (!_openStreams.TryGetValue(id, out var current) || !ReferenceEquals(current, liveStream))
                {
                    return false;
                }

                // Checked again under the stream's own lock, which also stops anyone starting to read it now
                if (!liveStream.TryStopNewReaders(minimumUnreadTime))
                {
                    return false;
                }

                _logger.LogWarning(
                    "Closing live stream {LiveStreamId} of {Channel}: nobody has read it for {Seconds:0} seconds, {ConsumerCount} consumers never closed it",
                    id,
                    GetChannelName(id),
                    (DateTime.UtcNow - (liveStream.LastReaderLeftUtc ?? DateTime.UtcNow)).TotalSeconds,
                    liveStream.ConsumerCount);
                await CloseOpenLiveStream(id, liveStream).ConfigureAwait(false);
                return true;
            }
        }

        /// <summary>
        /// Gets the name of the channel a live stream is of, for the log.
        /// </summary>
        private string GetChannelName(string id)
        {
            // The open token is "{provider}_{item type}_{item id}_{media source id}"
            if (_liveStreamOpenTokens.TryGetValue(id, out var openToken))
            {
                var parts = openToken.Split(LiveStreamIdDelimiter);
                if (parts.Length > 2 && Guid.TryParse(parts[2], out var itemId))
                {
                    var name = _libraryManager.GetItemById(itemId)?.Name;
                    if (!string.IsNullOrEmpty(name))
                    {
                        return name;
                    }
                }
            }

            return "an unknown channel";
        }

        /// <summary>
        /// Gets whether someone is reading a live stream, which is then never closed for having no consumers.
        /// </summary>
        private static bool IsBeingRead(ILiveStream liveStream)
            => liveStream.ActiveReaderCount > 0;

        /// <summary>
        /// Closes every open stream straight away, when the server shuts down.
        /// </summary>
        private void CloseAllLiveStreams()
        {
            foreach (var (id, liveStream) in _openStreams.ToList())
            {
                try
                {
                    CloseOpenLiveStream(id, liveStream).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error closing live stream {LiveStreamId}", id);
                }
            }
        }
    }
}
