using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Guide;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests;

public sealed class PrepareChannelsScheduledTaskTests : IDisposable
{
    private readonly string _cachePath = LiveTvTestHelpers.CreateTempDirectory();

    public void Dispose()
    {
        Directory.Delete(_cachePath, true);
    }

    [Fact]
    public async Task ExecuteAsync_TunerFailsOnOneChannel_CarriesOnWithoutClosingAnyonesStream()
    {
        var channels = new List<BaseItem>
        {
            new LiveTvChannel { Id = Guid.NewGuid(), Name = "BBC ONE" },
            new LiveTvChannel { Id = Guid.NewGuid(), Name = "BBC TWO" },
            new LiveTvChannel { Id = Guid.NewGuid(), Name = "ITV1" }
        };

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(channels);

        var opened = new List<(string Token, LiveStreamOpenOptions Options)>();
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager
            .Setup(m => m.GetPlaybackMediaSources(It.IsAny<BaseItem>(), It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync((BaseItem item, Jellyfin.Database.Implementations.Entities.User _, bool _, bool _, CancellationToken _) =>
                [new MediaSourceInfo { RequiresOpening = true, OpenToken = "token_" + item.Name }]);
        mediaSourceManager
            .Setup(m => m.OpenLiveStreamInternal(It.IsAny<LiveStreamRequest>(), It.IsAny<LiveStreamOpenOptions>(), It.IsAny<CancellationToken>()))
            .Returns((LiveStreamRequest request, LiveStreamOpenOptions options, CancellationToken _) =>
            {
                opened.Add((request.OpenToken, options));
                if (request.OpenToken == "token_BBC TWO")
                {
                    throw new LiveTvTunerException("The tuner didn't respond in time", true);
                }

                var source = new MediaSourceInfo { LiveStreamId = "live_" + request.OpenToken };
                return Task.FromResult(new Tuple<LiveStreamResponse, IDirectStreamProvider>(new LiveStreamResponse(source), null!));
            });

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(a => a.CachePath).Returns(_cachePath);

        var task = new PrepareChannelsScheduledTask(
            libraryManager.Object,
            mediaSourceManager.Object,
            Mock.Of<ILiveTvManager>(),
            Mock.Of<IConfigurationManager>(),
            appPaths.Object,
            NullLogger<PrepareChannelsScheduledTask>.Instance);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(3, opened.Count);
        Assert.All(opened, o => Assert.False(o.Options.CloseIdleStreamsWhenBusy));
        mediaSourceManager.Verify(m => m.CloseLiveStream("live_token_BBC ONE", true), Times.Once);
        mediaSourceManager.Verify(m => m.CloseLiveStream("live_token_ITV1", true), Times.Once);
        mediaSourceManager.Verify(m => m.CloseLiveStream(It.IsAny<string>(), It.IsAny<bool>()), Times.Exactly(2));
    }
}
