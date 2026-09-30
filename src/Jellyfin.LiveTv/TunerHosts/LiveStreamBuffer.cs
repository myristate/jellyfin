using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts;

/// <summary>
/// A live stream's buffer on disk, kept to a bounded size (Finly).
/// </summary>
/// <remarks>
/// <para>What the tuner sends used to go into one file that grew for as long as the stream was open, about 1 GB an hour
/// for an SD channel. It is now written in chunks, a new chunk every <see cref="ChunkDuration"/>, and a chunk is deleted
/// once no reader is still in it and the writer is two chunks further on. With everyone at the live edge that is at most
/// two chunks on disk; a reader that falls far behind keeps its chunk until it reads on or goes away.</para>
/// <para>To readers the chunks are one continuous stream. A reader reaching the end of a chunk moves to the next only
/// once the writer has finished the chunk and started the next one, so no data is skipped. Chunks start on transport
/// stream packet boundaries.</para>
/// </remarks>
internal sealed class LiveStreamBuffer
{
    /// <summary>
    /// How long each chunk covers.
    /// </summary>
    internal static readonly TimeSpan DefaultChunkDuration = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The most a chunk may hold, whatever its duration: 10 minutes at 13 Mbps.
    /// </summary>
    internal const long DefaultMaxChunkBytes = 1024L * 1024 * 1024;

    private const int TsPacketSize = 188;

    private readonly string _firstChunkPath;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();

    // Where each chunk starts in the stream, by chunk index
    private readonly List<long> _chunkStarts = [];

    // The chunk each reader is in
    private readonly Dictionary<Reader, int> _readers = [];

    private int _writerChunk = -1;
    private int _oldestChunk;
    private long _bytesWritten;
    private bool _writerFinished;

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveStreamBuffer"/> class.
    /// </summary>
    /// <param name="firstChunkPath">The first chunk's path, such as /transcodes/{id}.ts; later chunks are /transcodes/{id}.1.ts
    /// and so on, so every chunk's name starts with the stream's id.</param>
    /// <param name="logger">The logger.</param>
    public LiveStreamBuffer(string firstChunkPath, ILogger logger)
    {
        _firstChunkPath = firstChunkPath;
        _logger = logger;
    }

    /// <summary>
    /// Gets or sets how long each chunk covers.
    /// </summary>
    internal TimeSpan ChunkDuration { get; set; } = DefaultChunkDuration;

    /// <summary>
    /// Gets or sets the most a chunk may hold.
    /// </summary>
    internal long MaxChunkBytes { get; set; } = DefaultMaxChunkBytes;

    /// <summary>
    /// Gets how many bytes have been written.
    /// </summary>
    public long BytesWritten
    {
        get
        {
            lock (_lock)
            {
                return _bytesWritten;
            }
        }
    }

    /// <summary>
    /// Gets the chunks on disk.
    /// </summary>
    internal int ChunksOnDisk
    {
        get
        {
            lock (_lock)
            {
                return _writerChunk < 0 ? 0 : _writerChunk - _oldestChunk + 1;
            }
        }
    }

