using System;
using System.Text.Json.Serialization;

namespace MediaBrowser.Model.LiveTv;

/// <summary>
/// The latest signal reading for a tuner channel (Finly), from the tuner while it was on the channel or its multiplex,
/// or from the tuner's last channel scan.
/// </summary>
public class ChannelSignal
{
    /// <summary>
    /// Gets or sets the signal strength, in percent, or <c>null</c> when it isn't known.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? Strength { get; set; }

    /// <summary>
    /// Gets or sets the signal quality, in percent, or <c>null</c> when it isn't known.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? Quality { get; set; }

    /// <summary>
    /// Gets or sets the symbol quality, in percent, or <c>null</c> when it isn't known. Only live readings have it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? SymbolQuality { get; set; }

    /// <summary>
    /// Gets or sets when the reading was taken, in UTC.
    /// </summary>
    public DateTime MeasuredAt { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the reading came from a tuner on the channel or its multiplex, rather than
    /// from the tuner's last channel scan.
    /// </summary>
    public bool IsLive { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the signal is weak enough that the picture may break up: quality under
    /// the weak quality threshold, or, when quality isn't known, strength under 40%.
    /// </summary>
    public bool IsWeak { get; set; }
}
