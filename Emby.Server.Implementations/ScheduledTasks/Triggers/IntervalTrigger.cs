using System;
using System.Threading;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Triggers;

/// <summary>
/// Represents a task trigger that runs repeatedly on an interval.
/// </summary>
public sealed class IntervalTrigger : ITaskTrigger, IDisposable
{
    private readonly TimeSpan _interval;
    private DateTime _lastStartDate;
    private Timer? _timer;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntervalTrigger"/> class.
    /// </summary>
    /// <param name="interval">The interval.</param>
    /// <param name="taskOptions">The options of this task.</param>
    public IntervalTrigger(TimeSpan interval, TaskOptions taskOptions)
    {
        _interval = interval;
        TaskOptions = taskOptions;
    }

    /// <inheritdoc />
    public event EventHandler<EventArgs>? Triggered;

    /// <inheritdoc />
    public TaskOptions TaskOptions { get; }

    /// <inheritdoc />
    public void Start(TaskResult? lastResult, ILogger logger, string taskName, bool isApplicationStartup)
    {
        DisposeTimer();

        var dueTime = GetDueTime(DateTime.UtcNow, lastResult?.EndTimeUtc, _lastStartDate, _interval, isApplicationStartup);

        _timer = new Timer(_ => OnTriggered(), null, dueTime, TimeSpan.FromMilliseconds(-1));
    }

    /// <summary>
    /// Gets how long to wait before the task next runs.
    /// </summary>
    /// <remarks>
    /// The interval counts from the last run rather than from now, so a server that restarts more often than the
    /// interval (for example a nightly backup that stops the container) still runs the task, instead of pushing it
    /// back on every start.
    /// </remarks>
    /// <param name="now">The current time in UTC.</param>
    /// <param name="lastEndTime">When the task last finished, or <c>null</c> if it has never run.</param>
    /// <param name="lastStartTime">When this trigger last started the task.</param>
    /// <param name="interval">The interval between runs.</param>
    /// <param name="isApplicationStartup">Whether the server is starting up.</param>
    /// <returns>The time to wait.</returns>
    internal static TimeSpan GetDueTime(DateTime now, DateTime? lastEndTime, DateTime lastStartTime, TimeSpan interval, bool isApplicationStartup)
    {
        DateTime triggerDate;

        if (lastEndTime is null)
        {
            // Task has never been completed before
            triggerDate = now.AddHours(1);
        }
        else
        {
            triggerDate = (lastEndTime.Value > lastStartTime ? lastEndTime.Value : lastStartTime).Add(interval);

            // An overdue task runs soon, but gives a starting server a few minutes to settle first
            var earliest = now.AddMinutes(isApplicationStartup ? 5 : 1);
            if (triggerDate < earliest)
            {
                triggerDate = earliest;
            }
        }

        var dueTime = triggerDate - now;
        var maxDueTime = TimeSpan.FromDays(7);

        return dueTime > maxDueTime ? maxDueTime : dueTime;
    }

    /// <inheritdoc />
    public void Stop()
    {
        DisposeTimer();
    }

    /// <summary>
    /// Disposes the timer.
    /// </summary>
    private void DisposeTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// Called when [triggered].
    /// </summary>
    private void OnTriggered()
    {
        DisposeTimer();

        if (Triggered is not null)
        {
            _lastStartDate = DateTime.UtcNow;
            Triggered(this, EventArgs.Empty);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        DisposeTimer();

        _disposed = true;
    }
}
