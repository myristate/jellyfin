using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Configuration;
using Moq;

namespace Jellyfin.LiveTv.Tests;

/// <summary>
/// Shared set up for the live stream tests.
/// </summary>
internal static class LiveTvTestHelpers
{
    public static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "finly-livetv-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static Mock<IConfigurationManager> CreateConfig(string transcodePath)
    {
        var config = new Mock<IConfigurationManager>();
        config.Setup(c => c.GetConfiguration("encoding")).Returns(new EncodingOptions { TranscodingTempPath = transcodePath });
        config.Setup(c => c.CommonApplicationPaths).Returns(new Mock<IApplicationPaths>().Object);
        return config;
    }

    public static Mock<IServerApplicationHost> CreateAppHost()
    {
        var appHost = new Mock<IServerApplicationHost>();
        appHost.Setup(a => a.GetApiUrlForLocalAccess(It.IsAny<IPAddress>(), It.IsAny<bool>())).Returns("http://127.0.0.1:8096");
        return appHost;
    }

    public static IHttpClientFactory CreateHttpClientFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(new DelegateHandler(send)));
        return factory.Object;
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _send(request, cancellationToken);
    }
}
