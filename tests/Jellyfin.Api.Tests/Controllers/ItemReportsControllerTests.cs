using System;
using System.Globalization;
using System.Security.Claims;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Controllers;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers;

/// <summary>
/// Covers the checks on reporting a problem with a film (Finly).
/// </summary>
public class ItemReportsControllerTests
{
    private readonly Mock<IItemReportStore> _reports = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Movie _film = new() { Id = Guid.NewGuid(), Name = "Film" };
    private readonly ItemReportsController _subject;

    public ItemReportsControllerTests()
    {
        var user = new User("calum", typeof(DefaultAuthenticationProvider).FullName!, typeof(DefaultPasswordResetProvider).FullName!);
        user.AddDefaultPermissions();
        user.AddDefaultPreferences();
        _userManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
        _libraryManager.Setup(l => l.GetItemById(_film.Id)).Returns(_film);
        _reports.Setup(r => r.Add(It.IsAny<ItemReport>())).Returns<ItemReport>(r => r);

        _subject = new ItemReportsController(_reports.Object, _userManager.Object, _libraryManager.Object, new Mock<IActivityManager>().Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim(ClaimTypes.Role, UserRoles.User),
                            new Claim(InternalClaimTypes.UserId, user.Id.ToString("N", CultureInfo.InvariantCulture)),
                            new Claim(InternalClaimTypes.IsApiKey, bool.FalseString)
                        ],
                        "Test"))
                }
            }
        };
    }

    [Fact]
    public async Task ReportItem_UnknownProblem_IsABadRequest()
    {
        var result = await _subject.ReportItem(_film.Id, new ItemReportRequest { Problem = (ItemProblem)42 });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        _reports.Verify(r => r.Add(It.IsAny<ItemReport>()), Times.Never);
    }

    [Fact]
    public async Task ReportItem_OverTheHourlyLimit_IsTooManyRequests()
    {
        _reports.Setup(r => r.TryCountReport(It.IsAny<Guid>())).Returns(false);

        var result = await _subject.ReportItem(_film.Id, new ItemReportRequest { Problem = ItemProblem.Audio });

        Assert.Equal(StatusCodes.Status429TooManyRequests, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        _reports.Verify(r => r.Add(It.IsAny<ItemReport>()), Times.Never);
    }

    [Fact]
    public async Task ReportItem_WithinTheLimit_IsSaved()
    {
        _reports.Setup(r => r.TryCountReport(It.IsAny<Guid>())).Returns(true);

        var result = await _subject.ReportItem(_film.Id, new ItemReportRequest { Problem = ItemProblem.Subtitles, Note = "Out of sync" });

        Assert.Equal(ItemProblem.Subtitles, result.Value!.Problem);
        _reports.Verify(r => r.Add(It.IsAny<ItemReport>()), Times.Once);
    }
}
