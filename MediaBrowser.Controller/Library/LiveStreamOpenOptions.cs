using System;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// How to open a live stream (Finly).
/// </summary>
public sealed class LiveStreamOpenOptions
{
    /// <summary>
    /// Gets how long probing the stream may take, or <c>null</c> for the default for live viewing.
    /// </summary>
    public TimeSpan? ProbeTimeout { get; init; }

    /// <summary>
    /// Gets a value indicating whether streams kept open after their last viewer left may be closed to free a tuner when
    /// the tuner has none free. Background work sets this to <c>false</c> so it never closes anyone's stream.
    /// </summary>
    public bool CloseIdleStreamsWhenBusy { get; init; } = true;
}
