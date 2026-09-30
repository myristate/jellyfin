using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// A reader of a live stream's buffer (Finly). The stream counts it as reading until it is disposed, which happens when
/// whoever reads it goes away: ffmpeg's request ends, a client disconnects, a recording stops.
/// </summary>
internal sealed class LiveStreamReader : Stream
{
    private readonly Stream _inner;
    private Action? _onDisposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveStreamReader"/> class.
    /// </summary>
    /// <param name="inner">The stream read from.</param>
    /// <param name="onDisposed">Called once, when the reader is disposed.</param>
    public LiveStreamReader(Stream inner, Action onDisposed)
    {
        _inner = inner;
        _onDisposed = onDisposed;
    }

    /// <inheritdoc />
    public override bool CanRead => _inner.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => _inner.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _inner.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    /// <inheritdoc />
    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => _inner.ReadAsync(buffer, offset, count, cancellationToken);

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => _inner.ReadAsync(buffer, cancellationToken);

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            Interlocked.Exchange(ref _onDisposed, null)?.Invoke();
        }

        base.Dispose(disposing);
    }
}
