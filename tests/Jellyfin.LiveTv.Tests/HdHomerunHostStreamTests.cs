using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Jellyfin.LiveTv.TunerHosts.HdHomerun;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.LiveTv;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests;

/// <summary>
/// Opening a channel's stream from an HDHomeRun: telling a busy tuner from a failed one.
/// </summary>
public sealed class HdHomerunHostStreamTests : IDisposable
{
    private const string ChannelId = "hdhr_4.1";
    private const string StreamId = "native_test";

    private readonly string _transcodePath = LiveTvTestHelpers.CreateTempDirectory();
    private int _streamRequests;

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
    public async Task GetChannelStream_StatusSaysAllTunersInUse_BusyWithoutAskingForTheStream()
    {
        var host = CreateHost(
            status: """[{"Resource":"tuner0","VctNumber":"1"},{"Resource":"tuner1","TargetIP":"192.168.1.50"}]""",
            stream: _ => throw new InvalidOperationException("The stream shouldn't be asked for"));

        await Assert.ThrowsAsync<LiveTvConflictException>(() => host.GetChannelStream(ChannelId, StreamId, [], CancellationToken.None));
        Assert.Equal(0, _streamRequests);
    }

    [Fact]
    public async Task GetChannelStream_Tuner805_Busy()
    {
        var host = CreateHost(status: null, stream: _ => Task.FromResult(TunerError("805 All Tuners In Use")));

        await Assert.ThrowsAsync<LiveTvConflictException>(() => host.GetChannelStream(ChannelId, StreamId, [], CancellationToken.None));
    }

    [Fact]
    public async Task GetChannelStream_ConnectionRefused_FailureNotBusy()
    {
        var host = CreateHost(status: null, stream: _ => throw new HttpRequestException("Connection refused"));

        var ex = await Assert.ThrowsAsync<LiveTvTunerException>(() => host.GetChannelStream(ChannelId, StreamId, [], CancellationToken.None));
        Assert.False(ex.IsTimeout);
    }

    [Fact]
    public async Task GetChannelStream_TunerTimesOut_TimeoutFailure()
    {
        // HttpClient reports its own timeout as a cancellation the caller didn't ask for
        var host = CreateHost(status: null, stream: _ => throw new TaskCanceledException("timed out", new TimeoutException()));

        var ex = await Assert.ThrowsAsync<LiveTvTunerException>(() => host.GetChannelStream(ChannelId, StreamId, [], CancellationToken.None));
        Assert.True(ex.IsTimeout);
    }

    [Fact]
    public async Task GetChannelStream_StatusUnreachable_StillTriesTheTuner()
    {
        var host = CreateHost(status: null, stream: _ => Task.FromResult(TunerError("807 No Video Data")));

        await Assert.ThrowsAsync<LiveTvChannelUnavailableException>(() => host.GetChannelStream(ChannelId, StreamId, [], CancellationToken.None));
        Assert.Equal(1, _streamRequests);
    }

    [Fact]
    public async Task GetChannelStream_StatusHasAFreeTuner_TriesTheTuner()
    {
        var host = CreateHost(
            status: """[{"Resource":"tuner0","VctNumber":"1"},{"Resource":"tuner1"}]""",
            stream: _ => Task.FromResult(TunerError("805 All Tuners In Use")));

        await Assert.ThrowsAsync<LiveTvConflictException>(() => host.GetChannelStream(ChannelId, StreamId, [], CancellationToken.None));
        Assert.Equal(1, _streamRequests);
    }

    private static HttpResponseMessage TunerError(string tunerError)
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(string.Empty) };
        response.Headers.Add("X-HDHomeRun-Error", tunerError);
        return response;
    }

    private HdHomerunHost CreateHost(string? status, Func<HttpRequestMessage, Task<HttpResponseMessage>> stream)
    {
        var http = LiveTvTestHelpers.CreateHttpClientFactory((request, _) =>
        {
            var uri = request.RequestUri!;
            switch (uri.Segments[^1])
            {
                case "discover.json":
                case "lineup.json":
                    return Task.FromResult(new HttpResponseMessage
                    {
                        Content = new StreamContent(File.OpenRead(Path.Combine("Test Data/LiveTv", "192.168.1.182", uri.Segments[^1])))
                    });
                case "status.json":
                    return status is null
                        ? throw new HttpRequestException("No route to host")
                        : Task.FromResult(new HttpResponseMessage { Content = new StringContent(status) });
                default:
                    Interlocked.Increment(ref _streamRequests);
                    return stream(request);
            }
        });

        var config = new Mock<IServerConfigurationManager>();
        config.Setup(c => c.GetConfiguration("livetv")).Returns(new LiveTvOptions
        {
            TunerHosts = [new TunerHostInfo { Id = "tuner", Type = "hdhomerun", Url = "192.168.1.182" }]
        });
        config.Setup(c => c.GetConfiguration("encoding")).Returns(new EncodingOptions { TranscodingTempPath = _transcodePath });
        config.Setup(c => c.CommonApplicationPaths).Returns(new Mock<IApplicationPaths>().Object);
        config.Setup(c => c.ApplicationPaths).Returns(new Mock<IServerApplicationPaths>().Object);

        var fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
        fixture.Inject(http);
        fixture.Inject(config.Object);
        fixture.Inject(LiveTvTestHelpers.CreateAppHost().Object);
        return fixture.Create<HdHomerunHost>();
    }
}
