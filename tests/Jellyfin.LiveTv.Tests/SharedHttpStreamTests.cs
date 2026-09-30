using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.IO;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests;

public sealed class SharedHttpStreamTests : IDisposable
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
    public async Task Open_AllTunersInUse_IsBusy()
    {
        using var stream = CreateStream((_, _) => Task.FromResult(TunerError(HttpStatusCode.ServiceUnavailable, "805 All Tuners In Use")));

        await Assert.ThrowsAsync<LiveTvConflictException>(() => stream.Open(CancellationToken.None));
        Assert.True(stream.IsDisposed);
    }

    [Theory]
    [InlineData("806 Tune Failed")]
    [InlineData("807 No Video Data")]
    public async Task Open_ChannelNotBroadcasting_IsUnavailable(string tunerError)
    {
        using var stream = CreateStream((_, _) => Task.FromResult(TunerError(HttpStatusCode.ServiceUnavailable, tunerError)));

        await Assert.ThrowsAsync<LiveTvChannelUnavailableException>(() => stream.Open(CancellationToken.None));
    }

    [Fact]
    public async Task Open_OtherTunerError_IsNotBusy()
    {
        using var stream = CreateStream((_, _) => Task.FromResult(TunerError(HttpStatusCode.InternalServerError, "801 Unknown Channel")));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => stream.Open(CancellationToken.None));
        Assert.False(TunerErrors.ToTunerException(ex, "http://tuner").IsTimeout);
    }

    [Fact]
    public async Task Open_TunerDoesNotAnswer_TimesOut()
    {
        using var stream = CreateStream(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        });
        stream.OpenTimeout = TimeSpan.FromMilliseconds(200);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => stream.Open(CancellationToken.None));
        Assert.True(TunerErrors.ToTunerException(ex, "http://tuner").IsTimeout);
        Assert.True(stream.IsDisposed);
    }

    [Fact]
    public async Task Open_TunerSendsNoData_TimesOutAndReleasesTheTuner()
    {
        var body = new SilentTunerStream();
        using var stream = CreateStream((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));
        stream.OpenTimeout = TimeSpan.FromMilliseconds(200);

        await Assert.ThrowsAsync<TimeoutException>(() => stream.Open(CancellationToken.None));

        await body.Released.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(stream.IsDisposed);
    }

    [Fact]
    public async Task Open_CallerGivesUp_StopsCopyingAndDisposes()
    {
        var body = new SilentTunerStream();
        using var stream = CreateStream((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stream.Open(cancellation.Token));

        // The copy from the tuner stops, so the tuner is released
        await body.Released.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(stream.IsDisposed);
        Assert.False(stream.EnableStreamSharing);

        // Closing a stream that failed to open is harmless
        await stream.Close();
    }

    [Fact]
    public async Task Open_ConnectionRefused_IsAFailureNotABusyTuner()
    {
        using var stream = CreateStream((_, _) => throw new HttpRequestException("Connection refused"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => stream.Open(CancellationToken.None));
        Assert.False(TunerErrors.ToTunerException(ex, "http://tuner").IsTimeout);
        Assert.True(stream.IsDisposed);
    }

    private static HttpResponseMessage TunerError(HttpStatusCode status, string tunerError)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(string.Empty) };
        response.Headers.Add("X-HDHomeRun-Error", tunerError);
        return response;
    }

    private SharedHttpStream CreateStream(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        var mediaSource = new MediaSourceInfo { Path = "http://tuner:5004/auto/v1", Protocol = MediaProtocol.Http };
        return new SharedHttpStream(
            mediaSource,
            new TunerHostInfo { Id = "tuner", Url = "http://tuner" },
            "native_1",
            new Mock<IFileSystem>().Object,
            LiveTvTestHelpers.CreateHttpClientFactory(send),
            NullLogger.Instance,
            LiveTvTestHelpers.CreateConfig(_transcodePath).Object,
            LiveTvTestHelpers.CreateAppHost().Object,
            new StreamHelper());
    }
}
