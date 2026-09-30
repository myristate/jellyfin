using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.LiveTv.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.Guide;

/// <summary>
/// The "Prepare Live TV channels" scheduled task.
/// </summary>
/// <remarks>
/// The first time a channel is played the server reads a moment of it to learn its streams, which adds about two
/// seconds to that start; later starts reuse the result. This task tunes each channel without a saved result for a
/// moment, one tuner at a time, so every channel starts quickly the first time too. It stops as soon as the tuner has
/// no free tuner, so it never competes with someone watching.
/// </remarks>
public class PrepareChannelsScheduledTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ILiveTvManager _liveTvManager;
    private readonly IConfigurationManager _config;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<PrepareChannelsScheduledTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PrepareChannelsScheduledTask"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="liveTvManager">The live tv manager.</param>
    /// <param name="config">The configuration manager.</param>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public PrepareChannelsScheduledTask(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        ILiveTvManager liveTvManager,
        IConfigurationManager config,
        IApplicationPaths appPaths,
        ILogger<PrepareChannelsScheduledTask> logger)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _liveTvManager = liveTvManager;
        _config = config;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Prepare Live TV channels";

    /// <inheritdoc />
    public string Description => "Tunes each channel that hasn't been played yet for a moment, so it starts quickly the first time it is watched.";

    /// <inheritdoc />
    public string Category => "Live TV";

    /// <inheritdoc />
    public bool IsHidden => _liveTvManager.Services.Count == 1 && _config.GetLiveTvConfiguration().TunerHosts.Length == 0;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public string Key => "PrepareLiveTvChannels";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var channels = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.LiveTvChannel],
            DtoOptions = new DtoOptions(false)
        });

        int prepared = 0, alreadyPrepared = 0, failed = 0;
        for (var i = 0; i < channels.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100.0 * i / channels.Count);

            MediaSourceInfo? source;
            try
            {
                var sources = await _mediaSourceManager.GetPlaybackMediaSources(channels[i], null!, false, false, cancellationToken).ConfigureAwait(false);
                source = sources.FirstOrDefault(s => s.RequiresOpening && !string.IsNullOrEmpty(s.OpenToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Unable to get media sources for channel {Channel}", channels[i].Name);
                failed++;
                continue;
            }

            if (source is null || File.Exists(GetProbeCachePath(source.OpenToken)))
            {
                alreadyPrepared++;
                continue;
            }

            try
            {
                var response = await _mediaSourceManager.OpenLiveStream(new LiveStreamRequest { OpenToken = source.OpenToken }, cancellationToken).ConfigureAwait(false);
                await _mediaSourceManager.CloseLiveStream(response.MediaSource.LiveStreamId, true).ConfigureAwait(false);
                prepared++;
            }
            catch (LiveTvConflictException)
            {
                // Someone is watching, on this server or another one sharing the tuner: leave the rest for next time
                _logger.LogInformation("The tuner has no free tuner, stopping after preparing {Prepared} channels", prepared);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Unable to prepare channel {Channel}", channels[i].Name);
                failed++;
            }
        }

        _logger.LogInformation(
            "Prepared {Prepared} Live TV channels, {AlreadyPrepared} were already prepared, {Failed} failed",
            prepared,
            alreadyPrepared,
            failed);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // After the guide refresh
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4.75).Ticks
            }
        };
    }

    // Same location LiveStreamHelper saves live stream probes to
    private string GetProbeCachePath(string openToken)
        => Path.Combine(_appPaths.CachePath, "mediainfo", openToken.GetMD5().ToString("N", CultureInfo.InvariantCulture) + ".json");
}
