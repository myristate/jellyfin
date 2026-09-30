using Jellyfin.Api.Helpers;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Helpers;

public class StreamingHelpersLiveTvTests
{
    [Fact]
    public void CapLiveTvBitrate_TranscodedSdChannel_UsesTheMeasuredBitrate()
    {
        var liveStream = new Mock<ILiveStream>();
        liveStream.Setup(l => l.MeasuredBitrate).Returns(3_192_000);
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager.Setup(m => m.GetLiveStreamInfo("live")).Returns(liveStream.Object);
        var state = CreateState(mediaSourceManager.Object, "h264", isInfinite: true);

        StreamingHelpers.CapLiveTvBitrate(state, state.MediaSource, 120_000_000, mediaSourceManager.Object);

        // Twice the 3 Mbps of video the tuner sends
        Assert.Equal(6_000_000, state.OutputVideoBitrate);
    }

    [Fact]
    public void CapLiveTvBitrate_NotMeasuredYet_SdFloor()
    {
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        var state = CreateState(mediaSourceManager.Object, "h264", isInfinite: true);

        StreamingHelpers.CapLiveTvBitrate(state, state.MediaSource, 120_000_000, mediaSourceManager.Object);

        Assert.Equal(4_000_000, state.OutputVideoBitrate);
    }

    [Fact]
    public void CapLiveTvBitrate_VideoCopied_LeftAlone()
    {
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        var state = CreateState(mediaSourceManager.Object, "copy", isInfinite: true);

        StreamingHelpers.CapLiveTvBitrate(state, state.MediaSource, 120_000_000, mediaSourceManager.Object);

        Assert.Equal(30_000_000, state.OutputVideoBitrate);
    }

    [Fact]
    public void CapLiveTvBitrate_NotLiveTv_LeftAlone()
    {
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        var state = CreateState(mediaSourceManager.Object, "h264", isInfinite: false);

        StreamingHelpers.CapLiveTvBitrate(state, state.MediaSource, 120_000_000, mediaSourceManager.Object);

        Assert.Equal(30_000_000, state.OutputVideoBitrate);
    }

    private static StreamState CreateState(IMediaSourceManager mediaSourceManager, string outputVideoCodec, bool isInfinite)
    {
        var state = new StreamState(mediaSourceManager, TranscodingJobType.Hls, Mock.Of<ITranscodeManager>())
        {
            MediaSource = new MediaSourceInfo { LiveStreamId = "live", IsInfiniteStream = isInfinite },
            VideoStream = new MediaStream { Type = MediaStreamType.Video, Codec = "mpeg2video", BitRate = 15_000_000, Width = 720, Height = 576 },
            AudioStream = new MediaStream { Type = MediaStreamType.Audio, Codec = "mp2", BitRate = 192_000 },
            OutputVideoCodec = outputVideoCodec,
            OutputVideoBitrate = 30_000_000
        };
        return state;
    }
}
