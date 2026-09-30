using System;
using System.Collections.Generic;
using MediaBrowser.Model.Users;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Keeps the profile levels administrators define (Finly).
/// </summary>
public interface IProfileLevelStore
{
    /// <summary>
    /// Gets all levels, in the order they were created.
    /// </summary>
    /// <returns>The levels.</returns>
    IReadOnlyList<ProfileLevel> GetLevels();

    /// <summary>
    /// Gets a level.
    /// </summary>
    /// <param name="id">The level id.</param>
    /// <returns>The level, or <c>null</c> when there is none with that id.</returns>
    ProfileLevel? GetLevel(Guid id);

    /// <summary>
    /// Adds a level, when its id is empty, or updates it.
    /// </summary>
    /// <param name="level">The level.</param>
    /// <returns>The level as saved, with its id.</returns>
    ProfileLevel Save(ProfileLevel level);

    /// <summary>
    /// Deletes a level.
    /// </summary>
    /// <param name="id">The level id.</param>
    /// <returns><c>true</c> when the level existed.</returns>
    bool Delete(Guid id);
}
