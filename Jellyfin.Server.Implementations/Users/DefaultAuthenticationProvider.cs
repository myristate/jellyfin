using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Model.Cryptography;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Users
{
    /// <summary>
    /// The default authentication provider.
    /// </summary>
    public class DefaultAuthenticationProvider : IAuthenticationProvider, IRequiresResolvedUser
    {
        private readonly ILogger<DefaultAuthenticationProvider> _logger;
        private readonly ICryptoProvider _cryptographyProvider;

        /// <summary>
        /// Initializes a new instance of the <see cref="DefaultAuthenticationProvider"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="cryptographyProvider">The cryptography provider.</param>
        public DefaultAuthenticationProvider(ILogger<DefaultAuthenticationProvider> logger, ICryptoProvider cryptographyProvider)
        {
            _logger = logger;
            _cryptographyProvider = cryptographyProvider;
        }

        /// <inheritdoc />
        public string Name => "Default";

        /// <inheritdoc />
        public bool IsEnabled => true;

        /// <inheritdoc />
        // This is dumb and an artifact of the backwards way auth providers were designed.
        // This version of authenticate was never meant to be called, but needs to be here for interface compat
        // Only the providers that don't provide local user support use this
        public Task<ProviderAuthenticationResult> Authenticate(string username, string password)
        {
            throw new NotImplementedException();
        }

        /// <inheritdoc />
        // This is the version that we need to use for local users. Because reasons.
        public Task<ProviderAuthenticationResult> Authenticate(string username, string password, User? resolvedUser)
        {
            [DoesNotReturn]
            static void ThrowAuthenticationException()
            {
                throw new AuthenticationException("Invalid username or password");
            }

            if (resolvedUser is null)
            {
                ThrowAuthenticationException();
            }

            // As long as jellyfin supports password-less users, we need this little block here to accommodate
            if (string.IsNullOrEmpty(resolvedUser.Password) && string.IsNullOrEmpty(password))
            {
                return Task.FromResult(new ProviderAuthenticationResult
                {
                    Username = username
                });
            }

            // Handle the case when the stored password is null, but the user tried to login with a password
            if (resolvedUser.Password is null)
            {
                ThrowAuthenticationException();
            }

            PasswordHash readyHash = PasswordHash.Parse(resolvedUser.Password);
            if (!_cryptographyProvider.Verify(readyHash, password))
            {
                ThrowAuthenticationException();
            }

            // Migrate old hashes to the new default
            if (!string.Equals(readyHash.Id, _cryptographyProvider.DefaultHashMethod, StringComparison.Ordinal)
                || int.Parse(readyHash.Parameters["iterations"], CultureInfo.InvariantCulture) != Constants.DefaultIterations)
            {
                _logger.LogInformation("Migrating password hash of {User} to the latest default", username);
                ChangePassword(resolvedUser, password);
            }

            return Task.FromResult(new ProviderAuthenticationResult
            {
                Username = username
            });
        }

        /// <summary>
        /// Hashes a sign in PIN the same way as passwords.
        /// </summary>
        /// <param name="pin">The PIN.</param>
        /// <returns>The hash to store.</returns>
        public string HashPin(string pin) => _cryptographyProvider.CreatePasswordHash(pin).ToString();

        /// <summary>
        /// Checks a sign in PIN against its stored hash (Finly). A hash that can't be read never matches.
        /// </summary>
        /// <param name="hash">The stored hash.</param>
        /// <param name="pin">The PIN entered.</param>
        /// <returns><c>true</c> when the PIN matches.</returns>
        public bool VerifyPin(string hash, string pin)
        {
            var passwordHash = TryParsePinHash(hash);
            if (passwordHash is null)
            {
                _logger.LogWarning("A stored sign in PIN can't be read, it is ignored");
                return false;
            }

            return _cryptographyProvider.Verify(passwordHash, pin);
        }

        /// <summary>
        /// Checks whether a stored PIN hash can be read (Finly). A PIN whose hash can't be read is as good as none.
        /// </summary>
        /// <param name="hash">The stored hash.</param>
        /// <returns><c>true</c> when a PIN can be checked against it.</returns>
        public bool IsValidPinHash(string hash) => TryParsePinHash(hash) is not null;

        private PasswordHash? TryParsePinHash(string hash)
        {
            try
            {
                var passwordHash = PasswordHash.Parse(hash);
                if (passwordHash.Hash.Length > 0
                    && string.Equals(passwordHash.Id, _cryptographyProvider.DefaultHashMethod, StringComparison.Ordinal)
                    && passwordHash.Parameters.TryGetValue("iterations", out var iterations)
                    && int.TryParse(iterations, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                    && count > 0)
                {
                    return passwordHash;
                }
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                // Not a hash, as good as no PIN
            }

            return null;
        }

        /// <inheritdoc />
        public Task ChangePassword(User user, string newPassword)
        {
            if (string.IsNullOrEmpty(newPassword))
            {
                user.Password = null;
                return Task.CompletedTask;
            }

            PasswordHash newPasswordHash = _cryptographyProvider.CreatePasswordHash(newPassword);
            user.Password = newPasswordHash.ToString();

            return Task.CompletedTask;
        }
    }
}
