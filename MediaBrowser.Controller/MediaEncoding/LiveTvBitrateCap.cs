using System;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.MediaEncoding;

/// <summary>
/// Caps the bitrate a live TV channel's video is transcoded at (Finly).
/// </summary>
/// <remarks>
/// Clients ask for as much as their connection allows, and an SD channel of 2 to 4 Mbps was transcoded at up to 30 Mbps.
/// A transcode needs no more than about twice the source's bitrate to look as good as the source, with a floor so a
/// low bitrate channel still gets enough: 4 Mbps for SD (576 lines or fewer), 8 Mbps for HD. The cap never raises what
/// the client asked for.
/// </remarks>
public static class LiveTvBitrateCap
{
    /// <summary>
    /// The least an SD channel is transcoded at.
    /// </summary>
    public const int SdFloor = 4_000_000;

    /// <summary>
    /// The least an HD channel is transcoded at.
    /// </summary>
    public const int HdFloor = 8_000_000;

    /// <summary>
    /// The most lines an SD picture has.
    /// </summary>
    public const int SdMaxHeight = 576;

    /// <summary>
    /// Gets the cap for a channel: twice its video bitrate, but at least the floor for its picture size.
    /// </summary>
    /// <param name="sourceVideoBitrate">The channel's video bitrate, if known.</param>
    /// <param name="isSd">Whether the channel is SD.</param>
    /// <returns>The cap in bits per second.</returns>
    public static int GetCap(int? sourceVideoBitrate, bool isSd)
    {
        var floor = isSd ? SdFloor : HdFloor;
        if (sourceVideoBitrate is not > 0)
        {
            return floor;
        }

        return (int)Math.Clamp(2L * sourceVideoBitrate.Value, floor, int.MaxValue);
    }

    /// <summary>
    /// Caps a transcode's target video bitrate.
    /// </summary>
    /// <param name="target">The target worked out from the client's request, 0 or <c>null</c> for none.</param>
    /// <param name="requested">The video bitrate the client asked for, if any.</param>
    /// <param name="cap">The cap from <see cref="GetCap"/>.</param>
    /// <returns>The capped target.</returns>
    public static int Apply(int? target, int? requested, int cap)
    {
        var capped = target is > 0 ? Math.Min(target.Value, cap) : cap;
        return requested is > 0 ? Math.Min(capped, requested.Value) : capped;
    }

    /// <summary>
    /// Gets whether a channel's video is SD. A picture of unknown size is SD when it is MPEG-2, which broadcasters only
    /// use for SD; otherwise it is taken to be HD, which only allows more.
    /// </summary>
    /// <param name="video">The video stream.</param>
    /// <returns>Whether it is SD.</returns>
    public static bool IsSd(MediaStream video)
    {
        ArgumentNullException.ThrowIfNull(video);

        if (video.Height is > 0)
        {
            return video.Height.Value <= SdMaxHeight;
        }

        return IsMpeg2(video);
    }

    /// <summary>
    /// Estimates the channel's video bitrate. What the tuner is actually sending is best. Failing that the probed
    /// bitrate, except for MPEG-2, whose stream states its maximum (often 15 Mbps) rather than what it uses.
    /// </summary>
    /// <param name="measuredBitrate">The bitrate the live stream has been received at, audio included, if known.</param>
    /// <param name="audioBitrate">The audio bitrate, if known.</param>
    /// <param name="video">The video stream.</param>
    /// <returns>The estimate, or <c>null</c> when there is none worth using.</returns>
    public static int? EstimateSourceVideoBitrate(int? measuredBitrate, int? audioBitrate, MediaStream video)
    {
        ArgumentNullException.ThrowIfNull(video);

        if (measuredBitrate is > 0)
        {
            var videoBitrate = measuredBitrate.Value - (audioBitrate ?? 0);
            return videoBitrate > 0 ? videoBitrate : measuredBitrate;
        }

        if (video.BitRate is > 0 && !IsMpeg2(video))
        {
            return video.BitRate;
        }

        return null;
    }

    private static bool IsMpeg2(MediaStream video)
        => string.Equals(video.Codec, "mpeg2video", StringComparison.OrdinalIgnoreCase)
            || string.Equals(video.Codec, "mpeg2", StringComparison.OrdinalIgnoreCase);
}
