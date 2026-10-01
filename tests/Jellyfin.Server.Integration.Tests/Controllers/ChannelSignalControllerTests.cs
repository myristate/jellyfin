using System;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Signal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

/// <summary>
/// Covers the channel signal routes as clients call them (Finly).
/// </summary>
public sealed class ChannelSignalControllerTests : IClassFixture<JellyfinApplicationFactory>
{
    private static string? _accessToken;
    private readonly JellyfinApplicationFactory _factory;

    public ChannelSignalControllerTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/LiveTv/ChannelSignal")]
    [InlineData("/LiveTv/ChannelSignal/1a2b3c4d0000400080000123456789ab")]
    public async Task ChannelSignal_SignedOut_Unauthorized(string url)
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync(url, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetChannelSignals_NoTuner_EmptyWithThreshold()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        var response = await client.GetAsync("/LiveTv/ChannelSignal", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(80, json.RootElement.GetProperty("WeakQualityThreshold").GetInt32());
        Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("Channels").ValueKind);
    }

    [Fact]
    public async Task GetChannelSignal_NotAChannel_NotFound()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));

        var response = await client.GetAsync("/LiveTv/ChannelSignal/" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_HasBothRoutesUnderLiveTv()
    {
        var client = _factory.CreateClient();

        var spec = await client.GetStringAsync("/api-docs/openapi.json", TestContext.Current.CancellationToken);

        using var json = JsonDocument.Parse(spec);
        var paths = json.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/LiveTv/ChannelSignal", out _));
        Assert.True(paths.TryGetProperty("/LiveTv/ChannelSignal/{channelId}", out _));
    }

    [Fact]
    public void ChannelSignalMonitor_IsRegisteredAsHostedService()
    {
        _ = _factory.CreateClient();

        Assert.Contains(_factory.Services.GetServices<IHostedService>(), service => service is ChannelSignalMonitor);
    }
}
