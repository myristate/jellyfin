using System;
using Emby.Server.Implementations.Cryptography;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Users;

public class SignInPinTests
{
    private static readonly DateTime _now = new(2026, 9, 30, 18, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("1234", true)]
    [InlineData("0706", true)]
    [InlineData("123", false)]
    [InlineData("12345", false)]
    [InlineData("12a4", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValid_OnlyFourDigits(string? value, bool expected)
    {
        Assert.Equal(expected, SignInPin.IsValid(value));
    }

    [Fact]
    public void Attempts_FiveWrongPinsAreFree()
    {
        var user = CreateUser();

        Fail(user, 4);

        Assert.Equal(TimeSpan.Zero, SignInPinAttempts.GetLockout(user, _now));
    }

    [Fact]
    public void Attempts_FifthWrongPinLocksForThirtySeconds()
    {
        var user = CreateUser();

        var failure = Fail(user, 5);

        Assert.True(failure.LockoutStarted);
        Assert.Equal(TimeSpan.FromSeconds(30), SignInPinAttempts.GetLockout(user, _now));
    }

    [Fact]
    public void Attempts_EachFurtherWrongPinLocksLonger()
    {
        var user = CreateUser();

        Fail(user, 6);

        Assert.Equal(TimeSpan.FromSeconds(60), SignInPinAttempts.GetLockout(user, _now));
    }

    [Fact]
    public void Attempts_ResetClearsWrongPins()
    {
        var user = CreateUser();
        Fail(user, 5);

        Assert.True(SignInPinAttempts.Reset(user));
        SignInPinAttempts.RecordFailure(user, _now);

        Assert.Equal(TimeSpan.Zero, SignInPinAttempts.GetLockout(user, _now));
    }

    [Fact]
    public void Attempts_AreCountedPerUser()
    {
        var parent = CreateUser();
        Fail(parent, 5);

        Assert.Equal(TimeSpan.Zero, SignInPinAttempts.GetLockout(CreateUser(), _now));
    }

    [Fact]
    public void Attempts_TwentyWrongPinsTurnPinSignInOff()
    {
        var user = CreateUser();

        var nineteenth = Fail(user, 19);
        Assert.False(nineteenth.Disabled);
        Assert.False(SignInPinAttempts.IsDisabled(user));

        var twentieth = SignInPinAttempts.RecordFailure(user, _now);
        Assert.True(twentieth.Disabled);
        Assert.False(twentieth.LockoutStarted);
        Assert.True(SignInPinAttempts.IsDisabled(user));

        // Until the password is used, or the PIN set again
        Assert.True(SignInPinAttempts.Reset(user));
        Assert.False(SignInPinAttempts.IsDisabled(user));
        Assert.False(SignInPinAttempts.HasAnything(user));
    }

    [Fact]
    public void Attempts_OlderThanADayDontCount()
    {
        var user = CreateUser();
        Fail(user, 19, _now.AddHours(-25));

        var failure = SignInPinAttempts.RecordFailure(user, _now);

        Assert.Equal(1, failure.Failures);
        Assert.False(failure.Disabled);
        Assert.Equal(TimeSpan.Zero, SignInPinAttempts.GetLockout(user, _now));
    }

    [Fact]
    public void Attempts_LockoutEndsAfterItsTime()
    {
        var user = CreateUser();
        Fail(user, 5);

        Assert.Equal(TimeSpan.Zero, SignInPinAttempts.GetLockout(user, _now.AddSeconds(31)));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("$PBKDF2-SHA512$")]
    [InlineData("$PBKDF2-SHA512$iterations=0$00$00")]
    [InlineData("$UNKNOWN$iterations=1000$00$00")]
    public void VerifyPin_UnreadableHash_NeverMatchesAndDoesNotThrow(string hash)
    {
        var provider = new DefaultAuthenticationProvider(NullLogger<DefaultAuthenticationProvider>.Instance, new CryptographyProvider());

        Assert.False(provider.IsValidPinHash(hash));
        Assert.False(provider.VerifyPin(hash, "1234"));
    }

    [Fact]
    public void VerifyPin_MatchesItsOwnHash()
    {
        var provider = new DefaultAuthenticationProvider(NullLogger<DefaultAuthenticationProvider>.Instance, new CryptographyProvider());
        var hash = provider.HashPin("1234");

        Assert.True(provider.IsValidPinHash(hash));
        Assert.True(provider.VerifyPin(hash, "1234"));
        Assert.False(provider.VerifyPin(hash, "4321"));
    }

    private static User CreateUser() => new("calum", typeof(DefaultAuthenticationProvider).FullName!, typeof(DefaultPasswordResetProvider).FullName!);

    private static SignInPinFailure Fail(User user, int times, DateTime? at = null)
    {
        SignInPinFailure? failure = null;
        for (var i = 0; i < times; i++)
        {
            failure = SignInPinAttempts.RecordFailure(user, at ?? _now);
        }

        return failure!;
    }
}
