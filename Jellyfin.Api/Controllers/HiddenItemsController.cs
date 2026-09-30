using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Helpers;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
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
        var item = _libraryManager.GetItemById(itemId);
        if (user is null || item is null)
        {
            return NotFound();
        }

        if (!RequestHelpers.AssertCanUpdateUser(User, user, false))
        {
            return StatusCode(StatusCodes.Status403Forbidden, "Only the user and administrators can change this profile.");
        }

        await RemoveFromProfileAsync(userId, itemId, ItemIdentity.FromItem(item)).ConfigureAwait(false);
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
        var item = _libraryManager.GetItemById(itemId);
        if (level is null || item is null)
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
            await RemoveFromProfileAsync(member.Id, itemId, ItemIdentity.FromItem(item)).ConfigureAwait(false);
        }

        return members.Count;
    }

    /// <summary>
    /// Takes an item out of a profile: an item only there because it was allowed stops being allowed, and anything the
    /// profile would still see goes on its removed list.
    /// </summary>
    private async Task RemoveFromProfileAsync(Guid userId, Guid itemId, ItemIdentity? identity)
    {
        await _userManager.SetItemAllowedAsync(userId, itemId, false).ConfigureAwait(false);

        var user = _userManager.GetUserById(userId);
        var stillVisible = user is not null && _libraryManager.GetItemIds(new InternalItemsQuery(user)
        {
            ItemIds = [itemId],
            DtoOptions = new DtoOptions(false)
        }).Count > 0;
        if (stillVisible)
        {
            await _userManager.SetItemHiddenAsync(userId, itemId, true, identity).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Tells which restricted profiles can see each item, for showing parents what the children have (Finly).
    /// </summary>
    /// <param name="ids">The item ids, none to only list the restricted profiles.</param>
    /// <response code="200">For each item, the ids of the restricted profiles that can see it.</response>
    /// <returns>The profiles and what they can see.</returns>
    [HttpGet("Items/ProfileAccess")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ProfileAccessResult> GetProfileAccess([FromQuery, ModelBinder(typeof(Jellyfin.Api.ModelBinders.CommaDelimitedCollectionModelBinder))] Guid[] ids)
    {
        var result = new ProfileAccessResult();
        var profiles = _userManager.GetUsers().Where(u => u.HasContentRestrictions() || IsOnRestrictedLevel(u)).ToList();
        foreach (var id in ids)
        {
            result.Items[id] = [];
        }

        foreach (var profile in profiles)
        {
            result.Profiles.Add(new ProfileAccessProfile
            {
                Id = profile.Id,
                Name = profile.Username,
                LevelId = profile.GetProfileLevelId()
            });

            if (ids.Length == 0)
            {
                continue;
            }

            var visible = _libraryManager.GetItemIds(new InternalItemsQuery(profile)
            {
                ItemIds = ids,
                DtoOptions = new DtoOptions(false)
            });
            foreach (var id in visible)
            {
                if (result.Items.TryGetValue(id, out var list))
                {
                    list.Add(profile.Id);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Lets a profile see an item although its rating or tags would hide it, and puts it back if it was removed.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <response code="204">Item allowed.</response>
    /// <response code="404">User or item not found.</response>
    /// <returns>A <see cref="NoContentResult"/> on success.</returns>
    [HttpPost("Users/{userId}/AllowedItems/{itemId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> AllowItem([FromRoute, Required] Guid userId, [FromRoute, Required] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (_userManager.GetUserById(userId) is null || item is null)
        {
            return NotFound();
        }

        await _userManager.SetItemAllowedAsync(userId, itemId, true, ItemIdentity.FromItem(item)).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Takes back an allowance, the profile's rating and tags decide again.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="itemId">The item id.</param>
    /// <response code="204">Allowance taken back.</response>
    /// <response code="404">User not found.</response>
    /// <returns>A <see cref="NoContentResult"/> on success.</returns>
    [HttpDelete("Users/{userId}/AllowedItems/{itemId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DisallowItem([FromRoute, Required] Guid userId, [FromRoute, Required] Guid itemId)
    {
        if (_userManager.GetUserById(userId) is null)
        {
            return NotFound();
        }

        await _userManager.SetItemAllowedAsync(userId, itemId, false).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Adds an item to every profile on a level. A level limited to tags, such as Child with the kids tag, gets the
    /// tag added to the item, so profiles joining the level later have it too; other levels allow the item for each
    /// profile on them. Either way it is put back where it was removed.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="levelId">The profile level id.</param>
    /// <response code="200">The number of profiles on the level.</response>
    /// <response code="404">Item or level not found.</response>
    /// <returns>The number of profiles on the level.</returns>
    [HttpPost("Items/{itemId}/AllowForLevel/{levelId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<int>> AllowForLevel([FromRoute, Required] Guid itemId, [FromRoute, Required] Guid levelId)
    {
        var level = _levels.GetLevel(levelId);
        var item = _libraryManager.GetItemById(itemId);
        if (level is null || item is null)
        {
            return NotFound();
        }

        var tag = level.AllowedTags.FirstOrDefault();
        var tagged = tag is not null && !level.MaxParentalRating.HasValue && level.BlockUnratedItems.Length == 0;
        if (tagged && !item.Tags.Contains(tag!, StringComparer.OrdinalIgnoreCase))
        {
            item.AddTag(tag!);
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
        }

        var members = _userManager.GetUsers().Where(u => levelId.Equals(u.GetProfileLevelId())).ToList();
        foreach (var member in members)
        {
            if (tagged)
            {
                await _userManager.SetItemHiddenAsync(member.Id, itemId, false).ConfigureAwait(false);
            }
            else
            {
                await _userManager.SetItemAllowedAsync(member.Id, itemId, true, ItemIdentity.FromItem(item)).ConfigureAwait(false);
            }
        }

        return members.Count;
    }

    private bool IsOnRestrictedLevel(Jellyfin.Database.Implementations.Entities.User user)
    {
        var levelId = user.GetProfileLevelId();
        return levelId.HasValue && (_levels.GetLevel(levelId.Value)?.IsRestricted ?? false);
    }
}

/// <summary>
/// Which restricted profiles can see which items (Finly).
/// </summary>
public class ProfileAccessResult
{
    /// <summary>
    /// Gets the restricted profiles.
    /// </summary>
    public List<ProfileAccessProfile> Profiles { get; } = [];

    /// <summary>
    /// Gets, for each item asked about, the ids of the profiles that can see it.
    /// </summary>
    public Dictionary<Guid, List<Guid>> Items { get; } = [];
}

/// <summary>
/// A restricted profile.
/// </summary>
public class ProfileAccessProfile
{
    /// <summary>
    /// Gets or sets the user id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the user name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the profile level the user is on.
    /// </summary>
    public Guid? LevelId { get; set; }
}
