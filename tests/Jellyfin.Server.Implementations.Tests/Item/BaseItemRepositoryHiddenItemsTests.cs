using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Server.Implementations.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Item;

/// <summary>
/// Covers what a profile's removed and allowed items (Finly) reach in the database queries: everything below a series,
/// the programmes of a Live TV channel, the alternate versions of a film, and collections and playlists with nothing
/// else left in them.
/// </summary>
public sealed class BaseItemRepositoryHiddenItemsTests : SqliteDbTestFixture
{
    private const string FolderType = "MediaBrowser.Controller.Entities.Folder";
    private const string SeriesType = "MediaBrowser.Controller.Entities.TV.Series";
    private const string SeasonType = "MediaBrowser.Controller.Entities.TV.Season";
    private const string EpisodeType = "MediaBrowser.Controller.Entities.TV.Episode";
    private const string MovieType = "MediaBrowser.Controller.Entities.Movies.Movie";
    private const string BoxSetType = "MediaBrowser.Controller.Entities.Movies.BoxSet";
    private const string PlaylistType = "MediaBrowser.Controller.Playlists.Playlist";
    private const string ChannelType = "MediaBrowser.Controller.LiveTv.LiveTvChannel";
    private const string ProgramType = "MediaBrowser.Controller.LiveTv.LiveTvProgram";

    private readonly BaseItemRepository _repository;

    private readonly Guid _library = Guid.NewGuid();
    private readonly Guid _series = Guid.NewGuid();
    private readonly Guid _season = Guid.NewGuid();
    private readonly Guid _episode = Guid.NewGuid();
    private readonly Guid _otherSeries = Guid.NewGuid();
    private readonly Guid _otherEpisode = Guid.NewGuid();
    private readonly Guid _channel = Guid.NewGuid();
    private readonly Guid _programme = Guid.NewGuid();
    private readonly Guid _otherChannel = Guid.NewGuid();
    private readonly Guid _otherProgramme = Guid.NewGuid();
    private readonly Guid _film = Guid.NewGuid();
    private readonly Guid _filmVersion = Guid.NewGuid();
    private readonly Guid _otherFilm = Guid.NewGuid();
    private readonly Guid _collection = Guid.NewGuid();
    private readonly Guid _playlist = Guid.NewGuid();

    public BaseItemRepositoryHiddenItemsTests()
    {
        using (var ctx = CreateDbContext())
        {
            Seed(ctx);
        }

        _repository = CreateBaseItemRepository(new ItemTypeLookup());
    }

    [Fact]
    public void HiddenSeries_TakesItsSeasonsAndEpisodes()
    {
        var ids = Query(new InternalItemsQuery { HiddenItemIds = [_series] });

        Assert.DoesNotContain(_series, ids);
        Assert.DoesNotContain(_season, ids);
        Assert.DoesNotContain(_episode, ids);
        Assert.Contains(_otherEpisode, ids);
    }

    [Fact]
    public void HiddenChannel_TakesItsProgrammes()
    {
        var ids = Query(new InternalItemsQuery { HiddenItemIds = [_channel] });

        Assert.DoesNotContain(_programme, ids);
        Assert.Contains(_otherProgramme, ids);
    }

    [Fact]
    public void HiddenFilm_TakesItsAlternateVersions()
    {
        var ids = AccessFiltered(new InternalItemsQuery { HiddenItemIds = [_film], IncludeOwnedItems = true });

        Assert.DoesNotContain(_film, ids);
        Assert.DoesNotContain(_filmVersion, ids);
        Assert.Contains(_otherFilm, ids);
    }

    [Fact]
    public void Collection_WithEveryFilmHidden_IsHidden()
    {
        Assert.DoesNotContain(_collection, Query(new InternalItemsQuery { HiddenItemIds = [_film, _otherFilm] }));
        Assert.DoesNotContain(_collection, AccessFiltered(new InternalItemsQuery { HiddenItemIds = [_film, _otherFilm] }));
    }

    [Fact]
    public void Collection_WithOneFilmLeft_Stays()
    {
        Assert.Contains(_collection, Query(new InternalItemsQuery { HiddenItemIds = [_film] }));
    }

    [Fact]
    public void HiddenCollection_StaysHidden_ButNotItsFilms()
    {
        var ids = Query(new InternalItemsQuery { HiddenItemIds = [_collection] });

        Assert.DoesNotContain(_collection, ids);
        Assert.Contains(_film, ids);
    }

    [Fact]
    public void Playlist_WithOnlyARemovedSeriesInIt_IsHidden()
    {
        Assert.DoesNotContain(_playlist, Query(new InternalItemsQuery { HiddenItemIds = [_series] }));
        Assert.Contains(_playlist, Query(new InternalItemsQuery { HiddenItemIds = [_otherSeries] }));
    }

