using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.TunerHosts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.LiveTv.Tests;

/// <summary>
/// The chunked live stream buffer: every reader gets the stream's bytes in order, without gaps, from where it joined,
/// while old chunks are deleted behind them.
/// </summary>
public sealed class LiveStreamBufferTests : IDisposable
{
    private readonly string _folder = LiveTvTestHelpers.CreateTempDirectory();
    private readonly string _firstChunk;

    public LiveStreamBufferTests()
    {
        _firstChunk = Path.Combine(_folder, "0123abcd.ts");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void GetChunkPath_AllStartWithTheStreamsId()
    {
        var buffer = new LiveStreamBuffer(_firstChunk, NullLogger.Instance);

        Assert.Equal(_firstChunk, buffer.GetChunkPath(0));
        Assert.Equal(Path.Combine(_folder, "0123abcd.1.ts"), buffer.GetChunkPath(1));
        Assert.Equal(Path.Combine(_folder, "0123abcd.12.ts"), buffer.GetChunkPath(12));
    }

    [Fact]
    public async Task Reader_FromTheStart_ReadsEveryByteAcrossChunks()
    {
        var buffer = SmallChunks();
        using var reader = buffer.CreateReader(0);

        var writer = buffer.CreateWriter();
        await using (writer.ConfigureAwait(false))
        {
            await WritePattern(writer, 0, 100_000, 997);
        }

        var read = await ReadAll(reader, 100_000);

        AssertPattern(read, 0);
    }

    [Fact]
    public async Task Chunks_StartOnPacketBoundaries()
    {
        var buffer = SmallChunks();
        var writer = buffer.CreateWriter();
        await using (writer.ConfigureAwait(false))
        {
            await WritePattern(writer, 0, 50_000, 1000);
        }

        for (var index = 0; File.Exists(buffer.GetChunkPath(index)); index++)
        {
            if (File.Exists(buffer.GetChunkPath(index + 1)))
            {
                Assert.Equal(0, new FileInfo(buffer.GetChunkPath(index)).Length % 188);
            }
        }
    }

    [Fact]
    public async Task NoReaders_OnlyTheLastTwoChunksAreKept()
    {
        var buffer = SmallChunks();
        var writer = buffer.CreateWriter();
        await using (writer.ConfigureAwait(false))
        {
            await WritePattern(writer, 0, 200_000, 4096);

            Assert.Equal(200_000, buffer.BytesWritten);
            Assert.True(buffer.ChunksOnDisk <= 2);
            Assert.True(Directory.GetFiles(_folder).Length <= 2);
        }
    }

    [Fact]
    public async Task ReaderBehind_KeepsItsChunkUntilItReadsOn()
    {
        var buffer = SmallChunks();
        var writer = buffer.CreateWriter();
        await using (writer.ConfigureAwait(false))
        {
            await WritePattern(writer, 0, 1000, 1000);
            using var slow = buffer.CreateReader(0);

            // The writer goes well past the reader, which still has the first chunk to read
            await WritePattern(writer, 1000, 100_000, 4096);
            Assert.True(File.Exists(buffer.GetChunkPath(0)));

            // Reading on lets the chunks behind it go
            var read = await ReadAll(slow, 101_000);
            AssertPattern(read, 0);
            Assert.False(File.Exists(buffer.GetChunkPath(0)));
            Assert.True(buffer.ChunksOnDisk <= 3);
        }
    }

    [Fact]
    public async Task Reader_JoiningBehindTheOldestChunk_StartsAtTheOldestData()
    {
        var buffer = SmallChunks();
        var writer = buffer.CreateWriter();
        await using (writer.ConfigureAwait(false))
        {
            await WritePattern(writer, 0, 100_000, 4096);

            using var reader = buffer.CreateReader(0);
            Assert.True(reader.StartPosition > 0);

            await WritePattern(writer, 100_000, 5000, 1000);
            var read = await ReadAll(reader, (int)(105_000 - reader.StartPosition));
            AssertPattern(read, reader.StartPosition);
        }
    }

    [Fact]
    public async Task ReadersJoiningAtTheLiveEdgeWhileWriting_EachReadsWithoutGaps()
    {
        var buffer = SmallChunks();
        const int Total = 400_000;
        var writer = buffer.CreateWriter();
        var readers = new List<(LiveStreamBuffer.Reader Reader, Task<byte[]> Read)>();

        var writing = Task.Run(
            async () =>
        {
            await using (writer.ConfigureAwait(false))
            {
                var random = new Random(42);
                var written = 0;
                while (written < Total)
                {
                    var count = Math.Min(random.Next(1, 5000), Total - written);
                    await WritePattern(writer, written, count, count);
                    written += count;
                    if (random.Next(10) == 0)
                    {
                        // Spread the writing out, so viewers join all through it
                        await Task.Delay(1);
                    }
                }
            }
        },
            TestContext.Current.CancellationToken);

        // Viewers join at the live edge at random moments, including just as chunks change
        var joinRandom = new Random(7);
        while (!writing.IsCompleted && readers.Count < 40)
        {
            var reader = buffer.CreateReader(buffer.BytesWritten);
            readers.Add((reader, ReadUntilEnd(reader, writing)));
            await Task.Delay(joinRandom.Next(0, 3), TestContext.Current.CancellationToken);
        }

        await writing;
        Assert.True(readers.Count >= 10, $"Only {readers.Count} readers joined");
        foreach (var (reader, read) in readers)
        {
            var bytes = await read;
            Assert.Equal(Total - reader.StartPosition, bytes.Length);
            AssertPattern(bytes, reader.StartPosition);
            await reader.DisposeAsync();
        }

        // With everyone gone only the last chunks are left
        Assert.True(buffer.ChunksOnDisk <= 2);
    }

    [Fact]
    public async Task DeleteAll_RemovesEveryChunk()
    {
        var buffer = SmallChunks();
        var writer = buffer.CreateWriter();
        await using (writer.ConfigureAwait(false))
        {
            await WritePattern(writer, 0, 30_000, 4096);
        }

        Assert.True(buffer.DeleteAll());
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task Reader_BeforeTheWriterStarts_WaitsForData()
    {
        var buffer = SmallChunks();
        using var reader = buffer.CreateReader(0);

        Assert.Equal(0, await reader.ReadAsync(new byte[100], TestContext.Current.CancellationToken));

        var writer = buffer.CreateWriter();
        await using (writer.ConfigureAwait(false))
        {
            await WritePattern(writer, 0, 500, 500);
        }

        AssertPattern(await ReadAll(reader, 500), 0);
    }

    private static byte PatternByte(long position) => (byte)((position * 7) % 251);

    private static async Task WritePattern(Stream writer, long start, int count, int writeSize)
    {
        var data = new byte[count];
        for (var i = 0; i < count; i++)
        {
            data[i] = PatternByte(start + i);
        }

        for (var offset = 0; offset < count; offset += writeSize)
        {
            await writer.WriteAsync(data.AsMemory(offset, Math.Min(writeSize, count - offset)));
        }
    }

    private static void AssertPattern(byte[] bytes, long start)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != PatternByte(start + i))
            {
                Assert.Fail($"Byte {start + i} is wrong, read from {start}");
            }
        }
    }

    private static async Task<byte[]> ReadAll(Stream reader, int count)
    {
        var result = new byte[count];
        var total = 0;
        var idle = 0;
        while (total < count)
        {
            var read = await reader.ReadAsync(result.AsMemory(total, Math.Min(7000, count - total)));
            if (read == 0)
            {
                if (++idle > 1000)
                {
                    Assert.Fail($"Stuck after {total} of {count} bytes");
                }

                await Task.Delay(1);
                continue;
            }

            idle = 0;
            total += read;
        }

        return result;
    }

    private static Task<byte[]> ReadUntilEnd(Stream reader, Task writing)
        => Task.Run(async () =>
        {
            var result = new MemoryStream();
            var chunk = new byte[3000];
            while (true)
            {
                var read = await reader.ReadAsync(chunk);
                if (read > 0)
                {
                    await result.WriteAsync(chunk.AsMemory(0, read));
                    continue;
                }

                if (writing.IsCompleted)
                {
                    // One more read after the writer finished, in case it wrote just before
                    read = await reader.ReadAsync(chunk);
                    if (read == 0)
                    {
                        return result.ToArray();
                    }

                    await result.WriteAsync(chunk.AsMemory(0, read));
                    continue;
                }

                await Task.Delay(1);
            }
        });

    // Chunks change every 16 kB (and after 50 ms), so a test crosses many of them
    private LiveStreamBuffer SmallChunks()
        => new(_firstChunk, NullLogger.Instance) { MaxChunkBytes = 16 * 1024, ChunkDuration = TimeSpan.FromMilliseconds(50) };
}
