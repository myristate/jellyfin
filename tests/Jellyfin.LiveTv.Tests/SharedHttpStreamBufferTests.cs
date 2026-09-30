using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.IO;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests;

/// <summary>
/// A tuner stream end to end: viewers joining at the live edge while the buffer moves through its chunks.
/// </summary>
public sealed class SharedHttpStreamBufferTests : IDisposable
{
    private readonly string _transcodePath = LiveTvTestHelpers.CreateTempDirectory();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_transcodePath, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ViewersJoiningWhileChunksChange_EachGetTheStreamWithoutGaps_AndTheBufferStaysSmall()
    {
        using var liveStream = new SharedHttpStream(
            new MediaSourceInfo { Path = "http://tuner:5004/auto/v1", Protocol = MediaProtocol.Http },
            new TunerHostInfo { Id = "tuner", Url = "http://tuner" },
            "native_1",
            new Mock<IFileSystem>().Object,
            LiveTvTestHelpers.CreateHttpClientFactory((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CountingTunerStream()) })),
            NullLogger.Instance,
            LiveTvTestHelpers.CreateConfig(_transcodePath).Object,
            LiveTvTestHelpers.CreateAppHost().Object,
            new StreamHelper());
        liveStream.Buffer.MaxChunkBytes = 64 * 1024;
        liveStream.Buffer.ChunkDuration = TimeSpan.FromMilliseconds(200);

        await liveStream.Open(TestContext.Current.CancellationToken);

        // Viewers join through the first 3.5 seconds: at first from the start, later a couple of seconds behind live
        var viewers = new List<Task<int>>();
        var maxChunkFiles = 0;
        for (var i = 0; i < 14; i++)
        {
            viewers.Add(Watch(liveStream, TimeSpan.FromMilliseconds(700)));
            maxChunkFiles = Math.Max(maxChunkFiles, Directory.GetFiles(_transcodePath).Length);
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        var counters = await Task.WhenAll(viewers);
        Assert.All(counters, c => Assert.True(c > 1000, $"A viewer only got {c} counters"));
        Assert.True(liveStream.Buffer.BytesWritten > 4 * 64 * 1024, "The buffer should have moved through several chunks");

        // Viewers keep up, so only the chunks around the live edge are ever on disk
        Assert.InRange(maxChunkFiles, 1, 4);
        Assert.Equal(0, liveStream.ActiveReaderCount);

        await liveStream.Close();

        // Once the tuner stream stops the whole buffer goes
        for (var i = 0; i < 100 && Directory.GetFiles(_transcodePath).Length > 0; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Empty(Directory.GetFiles(_transcodePath));
    }

    // Reads the stream as a viewer would for a while, checking nothing is skipped or repeated
    private static Task<int> Watch(SharedHttpStream liveStream, TimeSpan duration)
        => Task.Run(async () =>
        {
            var reader = liveStream.GetStream();
            await using (reader.ConfigureAwait(false))
            {
                var received = new MemoryStream();
                var chunk = new byte[5000];
                var until = DateTime.UtcNow + duration;
                while (DateTime.UtcNow < until)
                {
                    var read = await reader.ReadAsync(chunk);
                    if (read == 0)
                    {
                        await Task.Delay(5);
                        continue;
                    }

                    await received.WriteAsync(chunk.AsMemory(0, read));
                }

                return CountingTunerStream.AssertConsecutive(received.ToArray());
            }
        });
}
