using System;
using System.Collections.Generic;
using MediaBrowser.Model.Library;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Keeps the problems people report with films and episodes (Finly).
/// </summary>
public interface IItemReportStore
{
    /// <summary>
    /// Gets the reports, newest first.
    /// </summary>
    /// <returns>The reports.</returns>
    IReadOnlyList<ItemReport> GetReports();

    /// <summary>
    /// Adds a report. When the same person already has the same problem open for the item, that report is updated
    /// instead.
    /// </summary>
    /// <param name="report">The report.</param>
    /// <returns>The report as saved.</returns>
    ItemReport Add(ItemReport report);

    /// <summary>
    /// Marks a report dealt with, or not.
    /// </summary>
    /// <param name="id">The report id.</param>
    /// <param name="resolved">Whether it has been dealt with.</param>
    /// <returns>The report, or <c>null</c> when there is none with the id.</returns>
    ItemReport? SetResolved(Guid id, bool resolved);

    /// <summary>
    /// Deletes a report.
    /// </summary>
    /// <param name="id">The report id.</param>
    /// <returns>Whether there was one to delete.</returns>
    bool Delete(Guid id);
}
