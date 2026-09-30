using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Library;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library.LiveStreams;

public sealed class LiveStreamProbeCacheTests : IDisposable
{
    private const string OpenToken = "provider_LiveTvChannel_0123_native";

    private readonly string _cachePath = Path.Combine(Path.GetTempPath(), "finly-probe-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IMediaEncoder> _mediaEncoder = new();
    private readonly LiveStreamHelper _helper;
    private readonly string _cacheFile;
    private int _probes;

    public LiveStreamProbeCacheTests()
    {
        Directory.CreateDirectory(_cachePath);
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(a => a.CachePath).Returns(_cachePath);
        _cacheFile = LiveStreamHelper.GetProbeCachePath(appPaths.Object, OpenToken);

        _mediaEncoder
            .Setup(m => m.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref _probes);
                return Probed(1920);
            });
        _helper = new LiveStreamHelper(_mediaEncoder.Object, NullLogger.Instance, appPaths.Object);
    }

    public void Dispose()
    {
        Directory.Delete(_cachePath, true);
    }

    [Fact]
    public async Task NotCached_ProbesAndCachesWithTheDate()
    {
        var source = NewSource();

        await _helper.AddMediaInfoWithProbe(source, false, OpenToken, false, TestContext.Current.CancellationToken);

        Assert.Equal(1, _probes);
        var cached = await _helper.ReadProbeCache(_cacheFile, TestContext.Current.CancellationToken);
        Assert.NotNull(cached);
        Assert.InRange(cached.ProbeDateUtc, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(1920, source.VideoStream.Width);
    }

    [Fact]
    public async Task FreshlyCached_UsesTheCacheWithoutTouchingIt()
    {
        await WriteCache(DateTime.UtcNow.AddDays(-1), 720);
        var lastWrite = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_cacheFile, lastWrite);
        var source = NewSource();

        await _helper.AddMediaInfoWithProbe(source, false, OpenToken, false, TestContext.Current.CancellationToken);
        await _helper.ProbeAgainTask;

        Assert.Equal(0, _probes);
        Assert.Equal(720, source.VideoStream.Width);
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(_cacheFile));
    }

    [Fact]
    public async Task CachedOverAWeekAgo_UsesTheCacheAndProbesAgainInTheBackground()
    {
        await WriteCache(DateTime.UtcNow.AddDays(-8), 720);
        var source = NewSource();

        await _helper.AddMediaInfoWithProbe(source, false, OpenToken, false, TestContext.Current.CancellationToken);

        // The viewer gets the cached probe at once
        Assert.Equal(720, source.VideoStream.Width);

        await _helper.ProbeAgainTask;
        Assert.Equal(1, _probes);
        var cached = await _helper.ReadProbeCache(_cacheFile, TestContext.Current.CancellationToken);
        Assert.False(LiveStreamHelper.IsStale(cached!.ProbeDateUtc, DateTime.UtcNow));
        Assert.Equal(1920, cached.MediaInfo!.VideoStream.Width);
    }

    [Fact]
    public async Task OldFormatCache_DatedByTheFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
        await File.WriteAllBytesAsync(_cacheFile, JsonSerializer.SerializeToUtf8Bytes(Probed(720), JsonDefaults.Options), TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(_cacheFile, DateTime.UtcNow.AddDays(-10));

        var cached = await _helper.ReadProbeCache(_cacheFile, TestContext.Current.CancellationToken);

        Assert.Equal(720, cached!.MediaInfo!.VideoStream.Width);
        Assert.True(LiveStreamHelper.IsStale(cached.ProbeDateUtc, DateTime.UtcNow));
    }

    [Fact]
    public void IsStale_AfterAWeek()
    {
        var now = DateTime.UtcNow;

        Assert.False(LiveStreamHelper.IsStale(now.AddDays(-6.9), now));
        Assert.True(LiveStreamHelper.IsStale(now.AddDays(-7.1), now));
    }

    [Fact]
    public async Task ProbeTimesOut_TimeoutException()
    {
        _mediaEncoder
            .Setup(m => m.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
            .Returns<MediaInfoRequest, CancellationToken>(async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Probed(720);
            });

        await Assert.ThrowsAsync<TimeoutException>(() => _helper.AddMediaInfoWithProbe(NewSource(), false, OpenToken, false, TimeSpan.FromMilliseconds(100), CancellationToken.None));
        Assert.False(File.Exists(_cacheFile));
    }

    private static MediaSourceInfo NewSource()
        => new() { Path = "http://127.0.0.1:8096/LiveTv/LiveStreamFiles/1/stream.ts", Protocol = MediaProtocol.Http, IsInfiniteStream = true };

    private static MediaInfo Probed(int width)
        => new()
        {
            Container = "mpegts",
            MediaStreams =
            [
                new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264", Width = width, Height = width * 9 / 16 },
                new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac" }
            ]
        };

    private Task WriteCache(DateTime probeDateUtc, int width)
        => _helper.WriteProbeCache(_cacheFile, Probed(width), probeDateUtc, TestContext.Current.CancellationToken);
}
