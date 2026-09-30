using System;
using System.IO;
using System.Linq;
using Jellyfin.Server.Implementations.Item;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Library;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Users;

/// <summary>
/// Covers Finly's JSON stores: profilelevels.json and itemreports.json, and the limit on reports.
/// </summary>
public sealed class FinlyStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "finly-store-tests-" + Guid.NewGuid().ToString("N"));
    private readonly IApplicationPaths _appPaths;

    public FinlyStoreTests()
    {
        Directory.CreateDirectory(_directory);
        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.ConfigurationDirectoryPath).Returns(_directory);
        _appPaths = appPaths.Object;
    }

    private string LevelsPath => Path.Combine(_directory, "profilelevels.json");

    private string ReportsPath => Path.Combine(_directory, "itemreports.json");

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void ProfileLevels_FirstRun_StartWithChildTeenAndAdult()
    {
        var store = new ProfileLevelStore(_appPaths, NullLogger<ProfileLevelStore>.Instance);

        Assert.Equal(["Child", "Teen", "Adult"], store.GetLevels().Select(l => l.Name));
        Assert.True(File.Exists(LevelsPath));
    }

    [Fact]
    public void ProfileLevels_UnreadableFile_IsSetAside_WithoutWritingDefaultsOverIt()
    {
        File.WriteAllText(LevelsPath, "[{ \"Name\": \"Child\", broken");

        var store = new ProfileLevelStore(_appPaths, NullLogger<ProfileLevelStore>.Instance);

        Assert.Empty(store.GetLevels());
        Assert.False(File.Exists(LevelsPath));
        var bad = Assert.Single(Directory.GetFiles(_directory, "profilelevels.json.bad-*"));
        Assert.Matches(@"profilelevels\.json\.bad-\d{14}$", bad);
        Assert.Equal("[{ \"Name\": \"Child\", broken", File.ReadAllText(bad));
    }

    [Fact]
    public void ProfileLevels_AfterAnUnreadableFile_SavingStartsANewOne()
    {
        File.WriteAllText(LevelsPath, "not json");
        var store = new ProfileLevelStore(_appPaths, NullLogger<ProfileLevelStore>.Instance);
        Assert.Empty(store.GetLevels());

        store.Save(new ProfileLevel { Name = "Child", AllowedTags = ["kids"] });

        var reloaded = new ProfileLevelStore(_appPaths, NullLogger<ProfileLevelStore>.Instance);
        Assert.Equal("Child", Assert.Single(reloaded.GetLevels()).Name);
    }

    [Fact]
    public void ProfileLevels_AreKeptAcrossRestarts()
    {
        var store = new ProfileLevelStore(_appPaths, NullLogger<ProfileLevelStore>.Instance);
        var saved = store.Save(new ProfileLevel { Name = "Toddler", MaxParentalRating = 0 });

        var reloaded = new ProfileLevelStore(_appPaths, NullLogger<ProfileLevelStore>.Instance);

        Assert.Equal(4, reloaded.GetLevels().Count);
        Assert.Equal("Toddler", reloaded.GetLevel(saved.Id)!.Name);
        Assert.Empty(Directory.GetFiles(_directory, "*.bad-*"));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Reports_UnreadableFile_IsSetAside()
    {
        File.WriteAllText(ReportsPath, "{");

        var store = new ItemReportStore(_appPaths, NullLogger<ItemReportStore>.Instance);

        Assert.Empty(store.GetReports());
        Assert.False(File.Exists(ReportsPath));
        Assert.Single(Directory.GetFiles(_directory, "itemreports.json.bad-*"));

        store.Add(new ItemReport { ItemId = Guid.NewGuid(), UserId = Guid.NewGuid(), Problem = ItemProblem.Audio });
        Assert.Single(new ItemReportStore(_appPaths, NullLogger<ItemReportStore>.Instance).GetReports());
    }

    [Fact]
    public void Reports_AreLimitedPerPersonPerHour()
    {
        var time = new UserManagerTestDatabase.TestTimeProvider();
        var store = new ItemReportStore(_appPaths, NullLogger<ItemReportStore>.Instance) { TimeProvider = time };
        var calum = Guid.NewGuid();

        for (var i = 0; i < ItemReportStore.MaxReportsPerHour; i++)
        {
            Assert.True(store.TryCountReport(calum));
            time.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.False(store.TryCountReport(calum));

        // Someone else can still report
        Assert.True(store.TryCountReport(Guid.NewGuid()));

        // An hour after the first one, there is room for one more
        time.Advance(TimeSpan.FromMinutes(40));
        Assert.True(store.TryCountReport(calum));
        Assert.False(store.TryCountReport(calum));
    }
}
