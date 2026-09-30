using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Server.Implementations.Tests.Library.LiveStreams;

/// <summary>
/// A media source provider whose tuner does whatever the test says, per channel.
/// </summary>
public class FakeMediaSourceProvider : IMediaSourceProvider
{
    private readonly ConcurrentDictionary<string, int> _calls = new();

    /// <summary>
    /// Gets or sets how a channel is opened: given the channel and the attempt number, returns the stream or throws.
    /// </summary>
    public Func<string, int, CancellationToken, Task<ILiveStream>> OpenChannel { get; set; }
        = (channel, _, _) => Task.FromResult<ILiveStream>(new FakeLiveStream(channel));

    public int TotalCalls => _calls.Values.Sum();

    public static string OpenToken(string channel)
        => typeof(FakeMediaSourceProvider).FullName!.GetMD5().ToString("N", CultureInfo.InvariantCulture) + "_" + channel;

    public int Calls(string channel) => _calls.GetValueOrDefault(channel);

    public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        => Task.FromResult(Enumerable.Empty<MediaSourceInfo>());

    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        var attempt = _calls.AddOrUpdate(openToken, 1, (_, value) => value + 1);
        return OpenChannel(openToken, attempt, cancellationToken);
    }
}
