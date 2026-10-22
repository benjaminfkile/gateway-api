using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Gateway.Api.Auth;
using Gateway.Api.Data;
using Gateway.Api.RealTime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gateway.Api.Management;

/// <summary>
/// Body of <c>PUT /mgmt/jobs/{name}</c> (create or update a job). On an update an
/// absent field preserves the stored value; an empty string clears the nullable
/// <see cref="Schedule"/> and <see cref="EnvSecretRef"/>. A create requires
/// <see cref="TimeZone"/> and <see cref="Script"/>.
/// </summary>
public sealed record UpsertJobRequest(
    string? Schedule = null,
    string? TimeZone = null,
    string? Script = null,
    string? Image = null,
    string? Tag = null,
    string? EnvSecretRef = null,
    int? TimeoutSeconds = null,
    bool? Enabled = null);

/// <summary>
/// The scheduled-job Management API under <c>/mgmt/jobs</c> (docs/jobs.md section
/// 4). Reads need OpsDeploy, mutations need OpsJobs, run logs need OpsAdmin. Every
/// mutation records the caller in <c>updated_by</c> or <c>actor</c> and publishes a
/// best-effort event on <see cref="OpsJobsChannel"/>. There is no executor here: a
/// queued run stays queued until the job scheduler claims it.
/// </summary>
public static partial class JobEndpoints
{
    /// <summary>Hub channel the dashboard listens on for live job and run events.</summary>
    public const string OpsJobsChannel = "ops:jobs";

    /// <summary>Configuration key for the runner repository a job gets when PUT omits <c>image</c>.</summary>
    public const string DefaultImageKey = "Jobs:DefaultImage";

    /// <summary>Configuration key for the tag a job gets when PUT omits <c>tag</c>.</summary>
    public const string DefaultTagKey = "Jobs:DefaultTag";

    /// <summary>Placeholder runner repository used when <see cref="DefaultImageKey"/> is unset.</summary>
    public const string FallbackImage = "gateway-job-runner";

    /// <summary>Tag used when <see cref="DefaultTagKey"/> is unset.</summary>
    public const string FallbackTag = "latest";

    public const int DefaultTimeoutSeconds = 3600;
    public const int MinTimeoutSeconds = 30;
    public const int MaxTimeoutSeconds = 21600;

    /// <summary>Largest accepted script body, in UTF-8 bytes (256 KiB).</summary>
    public const int MaxScriptBytes = 262144;

    /// <summary>DI key of the <see cref="TimeProvider"/> the job endpoints read the time from.</summary>
    public const string ClockKey = "jobs";

    public const int DefaultRunsLimit = 50;
    public const int MaxRunsLimit = 500;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$")]
    private static partial Regex JobNameRegex();

    /// <summary>Map the job endpoints onto <c>/mgmt/jobs</c>.</summary>
    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var jobs = endpoints.MapGroup("/mgmt/jobs");

