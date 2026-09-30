using System;
using Jellyfin.Api.Middleware;
using MediaBrowser.Controller.LiveTv;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Api.Tests.Middleware;

public class ExceptionMiddlewareLiveTvTests
{
    [Fact]
    public void GetStatusCode_LiveTvErrors()
    {
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ExceptionMiddleware.GetStatusCode(new LiveTvChannelUnavailableException("off air")));
        Assert.Equal(StatusCodes.Status409Conflict, ExceptionMiddleware.GetStatusCode(new LiveTvConflictException("busy")));
        Assert.Equal(StatusCodes.Status502BadGateway, ExceptionMiddleware.GetStatusCode(new LiveTvTunerException("failed", false)));
        Assert.Equal(StatusCodes.Status504GatewayTimeout, ExceptionMiddleware.GetStatusCode(new LiveTvTunerException("no answer", true)));
    }

    [Fact]
    public void AddLiveTvHeaders_NotBroadcasting()
    {
        var context = new DefaultHttpContext();

        ExceptionMiddleware.AddLiveTvHeaders(context.Response, new LiveTvChannelUnavailableException("off air"));

        Assert.Equal("NotBroadcasting", context.Response.Headers["X-Finly-Reason"].ToString());
    }

    [Fact]
    public void AddLiveTvHeaders_Busy_SaysWhenToRetry()
    {
        var context = new DefaultHttpContext();

        ExceptionMiddleware.AddLiveTvHeaders(context.Response, new LiveTvConflictException("busy"));

        Assert.Equal("TunersBusy", context.Response.Headers["X-Finly-Reason"].ToString());
        Assert.False(string.IsNullOrEmpty(context.Response.Headers.RetryAfter.ToString()));
    }

    [Fact]
    public void AddLiveTvHeaders_OtherErrors_NoHeader()
    {
        var context = new DefaultHttpContext();

        ExceptionMiddleware.AddLiveTvHeaders(context.Response, new InvalidOperationException());

        Assert.False(context.Response.Headers.ContainsKey("X-Finly-Reason"));
        Assert.True(ExceptionMiddleware.IsExpectedLiveTvError(new LiveTvTunerException("x", false)));
        Assert.False(ExceptionMiddleware.IsExpectedLiveTvError(new InvalidOperationException()));
    }
}
