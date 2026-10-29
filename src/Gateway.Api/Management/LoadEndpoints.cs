using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Gateway.Api.Auth;
using Gateway.Api.Data;
using Gateway.Api.Load;
using Gateway.Api.RealTime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gateway.Api.Management;

/// <summary>
/// Body of <c>PUT /mgmt/load/scenarios/{name}</c> (create or update a scenario). On an
/// update an absent field preserves the stored value; an empty <see cref="CommitSha"/>
/// clears it. A create requires <see cref="Repository"/>, <see cref="Script"/> and
/// <see cref="MaxVusPerGenerator"/>.
/// </summary>
public sealed record UpsertLoadScenarioRequest(
    string? Repository = null,
    string? CommitSha = null,
    string? Script = null,
    string? Image = null,
    string? Tag = null,
    JsonElement? ParamsDefault = null,
    int? MaxVusPerGenerator = null);

/// <summary>
/// Body of <c>POST /mgmt/load/runs</c> (Start). For a validation run the generator
/// count, users, ramp and hold are set by the gateway (1, 1, 0 and
/// <c>Load:ValidationHoldSeconds</c>) and the sent values are ignored.
/// </summary>
public sealed record StartLoadRunRequest(
    string? Scenario = null,
    string? Target = null,
    string? Confirm = null,
    int? Generators = null,
    int? VusPerGenerator = null,
    int? RampSeconds = null,
    int? HoldSeconds = null,
    JsonElement? Params = null,
    int? FleetSize = null,
    bool? RestoreFleet = null,
    bool? Validation = null);

/// <summary>Body of <c>PUT /mgmt/load/runs/{id}/verdict</c>; null or empty clears it.</summary>
public sealed record LoadVerdictRequest(string? Verdict = null);

/// <summary>
/// The load Management API under <c>/mgmt/load</c> (docs/load.md section 8). Reads
/// need OpsDeploy, mutations need OpsLoad, generator logs need OpsAdmin. The two
/// generator endpoints (<c>checkin</c> and <c>stats</c>) take the run token instead
/// of Cognito: anonymous, rate limited per source address, a 64 KiB body cap, and the
/// same <c>401</c> for a wrong token and an unknown run. Every mutation publishes a
/// best-effort event on <see cref="OpsLoadChannel"/>. There is no scheduler here: a
/// queued run stays queued until the load scheduler claims it.
/// </summary>
public static partial class LoadEndpoints
{
    /// <summary>Hub channel the dashboard listens on for live load events.</summary>
    public const string OpsLoadChannel = "ops:load";

    /// <summary>DI key of the <see cref="TimeProvider"/> the load endpoints read the time from.</summary>
    public const string ClockKey = "load";

    /// <summary>Header the generators present the run token in.</summary>
    public const string TokenHeader = "X-Gateway-Load-Token";

    /// <summary>Largest accepted scenario script, in UTF-8 bytes (256 KiB).</summary>
    public const int MaxScriptBytes = 262144;

    /// <summary>Largest accepted body on the run-token endpoints (64 KiB).</summary>
    public const int MaxTokenBodyBytes = 65536;

    public const int MinVusPerGenerator = 1;
    public const int MaxVusPerGeneratorLimit = 100000;
    public const int MaxRampSeconds = 7200;
    public const int MaxHoldSeconds = 10800;
    public const int MaxVerdictLength = 500;
    public const int MaxInstanceIdLength = 128;

    /// <summary>Slack after ramp and hold before the hard clock (10 minutes).</summary>
    public static readonly TimeSpan HardStopSlack = TimeSpan.FromMinutes(10);

    /// <summary>The longest a run may live from Start (4 hours).</summary>
    public static readonly TimeSpan HardStopCeiling = TimeSpan.FromHours(4);

    public const int DefaultRunsLimit = 50;
    public const int MaxRunsLimit = 500;
    public const int MaxStepSeconds = 86400;

