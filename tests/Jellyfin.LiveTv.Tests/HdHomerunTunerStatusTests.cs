using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.TunerHosts.HdHomerun;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.LiveTv.Tests;

public class HdHomerunTunerStatusTests
{
    private const string TwoInUse = """
        [
          {"Resource":"tuner0","VctNumber":"1","VctName":"BBC ONE Lon","Frequency":490000000,"SignalStrengthPercent":100,"TargetIP":"192.168.1.249","NetworkRate":3120000},
          {"Resource":"tuner1"},
          {"Resource":"tuner2","TargetIP":"192.168.1.50"},
          {"Resource":"tuner3","VctNumber":""}
        ]
        """;

    [Fact]
    public void Parse_CountsTunersWithAChannelOrTargetAsInUse()
    {
        var usage = HdHomerunTunerStatus.Parse(TwoInUse);

        Assert.NotNull(usage);
        Assert.Equal(4, usage.Value.Total);
        Assert.Equal(2, usage.Value.InUse);
        Assert.Equal(2, usage.Value.Free);
    }

    [Fact]
    public void Parse_AllInUse_NoneFree()
    {
        var usage = HdHomerunTunerStatus.Parse("""[{"Resource":"tuner0","VctNumber":"1"},{"Resource":"tuner1","VctNumber":"101","TargetIP":"192.168.1.20"}]""");

        Assert.Equal(0, usage!.Value.Free);
    }

    [Fact]
    public void Parse_IgnoresEntriesThatAreNotTuners()
    {
        var usage = HdHomerunTunerStatus.Parse("""[{"Resource":"tuner0"},{"Resource":"record","TargetIP":"1.2.3.4"},{"Name":"x"}]""");

        Assert.Equal(1, usage!.Value.Total);
        Assert.Equal(0, usage.Value.InUse);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Parse_Unreadable_Unknown(string json)
    {
        Assert.Null(HdHomerunTunerStatus.Parse(json));
    }

    [Fact]
    public async Task GetUsage_Unreachable_UnknownRatherThanAnError()
    {
        var status = new HdHomerunTunerStatus(
            LiveTvTestHelpers.CreateHttpClientFactory((_, _) => throw new HttpRequestException("No route to host")),
            NullLogger.Instance);

        Assert.Null(await status.GetUsage("http://tuner", CancellationToken.None));
    }

    [Fact]
    public async Task GetUsage_ReusesTheStatusBriefly()
    {
        var requests = 0;
        var status = new HdHomerunTunerStatus(
            LiveTvTestHelpers.CreateHttpClientFactory((_, _) =>
            {
                Interlocked.Increment(ref requests);
                return Task.FromResult(new HttpResponseMessage { Content = new StringContent(TwoInUse) });
            }),
            NullLogger.Instance);

        var first = await status.GetUsage("http://tuner", CancellationToken.None);
        var second = await status.GetUsage("http://tuner", CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task GetTuners_TellsOfEachRead()
    {
        var reads = 0;
        var status = new HdHomerunTunerStatus(
            LiveTvTestHelpers.CreateHttpClientFactory((_, _) => Task.FromResult(new HttpResponseMessage { Content = new StringContent(TwoInUse) })),
            NullLogger.Instance);
        status.StatusRead = (url, tuners) =>
        {
            Assert.Equal("http://tuner", url);
            Assert.Equal(4, tuners.Count);
            reads++;
        };

        await status.GetUsage("http://tuner", CancellationToken.None);
        await status.GetTuners("http://tuner", TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task GetTuners_ObserverFailing_StillReturnsTheStatus()
    {
        var status = new HdHomerunTunerStatus(
            LiveTvTestHelpers.CreateHttpClientFactory((_, _) => Task.FromResult(new HttpResponseMessage { Content = new StringContent(TwoInUse) })),
            NullLogger.Instance);
        status.StatusRead = (_, _) => throw new InvalidOperationException("Broken");

        Assert.Equal(2, (await status.GetUsage("http://tuner", CancellationToken.None))!.Value.InUse);
    }

    [Fact]
    public async Task GetTuners_ConcurrentCallers_ShareOneRead()
    {
        var requests = 0;
        var release = new TaskCompletionSource();
        var status = new HdHomerunTunerStatus(
            LiveTvTestHelpers.CreateHttpClientFactory(async (_, _) =>
            {
                Interlocked.Increment(ref requests);
                await release.Task;
                return new HttpResponseMessage { Content = new StringContent(TwoInUse) };
            }),
            NullLogger.Instance);

        var first = status.GetTuners("http://tuner", TimeSpan.FromSeconds(2), CancellationToken.None);
        var second = status.GetTuners("http://tuner", TimeSpan.FromSeconds(2), CancellationToken.None);
        release.SetResult();

        Assert.Same(await first, await second);
        Assert.Equal(1, requests);
    }
}
