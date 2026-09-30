using Jellyfin.Api.Helpers;
using Xunit;

namespace Jellyfin.Api.Tests.Helpers;

public class LiveHlsWindowTests
{
    [Theory]
    [InlineData(3, 600)]
    [InlineData(6, 300)]
    [InlineData(7, 258)]
    [InlineData(0, 1800)]
    public void GetSegmentCount_HalfAnHour(int segmentLength, int expected)
    {
        Assert.Equal(expected, LiveHlsWindow.GetSegmentCount(segmentLength));
    }

    [Fact]
    public void GetPlaylistArguments_LiveChannel_SlidingWindowThatDeletesOldSegments()
    {
        var arguments = LiveHlsWindow.GetPlaylistArguments(true, true, 6);

        Assert.Equal("-hls_list_size 300 -hls_delete_threshold 5 -hls_flags delete_segments", arguments);
        Assert.DoesNotContain("hls_playlist_type", arguments!, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void GetPlaylistArguments_NotALiveChannel_Unchanged(bool isLivePlaylist, bool isInfiniteStream)
    {
        Assert.Null(LiveHlsWindow.GetPlaylistArguments(isLivePlaylist, isInfiniteStream, 6));
    }
}
