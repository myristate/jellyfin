using System;
using System.Collections.Concurrent;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;

namespace Jellyfin.Server.Implementations.Users;

/// <summary>
/// A user's sign in PIN (Finly): a short number that signs the user in on the home network in place of their password.
/// Only its hash is stored, as a user preference.
/// </summary>
public static class SignInPin
{
    /// <summary>
    /// The number of digits in a PIN, the same as the Finly TV app's PIN pad.
    /// </summary>
    public const int Length = 4;

    /// <summary>
    /// Checks whether <paramref name="value"/> has the form of a PIN.
    /// </summary>
    /// <param name="value">The value to check.</param>
    /// <returns><c>true</c> when it is <see cref="Length"/> digits.</returns>
    public static bool IsValid(string? value)
        => value is not null && value.Length == Length && value.All(char.IsAsciiDigit);

    /// <summary>
    /// Gets the stored hash of the user's PIN.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns>The hash, or <c>null</c> when the user has no PIN.</returns>
    public static string? GetHash(User user)
    {
        var value = user.Preferences.FirstOrDefault(p => p.Kind == PreferenceKind.SignInPinHash)?.Value;
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Checks whether the user has a PIN.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns><c>true</c> when the user has a PIN.</returns>
    public static bool IsSet(User user) => GetHash(user) is not null;
}

/// <summary>
/// Counts wrong PINs per user. The first few are free, then PIN sign in is refused for a while, longer after each
/// further wrong PIN. Unlike wrong passwords this never disables the account, so a child pressing numbers on a parent's
/// profile can't lock the parent out.
/// </summary>
public sealed class SignInPinAttempts
{
    private const int FreeAttempts = 5;

    // After the free attempts, then after each further wrong PIN
    private static readonly int[] _lockoutSeconds = [30, 60, 120, 300, 900, 1800];

    private readonly ConcurrentDictionary<Guid, (int Failures, DateTime LockedUntil)> _attempts = new();

    /// <summary>
    /// Gets how long PIN sign in is refused for the user.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>The time left, <see cref="TimeSpan.Zero"/> when a PIN may be tried.</returns>
    public TimeSpan GetLockout(Guid userId)
    {
        if (!_attempts.TryGetValue(userId, out var attempts))
        {
            return TimeSpan.Zero;
        }

        var left = attempts.LockedUntil - DateTime.UtcNow;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>
    /// Counts a wrong PIN.
    /// </summary>
    /// <param name="userId">The user id.</param>
    public void Failed(Guid userId)
    {
        _attempts.AddOrUpdate(
            userId,
            _ => (1, DateTime.MinValue),
            (_, attempts) =>
            {
                var failures = attempts.Failures + 1;
                var lockouts = failures - FreeAttempts;
                var lockedUntil = lockouts < 0
                    ? DateTime.MinValue
                    : DateTime.UtcNow.AddSeconds(_lockoutSeconds[Math.Min(lockouts, _lockoutSeconds.Length - 1)]);
                return (failures, lockedUntil);
            });
    }

    /// <summary>
    /// Forgets the wrong PINs after a successful sign in.
    /// </summary>
    /// <param name="userId">The user id.</param>
    public void Succeeded(Guid userId) => _attempts.TryRemove(userId, out _);
}
