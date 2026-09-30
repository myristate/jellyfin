using System;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Data.Events.Users
{
    /// <summary>
    /// An event that occurs when too many wrong sign in PINs lock PIN sign in for a while, or turn it off (Finly).
    /// </summary>
    public class UserPinLockedOutEventArgs : GenericEventArgs<User>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="UserPinLockedOutEventArgs"/> class.
        /// </summary>
        /// <param name="arg">The user.</param>
        /// <param name="lockout">How long PIN sign in is refused.</param>
        /// <param name="disabled">Whether PIN sign in is turned off until the password is used.</param>
        public UserPinLockedOutEventArgs(User arg, TimeSpan lockout, bool disabled) : base(arg)
        {
            Lockout = lockout;
            Disabled = disabled;
        }

        /// <summary>
        /// Gets how long PIN sign in is refused.
        /// </summary>
        public TimeSpan Lockout { get; }

        /// <summary>
        /// Gets a value indicating whether PIN sign in is turned off until the password is used or an administrator
        /// sets the PIN again.
        /// </summary>
        public bool Disabled { get; }
    }
}
