using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Controller.Tests.MediaEncoding;

public class LiveTvBitrateCapTests
{
    [Theory]
    [InlineData(3_000_000, true, 6_000_000)] // SD at 3 Mbps: twice the source
    [InlineData(1_500_000, true, 4_000_000)] // SD at 1.5 Mbps: the SD floor
    [InlineData(null, true, 4_000_000)] // SD, bitrate unknown: the floor
    [InlineData(3_000_000, false, 8_000_000)] // HD at 3 Mbps: the HD floor
    [InlineData(10_000_000, false, 20_000_000)] // HD at 10 Mbps: twice the source
    [InlineData(null, false, 8_000_000)]
    [InlineData(2_000_000_000, false, int.MaxValue)] // No overflow
    public void GetCap_TwiceTheSourceButAtLeastTheFloor(int? sourceBitrate, bool isSd, int expected)
    {
        Assert.Equal(expected, LiveTvBitrateCap.GetCap(sourceBitrate, isSd));
    }

    [Theory]
    [InlineData(30_000_000, 120_000_000, 6_000_000, 6_000_000)] // The web client's 30 Mbps for an SD channel
    [InlineData(3_000_000, 3_000_000, 6_000_000, 3_000_000)] // Never above what the client asked for
    [InlineData(0, null, 4_000_000, 4_000_000)] // No target: the cap
    [InlineData(null, null, 8_000_000, 8_000_000)]
    [InlineData(null, 2_000_000, 8_000_000, 2_000_000)]
    public void Apply_NeverAboveTheCapOrTheRequest(int? target, int? requested, int cap, int expected)
    {
        Assert.Equal(expected, LiveTvBitrateCap.Apply(target, requested, cap));
    }

    [Fact]
    public void IsSd_ByHeightOrMpeg2()
    {
        Assert.True(LiveTvBitrateCap.IsSd(new MediaStream { Height = 576, Codec = "h264" }));
        Assert.False(LiveTvBitrateCap.IsSd(new MediaStream { Height = 720, Codec = "h264" }));
        Assert.True(LiveTvBitrateCap.IsSd(new MediaStream { Codec = "mpeg2video" }));
        Assert.False(LiveTvBitrateCap.IsSd(new MediaStream { Codec = "h264" }));
    }

    [Fact]
    public void EstimateSourceVideoBitrate_PrefersWhatTheTunerSends()
    {
        var mpeg2 = new MediaStream { Codec = "mpeg2video", BitRate = 15_000_000, Height = 576 };
        var h264 = new MediaStream { Codec = "h264", BitRate = 9_000_000, Height = 1080 };

        // Measured, less the audio
        Assert.Equal(2_800_000, LiveTvBitrateCap.EstimateSourceVideoBitrate(3_000_000, 200_000, mpeg2));

        // MPEG-2 states its maximum, not what it sends
        Assert.Null(LiveTvBitrateCap.EstimateSourceVideoBitrate(null, 192_000, mpeg2));

        Assert.Equal(9_000_000, LiveTvBitrateCap.EstimateSourceVideoBitrate(null, null, h264));
    }

    [Fact]
    public void SdChannelTranscodedForTheWeb_CappedFrom30MbpsTo4()
    {
        // The observed case: an SD MPEG-2 channel whose stream says 15 Mbps, a web client asking for 120 Mbps
        var video = new MediaStream { Codec = "mpeg2video", BitRate = 15_000_000, Width = 720, Height = 576 };
        var cap = LiveTvBitrateCap.GetCap(LiveTvBitrateCap.EstimateSourceVideoBitrate(null, 192_000, video), LiveTvBitrateCap.IsSd(video));

        Assert.Equal(4_000_000, LiveTvBitrateCap.Apply(15_000_000, 120_000_000, cap));
    }
}
