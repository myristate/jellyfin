using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.Signal;

/// <summary>
/// Keeps the channel signal readings current (Finly): seeds them from the tuner's lineup at startup and, while a live
/// stream from an HDHomeRun is open, reads its status every <see cref="PollInterval"/>, which records the reading of
/// every tuned tuner. Nothing is asked of the tuner while no stream is open.
/// </summary>
public sealed class ChannelSignalMonitor : BackgroundService
{
    /// <summary>
    /// How often the tuner's status is read while streams are open.
    /// </summary>
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly ChannelSignalService _signals;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILogger<ChannelSignalMonitor> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChannelSignalMonitor"/> class.
    /// </summary>
    /// <param name="signals">The channel signal service.</param>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="logger">The logger.</param>
    public ChannelSignalMonitor(ChannelSignalService signals, IMediaSourceManager mediaSourceManager, ILogger<ChannelSignalMonitor> logger)
    {
        _signals = signals;
        _mediaSourceManager = mediaSourceManager;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _signals.RefreshLineupIfDueAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Couldn't read the channel signal from the tuner lineup");
        }

        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var openStreams = _mediaSourceManager.GetOpenLiveStreams();
                    var tunerHostIds = openStreams?.Select(s => s.Value?.TunerHostId).ToList() ?? [];
                    await _signals.PollOpenStreamsAsync(tunerHostIds, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error reading the tuner signal");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down
        }
    }
}
