using System;
using System.Collections.Generic;
using System.Linq;
using Emby.Server.Implementations.Images;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.LiveTv;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Images;

public class LogoWallChannelsTests
{
    private readonly LiveTvChannel _bbc = Channel("BBC ONE");
    private readonly LiveTvChannel _cbeebies = Channel("CBeebies");
    private readonly LiveTvChannel _film4 = Channel("Film4");
    private readonly LiveTvChannel _noLogo = new() { Id = Guid.NewGuid(), Name = "No logo", ChannelType = ChannelType.TV };
    private readonly LiveTvChannel _radio = Channel("Radio 4", ChannelType.Radio);
    private readonly Mock<ILibraryManager> _libraryManager = new();

    public LogoWallChannelsTests()
    {
        _libraryManager
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.User == null)))
            .Returns([_bbc, _cbeebies, _film4, _noLogo, _radio]);
    }

    [Fact]
    public void GetChannels_NoRestrictedProfiles_EveryTvChannelWithALogo()
    {
        var channels = LogoWallChannels.GetChannels(_libraryManager.Object, Users(Adult("Dad")));

        Assert.Equal([_bbc.Id, _cbeebies.Id, _film4.Id], channels.Select(c => c.Id).OrderBy(Order));
    }

    [Fact]
    public void GetChannels_RestrictedProfiles_OnlyChannelsEveryOneOfThemSees()
    {
        var child = Restricted("Calum");
        var teen = Restricted("Teen");
        VisibleTo(child, _bbc, _cbeebies);
        VisibleTo(teen, _bbc, _cbeebies, _film4);

        var channels = LogoWallChannels.GetChannels(_libraryManager.Object, Users(Adult("Dad"), child, teen));

        Assert.Equal([_bbc.Id, _cbeebies.Id], channels.Select(c => c.Id).OrderBy(Order));
    }

    [Fact]
    public void GetChannels_RestrictedProfileWithoutLiveTv_Ignored()
    {
        var noTv = Restricted("NoTv");
        noTv.SetPermission(PermissionKind.EnableLiveTvAccess, false);
        VisibleTo(noTv);

        var channels = LogoWallChannels.GetChannels(_libraryManager.Object, Users(noTv));

        Assert.Equal(3, channels.Count);
    }

    private static LiveTvChannel Channel(string name, ChannelType type = ChannelType.TV)
    {
        var channel = new LiveTvChannel { Id = Guid.NewGuid(), Name = name, ChannelType = type };
        channel.ImageInfos = [new ItemImageInfo { Path = "/config/metadata/" + name + ".png", Type = ImageType.Primary }];
        return channel;
    }

    private static User Adult(string name)
    {
        var user = new User(name, "default", "default");
        user.AddDefaultPermissions();
        return user;
    }

    private static User Restricted(string name)
    {
        var user = Adult(name);
        user.MaxParentalRatingScore = 10;
        return user;
    }

    private static IUserManager Users(params User[] users)
    {
        var userManager = new Mock<IUserManager>();
        userManager.Setup(u => u.GetUsers()).Returns(users);
        return userManager.Object;
    }

    private void VisibleTo(User user, params LiveTvChannel[] channels)
    {
        _libraryManager
            .Setup(l => l.GetItemIds(It.Is<InternalItemsQuery>(q => q.User == user)))
            .Returns(channels.Select(c => c.Id).ToList());
    }

    private string Order(Guid id) => new List<LiveTvChannel> { _bbc, _cbeebies, _film4 }.First(c => c.Id.Equals(id)).Name;
}
