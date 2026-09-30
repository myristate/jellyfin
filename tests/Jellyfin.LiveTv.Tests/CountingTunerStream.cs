using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.LiveTv.Tests;

/// <summary>
/// A tuner's response body that never ends: 4 byte big endian counters, 0, 1, 2 and so on, a few kilobytes at a time.
/// A reader starting at any multiple of 4 can check it got every counter in order.
/// </summary>
internal sealed class CountingTunerStream : Stream
{
    private uint _next;
    private int _partial;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>
    /// Checks that data read from a multiple of 4 bytes into the stream holds consecutive counters.
    /// </summary>
    /// <param name="data">The data.</param>
    /// <returns>The number of counters checked.</returns>
    public static int AssertConsecutive(ReadOnlySpan<byte> data)
    {
        var count = data.Length / 4;
        if (count == 0)
        {
            return 0;
        }

        var first = BinaryPrimitives.ReadUInt32BigEndian(data);
        for (var i = 1; i < count; i++)
        {
            var value = BinaryPrimitives.ReadUInt32BigEndian(data[(i * 4)..]);
            if (value != first + (uint)i)
            {
                throw new InvalidDataException($"Counter {i} is {value}, expected {first + (uint)i}: data was skipped or repeated");
            }
        }

        return count;
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(1, cancellationToken).ConfigureAwait(false);

        // An odd amount each time, so writes don't line up with anything
        var length = Math.Min(buffer.Length, 3001);
        var span = buffer.Span;
        Span<byte> word = stackalloc byte[4];
        for (var i = 0; i < length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(word, _next);
            span[i] = word[_partial];
            if (++_partial == 4)
            {
                _partial = 0;
                _next++;
            }
        }

        return length;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
