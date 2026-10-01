using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.LiveTv;

namespace MediaBrowser.Controller.LiveTv;

/// <summary>
/// Keeps the latest tuner signal reading for each live TV channel (Finly), so clients can warn about weak channels and
/// show the signal while watching.
/// </summary>
public interface IChannelSignalService
{
    /// <summary>
    /// Gets the signal quality, in percent, under which a channel is weak.
    /// </summary>
    int WeakQualityThreshold { get; }

    /// <summary>
    /// Gets the latest reading of every channel that has one.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The readings, by channel item id.</returns>
    Task<IReadOnlyDictionary<Guid, ChannelSignal>> GetChannelSignalsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets a channel's latest reading, first asking the tuner for a fresh one, so the reading is live and current when a
    /// tuner is on the channel or its multiplex.
    /// </summary>
    /// <param name="channelId">The channel item id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The reading, or <c>null</c> when the channel isn't a tuner channel or has no reading.</returns>
    Task<ChannelSignal?> GetChannelSignalAsync(Guid channelId, CancellationToken cancellationToken);
}
