using System;
using System.Collections.Generic;
using System.IO;
using Emby.Server.Implementations.EntryPoints;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.EntryPoints;

public sealed class TranscodeFolderSweeperTests : IDisposable
{
    private const string HlsJob = "0123456789abcdef0123456789abcdef";
    private const string LiveStream = "fedcba9876543210fedcba9876543210";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "finly-sweeper-tests-" + Guid.NewGuid().ToString("N"));

    public TranscodeFolderSweeperTests()
    {
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        Directory.Delete(_folder, true);
    }

    [Fact]
    public void Sweep_DeletesOnlyOldFilesNothingUses()
    {
        var old = DateTime.UtcNow.AddHours(-1);
        var marker = CreateFile(".jellyfin-transcode", old);
        var playlist = CreateFile(HlsJob + ".m3u8", old);
        var segment = CreateFile(HlsJob + "0.ts", old);
        var init = CreateFile(HlsJob + "-1.mp4", old);
        var buffer = CreateFile(LiveStream + ".ts", old);
        var leftover = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa12.ts", old);
        var leftoverPlaylist = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.m3u8", old);
        var recent = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb.ts", DateTime.UtcNow.AddMinutes(-2));

        var sweeper = CreateSweeper([Path.Combine(_folder, HlsJob + ".m3u8")], [LiveStream]);

        Assert.Equal(2, sweeper.Sweep());

        Assert.True(File.Exists(marker));
        Assert.True(File.Exists(playlist));
        Assert.True(File.Exists(segment));
        Assert.True(File.Exists(init));
        Assert.True(File.Exists(buffer));
        Assert.True(File.Exists(recent));
        Assert.False(File.Exists(leftover));
        Assert.False(File.Exists(leftoverPlaylist));
    }

    [Fact]
    public void Sweep_CantTellWhatIsInUse_DeletesNothing()
    {
        var leftover = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa12.ts", DateTime.UtcNow.AddHours(-1));

        var sweeper = CreateSweeper(null, [LiveStream]);

        Assert.Equal(0, sweeper.Sweep());
        Assert.True(File.Exists(leftover));
    }

    [Fact]
    public void GetInUseNames_SkipsEmptyNames()
    {
        var names = TranscodeFolderSweeper.GetInUseNames([string.Empty, "/transcodes/abc.m3u8"], [null, "live"]);

        Assert.Equal(2, names.Count);
        Assert.Contains("abc", names);
        Assert.Contains("live", names);
    }

    private string CreateFile(string name, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private TranscodeFolderSweeper CreateSweeper(IReadOnlyList<string>? transcodePaths, IEnumerable<string> liveStreamIds)
    {
        var transcodeManager = new Mock<ITranscodeManager>();
        transcodeManager.Setup(t => t.GetActiveTranscodingPaths()).Returns(transcodePaths);

        var liveStreams = new List<KeyValuePair<string, ILiveStream>>();
        foreach (var id in liveStreamIds)
        {
            var liveStream = new Mock<ILiveStream>();
            liveStream.Setup(l => l.UniqueId).Returns(id);
            liveStreams.Add(new KeyValuePair<string, ILiveStream>("live_" + id, liveStream.Object));
        }

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager.Setup(m => m.GetOpenLiveStreams()).Returns(liveStreams);

        var config = new Mock<IConfigurationManager>();
        config.Setup(c => c.GetConfiguration("encoding")).Returns(new EncodingOptions { TranscodingTempPath = _folder });
        config.Setup(c => c.CommonApplicationPaths).Returns(Mock.Of<IApplicationPaths>());

        return new TranscodeFolderSweeper(transcodeManager.Object, mediaSourceManager.Object, config.Object, NullLogger<TranscodeFolderSweeper>.Instance);
    }
}
