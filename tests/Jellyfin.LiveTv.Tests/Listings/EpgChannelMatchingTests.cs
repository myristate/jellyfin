using Jellyfin.LiveTv.Listings;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Jellyfin.LiveTv.Tests.Listings;

public class EpgChannelMatchingTests
{
    [Theory]
    [InlineData("BBC NEWS", "BBC NEWS HD")]
    [InlineData("CBBC HD", "CBBC")]
    [InlineData("HobbyMaker", "Hobby Maker HD")]
    [InlineData("That's TV", "Thats TV")]
    [InlineData("Great! Movies", "GREAT! Movies")]
    [InlineData("Sky News", "Sky News HD")]
    public void NormalizeName_MatchesTheSameChannel(string tunerName, string guideName)
    {
        Assert.Equal(EpgChannelData.NormalizeName(guideName), EpgChannelData.NormalizeName(tunerName));
    }

    [Theory]
    [InlineData("Channel 4+1", "Channel 4")]
    [InlineData("STV+1", "STV")]
    [InlineData("Great! TV +1", "GREAT! tv")]
    public void NormalizeName_KeepsTimeshiftChannelsApart(string tunerName, string guideName)
    {
        Assert.NotEqual(EpgChannelData.NormalizeName(guideName), EpgChannelData.NormalizeName(tunerName));
    }

    [Fact]
    public void Matching_ByName_FindsTheHdGuideChannel()
    {
        var guide = new EpgChannelData([new ChannelInfo { Id = "BBCNews.uk", Name = "BBC NEWS HD" }]);
        var tuner = new ChannelInfo { Id = "hdhr_231", Number = "231", Name = "BBC NEWS" };

        var match = ListingsManager.GetEpgChannelFromTunerChannel([], tuner, guide);

        Assert.Equal("BBCNews.uk", match?.Id);
    }

    [Fact]
    public void Matching_ByName_PrefersTheExactName()
    {
        var guide = new EpgChannelData([
            new ChannelInfo { Id = "Film4HD.uk", Name = "Film4 HD" },
            new ChannelInfo { Id = "Film4.uk", Name = "Film4" }
        ]);
        var tuner = new ChannelInfo { Id = "hdhr_14", Number = "14", Name = "Film4" };

        Assert.Equal("Film4.uk", ListingsManager.GetEpgChannelFromTunerChannel([], tuner, guide)?.Id);
    }

    [Fact]
    public void LinkedOnlySource_IgnoresNumbersAndNames()
    {
        // A gap-filling source numbers its channels its own way: its "5" is not the tuner's channel 5
        var guide = new EpgChannelData([
            new ChannelInfo { Id = "I5.83545.schedulesdirect.org", Number = "5", Name = "3sat HD" },
            new ChannelInfo { Id = "I860.100263.schedulesdirect.org", Number = "860", Name = "Pop" }
        ]);

        var channel5 = new ChannelInfo { Id = "hdhr_5", Number = "5", Name = "5" };
        var pop = new ChannelInfo { Id = "hdhr_205", Number = "205", Name = "POP" };
        var links = new[] { new NameValuePair("hdhr_205", "I860.100263.schedulesdirect.org") };

        Assert.Null(ListingsManager.GetEpgChannelFromTunerChannel(links, channel5, guide, true));
        Assert.Equal("I860.100263.schedulesdirect.org", ListingsManager.GetEpgChannelFromTunerChannel(links, pop, guide, true)?.Id);
    }

    [Fact]
    public void LinkedOnlySource_WithoutALink_GivesNothingEvenForTheSameName()
    {
        var guide = new EpgChannelData([new ChannelInfo { Id = "I1000.115047.schedulesdirect.org", Name = "Nosey" }]);
        var nosey = new ChannelInfo { Id = "hdhr_278", Number = "278", Name = "Nosey" };

        Assert.Null(ListingsManager.GetEpgChannelFromTunerChannel([], nosey, guide, true));
    }
}
