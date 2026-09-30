using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Users;

public sealed class RelinkRemovedItemsTaskTests : IDisposable
{
    private readonly UserManagerTestDatabase _database = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Dictionary<Guid, BaseItem> _library = [];
    private readonly RelinkRemovedItemsTask _task;

    public RelinkRemovedItemsTaskTests()
    {
        _libraryManager.Setup(l => l.GetItemById(It.IsAny<Guid>()))
            .Returns<Guid>(id => _library.GetValueOrDefault(id));
        _libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => _library.Values
                .Where(i => q.IncludeItemTypes.Contains(i.GetBaseItemKind())
                    && q.HasAnyProviderId!.Any(p => i.ProviderIds.TryGetValue(p.Key, out var value) && value == p.Value))
                .ToList());

        _task = new RelinkRemovedItemsTask(_database.UserManager, _libraryManager.Object, NullLogger<RelinkRemovedItemsTask>.Instance);
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task HidingAnItem_RemembersItsProviderIds_UntilPutBack()
    {
        var user = await _database.UserManager.CreateUserAsync("calum");
        var film = AddFilm("tt0001");

        await _database.UserManager.SetItemHiddenAsync(user.Id, film.Id, true, ItemIdentity.FromItem(film));

        var record = Assert.Single(ItemRecord.Read(_database.UserManager.GetUserById(user.Id)!).Values);
        Assert.Equal(film.Id, record.ItemId);
        Assert.Equal(BaseItemKind.Movie, record.Identity!.Kind);
        Assert.Equal("tt0001", record.Identity.ProviderIds["Imdb"]);

        await _database.UserManager.SetItemHiddenAsync(user.Id, film.Id, false);

        Assert.Empty(ItemRecord.Read(_database.UserManager.GetUserById(user.Id)!));
    }

    [Fact]
    public async Task MovedFilm_StaysRemoved_UnderItsNewId()
    {
        var user = await _database.UserManager.CreateUserAsync("calum");
        var film = AddFilm("tt0001");
        await _database.UserManager.SetItemHiddenAsync(user.Id, film.Id, true, ItemIdentity.FromItem(film));

        // The file moved: the library dropped the old item and found it again with a new id
        _library.Remove(film.Id);
        var moved = AddFilm("tt0001");

        await _task.ExecuteAsync(new Progress<double>(), TestContext.Current.CancellationToken);

        var stored = _database.UserManager.GetUserById(user.Id)!;
        Assert.Equal([moved.Id], stored.GetPreferenceValues<Guid>(PreferenceKind.HiddenItems));
        Assert.Equal(moved.Id, Assert.Single(ItemRecord.Read(stored).Values).ItemId);
    }

    [Fact]
    public async Task MovedFilm_StaysAllowed_UnderItsNewId()
    {
        var user = await _database.UserManager.CreateUserAsync("euan");
        var film = AddFilm("tt0002");
        await _database.UserManager.SetItemAllowedAsync(user.Id, film.Id, true, ItemIdentity.FromItem(film));

        _library.Remove(film.Id);
        var moved = AddFilm("tt0002");

        await _task.ExecuteAsync(new Progress<double>(), TestContext.Current.CancellationToken);

        Assert.Equal([moved.Id], _database.UserManager.GetUserById(user.Id)!.GetPreferenceValues<Guid>(PreferenceKind.AllowedItems));
    }

    [Fact]
    public async Task MissingFilm_IsKeptForAWhile_ThenDropped()
    {
        var user = await _database.UserManager.CreateUserAsync("calum");
        var film = AddFilm("tt0003");
        await _database.UserManager.SetItemHiddenAsync(user.Id, film.Id, true, ItemIdentity.FromItem(film));
        _library.Remove(film.Id);

        await _task.ExecuteAsync(new Progress<double>(), TestContext.Current.CancellationToken);

        // Maybe only on a drive that is offline
        var stored = _database.UserManager.GetUserById(user.Id)!;
        Assert.Equal([film.Id], stored.GetPreferenceValues<Guid>(PreferenceKind.HiddenItems));
        Assert.NotNull(Assert.Single(ItemRecord.Read(stored).Values).MissingSince);

        _database.Time.Advance(RelinkRemovedItemsTask.GracePeriod + TimeSpan.FromDays(1));
        await _task.ExecuteAsync(new Progress<double>(), TestContext.Current.CancellationToken);

        stored = _database.UserManager.GetUserById(user.Id)!;
        Assert.Empty(stored.GetPreferenceValues<Guid>(PreferenceKind.HiddenItems));
        Assert.Empty(ItemRecord.Read(stored));
    }

    [Fact]
    public async Task FilmThatComesBack_IsNoLongerCountedAsMissing()
    {
        var user = await _database.UserManager.CreateUserAsync("calum");
        var film = AddFilm("tt0004");
        await _database.UserManager.SetItemHiddenAsync(user.Id, film.Id, true, ItemIdentity.FromItem(film));
        _library.Remove(film.Id);
        await _task.ExecuteAsync(new Progress<double>(), TestContext.Current.CancellationToken);

        _library[film.Id] = film;
        await _task.ExecuteAsync(new Progress<double>(), TestContext.Current.CancellationToken);
        _database.Time.Advance(RelinkRemovedItemsTask.GracePeriod + TimeSpan.FromDays(1));
        _library.Remove(film.Id);
        await _task.ExecuteAsync(new Progress<double>(), TestContext.Current.CancellationToken);

        // Missing again only just now, so kept
        Assert.Equal([film.Id], _database.UserManager.GetUserById(user.Id)!.GetPreferenceValues<Guid>(PreferenceKind.HiddenItems));
    }

    [Fact]
    public async Task FilmStillThere_IsLeftAlone()
    {
        var user = await _database.UserManager.CreateUserAsync("calum");
        var film = AddFilm("tt0005");
        await _database.UserManager.SetItemHiddenAsync(user.Id, film.Id, true, ItemIdentity.FromItem(film));

        await _task.ExecuteAsync(new Progress<double>(), TestContext.Current.CancellationToken);

        Assert.Equal([film.Id], _database.UserManager.GetUserById(user.Id)!.GetPreferenceValues<Guid>(PreferenceKind.HiddenItems));
        _libraryManager.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef;Movie;;Imdb=tt1;Tmdb=2")]
    [InlineData("0123456789abcdef0123456789abcdef;Series;638000000000000000;Tvdb=3")]
    [InlineData("0123456789abcdef0123456789abcdef;;")]
    public void Records_RoundTrip(string value)
    {
        var record = ItemRecord.Parse(value);

        Assert.NotNull(record);
        Assert.Equal(value, ItemRecord.Format(record));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a guid;Movie;")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void Records_Unreadable_AreSkipped(string value)
    {
        Assert.Null(ItemRecord.Parse(value));
    }

    private Movie AddFilm(string imdbId)
    {
        var film = new Movie { Id = Guid.NewGuid(), Name = "Film " + imdbId };
        film.ProviderIds["Imdb"] = imdbId;
        _library[film.Id] = film;
        return film;
    }
}
