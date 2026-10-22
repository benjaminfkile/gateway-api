using Cronos;
using Gateway.Api.Data;

namespace Gateway.Api.Management;

/// <summary>
/// Cron and time-zone handling for scheduled jobs (docs/jobs.md section 2). A
/// schedule is the standard five cron fields evaluated in the job's IANA zone;
/// seconds and macros are rejected. Daylight saving follows Cronos rules: a fire
/// time inside a spring-forward gap runs at the end of the gap, and a fire time in
/// a fall-back overlap runs once.
/// </summary>
public static class JobSchedule
{
    /// <summary>
    /// Parse a five-field cron expression. Returns false with a reason when the text
    /// does not have exactly five fields or Cronos rejects it.
    /// </summary>
    public static bool TryParse(string schedule, out CronExpression? expression, out string? error)
    {
        expression = null;
        var fields = schedule.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = "schedule must be a 5-field cron expression (minute hour day-of-month month day-of-week); seconds are not supported.";
            return false;
        }

        try
        {
            expression = CronExpression.Parse(string.Join(' ', fields), CronFormat.Standard);
            error = null;
            return true;
        }
        catch (CronFormatException ex)
        {
            error = $"schedule is not a valid cron expression: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Resolve an IANA time-zone id. Windows ids are rejected even where the
    /// platform would convert them, so the stored value is always IANA.
    /// </summary>
    public static bool TryFindZone(string timeZone, out TimeZoneInfo? zone)
    {
        zone = null;
        if (string.IsNullOrWhiteSpace(timeZone))
        {
            return false;
        }

        try
        {
            var found = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            if (!found.HasIanaId)
            {
                return false;
            }

            zone = found;
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    /// <summary>
    /// The first occurrence of <paramref name="schedule"/> strictly after
    /// <paramref name="after"/>, evaluated in <paramref name="timeZone"/> and returned
    /// in UTC. Null when the expression never fires again.
    /// </summary>
    public static DateTimeOffset? NextOccurrence(string schedule, string timeZone, DateTimeOffset after)
    {
        if (!TryParse(schedule, out var expression, out var error))
        {
            throw new ArgumentException(error, nameof(schedule));
        }

        if (!TryFindZone(timeZone, out var zone))
        {
            throw new ArgumentException($"Unknown IANA time zone '{timeZone}'.", nameof(timeZone));
        }

        return expression!.GetNextOccurrence(after.ToUniversalTime(), zone!)?.ToUniversalTime();
    }

    /// <summary>
    /// The value <c>next_run_at</c> takes for <paramref name="job"/> at
    /// <paramref name="now"/>: null when the job is disabled or has no schedule,
    /// otherwise the next occurrence after now.
    /// </summary>
    public static DateTimeOffset? ComputeNextRunAt(Job job, DateTimeOffset now) =>
        !job.Enabled || string.IsNullOrEmpty(job.Schedule)
            ? null
            : NextOccurrence(job.Schedule, job.TimeZone, now);
}
