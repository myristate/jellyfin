using Jellyfin.LiveTv.TunerHosts.HdHomerun;
using Xunit;

namespace Jellyfin.LiveTv.Tests;

public class HdHomerunTunerStateTests
{
    // As the HDHomeRun FLEX QUATRO reports it, one tuner on Channel 4 and three idle
    private const string OneTuned = """
        [
          {"Resource":"tuner0","VctNumber":"4","VctName":"Channel 4","Frequency":650000000,"SignalStrengthPercent":83,"SignalQualityPercent":100,"SymbolQualityPercent":100,"TargetIP":"192.168.1.249","NetworkRate":1601504},
          {"Resource":"tuner1"},
          {"Resource":"tuner2"},
          {"Resource":"tuner3"}
        ]
        """;

    [Fact]
    public void ParseStatus_TunedTuner_HasChannelFrequencyAndSignal()
    {
        var tuners = HdHomerunTunerState.ParseStatus(OneTuned);

        Assert.NotNull(tuners);
        Assert.Equal(4, tuners.Count);
        var tuned = tuners[0];
        Assert.Equal("tuner0", tuned.Resource);
        Assert.Equal("4", tuned.ChannelNumber);
        Assert.Equal("Channel 4", tuned.ChannelName);
        Assert.Equal(650000000, tuned.Frequency);
        Assert.Equal(83, tuned.SignalStrength);
        Assert.Equal(100, tuned.SignalQuality);
        Assert.Equal(100, tuned.SymbolQuality);
        Assert.Equal("192.168.1.249", tuned.TargetIp);
        Assert.True(tuned.IsInUse);
        Assert.True(tuned.HasSignal);
    }

    [Fact]
    public void ParseStatus_IdleTuner_HasNothing()
    {
        var idle = HdHomerunTunerState.ParseStatus(OneTuned)![1];

        Assert.Equal("tuner1", idle.Resource);
        Assert.Null(idle.ChannelNumber);
        Assert.Null(idle.Frequency);
        Assert.Null(idle.SignalStrength);
        Assert.Null(idle.SignalQuality);
        Assert.Null(idle.SymbolQuality);
        Assert.False(idle.IsInUse);
        Assert.False(idle.HasSignal);
    }

    [Fact]
    public void ParseStatus_MissingAndOddFields_AreUnknown()
    {
        var tuners = HdHomerunTunerState.ParseStatus("""
            [
              {"Resource":"tuner0","VctNumber":"","Frequency":0,"SignalStrengthPercent":"77","SignalQualityPercent":250},
              {"Resource":"tuner1","VctNumber":7,"SignalQualityPercent":null,"SymbolQualityPercent":"x"}
            ]
            """);

        Assert.NotNull(tuners);
        Assert.Null(tuners[0].ChannelNumber);
        Assert.Null(tuners[0].Frequency);
        Assert.Equal(77, tuners[0].SignalStrength);
        Assert.Null(tuners[0].SignalQuality);
        Assert.Equal("7", tuners[1].ChannelNumber);
        Assert.Null(tuners[1].SignalQuality);
        Assert.Null(tuners[1].SymbolQuality);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""[{"Resource":"record"}]""")]
    public void ParseStatus_Unreadable_Null(string json)
    {
        Assert.Null(HdHomerunTunerState.ParseStatus(json));
    }
}
