using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts.HdHomerun;

/// <summary>
/// Reads an HDHomeRun's status.json (Finly): which tuners are in use and, for each tuned tuner, its channel, frequency
/// and signal. It sees every user of the tuner, including another server sharing it, so a busy tuner is known before
/// asking it for a stream. One instance is shared, so every reader shares its short cache, and every read is handed on
/// to the channel signal store.
/// </summary>
public sealed class HdHomerunTunerStatus
{
    /// <summary>
    /// How long a status is reused for the free-tuner check. Short, so a tuner freed a moment ago is seen.
    /// </summary>
    internal static readonly TimeSpan CacheTime = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, (DateTime Time, IReadOnlyList<HdHomerunTunerState>? Tuners)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

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
    /// Gets or sets what is told of each status read from a tuner: its address and its tuners.
    /// </summary>
    internal Action<string, IReadOnlyList<HdHomerunTunerState>>? StatusRead { get; set; }

    /// <summary>
    /// Gets how many of the tuner's tuners are in use.
    /// </summary>
    /// <param name="baseUrl">The tuner's address, such as http://192.168.1.19.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tuners in use, or <c>null</c> when the status can't be read.</returns>
    internal async Task<TunerUsage?> GetUsage(string baseUrl, CancellationToken cancellationToken)
        => ToUsage(await GetTuners(baseUrl, CacheTime, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Gets the state of the tuner's tuners, reading status.json unless it was read within <paramref name="maxAge"/>.
    /// One read at a time is made of each tuner; a caller waiting on another's read uses its result.
    /// </summary>
    /// <param name="baseUrl">The tuner's address, such as http://192.168.1.19.</param>
    /// <param name="maxAge">How old a status may be reused.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tuners, or <c>null</c> when the status can't be read.</returns>
    internal async Task<IReadOnlyList<HdHomerunTunerState>?> GetTuners(string baseUrl, TimeSpan maxAge, CancellationToken cancellationToken)
    {
        if (TryGetCached(baseUrl, maxAge, out var cached))
        {
            return cached;
        }

        var gate = _gates.GetOrAdd(baseUrl, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetCached(baseUrl, maxAge, out cached))
            {
                return cached;
            }

            var now = DateTime.UtcNow;
            IReadOnlyList<HdHomerunTunerState>? tuners = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(RequestTimeout);
                var json = await _httpClientFactory.CreateClient(NamedClient.Default)
                    .GetStringAsync(baseUrl.TrimEnd('/') + "/status.json", timeout.Token)
                    .ConfigureAwait(false);
                tuners = HdHomerunTunerState.ParseStatus(json);
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

            _cache[baseUrl] = (now, tuners);
            if (tuners is not null)
            {
                OnStatusRead(baseUrl, tuners);
            }

            return tuners;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Reads how many tuners are in use from status.json, a list with an entry per tuner, such as
    /// <c>{"Resource":"tuner0","VctNumber":"1","TargetIP":"192.168.1.249",...}</c> for a tuner in use and
    /// <c>{"Resource":"tuner1"}</c> for a free one.
    /// </summary>
    /// <param name="json">The status.json.</param>
    /// <returns>The tuners in use, or <c>null</c> when it lists no tuners.</returns>
    internal static TunerUsage? Parse(string json) => ToUsage(HdHomerunTunerState.ParseStatus(json));

    private static TunerUsage? ToUsage(IReadOnlyList<HdHomerunTunerState>? tuners)
        => tuners is null || tuners.Count == 0 ? null : new TunerUsage(tuners.Count, tuners.Count(t => t.IsInUse));

    private bool TryGetCached(string baseUrl, TimeSpan maxAge, out IReadOnlyList<HdHomerunTunerState>? tuners)
    {
        if (_cache.TryGetValue(baseUrl, out var cached) && DateTime.UtcNow - cached.Time < maxAge)
        {
            tuners = cached.Tuners;
            return true;
        }

        tuners = null;
        return false;
    }

    private void OnStatusRead(string baseUrl, IReadOnlyList<HdHomerunTunerState> tuners)
    {
        try
        {
            StatusRead?.Invoke(baseUrl, tuners);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Couldn't record the tuner signal from {Url}", baseUrl);
        }
    }
}
