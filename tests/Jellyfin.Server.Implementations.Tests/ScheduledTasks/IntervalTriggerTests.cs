using System;
using Emby.Server.Implementations.ScheduledTasks.Triggers;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.ScheduledTasks
{
    public class IntervalTriggerTests
    {
        private static readonly DateTime _now = new DateTime(2026, 9, 30, 1, 3, 0, DateTimeKind.Utc);
        private static readonly TimeSpan _day = TimeSpan.FromHours(24);

        [Fact]
        public void GetDueTime_NeverRun_RunsInAnHour()
        {
            Assert.Equal(TimeSpan.FromHours(1), IntervalTrigger.GetDueTime(_now, null, DateTime.MinValue, _day, true));
        }

        [Fact]
        public void GetDueTime_RestartBeforeIntervalEnds_KeepsCountingFromLastRun()
        {
            // Last ran 20 hours ago, then the server restarted: the task is due in 4 hours, not 24
            var lastEnd = _now.AddHours(-20);

            Assert.Equal(TimeSpan.FromHours(4), IntervalTrigger.GetDueTime(_now, lastEnd, DateTime.MinValue, _day, true));
        }

        [Fact]
        public void GetDueTime_OverdueAtStartup_RunsAfterStartupSettles()
        {
            var lastEnd = _now.AddDays(-30);

            Assert.Equal(TimeSpan.FromMinutes(5), IntervalTrigger.GetDueTime(_now, lastEnd, DateTime.MinValue, _day, true));
        }

        [Fact]
        public void GetDueTime_OverdueWhileRunning_RunsInAMinute()
        {
            var lastEnd = _now.AddDays(-2);

            Assert.Equal(TimeSpan.FromMinutes(1), IntervalTrigger.GetDueTime(_now, lastEnd, DateTime.MinValue, _day, false));
        }

        [Fact]
        public void GetDueTime_JustFinished_WaitsFullInterval()
        {
            Assert.Equal(_day, IntervalTrigger.GetDueTime(_now, _now, DateTime.MinValue, _day, false));
        }

        [Fact]
        public void GetDueTime_StartedAfterLastEnd_CountsFromStart()
        {
            // The task was started 2 hours ago and is still running: count from the start, not the older end time
            var lastEnd = _now.AddHours(-30);
            var lastStart = _now.AddHours(-2);

            Assert.Equal(TimeSpan.FromHours(22), IntervalTrigger.GetDueTime(_now, lastEnd, lastStart, _day, false));
        }

        [Fact]
        public void GetDueTime_LongInterval_CappedAtSevenDays()
        {
            Assert.Equal(TimeSpan.FromDays(7), IntervalTrigger.GetDueTime(_now, _now, DateTime.MinValue, TimeSpan.FromDays(30), false));
        }
    }
}
