using Jellyfin.LiveTv.IO;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.LiveTv.Tests;

/// <summary>
/// A live stream whose buffer the test writes itself.
/// </summary>
internal sealed class TestLiveStream : LiveStream
{
    public TestLiveStream(string transcodePath)
        : base(
            new MediaSourceInfo { Id = "native_test", LiveStreamId = "native_test" },
            new TunerHostInfo { Id = "tuner" },
            new Mock<IFileSystem>().Object,
            NullLogger.Instance,
            LiveTvTestHelpers.CreateConfig(transcodePath).Object,
            new StreamHelper())
    {
        using var writer = CreateBufferWriter();
        writer.Write(new byte[188 * 10]);
    }
}
