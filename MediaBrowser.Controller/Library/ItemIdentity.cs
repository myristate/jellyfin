using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// What identifies an item apart from its library id (Finly): its kind and its IMDb, TMDB and TVDB ids. Remembered for
/// the items removed from, or allowed for, a profile, so they can be found again when a file is moved or renamed and the
/// library gives the item a new id.
/// </summary>
/// <param name="Kind">The kind of item, such as Movie or Series.</param>
/// <param name="ProviderIds">The provider ids, by provider name.</param>
public sealed record ItemIdentity(BaseItemKind Kind, IReadOnlyDictionary<string, string> ProviderIds)
{
    /// <summary>
    /// The providers whose ids are remembered.
    /// </summary>
    public static readonly IReadOnlyList<string> Providers =
    [
        nameof(MetadataProvider.Imdb),
        nameof(MetadataProvider.Tmdb),
        nameof(MetadataProvider.Tvdb)
    ];

    /// <summary>
    /// Gets the identity of an item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The identity, or <c>null</c> without an item.</returns>
    public static ItemIdentity? FromItem(BaseItem? item)
    {
        if (item is null)
        {
            return null;
        }

        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in Providers)
        {
            if (item.ProviderIds.TryGetValue(provider, out var value) && IsStorable(value))
            {
                ids[provider] = value.Trim();
            }
        }

        return new ItemIdentity(item.GetBaseItemKind(), ids);
    }

    /// <summary>
    /// Checks whether a provider id can be stored in a preference list.
    /// </summary>
    /// <param name="value">The provider id.</param>
    /// <returns><c>true</c> when it has something in it and none of the list's separators.</returns>
    public static bool IsStorable(string? value)
        => !string.IsNullOrWhiteSpace(value) && !value.Any(c => c is ',' or ';' or '=' or '|');
}
