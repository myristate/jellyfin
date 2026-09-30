using System;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Controller.Events;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Server.Implementations.Events.Consumers.Library
{
    /// <summary>
    /// Finds the items removed from, or allowed for, profiles again after a library scan, which is when moved or renamed
    /// files get their new ids (Finly).
    /// </summary>
    public class RelinkRemovedItemsAfterScan : IEventConsumer<TaskCompletionEventArgs>
    {
        private const string LibraryScanKey = "RefreshLibrary";

        private readonly ITaskManager _taskManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="RelinkRemovedItemsAfterScan"/> class.
        /// </summary>
        /// <param name="taskManager">The task manager.</param>
        public RelinkRemovedItemsAfterScan(ITaskManager taskManager)
        {
            _taskManager = taskManager;
        }

        /// <inheritdoc />
        public Task OnEvent(TaskCompletionEventArgs eventArgs)
        {
            ArgumentNullException.ThrowIfNull(eventArgs);
            if (string.Equals(eventArgs.Task.ScheduledTask.Key, LibraryScanKey, StringComparison.Ordinal)
                && eventArgs.Result.Status == TaskCompletionStatus.Completed)
            {
                _taskManager.QueueIfNotRunning<RelinkRemovedItemsTask>();
            }

            return Task.CompletedTask;
        }
    }
}
