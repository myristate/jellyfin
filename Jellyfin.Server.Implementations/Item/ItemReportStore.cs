using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Server.Implementations.StorageHelpers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Item;

/// <summary>
/// Keeps the reported problems in itemreports.json in the configuration folder (Finly). A file that can't be read is
/// set aside as itemreports.json.bad-&lt;time&gt; and a new one started.
/// </summary>
public sealed class ItemReportStore : IItemReportStore
{
    /// <summary>
    /// The most reports one person can make in an hour.
    /// </summary>
    public const int MaxReportsPerHour = 20;

    private readonly JsonListFile<ItemReport> _file;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, Queue<DateTime>> _recentReports = [];
    private List<ItemReport>? _reports;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemReportStore"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public ItemReportStore(IApplicationPaths appPaths, ILogger<ItemReportStore> logger)
    {
        _file = new JsonListFile<ItemReport>(Path.Combine(appPaths.ConfigurationDirectoryPath, "itemreports.json"), logger);
    }

    /// <summary>
    /// Gets or sets the clock the report limit runs on, replaced in tests.
    /// </summary>
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <inheritdoc />
    public bool TryCountReport(Guid userId)
    {
        var now = TimeProvider.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            if (!_recentReports.TryGetValue(userId, out var times))
            {
                times = new Queue<DateTime>();
                _recentReports[userId] = times;
            }

            while (times.Count > 0 && now - times.Peek() >= TimeSpan.FromHours(1))
            {
                times.Dequeue();
            }

            if (times.Count >= MaxReportsPerHour)
            {
                return false;
            }

            times.Enqueue(now);
            return true;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ItemReport> GetReports()
    {
        lock (_lock)
        {
            return Load().OrderByDescending(r => r.DateCreated).ToList();
        }
    }

    /// <inheritdoc />
    public ItemReport Add(ItemReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        lock (_lock)
        {
            var reports = Load();
            report.Note = string.IsNullOrWhiteSpace(report.Note) ? null : report.Note.Trim();

            var open = reports.FirstOrDefault(r => !r.IsResolved
                && r.ItemId.Equals(report.ItemId)
                && r.UserId.Equals(report.UserId)
                && r.Problem == report.Problem);
            if (open is not null)
            {
                open.Note = report.Note ?? open.Note;
                open.DateCreated = DateTime.UtcNow;
                Write(reports);
                return open;
            }

            report.Id = Guid.NewGuid();
            report.DateCreated = DateTime.UtcNow;
            report.IsResolved = false;
            report.DateResolved = null;
            reports.Add(report);
            Write(reports);
            return report;
        }
    }

    /// <inheritdoc />
    public ItemReport? SetResolved(Guid id, bool resolved)
    {
        lock (_lock)
        {
            var reports = Load();
            var report = reports.FirstOrDefault(r => r.Id.Equals(id));
            if (report is null)
            {
                return null;
            }

            report.IsResolved = resolved;
            report.DateResolved = resolved ? DateTime.UtcNow : null;
            Write(reports);
            return report;
        }
    }

    /// <inheritdoc />
    public bool Delete(Guid id)
    {
        lock (_lock)
        {
            var reports = Load();
            if (reports.RemoveAll(r => r.Id.Equals(id)) == 0)
            {
                return false;
            }

            Write(reports);
            return true;
        }
    }

    private List<ItemReport> Load()
    {
        if (_reports is not null)
        {
            return _reports;
        }

        _reports = _file.Read() ?? [];
        return _reports;
    }

    private void Write(List<ItemReport> reports)
    {
        _file.Write(reports);
        _reports = reports;
    }
}
