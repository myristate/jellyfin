using System;

namespace MediaBrowser.Controller.LiveTv
{
    /// <summary>
    /// The tuner failed to stream a channel (Finly): it couldn't be reached, didn't answer or send any data in time, or
    /// refused with an error other than having no free tuner. Unlike <see cref="LiveTvConflictException"/>, freeing a
    /// tuner won't help, so it isn't retried.
    /// </summary>
    public class LiveTvTunerException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="LiveTvTunerException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="isTimeout">Whether the tuner didn't answer or send data in time.</param>
        /// <param name="innerException">The error that caused it, if any.</param>
        public LiveTvTunerException(string message, bool isTimeout, Exception? innerException = null)
            : base(message, innerException)
        {
            IsTimeout = isTimeout;
        }

        /// <summary>
        /// Gets a value indicating whether the tuner didn't answer or send data in time, rather than failing outright.
        /// </summary>
        public bool IsTimeout { get; }
    }
}
