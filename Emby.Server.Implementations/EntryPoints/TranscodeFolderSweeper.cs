using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.EntryPoints;

/// <summary>
/// Deletes files left in the transcode folder (Finly).
/// </summary>
/// <remarks>
/// A transcode or live stream that ends badly can leave its files behind, and a live channel writes gigabytes an hour.
/// Every 10 minutes the files that belong to no running transcode and no open live stream, and haven't been written for
/// 10 minutes, are deleted. The daily "Clean transcode directory" task only deletes files a day old.
/// </remarks>
public sealed class TranscodeFolderSweeper : BackgroundService
{
    /// <summary>
    /// How often the folder is swept.
    /// </summary>
    internal static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a file must not have been written before it may be deleted.
    /// </summary>
    internal static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(10);

    private readonly ITranscodeManager _transcodeManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IConfigurationManager _configurationManager;
    private readonly ILogger<TranscodeFolderSweeper> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TranscodeFolderSweeper"/> class.
    /// </summary>
    /// <param name="transcodeManager">The transcode manager.</param>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="configurationManager">The configuration manager.</param>
    /// <param name="logger">The logger.</param>
    public TranscodeFolderSweeper(
        ITranscodeManager transcodeManager,
        IMediaSourceManager mediaSourceManager,
        IConfigurationManager configurationManager,
        ILogger<TranscodeFolderSweeper> logger)
    {
        _transcodeManager = transcodeManager;
        _mediaSourceManager = mediaSourceManager;
        _configurationManager = configurationManager;
        _logger = logger;
    }

    /// <summary>
    /// Picks the files to delete: not the folder's marker, not written for <see cref="MinimumAge"/>, and not belonging to
    /// anything in use. A transcode's files start with its output file's name (main.m3u8, main0.ts, main-1.mp4) and a
    /// live stream's with its unique id.
    /// </summary>
    /// <param name="files">The files in the folder with when they were last written.</param>
    /// <param name="inUseNames">The names the files in use start with.</param>
    /// <param name="now">The time now.</param>
    /// <returns>The files to delete.</returns>
    internal static IReadOnlyList<string> GetFilesToDelete(IEnumerable<(string Path, DateTime LastWriteUtc)> files, IReadOnlyCollection<string> inUseNames, DateTime now)
    {
        var result = new List<string>();
        foreach (var (path, lastWriteUtc) in files)
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith(".jellyfin-", StringComparison.OrdinalIgnoreCase)
                || now - lastWriteUtc < MinimumAge
                || inUseNames.Any(inUse => name.StartsWith(inUse, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(path);
        }

        return result;
    }

    /// <summary>
    /// Gets the names the files in use start with, or <c>null</c> when that can't be told.
    /// </summary>
    /// <param name="transcodePaths">The running transcodes' output paths.</param>
    /// <param name="liveStreamIds">The open live streams' unique ids.</param>
    /// <returns>The names.</returns>
    internal static IReadOnlyCollection<string> GetInUseNames(IEnumerable<string> transcodePaths, IEnumerable<string?> liveStreamIds)
        => transcodePaths
            .Select(Path.GetFileNameWithoutExtension)
            .Concat(liveStreamIds)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sweeps the transcode folder once.
    /// </summary>
    /// <returns>The number of files deleted.</returns>
    internal int Sweep()
    {
        var transcodePaths = _transcodeManager.GetActiveTranscodingPaths();
        var liveStreams = _mediaSourceManager.GetOpenLiveStreams();
        if (transcodePaths is null || liveStreams is null)
        {
            // Without knowing what is in use nothing is safe to delete
            return 0;
        }

        var folder = _configurationManager.GetTranscodePath();
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        var inUse = GetInUseNames(transcodePaths, liveStreams.Select(s => s.Value.UniqueId));
        var files = new DirectoryInfo(folder)
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Select(f => (f.FullName, f.LastWriteTimeUtc))
            .ToList();

        var deleted = 0;
        foreach (var path in GetFilesToDelete(files, inUse, DateTime.UtcNow))
        {
            try
            {
                var megabytes = new FileInfo(path).Length / 1048576.0;
                File.Delete(path);
                deleted++;
                _logger.LogInformation("Deleted {Path} ({Megabytes:0.0} MB) from the transcode folder, nothing was using it", path, megabytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("Couldn't delete {Path} from the transcode folder: {Message}", path, ex.Message);
            }
        }

        return deleted;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    Sweep();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error sweeping the transcode folder");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down
        }
    }
}
