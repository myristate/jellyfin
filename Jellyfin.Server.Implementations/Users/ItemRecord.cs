using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Server.Implementations.Users;

/// <summary>
/// What is remembered about an item on a profile's removed or allowed list (Finly), kept in the
/// <see cref="PreferenceKind.ItemProviderIds"/> preference.
/// </summary>
/// <param name="ItemId">The item id on the list.</param>
/// <param name="Identity">The item's kind and provider ids, when known.</param>
/// <param name="MissingSince">When the item was first found missing from the library, <c>null</c> while it is there.</param>
public sealed record ItemRecord(Guid ItemId, ItemIdentity? Identity, DateTime? MissingSince)
{
    /// <summary>
    /// Reads a user's item records.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The records, by item id.</returns>
    public static Dictionary<Guid, ItemRecord> Read(User user)
    {
        var records = new Dictionary<Guid, ItemRecord>();
        foreach (var value in user.GetPreference(PreferenceKind.ItemProviderIds))
        {
            var record = Parse(value);
            if (record is not null)
            {
                records[record.ItemId] = record;
            }
        }

        return records;
    }

    /// <summary>
    /// Writes a user's item records, keeping only the ones for items still on their removed or allowed list.
    /// </summary>
    /// <param name="user">The user, with the lists already updated.</param>
    /// <param name="records">The records.</param>
    public static void Write(User user, IEnumerable<ItemRecord> records)
    {
        var listed = user.GetPreferenceValues<Guid>(PreferenceKind.HiddenItems)
            .Concat(user.GetPreferenceValues<Guid>(PreferenceKind.AllowedItems))
            .ToHashSet();
        var values = records
            .Where(r => listed.Contains(r.ItemId))
            .DistinctBy(r => r.ItemId)
            .Select(Format)
            .ToArray();

        if (values.Length == 0 && user.GetPreference(PreferenceKind.ItemProviderIds).Length == 0)
        {
            return;
        }

        user.SetPreference(PreferenceKind.ItemProviderIds, values);
    }

    /// <summary>
    /// Remembers what identifies an item that was put on a user's removed or allowed list.
    /// </summary>
    /// <param name="user">The user, with the lists already updated.</param>
    /// <param name="itemId">The item id.</param>
    /// <param name="identity">What identifies the item, <c>null</c> when not known.</param>
    public static void Remember(User user, Guid itemId, ItemIdentity? identity)
    {
        var records = Read(user);
        if (identity is not null)
        {
            records[itemId] = new ItemRecord(itemId, identity, null);
        }

        Write(user, records.Values);
    }

    /// <summary>
    /// Formats a record: the item id, kind, missing since (UTC ticks) and each provider id, separated by semicolons.
    /// </summary>
    /// <param name="record">The record.</param>
    /// <returns>The preference value.</returns>
    public static string Format(ItemRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var parts = new List<string>
        {
            record.ItemId.ToString("N", CultureInfo.InvariantCulture),
            record.Identity?.Kind.ToString() ?? string.Empty,
            record.MissingSince?.Ticks.ToString(CultureInfo.InvariantCulture) ?? string.Empty
        };

        if (record.Identity is not null)
        {
            parts.AddRange(record.Identity.ProviderIds
                .Where(p => ItemIdentity.IsStorable(p.Key) && ItemIdentity.IsStorable(p.Value))
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + "=" + p.Value));
        }

        return string.Join(';', parts);
    }

    /// <summary>
    /// Parses a record written by <see cref="Format"/>.
    /// </summary>
    /// <param name="value">The preference value.</param>
    /// <returns>The record, or <c>null</c> when it can't be read.</returns>
    public static ItemRecord? Parse(string value)
    {
        var parts = value.Split(';');
        if (parts.Length < 3 || !Guid.TryParse(parts[0], out var itemId))
        {
            return null;
        }

        ItemIdentity? identity = null;
        if (Enum.TryParse<BaseItemKind>(parts[1], out var kind))
        {
            var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in parts.Skip(3))
            {
                var separator = part.IndexOf('=', StringComparison.Ordinal);
                if (separator > 0 && separator < part.Length - 1)
                {
                    ids[part[..separator]] = part[(separator + 1)..];
                }
            }

            identity = new ItemIdentity(kind, ids);
        }

        DateTime? missingSince = long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && ticks <= DateTime.MaxValue.Ticks
            ? new DateTime(ticks, DateTimeKind.Utc)
            : null;

        return new ItemRecord(itemId, identity, missingSince);
    }
}
