namespace Jellyfin.Database.Implementations.Enums;

/// <summary>
/// The types of user preferences.
/// </summary>
public enum PreferenceKind
{
    /// <summary>
    /// A list of blocked tags.
    /// </summary>
    BlockedTags = 0,

    /// <summary>
    /// A list of blocked channels.
    /// </summary>
    BlockedChannels = 1,

    /// <summary>
    /// A list of blocked media folders.
    /// </summary>
    BlockedMediaFolders = 2,

    /// <summary>
    /// A list of enabled devices.
    /// </summary>
    EnabledDevices = 3,

    /// <summary>
    /// A list of enabled channels.
    /// </summary>
    EnabledChannels = 4,

    /// <summary>
    /// A list of enabled folders.
    /// </summary>
    EnabledFolders = 5,

    /// <summary>
    /// A list of folders to allow content deletion from.
    /// </summary>
    EnableContentDeletionFromFolders = 6,

    /// <summary>
    /// A list of latest items to exclude.
    /// </summary>
    LatestItemExcludes = 7,

    /// <summary>
    /// A list of media to exclude.
    /// </summary>
    MyMediaExcludes = 8,

    /// <summary>
    /// A list of grouped folders.
    /// </summary>
    GroupedFolders = 9,

    /// <summary>
    /// A list of unrated items to block.
    /// </summary>
    BlockUnratedItems = 10,

    /// <summary>
    /// A list of ordered views.
    /// </summary>
    OrderedViews = 11,

    /// <summary>
    /// A list of allowed tags.
    /// </summary>
    AllowedTags = 12,

    /// <summary>
    /// The hash of the user's sign in PIN (Finly). Numbered well clear of upstream's kinds so a future upstream kind
    /// can't collide with it.
    /// </summary>
    SignInPinHash = 1000,

    /// <summary>
    /// Items the user removed from their own library, or a parent removed for them (Finly). Hides the item and
    /// everything below it, such as the episodes of a series.
    /// </summary>
    HiddenItems = 1001,

    /// <summary>
    /// The id of the profile level the user is on, such as Child or Adult (Finly).
    /// </summary>
    ProfileLevel = 1002,

    /// <summary>
    /// Items a parent let this user see although their rating or tags would hide them (Finly). Everything below them,
    /// such as the episodes of a series, is let through too. Removed items stay removed.
    /// </summary>
    AllowedItems = 1003,

    /// <summary>
    /// When the user's recent wrong sign in PINs were entered, as UTC ticks (Finly). Kept so restarting the server
    /// doesn't reset the PIN throttle.
    /// </summary>
    SignInPinFailures = 1004,

    /// <summary>
    /// Set, to the UTC ticks it happened, when too many wrong PINs turned PIN sign in off for the user (Finly). Signing
    /// in with the password, or an administrator setting or removing the PIN, turns it back on.
    /// </summary>
    SignInPinDisabled = 1005,

    /// <summary>
    /// The provider ids (IMDb, TMDB, TVDB) of the items in <see cref="HiddenItems"/> and <see cref="AllowedItems"/>
    /// (Finly), so they can be found again when a file is moved or renamed and the library gives the item a new id.
    /// </summary>
    ItemProviderIds = 1006
}
