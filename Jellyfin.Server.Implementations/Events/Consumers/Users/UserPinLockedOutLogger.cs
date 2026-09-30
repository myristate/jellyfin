using System;
using System.Globalization;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Events;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Events.Consumers.Users
{
    /// <summary>
    /// Creates an entry in the activity log when too many wrong sign in PINs lock a user's PIN sign in, or turn it off
    /// (Finly). The text is English only, the server's translations are left alone.
    /// </summary>
    public class UserPinLockedOutLogger : IEventConsumer<UserPinLockedOutEventArgs>
    {
        private readonly IActivityManager _activityManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="UserPinLockedOutLogger"/> class.
        /// </summary>
        /// <param name="activityManager">The activity manager.</param>
        public UserPinLockedOutLogger(IActivityManager activityManager)
        {
            _activityManager = activityManager;
        }

        /// <inheritdoc />
        public async Task OnEvent(UserPinLockedOutEventArgs eventArgs)
        {
            ArgumentNullException.ThrowIfNull(eventArgs);
            var user = eventArgs.Argument;
            var name = eventArgs.Disabled
                ? string.Format(CultureInfo.InvariantCulture, "PIN sign in for {0} is turned off after too many wrong PINs", user.Username)
                : string.Format(CultureInfo.InvariantCulture, "PIN sign in for {0} is locked for {1} after too many wrong PINs", user.Username, Describe(eventArgs.Lockout));

            await _activityManager.CreateAsync(new ActivityLog(name, eventArgs.Disabled ? "UserPinDisabled" : "UserPinLockedOut", user.Id)
            {
                ShortOverview = eventArgs.Disabled ? "Sign in with the password, or set the PIN again, to turn it back on." : null,
                LogSeverity = eventArgs.Disabled ? LogLevel.Error : LogLevel.Warning
            }).ConfigureAwait(false);
        }

        private static string Describe(TimeSpan lockout)
            => lockout.TotalMinutes >= 1
                ? string.Format(CultureInfo.InvariantCulture, "{0:0} minutes", Math.Ceiling(lockout.TotalMinutes))
                : string.Format(CultureInfo.InvariantCulture, "{0:0} seconds", Math.Ceiling(lockout.TotalSeconds));
    }
}