        jobs.MapGet("", ListJobsAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        jobs.MapGet("/{name}", GetJobAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        jobs.MapGet("/{name}/runs", ListRunsAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        jobs.MapGet("/{name}/runs/{id:long}", GetRunAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        jobs.MapGet("/{name}/script/versions", ListScriptVersionsAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        jobs.MapGet("/{name}/script/versions/{version:int}", GetScriptVersionAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);

        jobs.MapGet("/{name}/runs/{id:long}/log", GetRunLogAsync).RequireAuthorization(ManagementAuth.OpsAdminPolicy);

        jobs.MapPut("/{name}", UpsertJobAsync).RequireAuthorization(ManagementAuth.OpsJobsPolicy);
        jobs.MapDelete("/{name}", DeleteJobAsync).RequireAuthorization(ManagementAuth.OpsJobsPolicy);
        jobs.MapPost("/{name}/run", RunJobAsync).RequireAuthorization(ManagementAuth.OpsJobsPolicy);
        jobs.MapPost("/{name}/enable", EnableAsync).RequireAuthorization(ManagementAuth.OpsJobsPolicy);
        jobs.MapPost("/{name}/disable", DisableAsync).RequireAuthorization(ManagementAuth.OpsJobsPolicy);
        jobs.MapPost("/{name}/runs/{id:long}/cancel", CancelRunAsync).RequireAuthorization(ManagementAuth.OpsJobsPolicy);

        return endpoints;
    }

    // ---------- reads (OpsDeploy) ----------

    private static async Task<IResult> ListJobsAsync(IJobStore store, CancellationToken ct)
    {
        var jobs = await store.ListAsync(ct);
        var lastRuns = await store.GetLastRunsAsync(ct);

        // Summaries only: the script body is never part of the list.
        return Results.Json(jobs
            .Select(j => SummaryView(j, lastRuns.TryGetValue(j.Name, out var run) ? run : null))
            .ToList());
    }

    private static async Task<IResult> GetJobAsync(string name, IJobStore store, CancellationToken ct)
    {
        var job = await store.GetAsync(name, ct);
        if (job is null)
        {
            return NotFoundJob(name);
        }

        return Results.Json(DetailView(job, await LastRunAsync(store, name, ct)));
    }

    private static async Task<IResult> ListRunsAsync(
        string name,
        int? limit,
        long? before,
        IJobStore store,
        CancellationToken ct)
    {
        // History outlives a deleted job, so runs are listed whether or not the job
        // row still exists.
        var take = Math.Clamp(limit ?? DefaultRunsLimit, 1, MaxRunsLimit);
        var runs = await store.ListRunsAsync(name, take, before, ct);
        return Results.Json(runs.Select(RunView).ToList());
    }

    private static async Task<IResult> GetRunAsync(string name, long id, IJobStore store, CancellationToken ct)
    {
        var run = await FindRunAsync(store, name, id, ct);
        return run is null ? NotFoundRun(name, id) : Results.Json(RunView(run));
    }

    private static async Task<IResult> ListScriptVersionsAsync(string name, IJobStore store, CancellationToken ct)
    {
        if (await store.GetAsync(name, ct) is null)
        {
            return NotFoundJob(name);
        }

        var versions = await store.ListScriptVersionsAsync(name, ct);
        return Results.Json(versions.Select(v => new
        {
            version = v.Version,
            sha256 = v.Sha256,
            updatedBy = v.UpdatedBy,
            updatedAt = v.UpdatedAt,
        }).ToList());
    }

    private static async Task<IResult> GetScriptVersionAsync(
        string name,
        int version,
        IJobStore store,
        CancellationToken ct)
    {
        var row = await store.GetScriptVersionAsync(name, version, ct);
        if (row is null)
        {
            return Results.NotFound(new { error = $"Job '{name}' has no script version {version}." });
        }

        return Results.Json(new { version = row.Version, script = row.Script });
    }

    private static async Task<IResult> GetRunLogAsync(
        string name,
        long id,
        int? tail,
        IJobStore store,
        ILogStore logs,
        CancellationToken ct)
    {
        var run = await FindRunAsync(store, name, id, ct);
        if (run is null)
        {
            return NotFoundRun(name, id);
        }

        // Same window as service logs: default 500, clamped.
        var count = Math.Clamp(tail ?? 500, 1, 10_000);
        var lines = await logs.GetStreamLogsAsync(
            CloudWatchLogStore.JobLogGroupFor(name),
            CloudWatchLogStore.JobRunStreamFor(id),
            count,
            ct);
        return Results.Json(lines.Select(l => new { ts = l.Ts, message = l.Message }).ToList());
    }

    // ---------- mutations (OpsJobs) ----------

    private static async Task<IResult> UpsertJobAsync(
        string name,
        UpsertJobRequest request,
        ClaimsPrincipal user,
        IJobStore store,
        IConfiguration config,
        IChannelEventPublisher publisher,
        [FromKeyedServices(ClockKey)] TimeProvider time,
        CancellationToken ct)
    {
        if (!JobNameRegex().IsMatch(name))
        {
            return Invalid("name", "Job name must match ^[a-z0-9][a-z0-9-]{1,62}$.");
        }

        var existing = await store.GetAsync(name, ct);

        if (existing is null && request.TimeZone is null)
        {
            return Invalid("timeZone", "timeZone is required when creating a job.");
        }

        if (existing is null && request.Script is null)
        {
            return Invalid("script", "script is required when creating a job.");
        }

        // schedule: absent preserves, empty string clears (manual only), otherwise a
        // five-field cron expression stored in its normalised spacing.
        string? schedule;
        if (request.Schedule is null)
        {
            schedule = existing?.Schedule;
        }
        else if (request.Schedule.Trim().Length == 0)
        {
            schedule = null;
        }
        else if (!JobSchedule.TryParse(request.Schedule, out _, out var scheduleError))
        {
            return Invalid("schedule", scheduleError!);
        }
        else
        {
            schedule = string.Join(' ', request.Schedule.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        var timeZone = request.TimeZone?.Trim() ?? existing!.TimeZone;
        if (!JobSchedule.TryFindZone(timeZone, out _))
        {
            return Invalid("timeZone", $"timeZone '{timeZone}' is not a known IANA time zone id.");
        }

        var timeoutSeconds = request.TimeoutSeconds ?? existing?.TimeoutSeconds ?? DefaultTimeoutSeconds;
        if (timeoutSeconds is < MinTimeoutSeconds or > MaxTimeoutSeconds)
        {
            return Invalid("timeoutSeconds", $"timeoutSeconds must be in the range {MinTimeoutSeconds}..{MaxTimeoutSeconds}.");
        }

        var script = request.Script ?? existing!.Script;
        if (string.IsNullOrWhiteSpace(script))
        {
            return Invalid("script", "script must not be empty.");
        }

        if (Encoding.UTF8.GetByteCount(script) > MaxScriptBytes)
        {
            return Invalid("script", $"script must be at most {MaxScriptBytes} bytes.");
        }

        if (request.Image is not null && request.Image.Trim().Length == 0)
        {
            return Invalid("image", "image must not be empty.");
        }

        if (request.Tag is not null && request.Tag.Trim().Length == 0)
        {
            return Invalid("tag", "tag must not be empty.");
        }

        var image = request.Image?.Trim()
            ?? existing?.Image
            ?? NonEmpty(config[DefaultImageKey])
            ?? FallbackImage;
        var tag = request.Tag?.Trim()
            ?? existing?.Tag
            ?? NonEmpty(config[DefaultTagKey])
            ?? FallbackTag;

        var actor = ManagementAuth.ResolveUsername(user);
        var now = time.GetUtcNow();

        // A new body (or a create) bumps the version and writes a history row; an
        // identical body leaves both alone.
        var scriptChanged = existing is null || !string.Equals(existing.Script, script, StringComparison.Ordinal);
        var scriptVersion = existing is null ? 1 : existing.ScriptVersion + (scriptChanged ? 1 : 0);

        var job = new Job
        {
            Name = name,
            Schedule = schedule,
            TimeZone = timeZone,
            Script = script,
            ScriptVersion = scriptVersion,
            Image = image,
            Tag = tag,
            // Tri-state: absent preserves, empty string clears, non-empty sets.
            EnvSecretRef = request.EnvSecretRef is null
                ? existing?.EnvSecretRef
                : NonEmpty(request.EnvSecretRef.Trim()),
            TimeoutSeconds = timeoutSeconds,
            Enabled = request.Enabled ?? existing?.Enabled ?? true,
            UpdatedBy = actor,
            UpdatedAt = now,
        };
        job.NextRunAt = JobSchedule.ComputeNextRunAt(job, now);

        var history = scriptChanged
            ? new JobScriptVersion
            {
                Job = name,
                Version = scriptVersion,
                Script = script,
                Sha256 = Sha256Hex(script),
                UpdatedBy = actor,
                UpdatedAt = now,
            }
            : null;

        await store.UpsertAsync(job, history, ct);
        BroadcastJob(publisher, name, "upserted", actor);

        var body = DetailView(job, await LastRunAsync(store, name, ct));
        return existing is null
            ? Results.Json(body, statusCode: StatusCodes.Status201Created)
            : Results.Json(body);
    }

    private static async Task<IResult> DeleteJobAsync(
        string name,
        ClaimsPrincipal user,
        IJobStore store,
        IChannelEventPublisher publisher,
        [FromKeyedServices(ClockKey)] TimeProvider time,
        CancellationToken ct,
        bool force = false)
    {
        if (await store.GetAsync(name, ct) is null)
        {
            return NotFoundJob(name);
        }

        var active = await store.GetActiveRunAsync(name, ct);
        if (active is not null)
        {
            if (!force)
            {
                return Results.Conflict(new { error = "run_active" });
            }

            // Forced: cancel the active run exactly as the cancel endpoint does, then
            // delete. A running run keeps its row and is killed by its instance.
            var before = active.Status;
            var after = await store.CancelRunAsync(active.Id, time.GetUtcNow(), ct);
            if (after is not null && after.Status != before)
            {
                BroadcastRun(publisher, after);
            }
        }

        await store.DeleteAsync(name, ct);
        BroadcastJob(publisher, name, "deleted", ManagementAuth.ResolveUsername(user));

        return Results.NoContent();
    }

    private static async Task<IResult> RunJobAsync(
        string name,
        ClaimsPrincipal user,
        IJobStore store,
        IChannelEventPublisher publisher,
        [FromKeyedServices(ClockKey)] TimeProvider time,
        CancellationToken ct)
    {
        // Allowed on a disabled job: disabled turns the schedule off, not the job.
        var job = await store.GetAsync(name, ct);
        if (job is null)
        {
            return NotFoundJob(name);
        }

        var run = await store.TryEnqueueRunAsync(new JobRun
        {
            Job = name,
            Trigger = JobRunTrigger.Manual,
            Actor = ManagementAuth.ResolveUsername(user),
            Status = JobRunStatus.Queued,
            ScriptVersion = job.ScriptVersion,
            QueuedAt = time.GetUtcNow(),
        }, ct);

        if (run is null)
        {
            return Results.Conflict(new { error = "run_active" });
        }

        BroadcastRun(publisher, run);
        return Results.Json(new { runId = run.Id }, statusCode: StatusCodes.Status202Accepted);
    }

    private static Task<IResult> EnableAsync(
        string name,
        ClaimsPrincipal user,
        IJobStore store,
        IChannelEventPublisher publisher,
        [FromKeyedServices(ClockKey)] TimeProvider time,
        CancellationToken ct) =>
        SetEnabledAsync(name, true, user, store, publisher, time, ct);

    private static Task<IResult> DisableAsync(
        string name,
        ClaimsPrincipal user,
        IJobStore store,
        IChannelEventPublisher publisher,
        [FromKeyedServices(ClockKey)] TimeProvider time,
        CancellationToken ct) =>
        SetEnabledAsync(name, false, user, store, publisher, time, ct);

    private static async Task<IResult> SetEnabledAsync(
        string name,
        bool enabled,
        ClaimsPrincipal user,
        IJobStore store,
        IChannelEventPublisher publisher,
        [FromKeyedServices(ClockKey)] TimeProvider time,
        CancellationToken ct)
    {
        var job = await store.GetAsync(name, ct);
        if (job is null)
        {
            return NotFoundJob(name);
        }

        var actor = ManagementAuth.ResolveUsername(user);
        var now = time.GetUtcNow();
        job.Enabled = enabled;
        job.NextRunAt = JobSchedule.ComputeNextRunAt(job, now);
        job.UpdatedBy = actor;
        job.UpdatedAt = now;

        await store.UpsertAsync(job, null, ct);
        BroadcastJob(publisher, name, enabled ? "enabled" : "disabled", actor);

        return Results.Json(SummaryView(job, await LastRunAsync(store, name, ct)));
    }

    private static async Task<IResult> CancelRunAsync(
        string name,
        long id,
        IJobStore store,
        IChannelEventPublisher publisher,
        [FromKeyedServices(ClockKey)] TimeProvider time,
        CancellationToken ct)
    {
        var run = await FindRunAsync(store, name, id, ct);
        if (run is null)
        {
            return NotFoundRun(name, id);
        }

        if (!JobRunStatus.IsActive(run.Status))
        {
            return Results.Conflict(new { error = "run_finished" });
        }

        var after = await store.CancelRunAsync(id, time.GetUtcNow(), ct);
        if (after is null || (!JobRunStatus.IsActive(after.Status) && after.Status != JobRunStatus.Cancelled))
        {
            // Finished on its own between the read and the update.
            return Results.Conflict(new { error = "run_finished" });
        }

        if (after.Status != run.Status)
        {
            BroadcastRun(publisher, after);
        }

        return Results.Json(RunView(after), statusCode: StatusCodes.Status202Accepted);
    }

    // ---------- events ----------

    /// <summary>
    /// Fire-and-forget a <c>job</c> event on <c>ops:jobs</c>. Runs after the change has
    /// committed; a backplane failure never fails the request.
    /// </summary>
    public static void BroadcastJob(IChannelEventPublisher publisher, string name, string action, string by) =>
        publisher.TryPublish(OpsJobsChannel, "job", new { name, action, by });

    /// <summary>
    /// Fire-and-forget a <c>run</c> event on <c>ops:jobs</c>; called on every run
    /// status change, best-effort like <see cref="BroadcastJob"/>.
    /// </summary>
    public static void BroadcastRun(IChannelEventPublisher publisher, JobRun run) =>
        publisher.TryPublish(OpsJobsChannel, "run", new
        {
            runId = run.Id,
            job = run.Job,
            status = run.Status,
            trigger = run.Trigger,
            actor = run.Actor,
            instanceId = run.InstanceId,
            startedAt = run.StartedAt,
            finishedAt = run.FinishedAt,
            exitCode = run.ExitCode,
        });

    // ---------- helpers ----------

    private static IResult Invalid(string field, string error) =>
        Results.BadRequest(new { error, field });

    private static IResult NotFoundJob(string name) =>
        Results.NotFound(new { error = $"No job named '{name}'." });

    private static IResult NotFoundRun(string name, long id) =>
        Results.NotFound(new { error = $"Job '{name}' has no run {id}." });

    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Lowercase hex sha256 of the UTF-8 script body.</summary>
    public static string Sha256Hex(string script) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(script)));

    private static async Task<JobRun?> FindRunAsync(IJobStore store, string name, long id, CancellationToken ct)
    {
        var run = await store.GetRunAsync(id, ct);
        return run is not null && string.Equals(run.Job, name, StringComparison.Ordinal) ? run : null;
    }

    private static async Task<JobRun?> LastRunAsync(IJobStore store, string name, CancellationToken ct)
    {
        var runs = await store.ListRunsAsync(name, 1, null, ct);
        return runs.Count > 0 ? runs[0] : null;
    }

    private static Dictionary<string, object?> SummaryView(Job j, JobRun? lastRun) => new()
    {
        ["name"] = j.Name,
        ["schedule"] = j.Schedule,
        ["timeZone"] = j.TimeZone,
        ["image"] = j.Image,
        ["tag"] = j.Tag,
        // A secret name only; values are never read or returned here.
        ["envSecretRef"] = j.EnvSecretRef,
        ["timeoutSeconds"] = j.TimeoutSeconds,
        ["enabled"] = j.Enabled,
        ["scriptVersion"] = j.ScriptVersion,
        ["nextRunAt"] = j.NextRunAt,
        ["lastRun"] = lastRun is null ? null : RunView(lastRun),
        ["updatedBy"] = j.UpdatedBy,
        ["updatedAt"] = j.UpdatedAt,
    };

    private static Dictionary<string, object?> DetailView(Job j, JobRun? lastRun)
    {
        var view = SummaryView(j, lastRun);
        view["script"] = j.Script;
        return view;
    }

    private static object RunView(JobRun r) => new
    {
        id = r.Id,
        job = r.Job,
        trigger = r.Trigger,
        actor = r.Actor,
        status = r.Status,
        instanceId = r.InstanceId,
        imageDigest = r.ImageDigest,
        scriptVersion = r.ScriptVersion,
        exitCode = r.ExitCode,
        queuedAt = r.QueuedAt,
        startedAt = r.StartedAt,
        finishedAt = r.FinishedAt,
        cancelRequestedAt = r.CancelRequestedAt,
        detail = r.Detail,
    };
}
