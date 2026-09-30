using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using MediaBrowser.Controller.LiveTv;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// Tells a tuner that is busy from one that failed (Finly).
/// </summary>
internal static class TunerErrors
{
    /// <summary>
    /// Gets whether an error is one a tuner is expected to give now and then, which is logged without its stack trace.
    /// </summary>
    /// <param name="ex">The error.</param>
    /// <returns>Whether it is expected.</returns>
    internal static bool IsExpected(Exception ex)
        => ex is TimeoutException
            or HttpRequestException
            or EndOfStreamException
            or IOException
            or SocketException
            or OperationCanceledException
            or LiveTvTunerException;

    /// <summary>
    /// Turns an error opening a tuner stream into a <see cref="LiveTvTunerException"/>.
    /// </summary>
    /// <param name="ex">The error.</param>
    /// <param name="hostUrl">The tuner's address, for the message.</param>
    /// <returns>The failure.</returns>
    internal static LiveTvTunerException ToTunerException(Exception ex, string? hostUrl)
    {
        if (ex is LiveTvTunerException tunerException)
        {
            return tunerException;
        }

        // A stalled tuner surfaces as a timeout, or as a cancellation of the request's own timeout
        var isTimeout = ex is TimeoutException or OperationCanceledException;
        var what = isTimeout ? "didn't respond in time" : "failed";
        return new LiveTvTunerException($"The tuner {hostUrl} {what}: {ex.Message}", isTimeout, ex);
    }

    /// <summary>
    /// Picks the error to report when no tuner could stream a channel. A busy tuner comes first, since closing idle streams
    /// can free it, then a failure, and "not broadcasting" only when that is all the tuners said.
    /// </summary>
    /// <param name="busy">The last "no free tuner" error, if any.</param>
    /// <param name="failed">The last failure, if any.</param>
    /// <param name="unavailable">The last "not broadcasting" error, if any.</param>
    /// <returns>The error to throw, or <c>null</c> when there was none.</returns>
    internal static Exception? Choose(LiveTvConflictException? busy, LiveTvTunerException? failed, LiveTvChannelUnavailableException? unavailable)
        => (Exception?)busy ?? (Exception?)failed ?? unavailable;
}
