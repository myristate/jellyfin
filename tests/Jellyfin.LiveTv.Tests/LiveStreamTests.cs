using System;
using Jellyfin.LiveTv.TunerHosts;
using Xunit;

namespace Jellyfin.LiveTv.Tests;

public class LiveStreamTests
{
    [Fact]
    public void GetJoinPosition_StartsBacklogBeforeEndOnPacketBoundary()
    {
        // 10 seconds at 500 kB/s: 2.5 seconds back is 1,250,000 bytes before the end
        var length = 5_000_000L;
        var position = LiveStream.GetJoinPosition(length, TimeSpan.FromSeconds(10));

        Assert.Equal(0, position % 188);
        Assert.InRange(length - position, 1_250_000, 1_250_000 + 188);
    }

    [Fact]
    public void GetJoinPosition_UnalignedLength_StaysOnPacketBoundary()
    {
        var position = LiveStream.GetJoinPosition(5_000_123, TimeSpan.FromSeconds(10));

        Assert.Equal(0, position % 188);
    }

    [Fact]
    public void GetJoinPosition_EmptyOrNew_StartsAtBeginning()
    {
        Assert.Equal(0, LiveStream.GetJoinPosition(0, TimeSpan.FromSeconds(30)));
        Assert.Equal(0, LiveStream.GetJoinPosition(1000, TimeSpan.Zero));
    }
}
