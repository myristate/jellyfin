using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Data;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Profile levels such as Child, Teen and Adult, each with its own restrictions (Finly).
/// </summary>
[Route("ProfileLevels")]
[Authorize]
public class ProfileLevelsController : BaseJellyfinApiController
{
    private readonly IProfileLevelStore _levels;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileLevelsController"/> class.
    /// </summary>
    /// <param name="levels">The profile level store.</param>
    /// <param name="userManager">The user manager.</param>
    public ProfileLevelsController(IProfileLevelStore levels, IUserManager userManager)
    {
        _levels = levels;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets the profile levels.
    /// </summary>
    /// <response code="200">The levels.</response>
    /// <returns>The levels.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ProfileLevel>> GetProfileLevels() => Ok(_levels.GetLevels());

    /// <summary>
    /// Adds a profile level, or updates one. The profiles on the level take its restrictions.
    /// </summary>
    /// <param name="level">The level, with an empty id to add it.</param>
    /// <response code="200">The level as saved.</response>
    /// <response code="400">The level isn't valid.</response>
    /// <returns>The level.</returns>
    [HttpPost]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProfileLevel>> SaveProfileLevel([FromBody, Required] ProfileLevel level)
    {
        var saved = _levels.Save(level);

        foreach (var user in _userManager.GetUsers().Where(u => saved.Id.Equals(u.GetProfileLevelId())).ToList())
        {
            var policy = _userManager.GetUserDto(user).Policy!;
            saved.ApplyTo(policy);
            await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
        }

        return saved;
    }

    /// <summary>
    /// Deletes a profile level. Its profiles keep the restrictions they have, without a level.
    /// </summary>
    /// <param name="id">The level id.</param>
    /// <response code="204">Level deleted.</response>
    /// <response code="404">Level not found.</response>
    /// <returns>A <see cref="NoContentResult"/> on success.</returns>
    [HttpDelete("{id}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteProfileLevel([FromRoute, Required] Guid id)
    {
        if (!_levels.Delete(id))
        {
            return NotFound();
        }

        foreach (var user in _userManager.GetUsers().Where(u => id.Equals(u.GetProfileLevelId())).ToList())
        {
            var policy = _userManager.GetUserDto(user).Policy!;
            policy.ProfileLevelId = Guid.Empty;
            await _userManager.UpdatePolicyAsync(user.Id, policy).ConfigureAwait(false);
        }

        return NoContent();
    }
}
