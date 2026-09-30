#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Data.Events;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;

namespace MediaBrowser.Controller.Library
{
    /// <summary>
    /// Interface IUserManager.
    /// </summary>
    public interface IUserManager
    {
        /// <summary>
        /// Occurs when a user is updated.
        /// </summary>
        event EventHandler<GenericEventArgs<User>> OnUserUpdated;

        /// <summary>
        /// Gets the users.
        /// </summary>
        /// <returns>The users.</returns>
        IEnumerable<User> GetUsers();

        /// <summary>
        /// Gets the user ids.
        /// </summary>
        /// <returns>The users ids.</returns>
        IEnumerable<Guid> GetUsersIds();

        /// <summary>
        /// Initializes the user manager and ensures that a user exists.
        /// </summary>
        /// <returns>Awaitable task.</returns>
        Task InitializeAsync();

        /// <summary>
        /// Gets a user by Id.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>The user with the specified Id, or <c>null</c> if the user doesn't exist.</returns>
        /// <exception cref="ArgumentException"><c>id</c> is an empty Guid.</exception>
        User? GetUserById(Guid id);

        /// <summary>
        /// Gets the first available user.
        /// </summary>
        /// <returns>The first user, or <c>null</c> if no users exist.</returns>
        User? GetFirstUser();

        /// <summary>
        /// Gets the name of the user by.
        /// </summary>
        /// <param name="name">The name.</param>
        /// <returns>User.</returns>
        User? GetUserByName(string name);

        /// <summary>
        /// Renames the user.
        /// </summary>
        /// <param name="userId">The UserId to change.</param>
        /// <param name="oldName">The old Username.</param>
        /// <param name="newName">The new name.</param>
        /// <returns>Task.</returns>
        /// <exception cref="ArgumentNullException">If user is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">If the provided user doesn't exist.</exception>
        Task RenameUser(Guid userId, string oldName, string newName);

        /// <summary>
        /// Updates the user.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <exception cref="ArgumentNullException">If user is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">If the provided user doesn't exist.</exception>
        /// <returns>A task representing the update of the user.</returns>
        Task UpdateUserAsync(User user);

        /// <summary>
        /// Creates a user with the specified name.
        /// </summary>
        /// <param name="name">The name of the new user.</param>
        /// <returns>The created user.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is <c>null</c> or empty.</exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> already exists.</exception>
        Task<User> CreateUserAsync(string name);

        /// <summary>
        /// Deletes the specified user.
        /// </summary>
        /// <param name="userId">The id of the user to be deleted.</param>
        /// <returns>A task representing the deletion of the user.</returns>
        Task DeleteUserAsync(Guid userId);

        /// <summary>
        /// Checks whether an administrator other than the given user has a password. There must always be one.
        /// </summary>
        /// <param name="userId">The user to leave out.</param>
        /// <returns><c>true</c> when another administrator has a password.</returns>
        bool HasOtherAdministratorWithPassword(Guid userId);

        /// <summary>
        /// Sets or removes the user's sign in PIN, which signs them in on the home network in place of their password.
        /// Either way the wrong PINs held against the user are forgotten and PIN sign in is turned back on.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <param name="pin">The PIN, or <c>null</c> to remove it.</param>
        /// <returns>A task representing the change.</returns>
        Task SetPinAsync(Guid userId, string? pin);

        /// <summary>
        /// Forgets the wrong PINs held against the user and turns PIN sign in back on after too many of them (Finly).
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <returns>A task representing the change.</returns>
        Task ClearPinLockoutAsync(Guid userId);

        /// <summary>
        /// Removes an item from the user's library, or puts it back. A removed item and everything below it, such as
        /// the episodes of a series, no longer shows up for the user anywhere.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <param name="itemId">The item id.</param>
        /// <param name="hidden">Whether the item is removed.</param>
        /// <param name="identity">What identifies the item apart from its id, remembered to find it again when it moves.</param>
        /// <returns>A task representing the change.</returns>
        Task SetItemHiddenAsync(Guid userId, Guid itemId, bool hidden, ItemIdentity? identity = null);

        /// <summary>
        /// Lets the user see an item although their rating or tags would hide it, or takes that back. Allowing an item
        /// also puts it back if it was removed from the user's library.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <param name="itemId">The item id.</param>
        /// <param name="allowed">Whether the item is allowed.</param>
        /// <param name="identity">What identifies the item apart from its id, remembered to find it again when it moves.</param>
        /// <returns>A task representing the change.</returns>
        Task SetItemAllowedAsync(Guid userId, Guid itemId, bool allowed, ItemIdentity? identity = null);

