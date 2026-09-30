using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Server.Implementations.Tests.Library.LiveStreams;

/// <summary>
/// A live stream that records what was done to it.
/// </summary>
public sealed class FakeLiveStream : ILiveStream
{
    private static int _next;

    public FakeLiveStream(string liveStreamId)
    {
        var number = Interlocked.Increment(ref _next);
        UniqueId = "fake" + number.ToString(CultureInfo.InvariantCulture);
        MediaSource = new MediaSourceInfo
        {
            Id = liveStreamId,
            LiveStreamId = liveStreamId,
            MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Index = 0, Width = 720, Height = 576 }],
            IsInfiniteStream = true
        };
    }

    public int ConsumerCount { get; set; } = 1;

    public string OriginalStreamId { get; set; } = string.Empty;

    public string TunerHostId => "tuner";

    public bool EnableStreamSharing { get; set; } = true;

    public MediaSourceInfo MediaSource { get; set; }

    public string UniqueId { get; }

    public int CloseCount { get; private set; }

    public bool IsClosed => CloseCount > 0;

    public Task Open(CancellationToken openCancellationToken) => Task.CompletedTask;

    public Task Close()
    {
        CloseCount++;
        EnableStreamSharing = false;
        return Task.CompletedTask;
    }

    public Stream GetStream() => new MemoryStream();

    public void Dispose()
    {
    }
}
