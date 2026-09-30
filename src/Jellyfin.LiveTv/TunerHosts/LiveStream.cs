#nullable disable

#pragma warning disable CA1711
#pragma warning disable CS1591

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts
{
    public class LiveStream : ILiveStream
    {
        private readonly IConfigurationManager _configurationManager;

        public LiveStream(
            MediaSourceInfo mediaSource,
            TunerHostInfo tuner,
            IFileSystem fileSystem,
            ILogger logger,
            IConfigurationManager configurationManager,
            IStreamHelper streamHelper)
        {
            OriginalMediaSource = mediaSource;
            FileSystem = fileSystem;
            MediaSource = mediaSource;
            Logger = logger;
            EnableStreamSharing = true;
            UniqueId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

            if (tuner is not null)
            {
                TunerHostId = tuner.Id;
            }

            _configurationManager = configurationManager;
            StreamHelper = streamHelper;

            ConsumerCount = 1;
            SetTempFilePath("ts");
        }

        protected IFileSystem FileSystem { get; }

        protected IStreamHelper StreamHelper { get; }

        protected ILogger Logger { get; }

        protected CancellationTokenSource LiveStreamCancellationTokenSource { get; } = new CancellationTokenSource();

        protected string TempFilePath { get; set; }

        public MediaSourceInfo OriginalMediaSource { get; set; }

        public MediaSourceInfo MediaSource { get; set; }

        public int ConsumerCount { get; set; }

        public string OriginalStreamId { get; set; }

        public bool EnableStreamSharing { get; set; }

        public string UniqueId { get; }

        public string TunerHostId { get; }

        public DateTime DateOpened { get; protected set; }

        /// <summary>
        /// How many seconds of already buffered stream a viewer joining a running stream gets.
        /// </summary>
        internal const double JoinBacklogSeconds = 2.5;

        private const int TsPacketSize = 188;

        protected void SetTempFilePath(string extension)
        {
            TempFilePath = Path.Combine(_configurationManager.GetTranscodePath(), UniqueId + "." + extension);
        }

        public virtual Task Open(CancellationToken openCancellationToken)
        {
            DateOpened = DateTime.UtcNow;
            return Task.CompletedTask;
        }

        public async Task Close()
        {
            EnableStreamSharing = false;

            Logger.LogInformation("Closing {Type}", GetType().Name);

            await LiveStreamCancellationTokenSource.CancelAsync().ConfigureAwait(false);
        }

        public Stream GetStream()
        {
            var stream = new FileStream(
                TempFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                IODefaults.FileStreamBufferSize,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            // A viewer joining a stream that is already running starts a couple of seconds behind the live point
            // rather than at the start of the buffer file. The backlog fills the player's start buffer at once, so a
            // shared or pre-tuned channel shows a picture almost immediately instead of waiting for fresh data.
            var openFor = DateTime.UtcNow - DateOpened;
            if (openFor.TotalSeconds > JoinBacklogSeconds && stream.CanSeek)
            {
                try
                {
                    stream.Seek(GetJoinPosition(stream.Length, openFor), SeekOrigin.Begin);
                }
                catch (IOException ex)
                {
                    Logger.LogWarning(ex, "Error seeking live stream buffer");
                }
            }

            return stream;
        }

        /// <summary>
        /// Gets where in the buffer file a viewer joining a running stream starts: about <see cref="JoinBacklogSeconds"/>
        /// of stream before the end at its average rate so far, on a transport stream packet boundary.
        /// </summary>
        /// <param name="length">The current length of the buffer file.</param>
        /// <param name="openFor">How long the stream has been open.</param>
        /// <returns>The position from the start of the file.</returns>
        internal static long GetJoinPosition(long length, TimeSpan openFor)
        {
            if (length <= 0 || openFor <= TimeSpan.Zero)
            {
                return 0;
            }

            var bytesPerSecond = length / openFor.TotalSeconds;
            var position = Math.Max(0, length - (long)(bytesPerSecond * JoinBacklogSeconds));
            return position - (position % TsPacketSize);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool dispose)
        {
            if (dispose)
            {
                LiveStreamCancellationTokenSource?.Dispose();
            }
        }

        protected async Task DeleteTempFiles(string path, int retryCount = 0)
        {
            if (retryCount == 0)
            {
                Logger.LogInformation("Deleting temp file {FilePath}", path);
            }

            try
            {
                FileSystem.DeleteFile(path);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error deleting file {FilePath}", path);
                if (retryCount <= 40)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                    await DeleteTempFiles(path, retryCount + 1).ConfigureAwait(false);
                }
            }
        }

    }
}
