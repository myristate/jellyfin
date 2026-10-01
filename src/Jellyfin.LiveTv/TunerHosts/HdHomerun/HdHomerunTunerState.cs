using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Jellyfin.LiveTv.TunerHosts.HdHomerun;

/// <summary>
/// One tuner's entry in an HDHomeRun's status.json (Finly), such as <c>{"Resource":"tuner0","VctNumber":"4",
/// "VctName":"Channel 4","Frequency":650000000,"SignalStrengthPercent":83,"SignalQualityPercent":100,
/// "SymbolQualityPercent":100,"TargetIP":"192.168.1.249"}</c> for a tuned tuner, or <c>{"Resource":"tuner1"}</c> for a
/// free one.
/// </summary>
/// <param name="Resource">The tuner, such as tuner0.</param>
/// <param name="ChannelNumber">The channel it's on (VctNumber), if any.</param>
/// <param name="ChannelName">The name of the channel it's on (VctName), if any.</param>
/// <param name="Frequency">The frequency it's tuned to, in Hz, if any.</param>
/// <param name="SignalStrength">The signal strength, in percent, if reported.</param>
/// <param name="SignalQuality">The signal quality, in percent, if reported.</param>
/// <param name="SymbolQuality">The symbol quality, in percent, if reported.</param>
/// <param name="TargetIp">The address it streams to, if any.</param>
internal sealed record HdHomerunTunerState(
    string Resource,
    string? ChannelNumber,
    string? ChannelName,
    long? Frequency,
    int? SignalStrength,
    int? SignalQuality,
    int? SymbolQuality,
    string? TargetIp)
{
    /// <summary>
    /// Gets a value indicating whether someone is using the tuner: it's on a channel or streaming somewhere.
    /// </summary>
    public bool IsInUse => !string.IsNullOrWhiteSpace(ChannelNumber) || !string.IsNullOrWhiteSpace(TargetIp);

    /// <summary>
    /// Gets a value indicating whether the tuner reports a signal reading.
    /// </summary>
    public bool HasSignal => SignalStrength.HasValue || SignalQuality.HasValue;

    /// <summary>
    /// Reads status.json.
    /// </summary>
    /// <param name="json">The status.json.</param>
    /// <returns>The tuners, or <c>null</c> when it can't be read or lists no tuners.</returns>
    internal static IReadOnlyList<HdHomerunTunerState>? ParseStatus(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var tuners = new List<HdHomerunTunerState>();
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var resource = GetString(entry, "Resource");
                if (resource is null || !resource.StartsWith("tuner", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                tuners.Add(new HdHomerunTunerState(
                    resource,
                    GetString(entry, "VctNumber"),
                    GetString(entry, "VctName"),
                    GetLong(entry, "Frequency") is > 0 and var frequency ? frequency : null,
                    GetPercent(entry, "SignalStrengthPercent"),
                    GetPercent(entry, "SignalQualityPercent"),
                    GetPercent(entry, "SymbolQualityPercent"),
                    GetString(entry, "TargetIP")));
            }

            return tuners.Count == 0 ? null : tuners;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets a string property, or <c>null</c> when it's missing, blank or not a string or number.
    /// </summary>
    /// <param name="entry">The object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The trimmed value.</returns>
    internal static string? GetString(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var value))
        {
            return null;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };

        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>
    /// Gets a whole number property, which may be written as a number or a string.
    /// </summary>
    /// <param name="entry">The object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The value, or <c>null</c> when it's missing or not a whole number.</returns>
    internal static long? GetLong(JsonElement entry, string name)
    {
        if (!entry.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt64(out var number) ? number : null;
        }

        return value.ValueKind == JsonValueKind.String
            && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    /// <summary>
    /// Gets a percentage property.
    /// </summary>
    /// <param name="entry">The object.</param>
    /// <param name="name">The property name.</param>
    /// <returns>The value, or <c>null</c> when it's missing or not between 0 and 100.</returns>
    internal static int? GetPercent(JsonElement entry, string name)
        => GetLong(entry, name) is >= 0 and <= 100 and var percent ? (int)percent : null;
}
