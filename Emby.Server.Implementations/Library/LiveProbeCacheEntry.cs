using System;
using MediaBrowser.Model.MediaInfo;

namespace Emby.Server.Implementations.Library;

/// <summary>
/// A cached live stream probe with the date it was made (Finly).
/// </summary>
public sealed class LiveProbeCacheEntry
{
    /// <summary>
    /// Gets or sets when the channel was probed.
    /// </summary>
    public DateTime ProbeDateUtc { get; set; }

    /// <summary>
    /// Gets or sets what the probe found.
    /// </summary>
    public MediaInfo? MediaInfo { get; set; }
}
