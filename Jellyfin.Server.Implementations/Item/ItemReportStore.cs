using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Item;

/// <summary>
/// Keeps the reported problems in itemreports.json in the configuration folder (Finly).
/// </summary>
public sealed class ItemReportStore : IItemReportStore
{
    private readonly string _path;
    private readonly ILogger<ItemReportStore> _logger;
    private readonly object _lock = new();
    private List<ItemReport>? _reports;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemReportStore"/> class.
    /// </summary>
    /// <param name="appPaths">The application paths.</param>
    /// <param name="logger">The logger.</param>
    public ItemReportStore(IApplicationPaths appPaths, ILogger<ItemReportStore> logger)
    {
        _path = Path.Combine(appPaths.ConfigurationDirectoryPath, "itemreports.json");
        _logger = logger;
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

        if (File.Exists(_path))
        {
            try
            {
                _reports = JsonSerializer.Deserialize<List<ItemReport>>(File.ReadAllText(_path), JsonDefaults.Options) ?? [];
                return _reports;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                _logger.LogError(ex, "Unable to read the reported problems from {Path}", _path);
            }
        }

        _reports = [];
        return _reports;
    }

    private void Write(List<ItemReport> reports)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(reports, JsonDefaults.Options));
        File.Move(temp, _path, true);
        _reports = reports;
    }
}
