using System;

namespace MediaBrowser.Controller.LiveTv
{
    /// <summary>
    /// The channel was tuned but isn't broadcasting anything that can be played, for example a part time channel that
    /// is off air, or a channel that has moved since the tuner last scanned.
    /// </summary>
    public class LiveTvChannelUnavailableException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="LiveTvChannelUnavailableException"/> class.
        /// </summary>
        /// <param name="message">The message.</param>
        public LiveTvChannelUnavailableException(string message)
            : base(message)
        {
        }
    }
}
