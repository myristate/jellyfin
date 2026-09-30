using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Problems people report with films and episodes, such as a corrupted picture or the wrong language (Finly).
/// </summary>
[Route("")]
[Authorize]
public class ItemReportsController : BaseJellyfinApiController
{
    private readonly IItemReportStore _reports;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly IActivityManager _activityManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemReportsController"/> class.
    /// </summary>
    /// <param name="reports">The report store.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="activityManager">The activity manager.</param>
    public ItemReportsController(IItemReportStore reports, IUserManager userManager, ILibraryManager libraryManager, IActivityManager activityManager)
    {
        _reports = reports;
        _userManager = userManager;
        _libraryManager = libraryManager;
        _activityManager = activityManager;
    }

    /// <summary>
    /// Reports a problem with a film or episode.
    /// </summary>
    /// <param name="itemId">The item id.</param>
    /// <param name="body">What is wrong.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">The problem isn't one of the known ones.</response>
    /// <response code="404">Item not found.</response>
    /// <response code="429">Too many reports in the last hour.</response>
    /// <returns>The report as saved.</returns>
    [HttpPost("Items/{itemId}/Reports")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<ItemReport>> ReportItem([FromRoute, Required] Guid itemId, [FromBody, Required] ItemReportRequest body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!Enum.IsDefined(body.Problem))
        {
            return BadRequest("The problem isn't one of the known ones.");
        }

        var userId = User.GetUserId();
        var user = _userManager.GetUserById(userId);
        var item = _libraryManager.GetItemById(itemId);
        if (user is null || item is null || !item.IsVisible(user))
        {
            return NotFound();
        }

        if (!_reports.TryCountReport(user.Id))
        {
            return StatusCode(StatusCodes.Status429TooManyRequests, "Too many reports, please try again later.");
        }

        var name = item is Episode episode && !string.IsNullOrEmpty(episode.SeriesName)
            ? $"{episode.SeriesName} S{episode.ParentIndexNumber ?? 0}:E{episode.IndexNumber ?? 0} {episode.Name}"
            : item.ProductionYear is int year ? $"{item.Name} ({year})" : item.Name;

        var report = _reports.Add(new ItemReport
        {
            ItemId = item.Id,
            ItemName = name,
            UserId = user.Id,
            UserName = user.Username,
            Problem = body.Problem,
            Note = body.Note
        });

        // Administrators see it in the dashboard's activity too
        await _activityManager.CreateAsync(new ActivityLog(
            string.Format(CultureInfo.InvariantCulture, "{0} reported a problem with {1}", user.Username, name),
            "ItemReported",
            user.Id)
        {
            ItemId = item.Id.ToString("N", CultureInfo.InvariantCulture),
            ShortOverview = string.IsNullOrEmpty(report.Note) ? report.Problem.ToString() : $"{report.Problem}: {report.Note}"
        }).ConfigureAwait(false);

        return report;
    }

    /// <summary>
    /// Gets the reported problems, newest first.
    /// </summary>
    /// <param name="includeResolved">Whether to include the ones dealt with.</param>
    /// <response code="200">The reports.</response>
    /// <returns>The reports.</returns>
    [HttpGet("ItemReports")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ItemReport>> GetReports([FromQuery] bool includeResolved = false)
    {
        IReadOnlyList<ItemReport> reports = _reports.GetReports().Where(r => includeResolved || !r.IsResolved).ToList();
        return Ok(reports);
    }

    /// <summary>
    /// Marks a report dealt with, or not.
    /// </summary>
    /// <param name="reportId">The report id.</param>
    /// <param name="resolved">Whether it has been dealt with.</param>
    /// <response code="200">The report.</response>
    /// <response code="404">Report not found.</response>
    /// <returns>The report.</returns>
    [HttpPost("ItemReports/{reportId}/Resolve")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<ItemReport> ResolveReport([FromRoute, Required] Guid reportId, [FromQuery] bool resolved = true)
    {
        var report = _reports.SetResolved(reportId, resolved);
        return report is null ? NotFound() : report;
    }

    /// <summary>
    /// Deletes a report.
    /// </summary>
    /// <param name="reportId">The report id.</param>
    /// <response code="204">Report deleted.</response>
    /// <response code="404">Report not found.</response>
    /// <returns>A <see cref="NoContentResult"/> on success.</returns>
    [HttpDelete("ItemReports/{reportId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult DeleteReport([FromRoute, Required] Guid reportId)
        => _reports.Delete(reportId) ? NoContent() : NotFound();
}

/// <summary>
/// A problem to report with a film or episode.
/// </summary>
public class ItemReportRequest
{
    /// <summary>
    /// Gets or sets what is wrong.
    /// </summary>
    public ItemProblem Problem { get; set; }

    /// <summary>
    /// Gets or sets anything else to say about it.
    /// </summary>
    [MaxLength(1000)]
    public string? Note { get; set; }
}
