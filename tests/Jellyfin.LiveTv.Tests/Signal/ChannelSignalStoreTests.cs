using System;
using System.IO;
using Jellyfin.LiveTv.Signal;
using Jellyfin.LiveTv.TunerHosts.HdHomerun;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static Jellyfin.LiveTv.Signal.ChannelSignalStore;

namespace Jellyfin.LiveTv.Tests.Signal;

public sealed class ChannelSignalStoreTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 1, 8, 0, 0, 500, DateTimeKind.Utc);

    private readonly string _directory = LiveTvTestHelpers.CreateTempDirectory();

    private string FilePath => Path.Combine(_directory, "channel-signal.json");

    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData(84, 100, false)]
    [InlineData(84, 80, false)]
    [InlineData(84, 79, true)]
    [InlineData(95, 10, true)]
    [InlineData(10, 100, false)]
    [InlineData(39, null, true)]
    [InlineData(40, null, false)]
    [InlineData(null, 79, true)]
    [InlineData(null, null, false)]
    public void IsWeak_QualityUnder80_OrStrengthUnder40WhenQualityUnknown(int? strength, int? quality, bool weak)
    {
        Assert.Equal(weak, ChannelSignalStore.IsWeak(strength, quality));
    }

    [Fact]
    public void RecordLineup_SeedsScanValues_SkipsChannelsWithout()
    {
        var store = CreateStore();

        store.RecordLineup([new LineupSignal("1", 84, 100), new LineupSignal("78", null, null), new LineupSignal("9", 30, null)], Now);

        var signal = store.Get("1");
        Assert.NotNull(signal);
        Assert.Equal(84, signal.Strength);
        Assert.Equal(100, signal.Quality);
        Assert.Null(signal.SymbolQuality);
        Assert.False(signal.IsLive);
        Assert.False(signal.IsWeak);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), signal.MeasuredAt);
        Assert.Equal(DateTimeKind.Utc, signal.MeasuredAt.Kind);
        Assert.Null(store.Get("78"));
        Assert.True(store.Get("9")!.IsWeak);
        Assert.Equal(2, store.GetAll().Count);
    }

    [Fact]
    public void RecordStatus_TunedTuner_GivesLiveReadingAndFrequency()
    {
        var store = CreateStore();
        store.RecordLineup([new LineupSignal("4", 82, 100)], Now);

        store.RecordStatus([Tuned("4", 650000000, 83, 75, 90), Idle()], Now.AddMinutes(1));

        var signal = store.Get("4")!;
        Assert.True(signal.IsLive);
        Assert.Equal(83, signal.Strength);
        Assert.Equal(75, signal.Quality);
        Assert.Equal(90, signal.SymbolQuality);
        Assert.True(signal.IsWeak);
        Assert.Equal(Now.AddMinutes(1).AddMilliseconds(-500), signal.MeasuredAt);
        Assert.Equal(650000000, store.GetFrequency("4"));
    }

    [Fact]
    public void RecordStatus_AppliesReadingToChannelsOnTheSameMultiplex()
    {
        var store = CreateStore();
        store.RecordLineup([new LineupSignal("1", 84, 100), new LineupSignal("2", 84, 100), new LineupSignal("3", 82, 100)], Now);

        // Channels 1 and 2 are seen on the same frequency, 3 on another
        store.RecordStatus([Tuned("1", 490000000, 80, 100, 100), Tuned("3", 650000000, 81, 100, 100)], Now);
        store.RecordStatus([Tuned("2", 490000000, 80, 100, 100)], Now);

        // A later reading on channel 2 is channel 1's too, but not channel 3's
        store.RecordStatus([Tuned("2", 490000000, 60, 70, 65)], Now.AddMinutes(5));

        var one = store.Get("1")!;
        Assert.True(one.IsLive);
        Assert.Equal(70, one.Quality);
        Assert.Equal(65, one.SymbolQuality);
        Assert.Equal(Now.AddMinutes(5).AddMilliseconds(-500), one.MeasuredAt);
        Assert.Equal(100, store.Get("3")!.Quality);
    }

    [Fact]
    public void RecordStatus_TunedByFrequencyOnly_AppliesToKnownChannelsOnIt()
    {
        var store = CreateStore();
        store.RecordStatus([Tuned("5", 650000000, 80, 100, 100)], Now);

        store.RecordStatus([Tuned(null, 650000000, 50, 60, 55)], Now.AddMinutes(1));

        Assert.Equal(60, store.Get("5")!.Quality);
    }

    [Fact]
    public void RecordStatus_TunerWithoutSignal_Ignored()
    {
        var store = CreateStore();

        store.RecordStatus([new HdHomerunTunerState("tuner0", "4", "Channel 4", 650000000, null, null, null, "192.168.1.249")], Now);

        Assert.Null(store.Get("4"));
        Assert.Null(store.GetFrequency("4"));
    }

    [Fact]
    public void RecordLineup_DoesNotReplaceARecentLiveReading()
    {
        var store = CreateStore();
        store.RecordStatus([Tuned("4", 650000000, 83, 70, 90)], Now);

        store.RecordLineup([new LineupSignal("4", 82, 100)], Now.AddHours(5));

        Assert.True(store.Get("4")!.IsLive);
        Assert.Equal(70, store.Get("4")!.Quality);
    }

    [Fact]
    public void RecordLineup_ReplacesAnOldLiveReading()
    {
        var store = CreateStore();
        store.RecordStatus([Tuned("4", 650000000, 83, 70, 90)], Now);

        store.RecordLineup([new LineupSignal("4", 82, 100)], Now + LiveReadingPreferredFor + TimeSpan.FromMinutes(1));

        Assert.False(store.Get("4")!.IsLive);
        Assert.Equal(100, store.Get("4")!.Quality);
    }

    [Fact]
    public void Flush_SavedReadingsAndFrequencies_SurviveARestart()
    {
        var store = CreateStore();
        store.RecordLineup([new LineupSignal("1", 84, 100), new LineupSignal("2", 84, 100)], Now);
        store.RecordStatus([Tuned("1", 490000000, 80, 79, 85)], Now);
        store.Flush();

        var reloaded = CreateStore();

        var one = reloaded.Get("1")!;
        Assert.True(one.IsLive);
        Assert.Equal(80, one.Strength);
        Assert.Equal(79, one.Quality);
        Assert.Equal(85, one.SymbolQuality);
        Assert.True(one.IsWeak);
        Assert.Equal(store.Get("1")!.MeasuredAt, one.MeasuredAt);
        Assert.Equal(DateTimeKind.Utc, one.MeasuredAt.Kind);
        Assert.False(reloaded.Get("2")!.IsLive);
        Assert.Equal(490000000, reloaded.GetFrequency("1"));
    }

    [Fact]
    public void RecordStatus_NewFrequency_SavedAtOnce()
    {
        var store = CreateStore();

        store.RecordStatus([Tuned("4", 650000000, 83, 100, 100)], Now);

        Assert.True(File.Exists(FilePath));
        Assert.Equal(650000000, CreateStore().GetFrequency("4"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"Readings":[1]}""")]
    [InlineData("""{"Frequencies":{"4":"x"}}""")]
    public void Load_CorruptFile_StartsAfresh(string content)
    {
        File.WriteAllText(FilePath, content);

        var store = CreateStore();

        Assert.Empty(store.GetAll());
        store.RecordStatus([Tuned("4", 650000000, 83, 100, 100)], Now);
        store.Flush();
        Assert.Equal(100, CreateStore().Get("4")!.Quality);
    }

    [Fact]
    public void Load_DropsReadingsThatMakeNoSense()
    {
        File.WriteAllText(FilePath, """
            {"Frequencies":{"4":650000000,"5":-1},
             "Readings":{"4":{"Strength":83,"Quality":100,"MeasuredAt":"2026-10-01T08:00:00.0000000Z","IsLive":true},
                         "5":{"Strength":500,"Quality":100,"MeasuredAt":"2026-10-01T08:00:00.0000000Z","IsLive":true},
                         "6":{"MeasuredAt":"2026-10-01T08:00:00.0000000Z","IsLive":false}}}
            """);

        var store = CreateStore();

        Assert.Equal(83, store.Get("4")!.Strength);
        Assert.Null(store.Get("5"));
        Assert.Null(store.Get("6"));
        Assert.Null(store.GetFrequency("5"));
    }

    private static HdHomerunTunerState Tuned(string? channel, long frequency, int strength, int quality, int symbolQuality)
        => new("tuner0", channel, null, frequency, strength, quality, symbolQuality, "192.168.1.249");

    private static HdHomerunTunerState Idle() => new("tuner1", null, null, null, null, null, null, null);

    private ChannelSignalStore CreateStore() => new(FilePath, NullLogger.Instance);
}
