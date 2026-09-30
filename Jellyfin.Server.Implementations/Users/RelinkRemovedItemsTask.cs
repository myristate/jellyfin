using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Users;

/// <summary>
/// Keeps the items removed from, or allowed for, each profile pointing at the right films and shows (Finly). Moving or
/// renaming a file gives the item a new id, so this finds the ids that are gone and swaps in the item with the same
/// IMDb, TMDB or TVDB id. Ids missing for longer than <see cref="GracePeriod"/> without such an item are dropped. Runs
/// daily and after each library scan.
/// </summary>
public class RelinkRemovedItemsTask : IScheduledTask, IConfigurableScheduledTask
{
    /// <summary>
    /// How long an item may be missing, for example on a drive that is offline, before it is dropped from the lists.
    /// </summary>
    public static readonly TimeSpan GracePeriod = TimeSpan.FromDays(30);

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<RelinkRemovedItemsTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="RelinkRemovedItemsTask"/> class.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public RelinkRemovedItemsTask(IUserManager userManager, ILibraryManager libraryManager, ILogger<RelinkRemovedItemsTask> logger)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Find removed items again";

    /// <inheritdoc />
    public string Description => "Keeps the films and shows removed from, or allowed for, each profile right when their files are moved or renamed.";

    /// <inheritdoc />
    public string Category => "Library";

    /// <inheritdoc />
    public string Key => "FinlyRelinkRemovedItems";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var users = _userManager.GetUsers()
            .Select(user => (User: user, Ids: user.GetPreferenceValues<Guid>(PreferenceKind.HiddenItems)
                .Concat(user.GetPreferenceValues<Guid>(PreferenceKind.AllowedItems))
                .Distinct()
                .ToList()))
            .Where(u => u.Ids.Count > 0)
            .ToList();
        if (users.Count == 0)
        {
            progress.Report(100);
            return;
        }

        // What identifies each listed item, from whichever profile remembered it
        var identities = new Dictionary<Guid, ItemIdentity>();
        foreach (var (user, _) in users)
        {
            foreach (var record in ItemRecord.Read(user).Values)
            {
                if (record.Identity is { ProviderIds.Count: > 0 } identity)
                {
                    identities.TryAdd(record.ItemId, identity);
                }
            }
        }

        var missing = new Dictionary<Guid, Guid?>();
        foreach (var itemId in users.SelectMany(u => u.Ids).Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_libraryManager.GetItemById(itemId) is null)
            {
                missing[itemId] = identities.TryGetValue(itemId, out var identity) ? FindReplacement(identity) : null;
            }
        }

        _logger.LogInformation("{Missing} removed or allowed items are no longer in the library, {Found} found again", missing.Count, missing.Values.Count(v => v.HasValue));

        for (var i = 0; i < users.Count; i++)
        {
            var (user, ids) = users[i];
            var userMissing = ids.Where(missing.ContainsKey).ToDictionary(id => id, id => missing[id]);
            await _userManager.RelinkItemsAsync(user.Id, userMissing, GracePeriod).ConfigureAwait(false);
            progress.Report(100.0 * (i + 1) / users.Count);
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            }
        ];
    }

    /// <summary>
    /// Finds the item that has the same kind and one of the same provider ids.
    /// </summary>
    /// <param name="identity">What identifies the missing item.</param>
    /// <returns>The id of the item found, or <c>null</c>.</returns>
    private Guid? FindReplacement(ItemIdentity identity)
    {
        var candidates = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [identity.Kind],
            HasAnyProviderId = identity.ProviderIds.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
            Recursive = true,
            IsVirtualItem = false,
            DtoOptions = new DtoOptions(false)
        });

        // The main version of a film, not an alternate version or an extra
        return candidates
            .Where(item => item.OwnerId.Equals(Guid.Empty))
            .OrderBy(item => item is Video { PrimaryVersionId: not null } ? 1 : 0)
            .Select(item => (Guid?)item.Id)
            .FirstOrDefault();
    }
}
