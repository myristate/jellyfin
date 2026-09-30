namespace Jellyfin.LiveTv.TunerHosts.HdHomerun;

/// <summary>
/// How many of a tuner's tuners are in use (Finly).
/// </summary>
/// <param name="Total">The number of tuners.</param>
/// <param name="InUse">The number in use, by anyone.</param>
internal readonly record struct TunerUsage(int Total, int InUse)
{
    /// <summary>
    /// Gets the number of free tuners.
    /// </summary>
    public int Free => Total - InUse;
}
