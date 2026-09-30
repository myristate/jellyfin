using System;
using System.Linq;
using Jellyfin.Data.Enums;

namespace MediaBrowser.Model.Users;

/// <summary>
/// A kind of profile an administrator defines, such as Child, Teen or Adult (Finly). The profiles on a level share its
/// restrictions, and can be managed together, for example removing a film from all Child profiles at once.
/// </summary>
public class ProfileLevel
{
    /// <summary>
    /// Gets or sets the id, empty for a new level.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the name shown to people, such as Child.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the highest rating score allowed, on the server's age based scale (PG is 8, 12A is 12, 15 is 15),
    /// or <c>null</c> for no limit. Ratings from any country map onto the same scale.
    /// </summary>
    public int? MaxParentalRating { get; set; }

    /// <summary>
    /// Gets or sets the highest sub score allowed at <see cref="MaxParentalRating"/>.
    /// </summary>
    public int? MaxParentalSubRating { get; set; }

    /// <summary>
    /// Gets or sets the kinds of unrated items that are blocked.
    /// </summary>
    public UnratedItem[] BlockUnratedItems { get; set; } = [];

    /// <summary>
    /// Gets or sets tags an item needs to be shown; when set only tagged items are shown.
    /// </summary>
    public string[] AllowedTags { get; set; } = [];

    /// <summary>
    /// Gets or sets tags that hide an item.
    /// </summary>
    public string[] BlockedTags { get; set; } = [];

    /// <summary>
    /// Gets a value indicating whether the level restricts anything. Only restricted levels are offered for removing
    /// an item from all their profiles.
    /// </summary>
    public bool IsRestricted => MaxParentalRating.HasValue
        || BlockUnratedItems.Length > 0
        || AllowedTags.Any(t => !string.IsNullOrWhiteSpace(t))
        || BlockedTags.Any(t => !string.IsNullOrWhiteSpace(t));

    /// <summary>
    /// Applies the level's restrictions to a user's policy.
    /// </summary>
    /// <param name="policy">The policy.</param>
    public void ApplyTo(UserPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.MaxParentalRating = MaxParentalRating;
        policy.MaxParentalSubRating = MaxParentalSubRating;
        policy.BlockUnratedItems = BlockUnratedItems;
        policy.AllowedTags = AllowedTags;
        policy.BlockedTags = BlockedTags;
    }
}
