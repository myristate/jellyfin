using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Signal;
using Jellyfin.LiveTv.TunerHosts.HdHomerun;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests.Signal;

public sealed class ChannelSignalServiceTests : IDisposable
{
    private const string Lineup = """
        [
          {"GuideNumber":"1","GuideName":"BBC ONE Scot","VideoCodec":"MPEG2","AudioCodec":"MPEG","SignalStrength":84,"SignalQuality":100,"URL":"http://192.168.1.19:5004/auto/v1"},
          {"GuideNumber":"4","GuideName":"Channel 4","VideoCodec":"MPEG2","AudioCodec":"MPEG","SignalStrength":82,"SignalQuality":100,"URL":"http://192.168.1.19:5004/auto/v4"},
          {"GuideNumber":"78","GuideName":"That's 70s","URL":"http://192.168.1.19:5004/auto/v78"}
        ]
        """;

    private const string IdleStatus = """[{"Resource":"tuner0"},{"Resource":"tuner1"},{"Resource":"tuner2"},{"Resource":"tuner3"}]""";

    private const string Channel4Status = """
        [{"Resource":"tuner0","VctNumber":"4","VctName":"Channel 4","Frequency":650000000,"SignalStrengthPercent":83,"SignalQualityPercent":100,"SymbolQualityPercent":100,"TargetIP":"192.168.1.249"},{"Resource":"tuner1"}]
        """;

    private static readonly Guid Channel1Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Channel4Id = Guid.Parse("00000000-0000-0000-0000-000000000004");
    private static readonly Guid M3uChannelId = Guid.Parse("00000000-0000-0000-0000-000000000099");

    private readonly string _directory = LiveTvTestHelpers.CreateTempDirectory();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly List<string> _requests = [];
    private string _status = IdleStatus;

