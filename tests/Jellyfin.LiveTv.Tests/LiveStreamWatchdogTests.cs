using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests;

public sealed class LiveStreamWatchdogTests : IDisposable
{
    private readonly string _transcodePath = LiveTvTestHelpers.CreateTempDirectory();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_transcodePath, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task GetStream_CountsTheReaderUntilItIsDisposed()
    {
        using var liveStream = await OpenStream();

        var first = liveStream.GetStream();
        var second = liveStream.GetStream();
        Assert.Equal(2, liveStream.ActiveReaderCount);

        await first.DisposeAsync();
        Assert.Equal(1, liveStream.ActiveReaderCount);

        await second.DisposeAsync();
        await second.DisposeAsync();
        Assert.Equal(0, liveStream.ActiveReaderCount);
    }

    [Fact]
    public async Task TryStopNewReaders_WhileRead_Refused()
    {
        using var liveStream = await OpenStream();
        var reader = liveStream.GetStream();
        await using (reader.ConfigureAwait(false))
        {
            Assert.False(liveStream.TryStopNewReaders(TimeSpan.Zero));

            // The reader carries on
            Assert.Equal(188 * 10, await reader.ReadAsync(new byte[188 * 20], TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task TryStopNewReaders_ReadRecently_Refused()
    {
        using var liveStream = await OpenStream();
        await liveStream.GetStream().DisposeAsync();

        Assert.False(liveStream.TryStopNewReaders(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task TryStopNewReaders_Unread_StopsNewReaders()
    {
        using var liveStream = await OpenStream();
        await liveStream.GetStream().DisposeAsync();

        Assert.True(liveStream.TryStopNewReaders(TimeSpan.Zero));
        Assert.Throws<ResourceNotFoundException>(() => liveStream.GetStream());
    }

    [Fact]
    public async Task IsAbandoned_OnlyUnreadForAMinuteAndNotRecording()
    {
        using var liveStream = await OpenStream();
        var now = DateTime.UtcNow;

        Assert.False(LiveStreamWatchdog.IsAbandoned(liveStream, now, false));
        Assert.True(LiveStreamWatchdog.IsAbandoned(liveStream, now.AddMinutes(2), false));
        Assert.False(LiveStreamWatchdog.IsAbandoned(liveStream, now.AddMinutes(2), true));

        using var reader = liveStream.GetStream();
        Assert.False(LiveStreamWatchdog.IsAbandoned(liveStream, now.AddHours(3), false));
    }

    [Fact]
    public async Task CloseAbandonedStreams_ClosesTheUnreadStreamOnly()
    {
        using var read = await OpenStream();
        using var unread = await OpenStream();
        using var recorded = await OpenStream();
        using var reader = read.GetStream();

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager.Setup(m => m.GetOpenLiveStreams()).Returns(
        [
            new KeyValuePair<string, ILiveStream>("read", read),
            new KeyValuePair<string, ILiveStream>("unread", unread),
            new KeyValuePair<string, ILiveStream>("recorded", recorded)
        ]);
        mediaSourceManager
            .Setup(m => m.CloseUnreadLiveStream(It.IsAny<string>(), It.IsAny<ILiveStream>(), It.IsAny<TimeSpan>()))
            .ReturnsAsync(true);
        var recordings = new Mock<IRecordingsManager>();
        recordings.Setup(r => r.IsRecordingLiveStream("recorded")).Returns(true);

        var watchdog = new LiveStreamWatchdog(mediaSourceManager.Object, recordings.Object, NullLogger<LiveStreamWatchdog>.Instance);
        var closed = await watchdog.CloseAbandonedStreams(DateTime.UtcNow.AddMinutes(5));

        Assert.Equal(1, closed);
        mediaSourceManager.Verify(m => m.CloseUnreadLiveStream("unread", unread, LiveStreamWatchdog.UnreadTimeout), Times.Once);
        mediaSourceManager.Verify(m => m.CloseUnreadLiveStream("read", It.IsAny<ILiveStream>(), It.IsAny<TimeSpan>()), Times.Never);
        mediaSourceManager.Verify(m => m.CloseUnreadLiveStream("recorded", It.IsAny<ILiveStream>(), It.IsAny<TimeSpan>()), Times.Never);
    }

    private async Task<TestLiveStream> OpenStream()
    {
        var liveStream = new TestLiveStream(_transcodePath);
        await liveStream.Open(CancellationToken.None);
        return liveStream;
    }
}
