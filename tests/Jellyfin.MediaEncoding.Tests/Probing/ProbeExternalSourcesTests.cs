using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.MediaEncoding.Encoder;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.Probing
{
    public class ProbeExternalSourcesTests
    {
        [Fact]
        public void GetExtraArguments_Forwards_UserAgent()
        {
            var encoder = new MediaEncoder(
                Mock.Of<ILogger<MediaEncoder>>(),
                Mock.Of<IServerConfigurationManager>(),
                Mock.Of<IFileSystem>(),
                Mock.Of<IBlurayExaminer>(),
                Mock.Of<ILocalizationManager>(),
                new ConfigurationBuilder().Build(),
                Mock.Of<IServerConfigurationManager>());

            var userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            var req = new MediaBrowser.Controller.MediaEncoding.MediaInfoRequest()
            {
                MediaSource = new MediaBrowser.Model.Dto.MediaSourceInfo
                {
                    Path = "/path/to/stream",
                    Protocol = MediaProtocol.Http,
                    RequiredHttpHeaders = new Dictionary<string, string>()
                    {
                        { "User-Agent", userAgent },
                    }
                },
                ExtractChapters = false,
                MediaType = MediaBrowser.Model.Dlna.DlnaProfileType.Video,
            };

            var extraArg = encoder.GetExtraArguments(req);

            Assert.Contains($"-user_agent \"{userAgent}\"", extraArg, StringComparison.InvariantCulture);
        }

        [Theory]
        [InlineData(true, "-probesize 10M")]
        [InlineData(false, "-probesize 1G")]
        public void GetExtraArguments_LiveStream_CapsProbeSize(bool isInfiniteStream, string expected)
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { { "FFmpeg:probesize", "1G" } })
                .Build();
            var encoder = new MediaEncoder(
                Mock.Of<ILogger<MediaEncoder>>(),
                Mock.Of<IServerConfigurationManager>(),
                Mock.Of<IFileSystem>(),
                Mock.Of<IBlurayExaminer>(),
                Mock.Of<ILocalizationManager>(),
                configuration,
                Mock.Of<IServerConfigurationManager>());

            var req = new MediaBrowser.Controller.MediaEncoding.MediaInfoRequest()
            {
                MediaSource = new MediaBrowser.Model.Dto.MediaSourceInfo
                {
                    Path = "http://localhost/LiveTv/LiveStreamFiles/1/stream.ts",
                    Protocol = MediaProtocol.Http,
                    IsInfiniteStream = isInfiniteStream,
                    AnalyzeDurationMs = 1500,
                },
                ExtractChapters = false,
                MediaType = MediaBrowser.Model.Dlna.DlnaProfileType.Video,
            };

            var extraArg = encoder.GetExtraArguments(req);

            Assert.Contains(expected, extraArg, StringComparison.InvariantCulture);
            Assert.Contains("-analyzeduration 1500000", extraArg, StringComparison.InvariantCulture);
        }
    }
}