    public ChannelSignalServiceTests()
    {
        var channels = new List<BaseItem>
        {
            new LiveTvChannel { Id = Channel1Id, ExternalId = "hdhr_1", Number = "1" },
            new LiveTvChannel { Id = Channel4Id, ExternalId = "hdhr_4", Number = "4" },
            new LiveTvChannel { Id = M3uChannelId, ExternalId = "m3u_abc_1", Number = "1" }
        };
        foreach (var channel in channels)
        {
            _libraryManager.Setup(l => l.GetItemById(channel.Id)).Returns(channel);
        }

        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(channels);
    }

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void ParseLineup_ReadsScanValues_MissingAreUnknown()
    {
        var channels = ChannelSignalService.ParseLineup(Lineup);

        Assert.Equal(3, channels.Count);
        Assert.Equal(new ChannelSignalStore.LineupSignal("1", 84, 100), channels[0]);
        Assert.Equal(new ChannelSignalStore.LineupSignal("78", null, null), channels[2]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nope")]
    [InlineData("{}")]
    [InlineData("""[{"GuideName":"No number"}]""")]
    public void ParseLineup_Unreadable_Empty(string json)
    {
        Assert.Empty(ChannelSignalService.ParseLineup(json));
    }

    [Theory]
    [InlineData("hdhr_4", "4")]
    [InlineData("hdhr_4.1", "4.1")]
    [InlineData("hdhr_", null)]
    [InlineData("m3u_abc_1", null)]
    [InlineData(null, null)]
    public void GetGuideNumber_FromHdHomerunChannelIds(string? externalId, string? guideNumber)
    {
        Assert.Equal(guideNumber, ChannelSignalService.GetGuideNumber(externalId));
    }

    [Fact]
    public async Task GetChannelSignals_SeedsFromLineup_ByChannelItemId()
    {
        using var service = CreateService();

        var signals = await service.GetChannelSignalsAsync(CancellationToken.None);

        Assert.Equal(2, signals.Count);
        var four = signals[Channel4Id];
        Assert.Equal(82, four.Strength);
        Assert.Equal(100, four.Quality);
        Assert.False(four.IsLive);
        Assert.False(four.IsWeak);
        Assert.True(signals.ContainsKey(Channel1Id));
        Assert.False(signals.ContainsKey(M3uChannelId));
        Assert.Equal(80, service.WeakQualityThreshold);
    }

    [Fact]
    public async Task GetChannelSignal_TunerOnTheChannel_LiveReading()
    {
        using var service = CreateService();
        _status = Channel4Status;

        var signal = await service.GetChannelSignalAsync(Channel4Id, CancellationToken.None);

        Assert.NotNull(signal);
        Assert.True(signal.IsLive);
        Assert.Equal(83, signal.Strength);
        Assert.Equal(100, signal.SymbolQuality);
        Assert.Contains("http://192.168.1.19/status.json", _requests);
    }

    [Fact]
    public async Task GetChannelSignal_ReadsTheStatusAtMostEveryTwoSeconds()
    {
        using var service = CreateService();

        await service.GetChannelSignalAsync(Channel4Id, CancellationToken.None);
        await service.GetChannelSignalAsync(Channel4Id, CancellationToken.None);

        Assert.Single(_requests, r => r.EndsWith("/status.json", StringComparison.Ordinal));
        Assert.Single(_requests, r => r.EndsWith("/lineup.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetChannelSignal_NotATunerChannel_Null()
    {
        using var service = CreateService();

        Assert.Null(await service.GetChannelSignalAsync(M3uChannelId, CancellationToken.None));
        Assert.Null(await service.GetChannelSignalAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetChannelSignal_NoReading_Null()
    {
        _libraryManager.Setup(l => l.GetItemById(It.Is<Guid>(id => id.Equals(M3uChannelId))))
            .Returns(new LiveTvChannel { Id = M3uChannelId, ExternalId = "hdhr_78" });
        using var service = CreateService();

        Assert.Null(await service.GetChannelSignalAsync(M3uChannelId, CancellationToken.None));
    }

    [Fact]
    public async Task PollOpenStreams_OnlyReadsWhileAnHdHomerunStreamIsOpen()
    {
        using var service = CreateService();

        Assert.Equal(0, await service.PollOpenStreamsAsync([], CancellationToken.None));
        Assert.Equal(0, await service.PollOpenStreamsAsync(["m3u-host"], CancellationToken.None));
        Assert.Empty(_requests);

        _status = Channel4Status;
        Assert.Equal(1, await service.PollOpenStreamsAsync(["hdhr-host", null], CancellationToken.None));
        Assert.Equal(new[] { "http://192.168.1.19/status.json" }, _requests);
    }

    [Fact]
    public async Task FreeTunerCheck_RecordsTheReadingsItSees()
    {
        var tunerStatus = CreateTunerStatus();
        using var service = CreateService(tunerStatus);
        _status = Channel4Status;

        await tunerStatus.GetUsage("http://192.168.1.19", CancellationToken.None);

        _status = IdleStatus;
        var signals = await service.GetChannelSignalsAsync(CancellationToken.None);
        Assert.True(signals[Channel4Id].IsLive);
        Assert.Equal(83, signals[Channel4Id].Strength);
    }

    private HdHomerunTunerStatus CreateTunerStatus()
        => new(
            LiveTvTestHelpers.CreateHttpClientFactory((request, _) =>
            {
                var url = request.RequestUri!.ToString();
                lock (_requests)
                {
                    _requests.Add(url);
                }

                var body = url.EndsWith("/lineup.json", StringComparison.Ordinal) ? Lineup : _status;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
            }),
            NullLogger.Instance);

    private ChannelSignalService CreateService(HdHomerunTunerStatus? tunerStatus = null)
    {
        tunerStatus ??= CreateTunerStatus();
        var paths = new Mock<IApplicationPaths>();
        paths.Setup(p => p.ConfigurationDirectoryPath).Returns(_directory);
        var config = new Mock<IConfigurationManager>();
        config.Setup(c => c.CommonApplicationPaths).Returns(paths.Object);
        config.Setup(c => c.GetConfiguration("livetv")).Returns(new LiveTvOptions
        {
            TunerHosts =
            [
                new TunerHostInfo { Id = "hdhr-host", Type = "hdhomerun", Url = "192.168.1.19" },
                new TunerHostInfo { Id = "m3u-host", Type = "m3u", Url = "http://example/list.m3u" }
            ]
        });

        // The tuner status reader shares the HTTP client factory, so lineup reads are counted too
        var http = LiveTvTestHelpers.CreateHttpClientFactory((request, _) =>
        {
            var url = request.RequestUri!.ToString();
            lock (_requests)
            {
                _requests.Add(url);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Lineup) });
        });

        return new ChannelSignalService(tunerStatus, config.Object, _libraryManager.Object, http, NullLogger<ChannelSignalService>.Instance);
    }
}
