using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Controllers;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

/// <summary>
/// Covers who may remove and put back items, for one profile or a whole level (Finly).
/// </summary>
public class HiddenItemsControllerTests
{
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IProfileLevelStore> _levels = new();
    private readonly List<User> _users = [];
    private readonly Movie _film = new() { Id = Guid.NewGuid(), Name = "Film", Tags = ["kids"] };
    private readonly ProfileLevel _child = new() { Id = Guid.NewGuid(), Name = "Child", AllowedTags = ["kids"] };
    private readonly ProfileLevel _adult = new() { Id = Guid.NewGuid(), Name = "Adult" };
    private readonly HiddenItemsController _subject;

    // Which items each profile sees, as the library would answer with all its restrictions
    private Func<User, InternalItemsQuery, bool> _sees = (_, _) => true;

    public HiddenItemsControllerTests()
    {
        _userManager.Setup(m => m.GetUsers()).Returns(() => _users.ToArray());
        _userManager.Setup(m => m.GetUserById(It.IsAny<Guid>())).Returns<Guid>(id => _users.FirstOrDefault(u => u.Id.Equals(id)));
        _libraryManager.Setup(l => l.GetItemById(_film.Id)).Returns(_film);
        _libraryManager.Setup(l => l.GetItemIds(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q => q.ItemIds.Where(_ => _sees(q.User!, q)).ToList());
        _levels.Setup(l => l.GetLevel(_child.Id)).Returns(_child);
        _levels.Setup(l => l.GetLevel(_adult.Id)).Returns(_adult);

        _subject = new HiddenItemsController(_userManager.Object, _libraryManager.Object, new Mock<IDtoService>().Object, _levels.Object);
    }

    [Fact]
    public async Task HideFromLevel_ByAChild_IsForbidden()
    {
        var calum = AddUser("calum", _child);
        AddUser("euan", _child);
        SignIn(calum, false);

        var result = await _subject.HideFromLevel(_film.Id, _child.Id);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _userManager.Verify(m => m.SetItemHiddenAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<ItemIdentity?>()), Times.Never);
        _userManager.Verify(m => m.SetItemAllowedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<ItemIdentity?>()), Times.Never);
    }

    [Fact]
    public async Task HideFromLevel_ByAnAdultWhoIsntAParent_IsForbidden()
    {
        SignIn(AddUser("guest", _adult), false);
        AddUser("calum", _child);

        var result = await _subject.HideFromLevel(_film.Id, _child.Id);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    [Fact]
    public async Task HideFromLevel_ByAParent_RemovesItFromEveryone()
    {
        SignIn(AddUser("ryan", _adult, true), true);
        var calum = AddUser("calum", _child);
        var euan = AddUser("euan", _child);

        var result = await _subject.HideFromLevel(_film.Id, _child.Id);

        Assert.Equal(2, result.Value);
        foreach (var member in new[] { calum, euan })
        {
            _userManager.Verify(m => m.SetItemAllowedAsync(member.Id, _film.Id, false, null), Times.Once);
            _userManager.Verify(m => m.SetItemHiddenAsync(member.Id, _film.Id, true, It.Is<ItemIdentity?>(i => i != null)), Times.Once);
        }
    }

    [Fact]
    public async Task HideItem_OnYourOwnProfile_KeepsAParentsAllowance()
    {
        var calum = AddUser("calum", _child);
        SignIn(calum, false);

        Assert.IsType<NoContentResult>(await _subject.HideItem(calum.Id, _film.Id));

        _userManager.Verify(m => m.SetItemAllowedAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<ItemIdentity?>()), Times.Never);
        _userManager.Verify(m => m.SetItemHiddenAsync(calum.Id, _film.Id, true, It.IsAny<ItemIdentity?>()), Times.Once);
    }

    [Fact]
    public async Task HideItem_ByAParent_TakesBackTheAllowance()
    {
        SignIn(AddUser("ryan", _adult, true), true);
        var calum = AddUser("calum", _child);

        Assert.IsType<NoContentResult>(await _subject.HideItem(calum.Id, _film.Id));

        _userManager.Verify(m => m.SetItemAllowedAsync(calum.Id, _film.Id, false, null), Times.Once);
    }

    [Fact]
    public async Task RestoreItem_AdultWithoutALevel_WhoRemovedSomething_CanPutItBack()
    {
        var catriona = AddUser("catriona", null);
        catriona.SetPreference(PreferenceKind.HiddenItems, [_film.Id]);
        SignIn(catriona, false);

        Assert.IsType<NoContentResult>(await _subject.RestoreItem(catriona.Id, _film.Id));
        _userManager.Verify(m => m.SetItemHiddenAsync(catriona.Id, _film.Id, false, null), Times.Once);
    }

    [Fact]
    public async Task RestoreItem_ChildOnARestrictedLevel_NeedsAParent()
    {
        var calum = AddUser("calum", _child);
        calum.SetPreference(PreferenceKind.HiddenItems, [_film.Id]);
        SignIn(calum, false);

        var result = await _subject.RestoreItem(calum.Id, _film.Id);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public void ProfileAccess_AdultWhoRemovedSomething_IsNotARestrictedProfile()
    {
        var catriona = AddUser("catriona", null);
        catriona.SetPreference(PreferenceKind.HiddenItems, [_film.Id]);
        var calum = AddUser("calum", _child);
        calum.SetPreference(PreferenceKind.AllowedTags, ["kids"]);

        var result = _subject.GetProfileAccess([]).Value!;

        Assert.Equal(calum.Id, Assert.Single(result.Profiles).Id);
    }

    [Fact]
    public async Task AllowForLevel_WhenTheTagIsntEnough_AllowsTheItemForTheProfile()
    {
        SignIn(AddUser("ryan", _adult, true), true);
        var calum = AddUser("calum", _child);

        // The film also has a tag the profile blocks, so only an allowance lets it through
        _sees = (user, query) => query.AllowedItemIds.Contains(_film.Id);
        _userManager.Setup(m => m.SetItemAllowedAsync(calum.Id, _film.Id, true, It.IsAny<ItemIdentity?>()))
            .Callback(() => calum.SetPreference(PreferenceKind.AllowedItems, [_film.Id]))
            .Returns(Task.CompletedTask);

        var result = await _subject.AllowForLevel(_film.Id, _child.Id);

        Assert.Equal(1, result.Value);
        _userManager.Verify(m => m.SetItemHiddenAsync(calum.Id, _film.Id, false, null), Times.Once);
        _userManager.Verify(m => m.SetItemAllowedAsync(calum.Id, _film.Id, true, It.IsAny<ItemIdentity?>()), Times.Once);
    }

    [Fact]
    public async Task AllowForLevel_WhenTheTagIsEnough_DoesntAllowOneByOne()
    {
        SignIn(AddUser("ryan", _adult, true), true);
        var calum = AddUser("calum", _child);

        var result = await _subject.AllowForLevel(_film.Id, _child.Id);

        Assert.Equal(1, result.Value);
        _userManager.Verify(m => m.SetItemAllowedAsync(calum.Id, It.IsAny<Guid>(), It.IsAny<bool>(), It.IsAny<ItemIdentity?>()), Times.Never);
    }

    [Fact]
    public async Task AllowForLevel_CountsOnlyTheProfilesThatCanSeeIt()
    {
        SignIn(AddUser("ryan", _adult, true), true);
        AddUser("calum", _child);

        // Nothing lets it through, such as a library the profile can't open
        _sees = (_, _) => false;

        var result = await _subject.AllowForLevel(_film.Id, _child.Id);

        Assert.Equal(0, result.Value);
    }

    private User AddUser(string name, ProfileLevel? level, bool administrator = false)
    {
        var user = new User(name, typeof(DefaultAuthenticationProvider).FullName!, typeof(DefaultPasswordResetProvider).FullName!);
        user.AddDefaultPermissions();
        user.AddDefaultPreferences();
        user.SetPermission(PermissionKind.IsAdministrator, administrator);
        if (level is not null)
        {
            user.SetPreference(PreferenceKind.ProfileLevel, [level.Id.ToString("N", CultureInfo.InvariantCulture)]);
        }

        _users.Add(user);
        return user;
    }

    private void SignIn(User user, bool administrator)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Role, administrator ? UserRoles.Administrator : UserRoles.User),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(InternalClaimTypes.UserId, user.Id.ToString("N", CultureInfo.InvariantCulture)),
            new Claim(InternalClaimTypes.IsApiKey, bool.FalseString)
        };

        _subject.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }
}
