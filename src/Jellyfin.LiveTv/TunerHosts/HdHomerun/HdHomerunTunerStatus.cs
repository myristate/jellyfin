using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts.HdHomerun;

/// <summary>
/// Reads which of an HDHomeRun's tuners are in use from its status.json (Finly). It sees every user of the tuner, including
/// another server sharing it, so a busy tuner is known before asking it for a stream.
/// </summary>
internal sealed class HdHomerunTunerStatus
{
    /// <summary>
    /// How long a status is reused. Short, so a tuner freed a moment ago is seen.
    /// </summary>
    internal static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, (DateTime Time, TunerUsage? Usage)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="HdHomerunTunerStatus"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="logger">The logger.</param>
    public HdHomerunTunerStatus(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Gets how many of the tuner's tuners are in use.
    /// </summary>
    /// <param name="baseUrl">The tuner's address, such as http://192.168.1.19.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tuners in use, or <c>null</c> when the status can't be read.</returns>
    public async Task<TunerUsage?> GetUsage(string baseUrl, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(baseUrl, out var cached) && now - cached.Time < CacheTime)
        {
            return cached.Usage;
        }

        TunerUsage? usage = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            var json = await _httpClientFactory.CreateClient(NamedClient.Default)
                .GetStringAsync(baseUrl.TrimEnd('/') + "/status.json", timeout.Token)
                .ConfigureAwait(false);
            usage = Parse(json);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Not knowing is no reason not to try the tuner
            _logger.LogDebug("Couldn't read the tuner status from {Url}: {Message}", baseUrl, ex.Message);
        }

        _cache[baseUrl] = (now, usage);
        return usage;
    }

    /// <summary>
    /// Reads status.json: a list with an entry per tuner, such as <c>{"Resource":"tuner0","VctNumber":"1",
    /// "TargetIP":"192.168.1.249",...}</c> for a tuner in use and <c>{"Resource":"tuner1"}</c> for a free one.
    /// </summary>
    /// <param name="json">The status.json.</param>
    /// <returns>The tuners in use, or <c>null</c> when it lists no tuners.</returns>
    internal static TunerUsage? Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            int total = 0, inUse = 0;
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("Resource", out var resource)
                    || resource.ValueKind != JsonValueKind.String
                    || !(resource.GetString() ?? string.Empty).StartsWith("tuner", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                total++;
                if (HasValue(entry, "VctNumber") || HasValue(entry, "TargetIP"))
                {
                    inUse++;
                }
            }

            return total == 0 ? null : new TunerUsage(total, inUse);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasValue(JsonElement entry, string name)
        => entry.TryGetProperty(name, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && !(value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));
}
