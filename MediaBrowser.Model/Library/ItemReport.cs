using System;

namespace MediaBrowser.Model.Library;

/// <summary>
/// What is wrong with a reported film or episode (Finly).
/// </summary>
public enum ItemProblem
{
    /// <summary>
    /// The picture is corrupted, freezes or is missing.
    /// </summary>
    Video,

    /// <summary>
    /// The sound is corrupted, out of sync or missing.
    /// </summary>
    Audio,

    /// <summary>
    /// It plays in the wrong language.
    /// </summary>
    WrongLanguage,

    /// <summary>
    /// The subtitles are missing, wrong or out of sync.
    /// </summary>
    Subtitles,

    /// <summary>
    /// Something else, described in the note.
    /// </summary>
    Other
}

/// <summary>
/// A problem someone reported with a film or episode, for an administrator to look at (Finly).
/// </summary>
public class ItemReport
{
    /// <summary>
    /// Gets or sets the report id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the reported item's id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the item's name when it was reported, episodes with their series.
    /// </summary>
    public string ItemName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets who reported it.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the name of who reported it.
    /// </summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what is wrong.
    /// </summary>
    public ItemProblem Problem { get; set; }

    /// <summary>
    /// Gets or sets anything else they said.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// Gets or sets when it was reported.
    /// </summary>
    public DateTime DateCreated { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether an administrator has dealt with it.
    /// </summary>
    public bool IsResolved { get; set; }

    /// <summary>
    /// Gets or sets when it was dealt with.
    /// </summary>
    public DateTime? DateResolved { get; set; }
}
