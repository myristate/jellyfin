using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Controllers;
using Jellyfin.Api.Models.UserDtos;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.QuickConnect;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

/// <summary>
/// Covers the Finly rules for changing passwords and PINs, and what the sign in screen is told.
/// </summary>
public class UserControllerSignInTests
{
    private const string Token = "caller-token";

    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly Mock<INetworkManager> _networkManager = new();
    private readonly Mock<IServerConfigurationManager> _config = new();
    private readonly Mock<IProfileLevelStore> _profileLevels = new();
    private readonly UserController _subject;

    public UserControllerSignInTests()
    {
        _config.Setup(c => c.Configuration).Returns(new ServerConfiguration { IsStartupWizardCompleted = true });
        _networkManager.Setup(n => n.IsInLocalNetwork(It.IsAny<IPAddress>()))
            .Returns<IPAddress>(ip => ip.ToString().StartsWith("192.168.", StringComparison.Ordinal));

        _subject = new UserController(
            _userManager.Object,
            _sessionManager.Object,
            _networkManager.Object,
            new Mock<IDeviceManager>().Object,
            new Mock<IAuthorizationContext>().Object,
            _config.Object,
            new Mock<ILogger<UserController>>().Object,
            new Mock<IQuickConnect>().Object,
            new Mock<IPlaylistManager>().Object,
            _profileLevels.Object);
    }

