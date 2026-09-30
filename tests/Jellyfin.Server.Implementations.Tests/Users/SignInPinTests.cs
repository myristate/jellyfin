using System;
using Jellyfin.Server.Implementations.Users;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Users;

public class SignInPinTests
{
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
        var attempts = new SignInPinAttempts();
        var userId = Guid.NewGuid();

        for (var i = 0; i < 4; i++)
        {
            attempts.Failed(userId);
        }

        Assert.Equal(TimeSpan.Zero, attempts.GetLockout(userId));
    }

    [Fact]
    public void Attempts_FifthWrongPinLocksForThirtySeconds()
    {
        var attempts = new SignInPinAttempts();
        var userId = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
        {
            attempts.Failed(userId);
        }

        var lockout = attempts.GetLockout(userId);
        Assert.InRange(lockout, TimeSpan.FromSeconds(28), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Attempts_EachFurtherWrongPinLocksLonger()
    {
        var attempts = new SignInPinAttempts();
        var userId = Guid.NewGuid();

        for (var i = 0; i < 6; i++)
        {
            attempts.Failed(userId);
        }

        Assert.InRange(attempts.GetLockout(userId), TimeSpan.FromSeconds(58), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Attempts_SuccessClearsWrongPins()
    {
        var attempts = new SignInPinAttempts();
        var userId = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
        {
            attempts.Failed(userId);
        }

        attempts.Succeeded(userId);
        attempts.Failed(userId);

        Assert.Equal(TimeSpan.Zero, attempts.GetLockout(userId));
    }

    [Fact]
    public void Attempts_AreCountedPerUser()
    {
        var attempts = new SignInPinAttempts();
        var parent = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
        {
            attempts.Failed(parent);
        }

        Assert.Equal(TimeSpan.Zero, attempts.GetLockout(Guid.NewGuid()));
    }
}