    [Fact]
    public void AllowedChannel_LetsItsProgrammesThrough()
    {
        var ids = Query(new InternalItemsQuery { IncludeInheritedTags = ["kids"], AllowedItemIds = [_channel] });

        Assert.Contains(_programme, ids);
        Assert.DoesNotContain(_otherProgramme, ids);
    }

    [Fact]
    public void AllowedFilm_LetsItsAlternateVersionsThrough()
    {
        var ids = AccessFiltered(new InternalItemsQuery { IncludeInheritedTags = ["kids"], AllowedItemIds = [_film], IncludeOwnedItems = true });

        Assert.Contains(_film, ids);
        Assert.Contains(_filmVersion, ids);
        Assert.DoesNotContain(_otherFilm, ids);
    }

    [Fact]
    public void AllowedSeries_LetsItsEpisodesThrough()
    {
        var ids = Query(new InternalItemsQuery { IncludeInheritedTags = ["kids"], AllowedItemIds = [_series] });

        Assert.Contains(_episode, ids);
        Assert.Contains(_season, ids);
        Assert.DoesNotContain(_otherEpisode, ids);
    }

    [Fact]
    public void HiddenWins_OverAllowed()
    {
        var ids = Query(new InternalItemsQuery { IncludeInheritedTags = ["kids"], AllowedItemIds = [_series], HiddenItemIds = [_series] });

        Assert.DoesNotContain(_episode, ids);
    }

    private HashSet<Guid> Query(InternalItemsQuery filter) => _repository.GetItemIdsList(filter).ToHashSet();

    private HashSet<Guid> AccessFiltered(InternalItemsQuery filter)
    {
        using var ctx = CreateDbContext();
        return _repository.ApplyAccessFiltering(ctx, ctx.BaseItems, filter).Select(e => e.Id).ToHashSet();
    }

    private void Seed(JellyfinDbContext context)
    {
        AddItem(context, _library, FolderType, true);
        AddItem(context, _series, SeriesType, true);
        AddItem(context, _season, SeasonType, true, seriesId: _series);
        AddItem(context, _episode, EpisodeType, false, seriesId: _series);
        AddItem(context, _otherSeries, SeriesType, true);
        AddItem(context, _otherEpisode, EpisodeType, false, seriesId: _otherSeries);
        AddItem(context, _channel, ChannelType, false);
        AddItem(context, _otherChannel, ChannelType, false);
        AddItem(context, _programme, ProgramType, false, channelId: _channel);
        AddItem(context, _otherProgramme, ProgramType, false, channelId: _otherChannel);
        AddItem(context, _film, MovieType, false);
        AddItem(context, _otherFilm, MovieType, false);
        AddItem(context, _collection, BoxSetType, true);
        AddItem(context, _playlist, PlaylistType, true);
        context.SaveChanges();

        // The version refers to its main version, which has to be there first
        AddItem(context, _filmVersion, MovieType, false, primaryVersionId: _film);

        AddAncestors(context, _series, _library);
        AddAncestors(context, _season, _series, _library);
        AddAncestors(context, _episode, _season, _series, _library);
        AddAncestors(context, _otherSeries, _library);
        AddAncestors(context, _otherEpisode, _otherSeries, _library);
        AddAncestors(context, _film, _library);
        AddAncestors(context, _filmVersion, _library);
        AddAncestors(context, _otherFilm, _library);

        AddLink(context, _collection, _film, 0);
        AddLink(context, _collection, _otherFilm, 1);
        AddLink(context, _playlist, _episode, 0);

        context.SaveChanges();
    }

    private static void AddItem(JellyfinDbContext context, Guid id, string type, bool isFolder, Guid? seriesId = null, Guid? channelId = null, Guid? primaryVersionId = null)
    {
        context.BaseItems.Add(new BaseItemEntity
        {
            Id = id,
            Type = type,
            Name = type + " " + id,
            IsFolder = isFolder,
            SeriesId = seriesId,
            ChannelId = channelId,
            PrimaryVersionId = primaryVersionId
        });
    }

    private static void AddAncestors(JellyfinDbContext context, Guid itemId, params Guid[] ancestorIds)
    {
        foreach (var ancestorId in ancestorIds)
        {
            context.AncestorIds.Add(new AncestorId
            {
                ItemId = itemId,
                ParentItemId = ancestorId,
                Item = null!,
                ParentItem = null!
            });
        }
    }

    private static void AddLink(JellyfinDbContext context, Guid parentId, Guid childId, int sortOrder)
    {
        context.LinkedChildren.Add(new LinkedChildEntity
        {
            ParentId = parentId,
            ChildId = childId,
            ChildType = Jellyfin.Database.Implementations.Entities.LinkedChildType.Manual,
            SortOrder = sortOrder
        });
    }
}
