using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Models.LiveTvDtos;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

/// <summary>
/// Covers the channel signal endpoints and the JSON clients read (Finly).
/// </summary>
public class ChannelSignalControllerTests
{
    private static readonly Guid ChannelId = Guid.Parse("1a2b3c4d-0000-4000-8000-0123456789ab");

    private static readonly ChannelSignal Scanned = new()
    {
        Strength = 84,
        Quality = 100,
        SymbolQuality = null,
        MeasuredAt = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc),
        IsLive = false,
        IsWeak = false
    };

    private readonly Mock<IChannelSignalService> _signals = new();
    private readonly ChannelSignalController _subject;

    public ChannelSignalControllerTests()
    {
        _signals.Setup(s => s.WeakQualityThreshold).Returns(80);
        _subject = new ChannelSignalController(_signals.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    [Fact]
    public async Task GetChannelSignals_KeysAreChannelIdsWithoutDashes()
    {
        _signals.Setup(s => s.GetChannelSignalsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, ChannelSignal> { [ChannelId] = Scanned });

        var result = (await _subject.GetChannelSignals()).Value;

        Assert.NotNull(result);
        Assert.Equal(80, result.WeakQualityThreshold);
        Assert.Same(Scanned, result.Channels["1a2b3c4d0000400080000123456789ab"]);
    }

    [Fact]
    public async Task GetChannelSignal_NoReading_NotFound()
    {
        _signals.Setup(s => s.GetChannelSignalAsync(ChannelId, It.IsAny<CancellationToken>())).ReturnsAsync((ChannelSignal?)null);

        var result = await _subject.GetChannelSignal(ChannelId);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetChannelSignal_Reading_Ok()
    {
        _signals.Setup(s => s.GetChannelSignalAsync(ChannelId, It.IsAny<CancellationToken>())).ReturnsAsync(Scanned);

        var result = await _subject.GetChannelSignal(ChannelId);

        Assert.Same(Scanned, result.Value);
    }

    [Fact]
    public void Json_MatchesTheClientContract()
    {
        var dto = new ChannelSignalsDto
        {
            WeakQualityThreshold = 80,
            Channels = new Dictionary<string, ChannelSignal> { ["1a2b3c4d0000400080000123456789ab"] = Scanned }
        };

        var json = JsonSerializer.Serialize(dto, JsonDefaults.PascalCaseOptions);

        Assert.Equal(
            """{"WeakQualityThreshold":80,"Channels":{"1a2b3c4d0000400080000123456789ab":{"Strength":84,"Quality":100,"SymbolQuality":null,"MeasuredAt":"2026-10-01T08:00:00.0000000Z","IsLive":false,"IsWeak":false}}}""",
            json);
    }
}
