namespace Jellyfin.Api.Models.UserDtos;

/// <summary>
/// The update user PIN request body (Finly).
/// </summary>
public class UpdateUserPin
{
    /// <summary>
    /// Gets or sets the user's current password or PIN, needed when changing your own PIN.
    /// </summary>
    public string? CurrentPw { get; set; }

    /// <summary>
    /// Gets or sets the new PIN, 4 digits.
    /// </summary>
    public string? NewPin { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to remove the PIN.
    /// </summary>
    public bool ResetPin { get; set; }
}