    /// <summary>
    /// Gets the path of a chunk.
    /// </summary>
    /// <param name="index">The chunk's index.</param>
    /// <returns>The path.</returns>
    internal string GetChunkPath(int index)
    {
        if (index == 0)
        {
            return _firstChunkPath;
        }

        var directory = Path.GetDirectoryName(_firstChunkPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(_firstChunkPath);
        return Path.Combine(directory, name + "." + index.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(_firstChunkPath));
    }

    /// <summary>
    /// Creates the stream the tuner's data is written to. There is one writer.
    /// </summary>
    /// <returns>The writer.</returns>
    public Stream CreateWriter() => new Writer(this);

    /// <summary>
    /// Creates a reader starting at a position in the stream, or at the oldest data still kept if that is gone.
    /// </summary>
    /// <param name="position">The position, from the start of the stream.</param>
    /// <returns>The reader.</returns>
    public Reader CreateReader(long position)
    {
        lock (_lock)
        {
            position = Math.Clamp(position, _oldestChunk < _chunkStarts.Count ? _chunkStarts[_oldestChunk] : 0, _bytesWritten);
            var chunk = Math.Max(_oldestChunk, FindChunk(position));
            var offset = chunk < _chunkStarts.Count ? position - _chunkStarts[chunk] : 0;
            var reader = new Reader(this, chunk, offset, position);
            _readers[reader] = chunk;
            return reader;
        }
    }

    /// <summary>
    /// Deletes every chunk, when the stream has ended.
    /// </summary>
    /// <returns>Whether they are all gone.</returns>
    public bool DeleteAll()
    {
        int last;
        lock (_lock)
        {
            last = _writerChunk;
        }

        var allDeleted = true;
        for (var index = 0; index <= last; index++)
        {
            allDeleted &= TryDelete(GetChunkPath(index));
        }

        return allDeleted;
    }

    // The last chunk starting at or before the position
    private int FindChunk(long position)
    {
        var chunk = 0;
        for (var i = 0; i < _chunkStarts.Count; i++)
        {
            if (_chunkStarts[i] <= position)
            {
                chunk = i;
            }
        }

        return chunk;
    }

    private bool IsChunkFinished(int chunk)
    {
        lock (_lock)
        {
            return chunk < _writerChunk || (_writerFinished && chunk == _writerChunk);
        }
    }

    private bool HasChunk(int chunk)
    {
        lock (_lock)
        {
            return chunk <= _writerChunk;
        }
    }

    private void MoveReader(Reader reader, int chunk)
    {
        lock (_lock)
        {
            if (_readers.ContainsKey(reader))
            {
                _readers[reader] = chunk;
            }
        }

        DeleteOldChunks();
    }

    private void RemoveReader(Reader reader)
    {
        lock (_lock)
        {
            _readers.Remove(reader);
        }

        DeleteOldChunks();
    }

    // Deletes the chunks no reader is in, keeping the one being written and the one before, which a reader joining just
    // after a new chunk started takes its backlog from
    private void DeleteOldChunks()
    {
        lock (_lock)
        {
            var keepFrom = _writerChunk - 1;
            if (_readers.Count > 0)
            {
                keepFrom = Math.Min(keepFrom, _readers.Values.Min());
            }

            while (_oldestChunk < keepFrom)
            {
                if (!TryDelete(GetChunkPath(_oldestChunk)))
                {
                    // Tried again the next time
                    break;
                }

                _oldestChunk++;
            }
        }
    }

    private bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug("Couldn't delete live stream buffer {Path} yet: {Message}", path, ex.Message);
            return false;
        }
    }

    private static FileStream OpenChunkForWriting(string path)
        => new(path, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete, IODefaults.FileStreamBufferSize, FileOptions.Asynchronous);

    private static FileStream OpenChunkForReading(string path)
        => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, IODefaults.FileStreamBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>
    /// Writes the tuner's data into chunks.
    /// </summary>
    private sealed class Writer : Stream
    {
        private readonly LiveStreamBuffer _buffer;
        private FileStream? _chunk;
        private DateTime _chunkStarted;
        private long _chunkBytes;

        public Writer(LiveStreamBuffer buffer)
        {
            _buffer = buffer;
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _chunk?.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _chunk?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (buffer.Length > 0)
            {
                if (_chunk is null)
                {
                    await StartChunk(cancellationToken).ConfigureAwait(false);
                }

                var count = buffer.Length;
                if (IsChunkDue())
                {
                    // End the chunk on a packet boundary, so every chunk starts with a whole packet
                    var toBoundary = (int)((TsPacketSize - (_buffer.BytesWritten % TsPacketSize)) % TsPacketSize);
                    if (toBoundary == 0)
                    {
                        await StartChunk(cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        count = Math.Min(count, toBoundary);
                    }
                }

                await _chunk!.WriteAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
                await _chunk.FlushAsync(cancellationToken).ConfigureAwait(false);
                _chunkBytes += count;
                lock (_buffer._lock)
                {
                    _buffer._bytesWritten += count;
                }

                buffer = buffer[count..];
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _chunk?.Dispose();
                _chunk = null;
                lock (_buffer._lock)
                {
                    _buffer._writerFinished = true;
                }
            }

            base.Dispose(disposing);
        }

        private bool IsChunkDue()
            => _chunkBytes > 0 && (DateTime.UtcNow - _chunkStarted >= _buffer.ChunkDuration || _chunkBytes >= _buffer.MaxChunkBytes);

        private async Task StartChunk(CancellationToken cancellationToken)
        {
            // The chunk is complete on disk before the next one exists, a reader moves on only after that
            if (_chunk is not null)
            {
                await _chunk.FlushAsync(cancellationToken).ConfigureAwait(false);
                await _chunk.DisposeAsync().ConfigureAwait(false);
                _chunk = null;
            }

            int index;
            lock (_buffer._lock)
            {
                index = _buffer._writerChunk + 1;
            }

            _chunk = OpenChunkForWriting(_buffer.GetChunkPath(index));
            _chunkStarted = DateTime.UtcNow;
            _chunkBytes = 0;

            lock (_buffer._lock)
            {
                _buffer._chunkStarts.Add(_buffer._bytesWritten);
                _buffer._writerChunk = index;
            }

            _buffer.DeleteOldChunks();
        }
    }

    /// <summary>
    /// Reads the chunks as one stream.
    /// </summary>
    internal sealed class Reader : Stream
    {
        private readonly LiveStreamBuffer _buffer;
        private FileStream? _file;
        private int _chunk;
        private long _offset;
        private bool _disposed;

        public Reader(LiveStreamBuffer buffer, int chunk, long offset, long startPosition)
        {
            _buffer = buffer;
            _chunk = chunk;
            _offset = offset;
            StartPosition = startPosition;
        }

        /// <summary>
        /// Gets where in the stream the reader started.
        /// </summary>
        public long StartPosition { get; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        /// <summary>
        /// Reads what is there, or returns 0 at the live edge or once the stream has ended.
        /// </summary>
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            while (true)
            {
                if (_file is null)
                {
                    if (!_buffer.HasChunk(_chunk))
                    {
                        return 0;
                    }

                    try
                    {
                        _file = OpenChunkForReading(_buffer.GetChunkPath(_chunk));
                    }
                    catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                    {
                        // The stream ended and its buffer was deleted: nothing more to read
                        return 0;
                    }

                    if (_offset > 0)
                    {
                        _file.Seek(_offset, SeekOrigin.Begin);
                    }
                }

                var read = await _file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read > 0 || !_buffer.IsChunkFinished(_chunk))
                {
                    return read;
                }

                // The writer has finished this chunk: read what it wrote last, then go on to the next chunk
                read = await _file.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read > 0 || !_buffer.HasChunk(_chunk + 1))
                {
                    return read;
                }

                await _file.DisposeAsync().ConfigureAwait(false);
                _file = null;
                _chunk++;
                _offset = 0;
                _buffer.MoveReader(this, _chunk);
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _file?.Dispose();
                _file = null;
                _buffer.RemoveReader(this);
            }

            base.Dispose(disposing);
        }
    }
}
