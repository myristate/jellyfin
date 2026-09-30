using System;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Events.Consumers.Users;
using MediaBrowser.Model.Activity;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Users;

public class UserPinLockedOutLoggerTests
{
    [Theory]
    [InlineData(false, "PIN sign in for ryan is locked for 1 minute after too many wrong PINs", "UserPinLockedOut")]
    [InlineData(true, "PIN sign in for ryan is turned off after too many wrong PINs", "UserPinDisabled")]
    public async Task OnEvent_WritesTheActivityLog(bool disabled, string name, string type)
    {
        var activityManager = new Mock<IActivityManager>();
        var user = new User("ryan", "auth-provider", "reset-provider");

        await new UserPinLockedOutLogger(activityManager.Object).OnEvent(new UserPinLockedOutEventArgs(user, TimeSpan.FromSeconds(60), disabled));

        activityManager.Verify(
            a => a.CreateAsync(It.Is<ActivityLog>(l => l.Name == name && l.Type == type && l.UserId.Equals(user.Id))),
            Times.Once);
    }
}
