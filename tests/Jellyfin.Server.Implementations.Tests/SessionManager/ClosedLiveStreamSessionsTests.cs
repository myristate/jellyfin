using Emby.Server.Implementations.Session;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.SessionManager;

public class ClosedLiveStreamSessionsTests
{
    [Fact]
    public void WasClosed_EitherIdOfAClosedPair()
    {
        var closed = new ClosedLiveStreamSessions();

        closed.Remember("live", "session", "playSession");

        Assert.True(closed.WasClosed("live", "session"));
        Assert.True(closed.WasClosed("live", "playSession"));
        Assert.False(closed.WasClosed("live", "otherSession"));
        Assert.False(closed.WasClosed("otherLive", "session"));
        Assert.False(closed.WasClosed("live", null));
    }

    [Fact]
    public void Forget_WhenTheSessionPlaysTheStreamAgain()
    {
        var closed = new ClosedLiveStreamSessions();
        closed.Remember("live", "session", "playSession");

        closed.Forget("live", "session", "playSession");

        Assert.False(closed.WasClosed("live", "session"));
        Assert.False(closed.WasClosed("live", "playSession"));
    }
}
