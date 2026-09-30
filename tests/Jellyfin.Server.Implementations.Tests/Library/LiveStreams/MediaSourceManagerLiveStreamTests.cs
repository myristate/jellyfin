using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Emby.Server.Implementations.IO;
using Emby.Server.Implementations.Library;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library.LiveStreams;

public sealed class MediaSourceManagerLiveStreamTests : IDisposable
{
    private readonly FakeMediaSourceProvider _provider = new();
    private readonly string _cachePath = Path.Combine(Path.GetTempPath(), "finly-msm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IApplicationPaths> _appPaths;
    private readonly Mock<IUserManager> _userManager;
    private readonly MediaSourceManager _manager;

    public MediaSourceManagerLiveStreamTests()
    {
        IFixture fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
        fixture.Inject<IFileSystem>(fixture.Create<ManagedFileSystem>());
        _userManager = fixture.Freeze<Mock<IUserManager>>();
        _appPaths = fixture.Freeze<Mock<IApplicationPaths>>();
        _appPaths.Setup(a => a.CachePath).Returns(_cachePath);
        _manager = fixture.Create<MediaSourceManager>();
        _manager.AddParts([_provider]);
    }

    public void Dispose()
    {
        _manager.Dispose();
        if (Directory.Exists(_cachePath))
        {
            Directory.Delete(_cachePath, true);
        }
    }

    [Fact]
    public async Task Open_SameChannelTwiceAtOnce_SharesOneStream()
    {
        _provider.OpenChannel = async (channel, _, token) =>
        {
            await Task.Delay(300, token);
            return new FakeLiveStream(channel);
        };

        var first = Open("bbc1");
        var second = Open("bbc1");
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, _provider.Calls("bbc1"));
        Assert.Equal(results[0].Item1.MediaSource.LiveStreamId, results[1].Item1.MediaSource.LiveStreamId);
        var liveStream = _manager.GetLiveStreamInfo(results[0].Item1.MediaSource.LiveStreamId);
        Assert.Equal(2, liveStream.ConsumerCount);
    }

    [Fact]
    public async Task Open_StalledTuner_DoesNotHoldUpOtherChannels()
    {
        var stalled = new TaskCompletionSource<ILiveStream>();
        _provider.OpenChannel = (channel, _, _) => channel == "stalled"
            ? stalled.Task
            : Task.FromResult<ILiveStream>(new FakeLiveStream(channel));

        var other = await Open("itv");
        var stalledOpen = Open("stalled");

        // Opening and closing other channels carries on while one tuner doesn't answer
        var bbc = await Open("bbc1").WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await _manager.CloseLiveStream(other.Item1.MediaSource.LiveStreamId, true).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.NotNull(bbc.Item1.MediaSource);
        Assert.False(stalledOpen.IsCompleted);

        stalled.SetResult(new FakeLiveStream("stalled"));
        await stalledOpen;
    }

    [Fact]
    public async Task Open_TunerFails_NotRetriedAndIdleStreamsKept()
    {
        var idle = await OpenAndLeave("itv");
        _provider.OpenChannel = (channel, _, _) => channel == "bbc1"
            ? Task.FromException<ILiveStream>(new LiveTvTunerException("The tuner didn't respond in time", true))
            : Task.FromResult<ILiveStream>(new FakeLiveStream(channel));

        await Assert.ThrowsAsync<LiveTvTunerException>(() => Open("bbc1"));

        Assert.Equal(1, _provider.Calls("bbc1"));
        Assert.False(idle.IsClosed);
    }

    [Fact]
    public async Task Open_TunerBusy_ClosesIdleStreamsAndTriesAgain()
    {
        var idle = await OpenAndLeave("itv");
        _provider.OpenChannel = (channel, attempt, _) => channel == "bbc1" && attempt == 1
            ? Task.FromException<ILiveStream>(new LiveTvConflictException("805"))
            : Task.FromResult<ILiveStream>(new FakeLiveStream(channel));

        var result = await Open("bbc1");

        Assert.Equal(2, _provider.Calls("bbc1"));
        Assert.True(idle.IsClosed);
        Assert.NotNull(result.Item1.MediaSource);
    }

