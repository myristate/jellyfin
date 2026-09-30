using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Playlists;
using Moq;
using Xunit;

namespace Jellyfin.Controller.Tests.Entities;

/// <summary>
/// Covers the in-memory side of a profile's removed and allowed items (Finly), which has to agree with the database
/// queries: what an item belongs to, and collections and playlists with nothing else left in them.
/// </summary>
[Collection("LibraryManagerTests")]
public sealed class HiddenItemsVisibilityTests : IDisposable
{
    private readonly ILibraryManager _previousLibraryManager;
    private readonly Dictionary<Guid, BaseItem> _library = [];

    public HiddenItemsVisibilityTests()
    {
        _previousLibraryManager = BaseItem.LibraryManager;
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns<Guid>(id => _library.GetValueOrDefault(id));
        libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.ItemIds.Select(id => _library.GetValueOrDefault(id)).OfType<BaseItem>().ToList());
        libraryManager.Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>())).Returns([]);
        BaseItem.LibraryManager = libraryManager.Object;
    }

    public void Dispose() => BaseItem.LibraryManager = _previousLibraryManager;

    [Fact]
    public void HiddenSeries_HidesItsEpisodes()
    {
        var (series, _, episode) = AddShow();
        var (_, _, otherEpisode) = AddShow();
        var user = CreateUser(hidden: [series.Id]);

        Assert.False(episode.IsParentalAllowed(user, false));
        Assert.True(otherEpisode.IsParentalAllowed(user, false));
    }

    [Fact]
    public void HiddenChannel_HidesItsProgrammes()
    {
        var channel = Add(new LiveTvChannel { Id = Guid.NewGuid(), Name = "CBeebies" });
        var programme = Add(new LiveTvProgram { Id = Guid.NewGuid(), Name = "Bluey", ChannelId = channel.Id });
        var other = Add(new LiveTvProgram { Id = Guid.NewGuid(), Name = "News", ChannelId = Guid.NewGuid() });
        var user = CreateUser(hidden: [channel.Id]);

        Assert.True(programme.IsHiddenBy(user));
        Assert.False(programme.IsParentalAllowed(user, false));
        Assert.True(other.IsParentalAllowed(user, false));
    }

    [Fact]
    public void AllowedChannel_LetsItsProgrammesThrough()
    {
        var channel = Add(new LiveTvChannel { Id = Guid.NewGuid(), Name = "CBeebies" });
        var programme = Add(new LiveTvProgram { Id = Guid.NewGuid(), Name = "Bluey", ChannelId = channel.Id });
        var other = Add(new LiveTvProgram { Id = Guid.NewGuid(), Name = "News", ChannelId = Guid.NewGuid() });
        var user = CreateUser(allowed: [channel.Id]);
        user.SetPreference(PreferenceKind.AllowedTags, ["kids"]);

        Assert.True(programme.IsParentalAllowed(user, false));
        Assert.False(other.IsParentalAllowed(user, false));
    }

    [Fact]
    public void HiddenFilm_HidesItsAlternateVersions()
    {
        var film = Add(new Movie { Id = Guid.NewGuid(), Name = "Film" });
        var version = Add(new Movie { Id = Guid.NewGuid(), Name = "Film 4K", PrimaryVersionId = film.Id });
        var user = CreateUser(hidden: [film.Id]);

        Assert.False(version.IsParentalAllowed(user, false));
    }

    [Fact]
    public void AllowedFilm_LetsItsAlternateVersionsThrough()
    {
        var film = Add(new Movie { Id = Guid.NewGuid(), Name = "Film" });
        var version = Add(new Movie { Id = Guid.NewGuid(), Name = "Film 4K", PrimaryVersionId = film.Id });
        var user = CreateUser(allowed: [film.Id]);
        user.SetPreference(PreferenceKind.AllowedTags, ["kids"]);

        Assert.True(version.IsParentalAllowed(user, false));
    }

    [Fact]
    public void HiddenWins_OverAllowed()
    {
        var (series, _, episode) = AddShow();
        var user = CreateUser(hidden: [series.Id], allowed: [series.Id]);

        Assert.False(episode.IsParentalAllowed(user, false));
    }

    [Fact]
    public void Collection_WithEveryFilmHidden_IsHidden()
    {
        var film = Add(new Movie { Id = Guid.NewGuid(), Name = "Film" });
        var other = Add(new Movie { Id = Guid.NewGuid(), Name = "Other" });
        var collection = Add(new BoxSet { Id = Guid.NewGuid(), Name = "Collection", LibraryFolderIds = [], LinkedChildren = [LinkedChild.Create(film), LinkedChild.Create(other)] });

        Assert.False(collection.IsVisible(CreateUser(hidden: [film.Id, other.Id])));
        Assert.True(collection.IsVisible(CreateUser(hidden: [film.Id])));
    }

    [Fact]
    public void HiddenCollection_StaysHidden()
    {
        var film = Add(new Movie { Id = Guid.NewGuid(), Name = "Film" });
        var collection = Add(new BoxSet { Id = Guid.NewGuid(), Name = "Collection", LibraryFolderIds = [], LinkedChildren = [LinkedChild.Create(film)] });

        Assert.False(collection.IsVisible(CreateUser(hidden: [collection.Id])));
    }

    [Fact]
    public void Playlist_WithEveryItemHidden_IsHidden()
    {
        var (series, _, episode) = AddShow();
        var playlist = Add(new Playlist { Id = Guid.NewGuid(), Name = "Playlist", LinkedChildren = [LinkedChild.Create(episode)] });

        Assert.False(playlist.IsVisible(CreateUser(hidden: [series.Id])));
        Assert.True(playlist.IsVisible(CreateUser(hidden: [Guid.NewGuid()])));
    }

    [Fact]
    public void ItemIdSet_IsParsedOnceForEachValue()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var first = CreateUser(hidden: ids);
        var second = CreateUser(hidden: ids);

        var set = first.GetItemIdSet(PreferenceKind.HiddenItems);

        Assert.Same(set, first.GetItemIdSet(PreferenceKind.HiddenItems));
        Assert.Same(set, second.GetItemIdSet(PreferenceKind.HiddenItems));
        Assert.True(set.SetEquals(ids));

        // A changed list is parsed again
        first.SetPreference(PreferenceKind.HiddenItems, [ids[0]]);
        Assert.Single(first.GetItemIdSet(PreferenceKind.HiddenItems));
    }

    private static User CreateUser(Guid[]? hidden = null, Guid[]? allowed = null)
    {
        var user = new User("calum", "auth-provider", "reset-provider");
        if (hidden is not null)
        {
            user.SetPreference(PreferenceKind.HiddenItems, hidden);
        }

        if (allowed is not null)
        {
            user.SetPreference(PreferenceKind.AllowedItems, allowed);
        }

        return user;
    }

    private (Series Series, Season Season, Episode Episode) AddShow()
    {
        var series = Add(new Series { Id = Guid.NewGuid(), Name = "Show" });
        var season = Add(new Season { Id = Guid.NewGuid(), Name = "Season 1", ParentId = series.Id, SeriesId = series.Id });
        var episode = Add(new Episode { Id = Guid.NewGuid(), Name = "Episode 1", ParentId = season.Id, SeriesId = series.Id });
        return (series, season, episode);
    }

    private T Add<T>(T item)
        where T : BaseItem
    {
        _library[item.Id] = item;
        return item;
    }
}
