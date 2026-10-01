using System.Collections.Generic;
using MediaBrowser.Model.LiveTv;

namespace Jellyfin.Api.Models.LiveTvDtos;

/// <summary>
/// The latest signal reading of every tuner channel that has one (Finly).
/// </summary>
public class ChannelSignalsDto
{
    /// <summary>
    /// Gets the signal quality, in percent, under which a channel is weak.
    /// </summary>
    public required int WeakQualityThreshold { get; init; }

    /// <summary>
    /// Gets the readings, by channel id.
    /// </summary>
    public required IReadOnlyDictionary<string, ChannelSignal> Channels { get; init; }
}
