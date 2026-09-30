using System;
using System.IO;
using System.Threading;
using Emby.Server.Implementations.Cryptography;
using Jellyfin.Database.Implementations;
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
using MediaBrowser.Model.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Server.Implementations.Tests.Users;

/// <summary>
/// An in-memory user database with a real <see cref="UserManager"/> on it, and a clock the tests move.
/// </summary>
public sealed class UserManagerTestDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;

    public UserManagerTestDatabase()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_connection).Options;

        using var ctx = CreateDbContext();
        ctx.Database.EnsureCreated();

        UserManager = CreateUserManager();
    }

    public Mock<IEventManager> EventManager { get; } = new();

    public TestTimeProvider Time { get; } = new();

    public UserManager UserManager { get; }

    public UserManager CreateUserManager()
    {
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

        return new UserManager(
            factory.Object,
            EventManager.Object,
            network.Object,
            appHost.Object,
            new Mock<IImageProcessor>().Object,
            NullLogger<UserManager>.Instance,
            configManager.Object,
            new IPasswordResetProvider[] { new DefaultPasswordResetProvider(configManager.Object, appHost.Object) },
            new IAuthenticationProvider[] { defaultAuthProvider, new InvalidAuthProvider() })
        {
            TimeProvider = Time
        };
    }

    public JellyfinDbContext CreateDbContext()
    {
        return new JellyfinDbContext(
            _dbOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    public void Dispose()
    {
        UserManager.Dispose();
        _connection.Dispose();
    }

    public sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 30, 18, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
