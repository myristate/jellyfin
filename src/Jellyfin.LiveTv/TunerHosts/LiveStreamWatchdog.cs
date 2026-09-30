using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// Closes live streams nobody reads (Finly).
/// </summary>
/// <remarks>
/// A stream is closed when its last consumer closes it, but a client that goes away without saying so, such as a guide
/// preview left behind, keeps its stream, and the tuner, forever. Every read of a stream is counted, so a stream that no
/// one has read for a minute and isn't being recorded is closed whatever its consumer count. A stream being read is
/// never closed.
/// </remarks>
public sealed class LiveStreamWatchdog : BackgroundService
{
    /// <summary>
    /// How often the open streams are checked.
    /// </summary>
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long nobody may have read a stream before it is closed.
    /// </summary>
    internal static readonly TimeSpan UnreadTimeout = TimeSpan.FromSeconds(60);

    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IRecordingsManager _recordingsManager;
    private readonly ILogger<LiveStreamWatchdog> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveStreamWatchdog"/> class.
    /// </summary>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="recordingsManager">The recordings manager.</param>
    /// <param name="logger">The logger.</param>
    public LiveStreamWatchdog(IMediaSourceManager mediaSourceManager, IRecordingsManager recordingsManager, ILogger<LiveStreamWatchdog> logger)
    {
        _mediaSourceManager = mediaSourceManager;
        _recordingsManager = recordingsManager;
        _logger = logger;
    }

    /// <summary>
    /// Gets whether a stream looks abandoned: it counts its readers, has none, hasn't had any for
    /// <see cref="UnreadTimeout"/> and isn't being recorded.
    /// </summary>
    /// <param name="liveStream">The stream.</param>
    /// <param name="now">The time now.</param>
    /// <param name="isRecording">Whether a recording uses it.</param>
    /// <returns>Whether to close it.</returns>
    internal static bool IsAbandoned(ILiveStream liveStream, DateTime now, bool isRecording)
        => !isRecording
            && liveStream.LastReaderLeftUtc is DateTime unreadSince
            && liveStream.ActiveReaderCount <= 0
            && now - unreadSince >= UnreadTimeout;

    /// <summary>
    /// Closes the abandoned streams.
    /// </summary>
    /// <param name="now">The time now.</param>
    /// <returns>The number of streams closed.</returns>
    internal async Task<int> CloseAbandonedStreams(DateTime now)
    {
        var openStreams = _mediaSourceManager.GetOpenLiveStreams();
        if (openStreams is null)
        {
            return 0;
        }

        var closed = 0;
        foreach (var (id, liveStream) in openStreams)
        {
            if (IsAbandoned(liveStream, now, _recordingsManager.IsRecordingLiveStream(id))
                && await _mediaSourceManager.CloseUnreadLiveStream(id, liveStream, UnreadTimeout).ConfigureAwait(false))
            {
                closed++;
            }
        }

        return closed;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await CloseAbandonedStreams(DateTime.UtcNow).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error closing live streams nobody reads");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down
        }
    }
}
