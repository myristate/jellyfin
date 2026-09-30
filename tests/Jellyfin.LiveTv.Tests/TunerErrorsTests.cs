using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Controller.LiveTv;
using Xunit;

namespace Jellyfin.LiveTv.Tests;

public class TunerErrorsTests
{
    [Fact]
    public void ToTunerException_Timeout_IsTimeout()
    {
        Assert.True(TunerErrors.ToTunerException(new TimeoutException("No response"), "http://tuner").IsTimeout);
        Assert.True(TunerErrors.ToTunerException(new TaskCanceledException(), "http://tuner").IsTimeout);
    }

    [Fact]
    public void ToTunerException_HttpErrorOrNoData_IsFailure()
    {
        Assert.False(TunerErrors.ToTunerException(new HttpRequestException("refused"), "http://tuner").IsTimeout);
        Assert.False(TunerErrors.ToTunerException(new EndOfStreamException("Zero bytes"), "http://tuner").IsTimeout);
    }

    [Fact]
    public void Choose_BusyComesFirst_ThenFailure_ThenNotBroadcasting()
    {
        var busy = new LiveTvConflictException("805");
        var failed = new LiveTvTunerException("timeout", true);
        var unavailable = new LiveTvChannelUnavailableException("807");

        Assert.Same(busy, TunerErrors.Choose(busy, failed, unavailable));
        Assert.Same(failed, TunerErrors.Choose(null, failed, unavailable));
        Assert.Same(unavailable, TunerErrors.Choose(null, null, unavailable));
        Assert.Null(TunerErrors.Choose(null, null, null));
    }
}
