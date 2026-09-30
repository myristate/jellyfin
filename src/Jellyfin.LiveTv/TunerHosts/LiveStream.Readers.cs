using System;
using System.Threading;
using MediaBrowser.Common.Extensions;

namespace Jellyfin.LiveTv.TunerHosts
{
    /// <summary>
    /// Counting who reads a live stream (Finly). Every use of a stream reads it through <see cref="GetStream"/>: ffmpeg
    /// through the LiveStreamFiles endpoint, clients playing it directly, recordings. A stream nobody reads is one whose
    /// viewers went away without closing it, and can be closed to free the tuner.
    /// </summary>
    public partial class LiveStream
    {
        private readonly Lock _readersLock = new();
        private int _activeReaders;
        private DateTime _lastReaderLeftUtc = DateTime.UtcNow;
        private bool _noNewReaders;

        /// <inheritdoc />
        public int ActiveReaderCount
        {
            get
            {
                lock (_readersLock)
                {
                    return _activeReaders;
                }
            }
        }

        /// <inheritdoc />
        public DateTime? LastReaderLeftUtc
        {
            get
            {
                lock (_readersLock)
                {
                    return UnreadSince();
                }
            }
        }

        /// <inheritdoc />
        public bool TryStopNewReaders(TimeSpan minimumUnreadTime)
        {
            lock (_readersLock)
            {
                if (_activeReaders > 0 || DateTime.UtcNow - UnreadSince() < minimumUnreadTime)
                {
                    return false;
                }

                _noNewReaders = true;
                return true;
            }
        }

        private void BeginReading()
        {
            lock (_readersLock)
            {
                if (_noNewReaders)
                {
                    throw new ResourceNotFoundException("The live stream is closing");
                }

                _activeReaders++;
            }
        }

        private void EndReading()
        {
            lock (_readersLock)
            {
                _activeReaders--;
                _lastReaderLeftUtc = DateTime.UtcNow;
            }
        }

        // Nobody has read since the last reader left, or since the stream started if nobody has read it yet
        private DateTime UnreadSince()
            => _lastReaderLeftUtc > DateOpened ? _lastReaderLeftUtc : DateOpened;
    }
}
