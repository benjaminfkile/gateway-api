using Gateway.Api.Data;
using Gateway.Api.Management;

namespace Gateway.Api.Tests;

/// <summary>
/// Cron parsing, zone validation and <c>next_run_at</c> computation for scheduled
/// jobs (docs/jobs.md section 2): five fields only, IANA zones only, and fire
/// times evaluated in the job's zone across daylight saving changes.
/// </summary>
public class JobScheduleTests
{
    private const string Chicago = "America/Chicago";

    [Theory]
    [InlineData("0 4 * * 1")]
    [InlineData("*/15 * * * *")]
    [InlineData("  0   4 * *   MON-FRI ")]
    public void TryParse_AcceptsFiveFieldExpressions(string schedule)
    {
        Assert.True(JobSchedule.TryParse(schedule, out var expression, out var error));
        Assert.NotNull(expression);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("0 0 4 * * 1")] // six fields: seconds
    [InlineData("0 4 * *")]
    [InlineData("@daily")]
    [InlineData("61 4 * * *")]
    [InlineData("not a cron")]
    public void TryParse_RejectsAnythingButValidFiveFieldCron(string schedule)
    {
        Assert.False(JobSchedule.TryParse(schedule, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData(Chicago, true)]
    [InlineData("UTC", true)]
    [InlineData("Europe/London", true)]
    [InlineData("Mars/Olympus_Mons", false)]
    [InlineData("Central Standard Time", false)] // a Windows id, not IANA
    [InlineData("", false)]
    public void TryFindZone_AcceptsKnownIanaIdsOnly(string zone, bool expected)
    {
        Assert.Equal(expected, JobSchedule.TryFindZone(zone, out _));
    }

    [Fact]
    public void NextOccurrence_FollowsTheZoneAcrossSpringForward()
    {
        // Monday 04:00 in Chicago is 10:00Z under CST and 09:00Z under CDT; DST
        // starts Sunday 2026-03-08.
        var afterCst = JobSchedule.NextOccurrence("0 4 * * 1", Chicago, new DateTimeOffset(2026, 3, 2, 10, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 3, 9, 9, 0, 0, TimeSpan.Zero), afterCst);
    }

    [Fact]
    public void NextOccurrence_InTheSpringGap_FiresAtTheEndOfTheGap()
    {
        // 02:30 does not exist on 2026-03-08 in Chicago; Cronos fires at 03:00 CDT.
        var next = JobSchedule.NextOccurrence("30 2 * * *", Chicago, new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public void NextOccurrence_InTheFallOverlap_FiresOnce()
    {
        // 01:30 happens twice on 2026-11-01 in Chicago (CDT then CST). The job runs at
        // the first one only; the next run is the following day under CST.
        var first = JobSchedule.NextOccurrence("30 1 * * *", Chicago, new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero), first);

        var second = JobSchedule.NextOccurrence("30 1 * * *", Chicago, first!.Value);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 7, 30, 0, TimeSpan.Zero), second);
    }

    [Fact]
    public void ComputeNextRunAt_IsNullWhenDisabledOrUnscheduled()
    {
        var now = new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.Zero);
        var job = new Job { Name = "nightly", Schedule = "0 4 * * *", TimeZone = Chicago, Enabled = true };

        Assert.Equal(new DateTimeOffset(2026, 3, 8, 9, 0, 0, TimeSpan.Zero), JobSchedule.ComputeNextRunAt(job, now));

        job.Enabled = false;
        Assert.Null(JobSchedule.ComputeNextRunAt(job, now));

        job.Enabled = true;
        job.Schedule = null;
        Assert.Null(JobSchedule.ComputeNextRunAt(job, now));
    }
}