    [Fact]
    public async Task Open_TunerBusyAndNothingIdle_FailsAtOnce()
    {
        var watched = await Open("itv");
        _provider.OpenChannel = (channel, _, _) => channel == "bbc1"
            ? Task.FromException<ILiveStream>(new LiveTvConflictException("805"))
            : Task.FromResult<ILiveStream>(new FakeLiveStream(channel));

        await Assert.ThrowsAsync<LiveTvConflictException>(() => Open("bbc1"));

        Assert.Equal(1, _provider.Calls("bbc1"));
        Assert.False(((FakeLiveStream)_manager.GetLiveStreamInfo(watched.Item1.MediaSource.LiveStreamId)).IsClosed);
    }

    [Fact]
    public async Task Open_BackgroundTaskBusy_DoesNotCloseIdleStreams()
    {
        var idle = await OpenAndLeave("itv");
        _provider.OpenChannel = (channel, _, _) => channel == "bbc1"
            ? Task.FromException<ILiveStream>(new LiveTvConflictException("805"))
            : Task.FromResult<ILiveStream>(new FakeLiveStream(channel));

        await Assert.ThrowsAsync<LiveTvConflictException>(() => _manager.OpenLiveStreamInternal(
            new LiveStreamRequest { OpenToken = FakeMediaSourceProvider.OpenToken("bbc1") },
            new LiveStreamOpenOptions { CloseIdleStreamsWhenBusy = false },
            CancellationToken.None));

        Assert.False(idle.IsClosed);
    }

    [Fact]
    public async Task Open_SameIdInUse_SharesItAndClosesTheNewStream()
    {
        FakeLiveStream? first = null;
        FakeLiveStream? second = null;
        _provider.OpenChannel = (channel, _, _) =>
        {
            var stream = new FakeLiveStream("same");
            if (first is null)
            {
                first = stream;
            }
            else
            {
                second = stream;
            }

            return Task.FromResult<ILiveStream>(stream);
        };

        await Open("a");
        await Open("b");

        Assert.False(first!.IsClosed);
        Assert.True(second!.IsClosed);
        Assert.Equal(2, first.ConsumerCount);
    }

    [Fact]
    public async Task Open_SameIdNobodyUsing_ReplacesIt()
    {
        FakeLiveStream? first = null;
        _provider.OpenChannel = (channel, _, _) =>
        {
            var stream = new FakeLiveStream("same");
            first ??= stream;
            return Task.FromResult<ILiveStream>(stream);
        };

        var opened = await Open("a");
        await _manager.CloseLiveStream(opened.Item1.MediaSource.LiveStreamId, false);
        var replaced = await Open("b");

        Assert.True(first!.IsClosed);
        Assert.NotSame(first, _manager.GetLiveStreamInfo(replaced.Item1.MediaSource.LiveStreamId));
    }

