using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Helpers;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Removing films and shows from a profile's library, and putting them back (Finly). A removed item and everything
/// below it, such as the episodes of a series, no longer shows up for that profile in any app.
/// </summary>
[Route("")]
[Authorize]
public class HiddenItemsController : BaseJellyfinApiController
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IDtoService _dtoService;
    private readonly IProfileLevelStore _levels;

    /// <summary>
    /// Initializes a new instance of the <see cref="HiddenItemsController"/> class.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="dtoService">The dto service.</param>
    /// <param name="levels">The profile level store.</param>
    public HiddenItemsController(IUserManager userManager, ILibraryManager libraryManager, IDtoService dtoService, IProfileLevelStore levels)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _dtoService = dtoService;
        _levels = levels;
    }

    /// <summary>
    /// Gets the items removed from a profile's library.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <response code="200">The removed items.</response>
    /// <response code="403">Only the user and administrators can see this.</response>
    /// <response code="404">User not found.</response>
    /// <returns>The removed items.</returns>
    [HttpGet("Users/{userId}/HiddenItems")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<QueryResult<BaseItemDto>> GetHiddenItems([FromRoute, Required] Guid userId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        if (!RequestHelpers.AssertCanUpdateUser(User, user, false))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Only the user and administrators can see this.");
        }

        // The items as an administrator sees them, the user can't see them anymore
        var items = user.GetPreferenceValues<Guid>(PreferenceKind.HiddenItems)
            .Select(id => _libraryManager.GetItemById(id))
            .OfType<MediaBrowser.Controller.Entities.BaseItem>()
            .OrderBy(item => item.SortName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var dtos = _dtoService.GetBaseItemDtos(items, new DtoOptions { EnableImages = true, ImageTypeLimit = 1 });
        return new QueryResult<BaseItemDto>(dtos);
    }

    /// <summary>
    /// Removes an item from a profile's library. Anyone can remove things from their own profile, administrators
    /// from any profile.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <response code="204">Item removed.</response>
    /// <response code="403">Not allowed to change this profile.</response>
    /// <response code="404">User or item not found.</response>
    /// <returns>A <see cref="NoContentResult"/> on success.</returns>
    [HttpPost("Users/{userId}/HiddenItems/{itemId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> HideItem([FromRoute, Required] Guid userId, [FromRoute, Required] Guid itemId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null || _libraryManager.GetItemById(itemId) is null)
        {
            return NotFound();
        }

        if (!RequestHelpers.AssertCanUpdateUser(User, user, false))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Only the user and administrators can change this profile.");
        }

        await _userManager.SetItemHiddenAsync(userId, itemId, true).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Puts a removed item back in a profile's library. Administrators can do this for any profile, and people for
    /// their own when their profile level has no restrictions; otherwise a parent needs to do it.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <response code="204">Item put back.</response>
    /// <response code="403">Not allowed to put it back.</response>
    /// <response code="404">User not found.</response>
    /// <returns>A <see cref="NoContentResult"/> on success.</returns>
    [HttpDelete("Users/{userId}/HiddenItems/{itemId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> RestoreItem([FromRoute, Required] Guid userId, [FromRoute, Required] Guid itemId)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound();
        }

        var isAdministrator = User.IsInRole(UserRoles.Administrator);
        var levelId = user.GetProfileLevelId();
        var level = levelId.HasValue ? _levels.GetLevel(levelId.Value) : null;
        var isOwnUnrestrictedProfile = User.GetUserId().Equals(userId) && !(level?.IsRestricted ?? user.HasContentRestrictions());
        if (!isAdministrator && !isOwnUnrestrictedProfile)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "A parent needs to put this back.");
        }

        await _userManager.SetItemHiddenAsync(userId, itemId, false).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Removes an item from every profile on a level, such as all Child profiles. Anyone signed in can do this for a
    /// level with restrictions, it only ever hides things.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="levelId">The profile level id.</param>
    /// <response code="200">The number of profiles it was removed from.</response>
    /// <response code="403">Only administrators can do this for a level without restrictions.</response>
    /// <response code="404">Item or level not found.</response>
    /// <returns>The number of profiles changed.</returns>
    [HttpPost("Items/{itemId}/HideFromLevel/{levelId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<int>> HideFromLevel([FromRoute, Required] Guid itemId, [FromRoute, Required] Guid levelId)
    {
        var level = _levels.GetLevel(levelId);
        if (level is null || _libraryManager.GetItemById(itemId) is null)
        {
            return NotFound();
        }

        // Hiding things from adults is theirs to decide
        if (!level.IsRestricted && !User.IsInRole(UserRoles.Administrator))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Only administrators can do this for this level.");
        }

        var members = _userManager.GetUsers().Where(u => levelId.Equals(u.GetProfileLevelId())).ToList();
        foreach (var member in members)
        {
            await _userManager.SetItemHiddenAsync(member.Id, itemId, true).ConfigureAwait(false);
        }

        return members.Count;
    }
}
