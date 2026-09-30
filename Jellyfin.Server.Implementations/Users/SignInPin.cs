using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data;
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
/// Counts wrong PINs per user (Finly). The first few are free, then PIN sign in is refused for a while, longer after each
/// further wrong PIN, and after <see cref="FailuresBeforeDisabling"/> PIN sign in is turned off until the user signs in
/// with their password or an administrator sets or removes the PIN. Unlike wrong passwords this never disables the
/// account, so a child pressing numbers on a parent's profile can't lock the parent out.
/// </summary>
/// <remarks>
/// The wrong PINs are kept in the user's preferences, so restarting the server doesn't forget them. Only the last
/// <see cref="FailureMemory"/> of them count.
/// </remarks>
public static class SignInPinAttempts
{
    /// <summary>
    /// The number of wrong PINs before PIN sign in is refused for a while.
    /// </summary>
    public const int FreeAttempts = 5;

    /// <summary>
    /// The number of wrong PINs in a row, within <see cref="FailureMemory"/>, that turn PIN sign in off.
    /// </summary>
    public const int FailuresBeforeDisabling = 20;

    /// <summary>
    /// How long a wrong PIN counts.
    /// </summary>
    public static readonly TimeSpan FailureMemory = TimeSpan.FromHours(24);

    // After the free attempts, then after each further wrong PIN
    private static readonly int[] _lockoutSeconds = [30, 60, 120, 300, 900, 1800];

    /// <summary>
    /// Gets the times of the wrong PINs that still count, oldest first.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="now">The current UTC time.</param>
    /// <returns>The times.</returns>
    public static IReadOnlyList<DateTime> GetRecentFailures(User user, DateTime now)
    {
        var since = now - FailureMemory;
        return user.GetPreference(PreferenceKind.SignInPinFailures)
            .Select(value => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
                ? new DateTime(ticks, DateTimeKind.Utc)
                : DateTime.MinValue)
            .Where(time => time > since && time <= now.AddMinutes(1))
            .Order()
            .ToList();
    }

    /// <summary>
    /// Gets how long PIN sign in is refused for the user.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="now">The current UTC time.</param>
    /// <returns>The time left, <see cref="TimeSpan.Zero"/> when a PIN may be tried.</returns>
    public static TimeSpan GetLockout(User user, DateTime now)
    {
        var failures = GetRecentFailures(user, now);
        var lockouts = failures.Count - FreeAttempts;
        if (lockouts < 0)
        {
            return TimeSpan.Zero;
        }

        var lockedUntil = failures[^1].AddSeconds(_lockoutSeconds[Math.Min(lockouts, _lockoutSeconds.Length - 1)]);
        var left = lockedUntil - now;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>
    /// Checks whether PIN sign in was turned off after too many wrong PINs.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns><c>true</c> when only the password signs the user in until it is turned back on.</returns>
    public static bool IsDisabled(User user) => user.GetPreference(PreferenceKind.SignInPinDisabled).Length > 0;

    /// <summary>
    /// Counts a wrong PIN, in the user's preferences.
    /// </summary>
    /// <param name="user">The user, with their preferences.</param>
    /// <param name="now">The current UTC time.</param>
    /// <returns>What the wrong PIN led to.</returns>
    public static SignInPinFailure RecordFailure(User user, DateTime now)
    {
        var failures = GetRecentFailures(user, now).ToList();
        failures.Add(now);

        // Only the ones that can still matter are kept
        if (failures.Count > FailuresBeforeDisabling)
        {
            failures.RemoveRange(0, failures.Count - FailuresBeforeDisabling);
        }

        user.SetPreference(PreferenceKind.SignInPinFailures, failures.Select(f => f.Ticks.ToString(CultureInfo.InvariantCulture)).ToArray());

        var disabled = failures.Count >= FailuresBeforeDisabling && !IsDisabled(user);
        if (disabled)
        {
            user.SetPreference(PreferenceKind.SignInPinDisabled, [now.Ticks.ToString(CultureInfo.InvariantCulture)]);
        }

        // A PIN can't be tried while locked, so every wrong PIN from the free ones on starts a new lockout
        var lockout = GetLockout(user, now);
        return new SignInPinFailure(failures.Count, lockout, lockout > TimeSpan.Zero && !disabled, disabled);
    }

    /// <summary>
    /// Forgets the wrong PINs and turns PIN sign in back on, after a successful sign in or when the PIN is set again.
    /// </summary>
    /// <param name="user">The user, with their preferences.</param>
    /// <returns><c>true</c> when there was anything to forget.</returns>
    public static bool Reset(User user)
    {
        var changed = false;
        foreach (var kind in new[] { PreferenceKind.SignInPinFailures, PreferenceKind.SignInPinDisabled })
        {
            if (user.GetPreference(kind).Length > 0)
            {
                user.SetPreference(kind, Array.Empty<string>());
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Checks whether the user has any wrong PINs or a turned off PIN to forget.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <returns><c>true</c> when <see cref="Reset"/> would change anything.</returns>
    public static bool HasAnything(User user)
        => user.GetPreference(PreferenceKind.SignInPinFailures).Length > 0 || IsDisabled(user);
}

/// <summary>
/// What a wrong PIN led to (Finly).
/// </summary>
/// <param name="Failures">The wrong PINs that now count.</param>
/// <param name="Lockout">How long PIN sign in is now refused.</param>
/// <param name="LockoutStarted">Whether this wrong PIN started a lockout.</param>
/// <param name="Disabled">Whether this wrong PIN turned PIN sign in off.</param>
public sealed record SignInPinFailure(int Failures, TimeSpan Lockout, bool LockoutStarted, bool Disabled);
