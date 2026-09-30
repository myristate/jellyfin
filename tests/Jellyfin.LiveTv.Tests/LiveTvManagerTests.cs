using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using AutoFixture;
using AutoFixture.AutoMoq;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests;

public class LiveTvManagerTests
{
    [Fact]
    public void GetInternalLiveTvFolder_NamesTheViewTv()
    {
        var fixture = new Fixture().Customize(new AutoMoqCustomization());
        var libraryManager = fixture.Freeze<Mock<ILibraryManager>>();
        var view = new UserView { Id = Guid.NewGuid(), Name = "TV" };
        libraryManager
            .Setup(m => m.GetNamedView(It.IsAny<string>(), CollectionType.livetv, It.IsAny<string>()))
            .Returns(view);

        var config = fixture.Freeze<Mock<IConfigurationManager>>();
        config.Setup(c => c.CommonApplicationPaths.DataPath).Returns(Path.GetTempPath());
        fixture.Inject<IEnumerable<ILiveTvService>>([fixture.Create<DefaultLiveTvService>()]);
        var manager = fixture.Create<LiveTvManager>();

        var folder = manager.GetInternalLiveTvFolder(CancellationToken.None);

        Assert.Same(view, folder);
        libraryManager.Verify(m => m.GetNamedView("TV", CollectionType.livetv, "TV"), Times.Once);
    }
}
