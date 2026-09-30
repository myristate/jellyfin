using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.MediaEncoding.Transcoding;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.Transcoding;

public sealed class TranscodeManagerLiveStreamTests : IDisposable
{
    private readonly string _transcodePath = Path.Combine(Path.GetTempPath(), "finly-transcode-tests-" + Guid.NewGuid());

    public TranscodeManagerLiveStreamTests()
    {
        Directory.CreateDirectory(_transcodePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_transcodePath))
        {
            Directory.Delete(_transcodePath, true);
        }
    }

    [Fact]
    public void GetPingTimeoutMs_LiveHls_ShorterThanRecordedHls()
    {
        var live = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            Type = TranscodingJobType.Hls,
            MediaSource = new MediaSourceInfo { IsInfiniteStream = true }
        };
        var film = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            Type = TranscodingJobType.Hls,
            MediaSource = new MediaSourceInfo()
        };
        var progressive = new TranscodingJob(NullLogger<TranscodingJob>.Instance) { Type = TranscodingJobType.Progressive };

        Assert.Equal(25000, TranscodeManager.GetPingTimeoutMs(live));
        Assert.Equal(60000, TranscodeManager.GetPingTimeoutMs(film));
        Assert.Equal(10000, TranscodeManager.GetPingTimeoutMs(progressive));
    }

    [Fact]
    public async Task StartFfMpeg_FailsAfterOpeningTheLiveStream_ClosesIt()
    {
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager
            .Setup(m => m.OpenLiveStream(It.IsAny<LiveStreamRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LiveStreamResponse(new MediaSourceInfo
            {
                Id = "native_1",
                LiveStreamId = "live1",
                Path = "http://127.0.0.1:8096/LiveTv/LiveStreamFiles/1/stream.ts",
                Protocol = MediaProtocol.Http,
                IsInfiniteStream = true,
                MediaStreams = []
            }));

        // No ffmpeg: starting the transcode fails after the live stream was opened
        var mediaEncoder = new Mock<IMediaEncoder>();
        mediaEncoder.Setup(m => m.EncoderPath).Returns(string.Empty);

        var transcodeManager = CreateTranscodeManager(mediaSourceManager.Object, mediaEncoder.Object);
        var state = new StreamState(mediaSourceManager.Object, TranscodingJobType.Progressive, transcodeManager)
        {
            Request = new StreamingRequestDto { Id = Guid.NewGuid() },
            MediaSource = new MediaSourceInfo { RequiresOpening = true, OpenToken = "token", MediaStreams = [] }
        };

        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => transcodeManager.StartFfMpeg(
            state,
            Path.Combine(_transcodePath, "out.ts"),
            string.Empty,
            Guid.Empty,
            TranscodingJobType.Progressive,
            cancellation));

        mediaSourceManager.Verify(m => m.CloseLiveStream("live1", true), Times.Once);
    }

    [Fact]
    public async Task StartFfMpeg_FailsWithTheClientsLiveStream_LeavesItToTheClient()
    {
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        var mediaEncoder = new Mock<IMediaEncoder>();
        mediaEncoder.Setup(m => m.EncoderPath).Returns(string.Empty);

        var transcodeManager = CreateTranscodeManager(mediaSourceManager.Object, mediaEncoder.Object);
        var state = new StreamState(mediaSourceManager.Object, TranscodingJobType.Progressive, transcodeManager)
        {
            Request = new StreamingRequestDto { Id = Guid.NewGuid(), LiveStreamId = "clients" },
            MediaSource = new MediaSourceInfo { RequiresOpening = true, LiveStreamId = "clients", MediaStreams = [] }
        };

        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => transcodeManager.StartFfMpeg(
            state,
            Path.Combine(_transcodePath, "out.ts"),
            string.Empty,
            Guid.Empty,
            TranscodingJobType.Progressive,
            cancellation));

        mediaSourceManager.Verify(m => m.CloseLiveStream(It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    private TranscodeManager CreateTranscodeManager(IMediaSourceManager mediaSourceManager, IMediaEncoder mediaEncoder)
    {
        var config = new Mock<IServerConfigurationManager>();
        config.Setup(c => c.GetConfiguration("encoding"))
            .Returns(new EncodingOptions { TranscodingTempPath = _transcodePath });
        config.SetupGet(c => c.CommonApplicationPaths).Returns(Mock.Of<IApplicationPaths>());

        var encodingHelper = new EncodingHelper(
            Mock.Of<IApplicationPaths>(),
            mediaEncoder,
            Mock.Of<ISubtitleEncoder>(),
            Mock.Of<IConfiguration>(),
            config.Object,
            Mock.Of<IPathManager>());

        return new TranscodeManager(
            NullLoggerFactory.Instance,
            Mock.Of<IFileSystem>(),
            Mock.Of<IApplicationPaths>(),
            config.Object,
            Mock.Of<IUserManager>(),
            Mock.Of<ISessionManager>(),
            encodingHelper,
            mediaEncoder,
            mediaSourceManager,
            Mock.Of<IAttachmentExtractor>());
    }
}
