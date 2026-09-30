using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Cryptography;
using Jellyfin.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Users;

public sealed class UserManagerSignInPinTests : IDisposable
{
    private const string Home = "192.168.1.20";
    private const string Away = "8.8.8.8";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;
    private readonly UserManager _userManager;

    public UserManagerSignInPinTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_connection).Options;

        using var ctx = CreateDbContext();
        ctx.Database.EnsureCreated();

        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateDbContext);

        var configManager = new Mock<IServerConfigurationManager>();
        var appPaths = new Mock<IServerApplicationPaths>();
        appPaths.Setup(x => x.ProgramDataPath).Returns(Path.GetTempPath());
        configManager.Setup(x => x.ApplicationPaths).Returns(appPaths.Object);
        configManager.Setup(x => x.Configuration).Returns(new ServerConfiguration());

        var network = new Mock<INetworkManager>();
        network.Setup(n => n.IsInLocalNetwork(It.IsAny<string>())).Returns<string>(ip => ip.StartsWith("192.168.", StringComparison.Ordinal));

        var appHost = new Mock<IApplicationHost>();
        var defaultAuthProvider = new DefaultAuthenticationProvider(NullLogger<DefaultAuthenticationProvider>.Instance, new CryptographyProvider());

        _userManager = new UserManager(
            factory.Object,
            new Mock<IEventManager>().Object,
            network.Object,
            appHost.Object,
            new Mock<IImageProcessor>().Object,
            NullLogger<UserManager>.Instance,
            configManager.Object,
            new IPasswordResetProvider[] { new DefaultPasswordResetProvider(configManager.Object, appHost.Object) },
            new IAuthenticationProvider[] { defaultAuthProvider, new InvalidAuthProvider() });
    }

    public void Dispose()
    {
        _userManager.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task Pin_SignsInAtHome_NotAway()
    {
        var user = await _userManager.CreateUserAsync("ryan");
        await _userManager.ChangePassword(user.Id, "secret");
        await _userManager.SetPinAsync(user.Id, "1234");

        Assert.NotNull(await _userManager.AuthenticateUser("ryan", "1234", Home, false));
        Assert.Null(await _userManager.AuthenticateUser("ryan", "1234", Away, false));
        Assert.NotNull(await _userManager.AuthenticateUser("ryan", "secret", Away, false));
    }

    [Fact]
    public async Task PinOnlyProfile_NeedsItsPin()
    {
        var user = await _userManager.CreateUserAsync("calum");
        await _userManager.SetPinAsync(user.Id, "4321");

        Assert.Null(await _userManager.AuthenticateUser("calum", string.Empty, Home, false));
        Assert.Null(await _userManager.AuthenticateUser("calum", "0000", Home, false));
        Assert.NotNull(await _userManager.AuthenticateUser("calum", "4321", Home, false));
        Assert.Null(await _userManager.AuthenticateUser("calum", "4321", Away, false));
    }

    [Fact]
    public async Task NoPasswordNoPin_SignsInWithATap()
    {
        await _userManager.CreateUserAsync("euan");

        Assert.NotNull(await _userManager.AuthenticateUser("euan", string.Empty, Home, false));
    }

    [Fact]
    public async Task WrongPins_DontDisableTheAccount_ButLockPinSignIn()
    {
        var user = await _userManager.CreateUserAsync("catriona");
        await _userManager.ChangePassword(user.Id, "secret");
        await _userManager.SetPinAsync(user.Id, "1234");

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await _userManager.AuthenticateUser("catriona", "9999", Home, false));
        }

        var stored = _userManager.GetUserById(user.Id)!;
        Assert.Equal(0, stored.InvalidLoginAttemptCount);
        Assert.False(stored.HasPermission(PermissionKind.IsDisabled));

        // Locked for PINs now, even the right one, but the password still works
        await Assert.ThrowsAsync<SecurityException>(() => _userManager.AuthenticateUser("catriona", "1234", Home, false));
        Assert.NotNull(await _userManager.AuthenticateUser("catriona", "secret", Home, false));
    }

    [Fact]
    public async Task RemovingPin_LeavesThePassword()
    {
        var user = await _userManager.CreateUserAsync("ryan");
        await _userManager.ChangePassword(user.Id, "secret");
        await _userManager.SetPinAsync(user.Id, "1234");
        await _userManager.SetPinAsync(user.Id, null);

        Assert.Null(await _userManager.AuthenticateUser("ryan", "1234", Home, false));
        Assert.NotNull(await _userManager.AuthenticateUser("ryan", "secret", Home, false));
    }

    [Theory]
    [InlineData("123")]
    [InlineData("12345")]
    [InlineData("abcd")]
    public async Task SetPin_RejectsAnythingButFourDigits(string pin)
    {
        var user = await _userManager.CreateUserAsync("ryan");

        await Assert.ThrowsAsync<ArgumentException>(() => _userManager.SetPinAsync(user.Id, pin));
    }

    [Fact]
    public async Task Administrators_MayDropTheirPassword_WhileAnotherKeepsOne()
    {
        var first = await CreateAdminAsync("ryan", "secret");
        var second = await CreateAdminAsync("catriona", "secret");

        await _userManager.ChangePassword(first.Id, string.Empty);

        // Catriona is now the only administrator with a password
        await Assert.ThrowsAsync<ArgumentException>(() => _userManager.ChangePassword(second.Id, string.Empty));
        Assert.False(_userManager.HasOtherAdministratorWithPassword(second.Id));
        Assert.True(_userManager.HasOtherAdministratorWithPassword(first.Id));
    }

    [Fact]
    public async Task LastAdministratorWithPassword_CantBeDeleted()
    {
        var withPassword = await CreateAdminAsync("ryan", "secret");
        await CreateAdminAsync("catriona", null);

        await Assert.ThrowsAsync<ArgumentException>(() => _userManager.DeleteUserAsync(withPassword.Id));
    }

    [Fact]
    public async Task UserDto_TellsClientsHowToSignIn()
    {
        var pinOnly = await _userManager.CreateUserAsync("calum");
        await _userManager.SetPinAsync(pinOnly.Id, "4321");
        var neither = await _userManager.CreateUserAsync("euan");

#pragma warning disable CS0618 // Obsolete upstream, Finly fills them in again
        var home = _userManager.GetUserDto(_userManager.GetUserById(pinOnly.Id)!, Home);
        Assert.True(home.HasPin);
        Assert.True(home.HasPassword);
        Assert.False(home.HasConfiguredPassword);

        Assert.False(_userManager.GetUserDto(_userManager.GetUserById(pinOnly.Id)!, Away).HasPin);

        var tap = _userManager.GetUserDto(_userManager.GetUserById(neither.Id)!, Home);
        Assert.False(tap.HasPin);
        Assert.False(tap.HasPassword);
#pragma warning restore CS0618
    }

    private async Task<Jellyfin.Database.Implementations.Entities.User> CreateAdminAsync(string name, string? password)
    {
        var user = await _userManager.CreateUserAsync(name);
        await using (var ctx = CreateDbContext())
        {
            var dbUser = await ctx.Users.Include(u => u.Permissions).FirstAsync(u => u.Id.Equals(user.Id));
            dbUser.SetPermission(PermissionKind.IsAdministrator, true);
            await ctx.SaveChangesAsync();
        }

        if (password is not null)
        {
            await _userManager.ChangePassword(user.Id, password);
        }

        return _userManager.GetUserById(user.Id)!;
    }

    private JellyfinDbContext CreateDbContext()
    {
        return new JellyfinDbContext(
            _dbOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
