#pragma warning disable CA1711
#pragma warning disable CS1591

using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv.TunerHosts
{
    public class SharedHttpStream : LiveStream, IDirectStreamProvider
    {
        /// <summary>
        /// How long to wait for the tuner to answer and to send the first data before giving up on opening.
        /// </summary>
        private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IServerApplicationHost _appHost;

        public SharedHttpStream(
            MediaSourceInfo mediaSource,
            TunerHostInfo tunerHostInfo,
            string originalStreamId,
            IFileSystem fileSystem,
            IHttpClientFactory httpClientFactory,
            ILogger logger,
            IConfigurationManager configurationManager,
            IServerApplicationHost appHost,
            IStreamHelper streamHelper)
            : base(mediaSource, tunerHostInfo, fileSystem, logger, configurationManager, streamHelper)
        {
            _httpClientFactory = httpClientFactory;
            _appHost = appHost;
            OriginalStreamId = originalStreamId;
        }

        public override async Task Open(CancellationToken openCancellationToken)
        {
            LiveStreamCancellationTokenSource.Token.ThrowIfCancellationRequested();

            var mediaSource = OriginalMediaSource;

            var url = mediaSource.Path;

            Directory.CreateDirectory(Path.GetDirectoryName(TempFilePath) ?? throw new InvalidOperationException("Path can't be a root directory."));

            var typeName = GetType().Name;
            Logger.LogInformation("Opening {StreamType} Live stream from {Url}", typeName, url);
            var openTimer = System.Diagnostics.Stopwatch.StartNew();

            // Response stream is disposed manually.
            HttpResponseMessage response;
            using (var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(openCancellationToken, LiveStreamCancellationTokenSource.Token))
            {
                headersTimeout.CancelAfter(OpenTimeout);
                try
                {
                    response = await _httpClientFactory.CreateClient(NamedClient.Default)
                        .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!openCancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"No response from the tuner within {OpenTimeout.TotalSeconds} seconds");
                }
            }

            // A tuner that can't stream the channel answers with an error, for example an HDHomeRun that has no free
            // tuner (X-HDHomeRun-Error 805) or can't lock the multiplex (806). Don't treat its error body as video.
            if (!response.IsSuccessStatusCode)
            {
                var tunerError = response.Headers.TryGetValues("X-HDHomeRun-Error", out var values) ? string.Join(", ", values) : null;
                var status = (int)response.StatusCode;
                response.Dispose();
                Logger.LogWarning("Tuner refused {Url} with HTTP {Status} {TunerError}", url, status, tunerError);
                if (status == 503 || (tunerError is not null && tunerError.StartsWith("805", StringComparison.Ordinal)))
                {
                    throw new LiveTvConflictException($"The tuner has no free tuner for this channel ({tunerError ?? status.ToString(CultureInfo.InvariantCulture)})");
                }

                throw new HttpRequestException($"The tuner refused the stream with HTTP {status} {tunerError}");
            }

            Logger.LogInformation("Tuner answered {Url} in {Milliseconds} ms", url, openTimer.ElapsedMilliseconds);

            var taskCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _ = StartStreaming(response, taskCompletionSource, LiveStreamCancellationTokenSource.Token);

            MediaSource.Path = _appHost.GetApiUrlForLocalAccess() + "/LiveTv/LiveStreamFiles/" + UniqueId + "/stream.ts";
            MediaSource.Protocol = MediaProtocol.Http;

            bool res;
            try
            {
                res = await taskCompletionSource.Task.WaitAsync(OpenTimeout, openCancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Headers but no data: stop copying so the tuner is released, and let the client retry or report it
                Logger.LogWarning("No data from {Url} within {Seconds} seconds, closing the stream", url, OpenTimeout.TotalSeconds);
                await LiveStreamCancellationTokenSource.CancelAsync().ConfigureAwait(false);
                throw;
            }

            if (res)
            {
                Logger.LogInformation("First data from {Url} after {Milliseconds} ms", url, openTimer.ElapsedMilliseconds);
            }

            if (!res)
            {
                Logger.LogWarning("Zero bytes copied from stream {StreamType} to {FilePath} but no exception raised", GetType().Name, TempFilePath);
                throw new EndOfStreamException(string.Format(CultureInfo.InvariantCulture, "Zero bytes copied from stream {0}", GetType().Name));
            }
        }

        private Task StartStreaming(HttpResponseMessage response, TaskCompletionSource<bool> openTaskCompletionSource, CancellationToken cancellationToken)
        {
            return Task.Run(
                async () =>
                {
                    try
                    {
                        Logger.LogInformation("Beginning {StreamType} stream to {FilePath}", GetType().Name, TempFilePath);
                        using (response)
                        {
                            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                            await using (stream.ConfigureAwait(false))
                            {
                                var fileStream = new FileStream(
                                    TempFilePath,
                                    FileMode.Create,
                                    FileAccess.Write,
                                    FileShare.Read,
                                    IODefaults.FileStreamBufferSize,
                                    FileOptions.Asynchronous);

                                await using (fileStream.ConfigureAwait(false))
                                {
                                    await StreamHelper.CopyToAsync(
                                        stream,
                                        fileStream,
                                        IODefaults.CopyToBufferSize,
                                        () => Resolve(openTaskCompletionSource),
                                        cancellationToken).ConfigureAwait(false);
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException ex)
                    {
                        Logger.LogInformation("Copying of {StreamType} to {FilePath} was canceled", GetType().Name, TempFilePath);
                        openTaskCompletionSource.TrySetException(ex);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Error copying live stream {StreamType} to {FilePath}", GetType().Name, TempFilePath);
                        openTaskCompletionSource.TrySetException(ex);
                    }

                    openTaskCompletionSource.TrySetResult(false);

                    EnableStreamSharing = false;
                    await DeleteTempFiles(TempFilePath).ConfigureAwait(false);
                },
                CancellationToken.None);
        }

        private void Resolve(TaskCompletionSource<bool> openTaskCompletionSource)
        {
            DateOpened = DateTime.UtcNow;
            openTaskCompletionSource.TrySetResult(true);
        }
    }
}