    [Fact]
    public async Task ResettingYourOwnPassword_NeedsTheCurrentOne()
    {
        var ryan = AddUser("ryan", true);
        SignIn(ryan, true);

        var result = await _subject.UpdateUserPassword(null, new UpdateUserPassword { ResetPassword = true });

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        _userManager.Verify(m => m.ResetPassword(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task ResettingYourOwnPassword_WithTheCurrentOne_Works()
    {
        var ryan = AddUser("ryan", true);
        SignIn(ryan, true);
        AcceptPassword(ryan, "secret");

        var result = await _subject.UpdateUserPassword(ryan.Id, new UpdateUserPassword { ResetPassword = true, CurrentPw = "secret" });

        Assert.IsType<NoContentResult>(result);
        _userManager.Verify(m => m.ResetPassword(ryan.Id), Times.Once);
    }

    [Fact]
    public async Task AnAdministrator_ResetsSomeoneElsesPassword_WithoutTheirs()
    {
        var ryan = AddUser("ryan", true);
        var calum = AddUser("calum", false);
        SignIn(ryan, true);

        var result = await _subject.UpdateUserPassword(calum.Id, new UpdateUserPassword { ResetPassword = true });

        Assert.IsType<NoContentResult>(result);
        _userManager.Verify(m => m.ResetPassword(calum.Id), Times.Once);
        _userManager.Verify(m => m.AuthenticateUser(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task ChangingYourOwnPassword_ChecksThePasswordOnly()
    {
        var ryan = AddUser("ryan", true);
        SignIn(ryan, true);

        // No userId: the caller's own account, which used to skip the check for administrators
        var result = await _subject.UpdateUserPassword(null, new UpdateUserPassword { CurrentPw = "1234", NewPw = "new" });

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        _userManager.Verify(m => m.AuthenticateUser("ryan", "1234", It.IsAny<string>(), false, false), Times.Once);
        _userManager.Verify(m => m.ChangePassword(It.IsAny<Guid>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ChangingYourOwnPin_ChecksThePasswordOnly()
    {
        var calum = AddUser("calum", false);
        SignIn(calum, false);

        var result = await _subject.UpdateUserPin(null, new UpdateUserPin { CurrentPw = "4321", NewPin = "1111" });

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(result).StatusCode);
        _userManager.Verify(m => m.AuthenticateUser("calum", "4321", It.IsAny<string>(), false, false), Times.Once);
        _userManager.Verify(m => m.SetPinAsync(It.IsAny<Guid>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task ChangingAPin_SignsOutTheUsersOtherSessions()
    {
        var ryan = AddUser("ryan", true);
        var calum = AddUser("calum", false);
        SignIn(ryan, true);

        var result = await _subject.UpdateUserPin(calum.Id, new UpdateUserPin { NewPin = "1111" });

        Assert.IsType<NoContentResult>(result);
        _userManager.Verify(m => m.SetPinAsync(calum.Id, "1111"), Times.Once);
        _sessionManager.Verify(m => m.RevokeUserTokens(calum.Id, Token), Times.Once);
    }

    [Fact]
    public async Task RemovingYourOwnPin_SignsOutYourOtherSessions()
    {
        var ryan = AddUser("ryan", true);
        SignIn(ryan, true);
        AcceptPassword(ryan, "secret");

        var result = await _subject.UpdateUserPin(null, new UpdateUserPin { CurrentPw = "secret", ResetPin = true });

        Assert.IsType<NoContentResult>(result);
        _userManager.Verify(m => m.SetPinAsync(ryan.Id, null), Times.Once);
        _sessionManager.Verify(m => m.RevokeUserTokens(ryan.Id, Token), Times.Once);
    }

    [Fact]
    public void PublicUsers_AwayFromHome_DontTellWhoHasNoPassword()
    {
        AddUser("euan", false);
        SetCaller("8.8.8.8");

        var user = Assert.Single(GetPublicUsers());

#pragma warning disable CS0618 // Obsolete upstream, Finly fills them in again
        Assert.True(user.HasPassword);
        Assert.True(user.HasConfiguredPassword);
        Assert.False(user.HasPin);
#pragma warning restore CS0618
    }

    [Fact]
    public void PublicUsers_AtHome_TellHowEachSignsIn()
    {
        AddUser("euan", false);
        SetCaller("192.168.1.20");

        var user = Assert.Single(GetPublicUsers());

#pragma warning disable CS0618 // Obsolete upstream, Finly fills them in again
        Assert.False(user.HasPassword);
        Assert.False(user.HasConfiguredPassword);
        Assert.True(user.HasPin);
#pragma warning restore CS0618
    }

    [Fact]
    public async Task UpdateUserPolicy_WithALevelThatDoesntExist_IsABadRequest()
    {
        var calum = AddUser("calum", false);
        SignIn(AddUser("ryan", true), true);

        var result = await _subject.UpdateUserPolicy(calum.Id, new UserPolicy
        {
            ProfileLevelId = Guid.NewGuid(),
            AuthenticationProviderId = "a",
            PasswordResetProviderId = "b"
        });

        Assert.IsType<BadRequestObjectResult>(result);
        _userManager.Verify(m => m.UpdatePolicyAsync(It.IsAny<Guid>(), It.IsAny<UserPolicy>()), Times.Never);
    }

    [Fact]
    public async Task UnlockUserPin_TurnsPinSignInBackOn()
    {
        var calum = AddUser("calum", false);
        SignIn(AddUser("ryan", true), true);

        Assert.IsType<NoContentResult>(await _subject.UnlockUserPin(calum.Id));
        _userManager.Verify(m => m.ClearPinLockoutAsync(calum.Id), Times.Once);
    }

    private List<UserDto> GetPublicUsers()
    {
        var result = _subject.GetPublicUsers();
        return Assert.IsAssignableFrom<IEnumerable<UserDto>>(Assert.IsAssignableFrom<ObjectResult>(result.Result).Value).ToList();
    }

    private User AddUser(string name, bool administrator)
    {
        var user = new User(name, typeof(DefaultAuthenticationProvider).FullName!, typeof(DefaultPasswordResetProvider).FullName!);
        user.AddDefaultPermissions();
        user.AddDefaultPreferences();
        user.SetPermission(PermissionKind.IsAdministrator, administrator);
        user.SetPermission(PermissionKind.IsHidden, false);
        user.SetPermission(PermissionKind.EnableRemoteAccess, true);

        _userManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        _userManager.Setup(m => m.GetUsers()).Returns(() => [user]);
#pragma warning disable CS0618 // Obsolete upstream, Finly fills them in again
        _userManager.Setup(m => m.GetUserDto(user, It.IsAny<string?>()))
            .Returns(() => new UserDto { Id = user.Id, Name = name, HasPassword = false, HasConfiguredPassword = false, HasPin = true });
#pragma warning restore CS0618
        return user;
    }

    private void AcceptPassword(User user, string password)
        => _userManager.Setup(m => m.AuthenticateUser(user.Username, password, It.IsAny<string>(), false, false)).ReturnsAsync(user);

    private void SignIn(User user, bool administrator)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Role, administrator ? UserRoles.Administrator : UserRoles.User),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(InternalClaimTypes.UserId, user.Id.ToString("N", CultureInfo.InvariantCulture)),
            new Claim(InternalClaimTypes.Token, Token),
            new Claim(InternalClaimTypes.IsApiKey, bool.FalseString)
        };

        SetCaller("192.168.1.20", new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")));
    }

    private void SetCaller(string ip, ClaimsPrincipal? user = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        if (user is not null)
        {
            httpContext.User = user;
        }

        _subject.ControllerContext = new ControllerContext { HttpContext = httpContext };
    }
}
