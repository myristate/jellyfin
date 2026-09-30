using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Emby.Server.Implementations.Session;

/// <summary>
/// Remembers for a short while which sessions closed their share of a live stream (Finly). A client stopping playback
/// often closes the stream twice, once with its play session id when its encoding is stopped and once with its session
/// id when playback stopped is reported. Only the first may close anything, or the second takes another viewer's share
/// of the stream and closes it under them.
/// </summary>
internal sealed class ClosedLiveStreamSessions
{
    /// <summary>
    /// How long a close is remembered.
    /// </summary>
    internal static readonly TimeSpan RememberFor = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, DateTime> _closed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Remembers that the sessions closed their share of the stream.
    /// </summary>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="ids">The session and play session ids.</param>
    public void Remember(string liveStreamId, params string?[] ids)
    {
        var now = DateTime.UtcNow;
        foreach (var id in ids)
        {
            if (!string.IsNullOrEmpty(id))
            {
                _closed[Key(liveStreamId, id)] = now;
            }
        }

        Prune(now);
    }

    /// <summary>
    /// Forgets the sessions' close, when they start using the stream again.
    /// </summary>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="ids">The session and play session ids.</param>
    public void Forget(string liveStreamId, params string?[] ids)
    {
        if (_closed.IsEmpty)
        {
            return;
        }

        foreach (var id in ids)
        {
            if (!string.IsNullOrEmpty(id))
            {
                _closed.TryRemove(Key(liveStreamId, id), out _);
            }
        }
    }

    /// <summary>
    /// Gets whether the session already closed its share of the stream a moment ago.
    /// </summary>
    /// <param name="liveStreamId">The live stream id.</param>
    /// <param name="id">The session or play session id.</param>
    /// <returns>Whether it did.</returns>
    public bool WasClosed(string liveStreamId, string? id)
        => !string.IsNullOrEmpty(id)
            && _closed.TryGetValue(Key(liveStreamId, id), out var closedAt)
            && DateTime.UtcNow - closedAt < RememberFor;

    private static string Key(string liveStreamId, string id) => liveStreamId + "|" + id;

    private void Prune(DateTime now)
    {
        foreach (var (key, closedAt) in _closed.ToArray())
        {
            if (now - closedAt >= RememberFor)
            {
                _closed.TryRemove(key, out _);
            }
        }
    }
}
