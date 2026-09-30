using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Server.Implementations.StorageHelpers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Users;

/// <summary>
/// Keeps the profile levels in profilelevels.json in the configuration folder (Finly). The first time, it starts with
/// Child (up to PG), Teen (up to 15) and Adult (no limits). A file that can't be read is set aside as
/// profilelevels.json.bad-&lt;time&gt; and the server runs without levels, rather than writing the defaults over it.
/// </summary>
public sealed class ProfileLevelStore : IProfileLevelStore
{
    private readonly JsonListFile<ProfileLevel> _file;
    private readonly object _lock = new();
    private List<ProfileLevel>? _levels;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileLevelStore"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public ProfileLevelStore(IApplicationPaths appPaths, ILogger<ProfileLevelStore> logger)
    {
        _file = new JsonListFile<ProfileLevel>(Path.Combine(appPaths.ConfigurationDirectoryPath, "profilelevels.json"), logger);
    }

    /// <inheritdoc />
    public IReadOnlyList<ProfileLevel> GetLevels()
    {
        lock (_lock)
        {
            return Load().ToList();
        }
    }

    /// <inheritdoc />
    public ProfileLevel? GetLevel(Guid id)
    {
        lock (_lock)
        {
            return Load().FirstOrDefault(l => l.Id.Equals(id));
        }
    }

    /// <inheritdoc />
    public ProfileLevel Save(ProfileLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (string.IsNullOrWhiteSpace(level.Name))
        {
            throw new ArgumentException("A level needs a name.", nameof(level));
        }

        lock (_lock)
        {
            var levels = Load();
            level.Name = level.Name.Trim();
            level.AllowedTags = Clean(level.AllowedTags);
            level.BlockedTags = Clean(level.BlockedTags);
            level.BlockUnratedItems ??= [];

            if (levels.Any(l => !l.Id.Equals(level.Id) && string.Equals(l.Name, level.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"There is already a level called {level.Name}.", nameof(level));
            }

            var index = levels.FindIndex(l => l.Id.Equals(level.Id));
            if (level.Id.Equals(Guid.Empty) || index < 0)
            {
                level.Id = level.Id.Equals(Guid.Empty) ? Guid.NewGuid() : level.Id;
                levels.Add(level);
            }
            else
            {
                levels[index] = level;
            }

            Write(levels);
            return level;
        }
    }

    /// <inheritdoc />
    public bool Delete(Guid id)
    {
        lock (_lock)
        {
            var levels = Load();
            if (levels.RemoveAll(l => l.Id.Equals(id)) == 0)
            {
                return false;
            }

            Write(levels);
            return true;
        }
    }

    private static string[] Clean(string[]? tags)
        => (tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private List<ProfileLevel> Load()
    {
        if (_levels is not null)
        {
            return _levels;
        }

        // Only a missing file, the first time, gets the default levels
        _levels = _file.Read();
        if (_levels is not null)
        {
            return _levels;
        }

        _levels =
        [
            new ProfileLevel { Id = Guid.NewGuid(), Name = "Child", MaxParentalRating = 8 },
            new ProfileLevel { Id = Guid.NewGuid(), Name = "Teen", MaxParentalRating = 15 },
            new ProfileLevel { Id = Guid.NewGuid(), Name = "Adult" },
        ];
        Write(_levels);
        return _levels;
    }

    private void Write(List<ProfileLevel> levels)
    {
        _file.Write(levels);
        _levels = levels;
    }
}
