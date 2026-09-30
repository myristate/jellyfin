#nullable disable

#pragma warning disable CA1711, CS1591

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;

namespace MediaBrowser.Controller.Library
{
    public interface ILiveStream : IDisposable
    {
        int ConsumerCount { get; set; }

        string OriginalStreamId { get; set; }

        string TunerHostId { get; }

        bool EnableStreamSharing { get; }

        MediaSourceInfo MediaSource { get; set; }

        string UniqueId { get; }

        Task Open(CancellationToken openCancellationToken);

        Task Close();

        Stream GetStream();

        /// <summary>
        /// Gets the number of readers reading the stream through <see cref="GetStream"/> right now (Finly).
        /// </summary>
        int ActiveReaderCount => 0;

        /// <summary>
        /// Gets when the last reader stopped reading, or when the stream was opened if nobody has read it yet (Finly).
        /// Only meaningful while <see cref="ActiveReaderCount"/> is 0. <c>null</c> when the stream doesn't count its
        /// readers, so it can't be told whether anyone is using it.
        /// </summary>
        DateTime? LastReaderLeftUtc => null;

        /// <summary>
        /// Stops new readers joining, if nobody is reading the stream and nobody has for at least the given time, so it
        /// can be closed without cutting anyone off (Finly).
        /// </summary>
        /// <param name="minimumUnreadTime">How long nobody must have been reading it.</param>
        /// <returns>Whether new readers were stopped, so the stream may be closed.</returns>
        bool TryStopNewReaders(TimeSpan minimumUnreadTime) => false;
    }
}