    // Compared against when the run is unknown, so an unknown run costs the same hash
    // and comparison as a wrong token.
    private static readonly string UnknownRunHash = new('0', 64);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,62}$")]
    private static partial Regex ScenarioNameRegex();

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryRegex();

    [GeneratedRegex("^[0-9a-fA-F]{7,64}$")]
    private static partial Regex CommitShaRegex();

    /// <summary>Map the load endpoints onto <c>/mgmt/load</c>.</summary>
    public static IEndpointRouteBuilder MapLoadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var load = endpoints.MapGroup("/mgmt/load");

        load.MapGet("/scenarios", ListScenariosAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        load.MapGet("/scenarios/{name}", GetScenarioAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        load.MapGet("/scenarios/{name}/script/versions", ListScriptVersionsAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        load.MapGet("/scenarios/{name}/script/versions/{version:int}", GetScriptVersionAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        load.MapGet("/preflight", PreflightAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        load.MapGet("/runs", ListRunsAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        load.MapGet("/runs/{id:long}", GetRunAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        load.MapGet("/runs/{id:long}/stats", GetStatsAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        load.MapGet("/runs/{id:long}/stats.csv", GetStatsCsvAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);

        load.MapGet("/runs/{id:long}/log", GetRunLogAsync).RequireAuthorization(ManagementAuth.OpsAdminPolicy);

        load.MapPut("/scenarios/{name}", UpsertScenarioAsync).RequireAuthorization(ManagementAuth.OpsLoadPolicy);
        load.MapDelete("/scenarios/{name}", DeleteScenarioAsync).RequireAuthorization(ManagementAuth.OpsLoadPolicy);
        load.MapPost("/runs", StartRunAsync).RequireAuthorization(ManagementAuth.OpsLoadPolicy);
        load.MapPost("/runs/{id:long}/stop", StopRunAsync).RequireAuthorization(ManagementAuth.OpsLoadPolicy);
        load.MapPut("/runs/{id:long}/verdict", SetVerdictAsync).RequireAuthorization(ManagementAuth.OpsLoadPolicy);
        load.MapDelete("/runs/{id:long}", DeleteRunAsync).RequireAuthorization(ManagementAuth.OpsLoadPolicy);

        // The generators' endpoints: the run token is the only credential.
        load.MapPost("/runs/{id:long}/checkin", CheckInAsync).AllowAnonymous();
        load.MapPost("/runs/{id:long}/stats", PostStatsAsync).AllowAnonymous();

        return endpoints;
    }

    // ---------- scenarios: reads (OpsDeploy) ----------

    private static async Task<IResult> ListScenariosAsync(ILoadStore store, CancellationToken ct)
    {
        // Summaries only: the script body is never part of the list.
        var scenarios = await store.ListScenariosAsync(ct);
        return Results.Json(scenarios.Select(ScenarioSummaryView).ToList());
    }

    private static async Task<IResult> GetScenarioAsync(string name, ILoadStore store, CancellationToken ct)
    {
        var scenario = await store.GetScenarioAsync(name, ct);
        return scenario is null ? NotFoundScenario(name) : Results.Json(ScenarioDetailView(scenario));
    }

    private static async Task<IResult> ListScriptVersionsAsync(string name, ILoadStore store, CancellationToken ct)
    {
        if (await store.GetScenarioAsync(name, ct) is null)
        {
            return NotFoundScenario(name);
        }

        var versions = await store.ListScenarioVersionsAsync(name, ct);
        return Results.Json(versions.Select(v => new
        {
            version = v.Version,
            sha256 = v.Sha256,
            updatedBy = v.UpdatedBy,
            updatedAt = v.UpdatedAt,
        }).ToList());
    }

    private static async Task<IResult> GetScriptVersionAsync(string name, int version, ILoadStore store, CancellationToken ct)
    {
        var row = await store.GetScenarioVersionAsync(name, version, ct);
        if (row is null)
        {
            return Results.NotFound(new { error = $"Scenario '{name}' has no script version {version}." });
        }

        return Results.Json(new { version = row.Version, script = row.Script });
    }

    // ---------- scenarios: mutations (OpsLoad) ----------

    private static async Task<IResult> UpsertScenarioAsync(
        string name,
        UpsertLoadScenarioRequest request,
        ClaimsPrincipal user,
        ILoadStore store,
        LoadOptions options,
        IChannelEventPublisher publisher,
        [FromKeyedServices(ClockKey)] TimeProvider time,
        CancellationToken ct)
    {
        if (!ScenarioNameRegex().IsMatch(name))
        {
            return Invalid("name", "Scenario name must match ^[a-z0-9][a-z0-9-]{1,62}$.");
        }

        var existing = await store.GetScenarioAsync(name, ct);

        if (existing is null && request.Repository is null)
        {
            return Invalid("repository", "repository is required when creating a scenario.");
        }

        if (existing is null && request.Script is null)
        {
            return Invalid("script", "script is required when creating a scenario.");
        }

        if (existing is null && request.MaxVusPerGenerator is null)
        {
            return Invalid("maxVusPerGenerator", "maxVusPerGenerator is required when creating a scenario.");
        }

        var repository = request.Repository?.Trim() ?? existing!.Repository;
        if (!RepositoryRegex().IsMatch(repository))
        {
            return Invalid("repository", "repository must be owner/name.");
        }

        // commitSha: absent preserves, empty string clears, otherwise a hex commit id.
        string? commitSha;
        if (request.CommitSha is null)
        {
            commitSha = existing?.CommitSha;
        }
        else if (request.CommitSha.Trim().Length == 0)
        {
            commitSha = null;
        }
        else if (!CommitShaRegex().IsMatch(request.CommitSha.Trim()))
        {
            return Invalid("commitSha", "commitSha must be 7 to 64 hex characters.");
        }
        else
        {
            commitSha = request.CommitSha.Trim();
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

        var maxVus = request.MaxVusPerGenerator ?? existing!.MaxVusPerGenerator;
        if (maxVus is < MinVusPerGenerator or > MaxVusPerGeneratorLimit)
        {
            return Invalid("maxVusPerGenerator", $"maxVusPerGenerator must be in the range {MinVusPerGenerator}..{MaxVusPerGeneratorLimit}.");
        }

        string paramsDefault;
        if (IsAbsent(request.ParamsDefault))
        {
            paramsDefault = existing?.ParamsDefault ?? "{}";
        }
        else if (TryFlatStrings(request.ParamsDefault!.Value, out var defaults))
        {
            paramsDefault = WriteParams(defaults);
        }
        else
        {
            return Invalid("paramsDefault", "paramsDefault must be a flat JSON object of strings.");
        }

        var actor = ManagementAuth.ResolveUsername(user);
        var now = time.GetUtcNow();

        // A new body (or a create) bumps the version, writes a history row and clears
        // the validation; an identical body leaves all three alone. History outlives a
        // deleted scenario, so a re-create continues after the newest kept version.
        var scriptChanged = existing is null || !string.Equals(existing.Script, script, StringComparison.Ordinal);
        int scriptVersion;
        if (existing is not null)
        {
            scriptVersion = existing.ScriptVersion + (scriptChanged ? 1 : 0);
        }
        else
        {
            var kept = await store.ListScenarioVersionsAsync(name, ct);
            scriptVersion = kept.Count == 0 ? 1 : kept.Max(v => v.Version) + 1;
        }

        var scenario = new LoadScenario
        {
            Name = name,
            Repository = repository,
            CommitSha = commitSha,
            Script = script,
            ScriptVersion = scriptVersion,
            Image = request.Image?.Trim() ?? existing?.Image ?? options.DefaultImage,
            Tag = request.Tag?.Trim() ?? existing?.Tag ?? options.DefaultTag,
            ParamsDefault = paramsDefault,
            MaxVusPerGenerator = maxVus,
            LastValidationRunId = scriptChanged ? null : existing!.LastValidationRunId,
            UpdatedBy = actor,
            UpdatedAt = now,
        };

        var history = scriptChanged
            ? new LoadScenarioVersion
            {
                Scenario = name,
                Version = scriptVersion,
                Script = script,
                Sha256 = JobEndpoints.Sha256Hex(script),
                UpdatedBy = actor,
                UpdatedAt = now,
            }
            : null;

        await store.UpsertScenarioAsync(scenario, history, ct);
        BroadcastScenario(publisher, name, "upserted", actor);

        var body = ScenarioDetailView(scenario);
        return existing is null
            ? Results.Json(body, statusCode: StatusCodes.Status201Created)
            : Results.Json(body);
    }

    private static async Task<IResult> DeleteScenarioAsync(
        string name,
        ClaimsPrincipal user,
        ILoadStore store,
        IChannelEventPublisher publisher,
        CancellationToken ct)
    {
        if (await store.GetScenarioAsync(name, ct) is null)
        {
            return NotFoundScenario(name);
        }

        if (await store.GetActiveRunForScenarioAsync(name, ct) is not null)
        {
            return Results.Conflict(new { error = "run_active" });
        }

        await store.DeleteScenarioAsync(name, ct);
        BroadcastScenario(publisher, name, "deleted", ManagementAuth.ResolveUsername(user));
        return Results.NoContent();
    }

    // ---------- pre-flight (OpsDeploy) ----------

    private static async Task<IResult> PreflightAsync(
        string? scenario,
        string? target,
        string? confirm,
        int? generators,
        int? vus,
        int? fleet,
        bool? validation,
        ILoadPreflight preflight,
        CancellationToken ct)
    {
        var isValidation = validation ?? false;
        var checks = await preflight.EvaluateAsync(new LoadPreflightRequest(
            scenario,
            target,
            confirm,
            isValidation ? 1 : generators ?? 1,
            isValidation ? 1 : vus ?? 1,
            fleet,
            isValidation), ct);

        return Results.Json(new
        {
            ok = checks.All(c => c.Status != PreflightStatus.Red),
            checks = checks.Select(CheckView).ToList(),
        });
    }

    // ---------- runs: reads (OpsDeploy) ----------

    private static async Task<IResult> ListRunsAsync(int? limit, long? before, ILoadStore store, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultRunsLimit, 1, MaxRunsLimit);
        var runs = await store.ListRunsAsync(take, before, ct);
        return Results.Json(runs.Select(RunSummaryView).ToList());
    }

    private static async Task<IResult> GetRunAsync(long id, ILoadStore store, CancellationToken ct)
    {
        var run = await store.GetRunAsync(id, ct);
        return run is null ? NotFoundRun(id) : Results.Json(RunDetailView(run));
    }

    private static async Task<IResult> GetStatsAsync(
        long id,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? source,
        int? index,
        int? step,
        ILoadStore store,
        CancellationToken ct)
    {
        var (rows, error) = await ReadStatsAsync(id, from, to, source, index, step, store, ct);
        if (error is not null)
        {
            return error;
        }

        return Results.Json(rows!.Select(r => new
        {
            id = r.Id,
            source = r.Source,
            index = r.Index,
            at = r.At,
            body = r.Body,
        }).ToList());
    }

    private static async Task<IResult> GetStatsCsvAsync(
        long id,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? source,
        int? index,
        int? step,
        ILoadStore store,
        CancellationToken ct)
    {
        var (rows, error) = await ReadStatsAsync(id, from, to, source, index, step, store, ct);
        if (error is not null)
        {
            return error;
        }

        return Results.File(
            Encoding.UTF8.GetBytes(LoadStats.Csv(rows!)),
            "text/csv; charset=utf-8",
            $"load-run-{id}-stats.csv");
    }

    private static async Task<IResult> GetRunLogAsync(
        long id,
        int? index,
        int? tail,
        ILoadStore store,
        ILogStore logs,
        CancellationToken ct)
    {
        var run = await store.GetRunAsync(id, ct);
        if (run is null)
        {
            return NotFoundRun(id);
        }

        var generator = index ?? 0;
        if (generator < 0 || generator >= run.Generators)
        {
            return Invalid("index", $"index must be in the range 0..{run.Generators - 1}.");
        }

        // Same window as service and job logs: default 500, clamped.
        var count = Math.Clamp(tail ?? 500, 1, 10_000);
        var lines = await logs.GetStreamLogsAsync(
            CloudWatchLogStore.LoadLogGroup,
            CloudWatchLogStore.LoadRunStreamFor(id, generator),
            count,
            ct);
        return Results.Json(lines.Select(l => new { ts = l.Ts, message = l.Message }).ToList());
    }

    // ---------- runs: mutations (OpsLoad) ----------

    private static async Task<IResult> StartRunAsync(
        StartLoadRunRequest request,
        ClaimsPrincipal user,
        ILoadStore store,
        ILoadPreflight preflight,
        LoadOptions options,
        IChannelEventPublisher publisher,
        [FromKeyedServices(ClockKey)] TimeProvider time,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Scenario))
        {
            return Invalid("scenario", "scenario is required.");
        }

        var scenario = await store.GetScenarioAsync(request.Scenario.Trim(), ct);
        if (scenario is null)
        {
            return Invalid("scenario", $"No scenario named '{request.Scenario.Trim()}'.");
        }

        if (string.IsNullOrWhiteSpace(request.Target))
        {
            return Invalid("target", "target is required.");
        }

        var validation = request.Validation ?? false;
        int generators, vus, ramp, hold;
        if (validation)
        {
            (generators, vus, ramp, hold) = (1, 1, 0, options.ValidationHoldSeconds);
        }
        else
        {
            if (request.Generators is not { } g || g < 1 || g > options.MaxGenerators)
            {
                return Invalid("generators", $"generators is required and must be in the range 1..{options.MaxGenerators}.");
            }

            if (request.VusPerGenerator is not { } v || v < MinVusPerGenerator || v > scenario.MaxVusPerGenerator)
            {
                return Invalid("vusPerGenerator", $"vusPerGenerator is required and must be in the range 1..{scenario.MaxVusPerGenerator}.");
            }

            if (request.RampSeconds is not { } r || r < 0 || r > MaxRampSeconds)
            {
                return Invalid("rampSeconds", $"rampSeconds is required and must be in the range 0..{MaxRampSeconds}.");
            }

            if (request.HoldSeconds is not { } h || h < 0 || h > MaxHoldSeconds)
            {
                return Invalid("holdSeconds", $"holdSeconds is required and must be in the range 0..{MaxHoldSeconds}.");
            }

            (generators, vus, ramp, hold) = (g, v, r, h);
        }

        if (request.FleetSize is < 1)
        {
            return Invalid("fleetSize", "fleetSize must be at least 1, or absent to leave the fleet alone.");
        }

        // The run's params: the scenario's defaults with the run's overrides on top.
        var runParams = new Dictionary<string, string>(StringComparer.Ordinal);
        if (TryFlatStrings(LoadJson.Element(scenario.ParamsDefault), out var defaults))
        {
            foreach (var (key, value) in defaults)
            {
                runParams[key] = value;
            }
        }

        if (!IsAbsent(request.Params))
        {
            if (!TryFlatStrings(request.Params!.Value, out var overrides))
            {
                return Invalid("params", "params must be a flat JSON object of strings.");
            }

            foreach (var (key, value) in overrides)
            {
                runParams[key] = value;
            }
        }

        if (!options.IsConfigured)
        {
            return Results.Json(new { error = "load_unconfigured" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (await store.GetActiveRunAsync(ct) is not null)
        {
            return Results.Conflict(new { error = "run_active" });
        }

        var target = request.Target.Trim();
        var checks = await preflight.EvaluateAsync(new LoadPreflightRequest(
            scenario.Name, target, request.Confirm, generators, vus, request.FleetSize, validation), ct);
        if (checks.Any(c => c.Status == PreflightStatus.Red))
        {
            return Results.Json(
                new { error = "preflight_failed", checks = checks.Select(CheckView).ToList() },
                statusCode: StatusCodes.Status409Conflict);
        }

        var actor = ManagementAuth.ResolveUsername(user);
        var now = time.GetUtcNow();

        // The token is shown to nobody: only its hash is kept.
        var token = MintToken();
        var lifetime = TimeSpan.FromSeconds(ramp + hold) + HardStopSlack;

        var run = await store.TryCreateRunAsync(new LoadRun
        {
            Scenario = scenario.Name,
            ScenarioVersion = scenario.ScriptVersion,
            Target = target,
            Validation = validation,
            Actor = actor,
            Status = LoadRunStatus.Queued,
            Generators = generators,
            VusPerGenerator = vus,
            RampSeconds = ramp,
            HoldSeconds = hold,
            Params = WriteParams(runParams),
            FleetSizeRequested = request.FleetSize,
            RestoreFleet = request.RestoreFleet ?? true,
            HardStopAt = now + (lifetime < HardStopCeiling ? lifetime : HardStopCeiling),
            TokenHash = TokenHash(token),
            CheckedIn = 0,
            Checkins = "{}",
            QueuedAt = now,
            Peaks = "{}",
            Timeline = LoadJson.AppendTimeline(null, now, LoadRunStatus.Queued, actor),
        }, ct);

        if (run is null)
        {
            return Results.Conflict(new { error = "run_active" });
        }

        BroadcastRun(publisher, run);
        return Results.Json(new { runId = run.Id }, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> StopRunAsync(
        long id,
        ClaimsPrincipal user,
        ILoadStore store,
        IChannelEventPublisher publisher,
        [FromKeyedServices(ClockKey)] TimeProvider time,
        CancellationToken ct)
    {
        var run = await store.GetRunAsync(id, ct);
        if (run is null)
        {
            return NotFoundRun(id);
        }

        if (LoadRunStatus.IsTerminal(run.Status))
        {
            return Results.Conflict(new { error = "run_finished" });
        }

        var after = await store.RequestStopAsync(id, ManagementAuth.ResolveUsername(user), time.GetUtcNow(), ct);
        if (after is null || LoadRunStatus.IsTerminal(after.Status))
        {
            // Finished between the read and the update.
            return Results.Conflict(new { error = "run_finished" });
        }

        BroadcastRun(publisher, after);
        return Results.Json(RunDetailView(after), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> SetVerdictAsync(
        long id,
        LoadVerdictRequest request,
        ILoadStore store,
        CancellationToken ct)
    {
        var run = await store.GetRunAsync(id, ct);
        if (run is null)
        {
            return NotFoundRun(id);
        }

        var verdict = string.IsNullOrWhiteSpace(request.Verdict) ? null : request.Verdict.Trim();
        if (verdict is { Length: > MaxVerdictLength })
        {
            return Invalid("verdict", $"verdict must be at most {MaxVerdictLength} characters.");
        }

        if (!LoadRunStatus.IsTerminal(run.Status) || !await store.SetVerdictAsync(id, verdict, ct))
        {
            return Results.Conflict(new { error = "run_active" });
        }

        var after = await store.GetRunAsync(id, ct);
        return after is null ? NotFoundRun(id) : Results.Json(RunDetailView(after));
    }

    private static async Task<IResult> DeleteRunAsync(long id, ILoadStore store, CancellationToken ct)
    {
        var run = await store.GetRunAsync(id, ct);
        if (run is null)
        {
            return NotFoundRun(id);
        }

        if (!LoadRunStatus.IsTerminal(run.Status) || !await store.DeleteRunAsync(id, ct))
        {
            return Results.Conflict(new { error = "run_active" });
        }

        return Results.NoContent();
    }

    // ---------- generators (run token) ----------

    private static async Task<IResult> CheckInAsync(
        long id,
        HttpContext http,
        ILoadStore store,
        LoadTokenRateLimiter limiter,
        CancellationToken ct)
    {
        var (run, body, refusal) = await AdmitAsync(id, http, store, limiter, ct);
        if (refusal is not null)
        {
            return refusal;
        }

        string? instanceId = null;
        try
        {
            if (JsonNode.Parse(body!) is JsonObject root
                && root["instanceId"] is JsonValue value
                && value.TryGetValue<string>(out var text))
            {
                instanceId = text.Trim();
            }
        }
        catch (JsonException)
        {
        }

        if (string.IsNullOrEmpty(instanceId) || instanceId.Length > MaxInstanceIdLength)
        {
            return Invalid("instanceId", $"instanceId is required, at most {MaxInstanceIdLength} characters.");
        }

        // The script the run was started with: its history row, or the current body
        // when the version still matches.
        var script = (await store.GetScenarioVersionAsync(run!.Scenario, run.ScenarioVersion, ct))?.Script;
        if (script is null)
        {
            var scenario = await store.GetScenarioAsync(run.Scenario, ct);
            if (scenario?.ScriptVersion == run.ScenarioVersion)
            {
                script = scenario.Script;
            }
        }

        if (script is null)
        {
            return Results.Conflict(new { error = "run_not_accepting" });
        }

        var result = await store.CheckInAsync(id, instanceId, ct);
        return result.Outcome switch
        {
            LoadCheckInOutcome.Assigned => Results.Json(new
            {
                index = result.Index,
                count = run.Generators,
                vus = run.VusPerGenerator,
                rampSeconds = run.RampSeconds,
                holdSeconds = run.HoldSeconds,
                scenario = run.Scenario,
                target = run.Target,
                script,
                @params = LoadJson.Element(run.Params),
                statsUrl = $"{BaseUrl(http)}/mgmt/load/runs/{id}/stats",
            }),
            LoadCheckInOutcome.Full => Results.Conflict(new { error = "run_full" }),
            LoadCheckInOutcome.NotFound => Unauthorized(),
            _ => Results.Conflict(new { error = "run_not_accepting" }),
        };
    }

    private static async Task<IResult> PostStatsAsync(
        long id,
        HttpContext http,
        ILoadStore store,
        LoadTokenRateLimiter limiter,
        IChannelEventPublisher publisher,
        CancellationToken ct)
    {
        var (run, body, refusal) = await AdmitAsync(id, http, store, limiter, ct);
        if (refusal is not null)
        {
            return refusal;
        }

        // The reporter treats 409 as stop.
        if (LoadRunStatus.IsTerminal(run!.Status))
        {
            return Results.Conflict(new { error = "run_terminal" });
        }

        if (!LoadStats.TryParseLine(body!, id, run.Generators, out var line, out var error, out var field))
        {
            return Invalid(field!, error!);
        }

        await store.AppendStatAsync(new LoadRunStat
        {
            RunId = id,
            Source = LoadStatSource.Generator,
            Index = line!.Index,
            At = line.At,
            Body = body!,
        }, ct);
        await store.UpdatePeaksAsync(id, LoadStats.MetricLeaves(line.Metrics), ct);

        // The fleet-wide view: the newest line of every generator, merged.
        var latest = await store.GetLatestGeneratorStatsAsync(id, ct);
        var aggregate = LoadStats.Aggregate(latest
            .Select(s => LoadStats.ParseBody(s.Body) as JsonObject)
            .Where(o => o is not null)
            .Select(o => o!));
        publisher.TryPublish(OpsLoadChannel, "stats", new { runId = id, at = line.At, aggregate });

        var stop = run.StopRequestedAt is not null || run.Status == LoadRunStatus.Draining;
        return Results.Json(new { action = stop ? "stop" : "continue" });
    }

    /// <summary>
    /// The shared gate of the run-token endpoints, in order: the per-address rate
    /// limit (<c>429</c>), the 64 KiB body cap (<c>413</c>), then the token against the
    /// run's hash (<c>401</c> for a wrong token and an unknown run alike). Nothing about
    /// the token is logged.
    /// </summary>
    private static async Task<(LoadRun? Run, string? Body, IResult? Refusal)> AdmitAsync(
        long id,
        HttpContext http,
        ILoadStore store,
        LoadTokenRateLimiter limiter,
        CancellationToken ct)
    {
        if (!limiter.TryTake(SourceAddress(http), out var retryAfter))
        {
            http.Response.Headers.RetryAfter =
                Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            return (null, null, Results.Json(new { error = "rate_limited" }, statusCode: StatusCodes.Status429TooManyRequests));
        }

        var body = await ReadCappedAsync(http.Request, ct);
        if (body is null)
        {
            return (null, null, Results.Json(
                new { error = "body_too_large", limit = MaxTokenBodyBytes },
                statusCode: StatusCodes.Status413PayloadTooLarge));
        }

        var run = await store.GetRunAsync(id, ct);
        var presented = http.Request.Headers[TokenHeader].ToString();
        if (!TokenMatches(presented, run?.TokenHash ?? UnknownRunHash) || run is null)
        {
            return (null, null, Unauthorized());
        }

        return (run, body, null);
    }

    // ---------- events ----------

    /// <summary>
    /// Fire-and-forget a <c>scenario</c> event on <c>ops:load</c>. Runs after the change
    /// has committed; a backplane failure never fails the request.
    /// </summary>
    public static void BroadcastScenario(IChannelEventPublisher publisher, string name, string action, string by) =>
        publisher.TryPublish(OpsLoadChannel, "scenario", new { name, action, by });

    /// <summary>
    /// Fire-and-forget a <c>run</c> event on <c>ops:load</c>; called on every run status
    /// change and on a stop request, best-effort like <see cref="BroadcastScenario"/>.
    /// </summary>
    public static void BroadcastRun(IChannelEventPublisher publisher, LoadRun run) =>
        publisher.TryPublish(OpsLoadChannel, "run", new
        {
            runId = run.Id,
            scenario = run.Scenario,
            status = run.Status,
            actor = run.Actor,
            generators = run.Generators,
            vusPerGenerator = run.VusPerGenerator,
            startedAt = run.StartedAt,
            finishedAt = run.FinishedAt,
            stopReason = run.StopReason,
        });

    /// <summary>Fire-and-forget a <c>fleet</c> event on <c>ops:load</c> after a fleet sample is stored.</summary>
    public static void BroadcastFleet(IChannelEventPublisher publisher, long runId, DateTimeOffset at, object sample) =>
        publisher.TryPublish(OpsLoadChannel, "fleet", new { runId, at, sample });

    // ---------- helpers ----------

    /// <summary>A new run token: 32 random bytes, base64url without padding.</summary>
    public static string MintToken() => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>Lowercase hex sha256 of the token's UTF-8 bytes, as stored in <c>token_hash</c>.</summary>
    public static string TokenHash(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Constant-time comparison of the sha256 of <paramref name="presented"/> with
    /// <paramref name="tokenHash"/>. False for an empty token or a malformed hash.
    /// </summary>
    public static bool TokenMatches(string? presented, string tokenHash)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented ?? string.Empty));
        byte[] stored;
        try
        {
            stored = Convert.FromHexString(tokenHash);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(hash, stored) && !string.IsNullOrEmpty(presented);
    }

    /// <summary>
    /// The rate limit key of a request: the last <c>X-Forwarded-For</c> entry (the
    /// address the load balancer saw) when present, else the connection's remote
    /// address.
    /// </summary>
    public static string SourceAddress(HttpContext http)
    {
        var forwarded = http.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var last = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            if (!string.IsNullOrEmpty(last))
            {
                return last;
            }
        }

        return http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private static async Task<string?> ReadCappedAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxTokenBodyBytes)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxTokenBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static async Task<(IReadOnlyList<LoadStatsRow>? Rows, IResult? Error)> ReadStatsAsync(
        long id,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? source,
        int? index,
        int? step,
        ILoadStore store,
        CancellationToken ct)
    {
        if (await store.GetRunAsync(id, ct) is null)
        {
            return (null, NotFoundRun(id));
        }

        if (source is not null and not (LoadStatSource.Generator or LoadStatSource.Fleet))
        {
            return (null, Invalid("source", "source must be generator or fleet."));
        }

        if (step is < 1 or > MaxStepSeconds)
        {
            return (null, Invalid("step", $"step must be in the range 1..{MaxStepSeconds} seconds."));
        }

        var rows = await store.ListStatsAsync(id, from, to, source, index, ct);
        return (step is { } s ? LoadStats.Downsample(rows, s) : LoadStats.Raw(rows), null);
    }

    private static string BaseUrl(HttpContext http)
    {
        var proto = http.Request.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim();
        var scheme = string.IsNullOrEmpty(proto) ? http.Request.Scheme : proto;
        return $"{scheme}://{http.Request.Host}{http.Request.PathBase}";
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool IsAbsent(JsonElement? element) =>
        element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    private static bool TryFlatStrings(JsonElement element, out Dictionary<string, string> map)
    {
        map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            map[property.Name] = property.Value.GetString()!;
        }

        return true;
    }

    private static string WriteParams(IReadOnlyDictionary<string, string> map) =>
        JsonSerializer.Serialize(new SortedDictionary<string, string>(
            map.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal), StringComparer.Ordinal));

    private static IResult Invalid(string field, string error) =>
        Results.BadRequest(new { error, field });

    private static IResult Unauthorized() =>
        Results.Json(new { error = "unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult NotFoundScenario(string name) =>
        Results.NotFound(new { error = $"No scenario named '{name}'." });

    private static IResult NotFoundRun(long id) =>
        Results.NotFound(new { error = $"No load run {id}." });

    private static object CheckView(PreflightCheckResult c) => new
    {
        name = c.Name,
        status = c.Status,
        message = c.Message,
        fix = c.Fix,
    };

    private static Dictionary<string, object?> ScenarioSummaryView(LoadScenario s) => new()
    {
        ["name"] = s.Name,
        ["repository"] = s.Repository,
        ["commitSha"] = s.CommitSha,
        ["scriptVersion"] = s.ScriptVersion,
        ["image"] = s.Image,
        ["tag"] = s.Tag,
        ["paramsDefault"] = LoadJson.Element(s.ParamsDefault),
        ["maxVusPerGenerator"] = s.MaxVusPerGenerator,
        ["lastValidationRunId"] = s.LastValidationRunId,
        // A changed script clears the validation, so a set id means the current version passed.
        ["validated"] = s.LastValidationRunId is not null,
        ["updatedBy"] = s.UpdatedBy,
        ["updatedAt"] = s.UpdatedAt,
    };

    private static Dictionary<string, object?> ScenarioDetailView(LoadScenario s)
    {
        var view = ScenarioSummaryView(s);
        view["script"] = s.Script;
        return view;
    }

    // The token hash is never part of a view.
    private static Dictionary<string, object?> RunSummaryView(LoadRun r) => new()
    {
        ["id"] = r.Id,
        ["scenario"] = r.Scenario,
        ["scenarioVersion"] = r.ScenarioVersion,
        ["target"] = r.Target,
        ["validation"] = r.Validation,
        ["actor"] = r.Actor,
        ["status"] = r.Status,
        ["generators"] = r.Generators,
        ["vusPerGenerator"] = r.VusPerGenerator,
        ["rampSeconds"] = r.RampSeconds,
        ["holdSeconds"] = r.HoldSeconds,
        ["params"] = LoadJson.Element(r.Params),
        ["fleetSizeRequested"] = r.FleetSizeRequested,
        ["fleetSizeBefore"] = r.FleetSizeBefore,
        ["restoreFleet"] = r.RestoreFleet,
        ["hardStopAt"] = r.HardStopAt,
        ["checkedIn"] = r.CheckedIn,
        ["queuedAt"] = r.QueuedAt,
        ["startedAt"] = r.StartedAt,
        ["rampStartedAt"] = r.RampStartedAt,
        ["finishedAt"] = r.FinishedAt,
        ["stopRequestedAt"] = r.StopRequestedAt,
        ["stopReason"] = r.StopReason,
        ["verdict"] = r.Verdict,
        ["detail"] = r.Detail,
        ["peaks"] = LoadJson.Element(r.Peaks),
    };

    private static Dictionary<string, object?> RunDetailView(LoadRun r)
    {
        var view = RunSummaryView(r);
        view["checkins"] = LoadJson.Element(r.Checkins);
        view["timeline"] = LoadJson.Element(r.Timeline, "[]");
        return view;
    }
}