        /// <summary>
        /// Brings the user's removed and allowed items up to date with the library (Finly): items that moved get their
        /// new id, and items missing for longer than <paramref name="gracePeriod"/> are dropped.
        /// </summary>
        /// <param name="userId">The user id.</param>
        /// <param name="missingItems">The listed items no longer in the library, each with the id of the same item found
        /// again under a new id, or <c>null</c> when it wasn't found.</param>
        /// <param name="gracePeriod">How long an item may be missing before it is dropped.</param>
        /// <returns>A task representing the change.</returns>
        Task RelinkItemsAsync(Guid userId, IReadOnlyDictionary<Guid, Guid?> missingItems, TimeSpan gracePeriod);

        /// <summary>
        /// Resets the password.
        /// </summary>
        /// <param name="userId">The users Id.</param>
        /// <returns>Task.</returns>
        Task ResetPassword(Guid userId);

        /// <summary>
        /// Changes the password.
        /// </summary>
        /// <param name="userId">The users id.</param>
        /// <param name="newPassword">New password to use.</param>
        /// <returns>Awaitable task.</returns>
        Task ChangePassword(Guid userId, string newPassword);

        /// <summary>
        /// Gets the user dto.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <param name="remoteEndPoint">The remote end point.</param>
        /// <returns>UserDto.</returns>
        UserDto GetUserDto(User user, string? remoteEndPoint = null);

        /// <summary>
        /// Authenticates the user.
        /// </summary>
        /// <param name="username">The user.</param>
        /// <param name="password">The password to use.</param>
        /// <param name="remoteEndPoint">Remove endpoint to use.</param>
        /// <param name="isUserSession">Specifies if a user session.</param>
        /// <returns>User wrapped in awaitable task.</returns>
        Task<User?> AuthenticateUser(string username, string password, string remoteEndPoint, bool isUserSession);

        /// <summary>
        /// Authenticates the user, optionally without accepting their sign in PIN (Finly). Checking a user's current
        /// password, before changing their password or PIN, must not accept the PIN.
        /// </summary>
        /// <param name="username">The user.</param>
        /// <param name="password">The password to use.</param>
        /// <param name="remoteEndPoint">Remove endpoint to use.</param>
        /// <param name="isUserSession">Specifies if a user session.</param>
        /// <param name="allowPin">Whether the user's PIN may be entered in place of the password.</param>
        /// <returns>User wrapped in awaitable task.</returns>
        Task<User?> AuthenticateUser(string username, string password, string remoteEndPoint, bool isUserSession, bool allowPin);

        /// <summary>
        /// Starts the forgot password process.
        /// </summary>
        /// <param name="enteredUsername">The entered username.</param>
        /// <param name="isInNetwork">if set to <c>true</c> [is in network].</param>
        /// <returns>ForgotPasswordResult.</returns>
        Task<ForgotPasswordResult> StartForgotPasswordProcess(string enteredUsername, bool isInNetwork);

        /// <summary>
        /// Redeems the password reset pin.
        /// </summary>
        /// <param name="pin">The pin.</param>
        /// <returns><c>true</c> if XXXX, <c>false</c> otherwise.</returns>
        Task<PinRedeemResult> RedeemPasswordResetPin(string pin);

        NameIdPair[] GetAuthenticationProviders();

        NameIdPair[] GetPasswordResetProviders();

        /// <summary>
        /// This method updates the user's configuration.
        /// This is only included as a stopgap until the new API, using this internally is not recommended.
        /// Instead, modify the user object directly, then call <see cref="UpdateUserAsync"/>.
        /// </summary>
        /// <param name="userId">The user's Id.</param>
        /// <param name="config">The request containing the new user configuration.</param>
        /// <returns>A task representing the update.</returns>
        Task UpdateConfigurationAsync(Guid userId, UserConfiguration config);

        /// <summary>
        /// This method updates the user's policy.
        /// This is only included as a stopgap until the new API, using this internally is not recommended.
        /// Instead, modify the user object directly, then call <see cref="UpdateUserAsync"/>.
        /// </summary>
        /// <param name="userId">The user's Id.</param>
        /// <param name="policy">The request containing the new user policy.</param>
        /// <returns>A task representing the update.</returns>
        Task UpdatePolicyAsync(Guid userId, UserPolicy policy);

        /// <summary>
        /// Clears the user's profile image.
        /// </summary>
        /// <param name="user">The user.</param>
        /// <returns>A task representing the clearing of the profile image.</returns>
        Task ClearProfileImageAsync(User user);
    }
}