    [Fact]
    public async Task Open_FailsAfterOpening_ClosesTheStream()
    {
        FakeLiveStream? stream = null;
        _provider.OpenChannel = (channel, _, _) => Task.FromResult<ILiveStream>(stream = new FakeLiveStream(channel));
        _userManager.Setup(m => m.GetUserById(It.IsAny<Guid>())).Throws(new InvalidOperationException("broken"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.OpenLiveStreamInternal(
            new LiveStreamRequest { OpenToken = FakeMediaSourceProvider.OpenToken("bbc1"), UserId = Guid.NewGuid() },
            CancellationToken.None));

        Assert.True(stream!.IsClosed);
        Assert.Null(_manager.GetLiveStreamInfo(stream.MediaSource.LiveStreamId));
    }

    [Fact]
    public async Task Dispose_ClosesOpenStreamsStraightAway()
    {
        var idle = await OpenAndLeave("itv");
        var watched = await Open("bbc1");
        var watchedStream = (FakeLiveStream)_manager.GetLiveStreamInfo(watched.Item1.MediaSource.LiveStreamId);

        _manager.Dispose();

        Assert.True(idle.IsClosed);
        Assert.True(watchedStream.IsClosed);
    }

    [Fact]
    public async Task CloseUnreadLiveStream_ClosesAStreamNobodyReadsWhateverItsConsumers()
    {
        var opened = await Open("bbc1");
        var id = opened.Item1.MediaSource.LiveStreamId;
        var liveStream = (FakeLiveStream)_manager.GetLiveStreamInfo(id);
        liveStream.LastReaderLeftUtc = DateTime.UtcNow.AddMinutes(-5);

        Assert.True(await _manager.CloseUnreadLiveStream(id, liveStream, TimeSpan.FromMinutes(1)));

        Assert.True(liveStream.IsClosed);
        Assert.Null(_manager.GetLiveStreamInfo(id));
    }

    [Fact]
    public async Task CloseUnreadLiveStream_NeverClosesAStreamBeingRead()
    {
        var opened = await Open("bbc1");
        var id = opened.Item1.MediaSource.LiveStreamId;
        var liveStream = (FakeLiveStream)_manager.GetLiveStreamInfo(id);
        liveStream.ActiveReaderCount = 1;
        liveStream.LastReaderLeftUtc = DateTime.UtcNow.AddHours(-3);

        Assert.False(await _manager.CloseUnreadLiveStream(id, liveStream, TimeSpan.Zero));

        Assert.False(liveStream.IsClosed);
        Assert.Same(liveStream, _manager.GetLiveStreamInfo(id));
    }

    [Fact]
    public async Task Open_TunerBusy_DoesNotCloseAnIdleStreamSomeoneStillReads()
    {
        var idle = await OpenAndLeave("itv");
        idle.ActiveReaderCount = 1;
        _provider.OpenChannel = (channel, _, _) => channel == "bbc1"
            ? Task.FromException<ILiveStream>(new LiveTvConflictException("805"))
            : Task.FromResult<ILiveStream>(new FakeLiveStream(channel));

        await Assert.ThrowsAsync<LiveTvConflictException>(() => Open("bbc1"));

        Assert.False(idle.IsClosed);
    }

    [Fact]
    public async Task InvalidateLiveStreamProbe_DeletesTheChannelsProbe()
    {
        var opened = await Open("bbc1");
        var cacheFile = LiveStreamHelper.GetProbeCachePath(_appPaths.Object, FakeMediaSourceProvider.OpenToken("bbc1"));
        Directory.CreateDirectory(Path.GetDirectoryName(cacheFile)!);
        await File.WriteAllTextAsync(cacheFile, "{}", TestContext.Current.CancellationToken);

        _manager.InvalidateLiveStreamProbe(opened.Item1.MediaSource.LiveStreamId);

        Assert.False(File.Exists(cacheFile));
    }

    private Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> Open(string channel)
        => _manager.OpenLiveStreamInternal(new LiveStreamRequest { OpenToken = FakeMediaSourceProvider.OpenToken(channel) }, CancellationToken.None);

    // Opens a channel and leaves it, so it is kept open for the grace period with nobody watching
    private async Task<FakeLiveStream> OpenAndLeave(string channel)
    {
        var opened = await Open(channel);
        var id = opened.Item1.MediaSource.LiveStreamId;
        var liveStream = (FakeLiveStream)_manager.GetLiveStreamInfo(id);
        await _manager.CloseLiveStream(id, false);
        Assert.Equal(0, liveStream.ConsumerCount);
        Assert.False(liveStream.IsClosed);
        return liveStream;
    }
}
