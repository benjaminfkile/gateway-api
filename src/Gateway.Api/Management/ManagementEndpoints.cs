using System.Security.Claims;
using System.Text.RegularExpressions;
using Gateway.Api.Auth;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
using Gateway.Api.RealTime;
using Gateway.Api.Reconcile;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gateway.Api.Management;

/// <summary>Body of <c>POST /mgmt/services/{name}/deploy</c>.</summary>
public sealed record DeployRequest(string? Tag);

/// <summary>
/// Body of <c>PUT /mgmt/services/{name}</c> (create/update a manifest entry).
/// <para>
/// The realtime fields — <see cref="RealtimeAuthPath"/>, <see cref="RealtimeMessagePath"/>,
/// and <see cref="RealtimeAllowedOrigins"/> — are tri-state on an edit so a minimal
/// re-upsert (e.g. a pre-phase-2 CI workflow re-pushing only <c>image/tag/port</c>)
/// can never silently strip them:
/// <list type="bullet">
/// <item><b>Absent / null</b> — preserve the value already stored on the row.</item>
/// <item><b>Empty string</b> — explicitly clear the value to null.</item>
/// <item><b>Non-empty</b> — validate and set the new value.</item>
/// </list>
/// (A create has no existing row, so absent/null simply lands as null.)
/// </para>
/// </summary>
public sealed record UpsertServiceRequest(
    string? Image,
    string? Tag,
    int Port,
    string? DesiredStatus,
    string? EnvSecretRef,
    bool? IncludeInHealth = null,
    string? RealtimeAuthPath = null,
    string? RealtimeMessagePath = null,
    string? RealtimeAllowedOrigins = null,
    bool? RealtimePresence = null);

/// <summary>
/// The fleet-aware Management API (tech-spec §4.5). Every endpoint reads/writes the
/// database (manifest, <c>instance_status</c>, <c>deploy_history</c>), never local
/// Docker, so it answers identically whichever instance the load balancer routed
/// the dashboard's request to. All endpoints require a Cognito access token; each
/// mutating call is audit-logged with the caller's username.
/// </summary>
public static partial class ManagementEndpoints
{
    /// <summary>Hub channel the dashboard listens on for live deploy events (tech-spec §4.5).</summary>
    public const string OpsDeploysChannel = "ops:deploys";

    /// <summary>
    /// Hub channel the dashboard listens on for live fleet events — heartbeat,
    /// serviceError, leaderChange, instances (tech-spec §4.3, §4.4). Fleet-wide events
    /// (heartbeat/leaderChange/instances) are published by the leader only so a fleet of
    /// N instances emits each exactly once; serviceError is per-instance.
    /// </summary>
    public const string OpsFleetChannel = "ops:fleet";

    /// <summary>The gateway is never a managed service; its name is reserved (tech-spec §4.5).</summary>
    public const string ReservedGatewayName = "gateway";

    /// <summary>
    /// Service names the manifest may never claim. Beyond the gateway itself,
    /// <c>hub</c> and <c>internal</c> are reserved because a manifest service by
    /// either name would generate a YARP route (<c>/hub/{**catch-all}</c>,
    /// <c>/internal/{**catch-all}</c>) that shadows the SignalR hub endpoint and the
    /// internal publish listener respectively. <c>ops</c> is reserved because its
    /// realtime namespace is gateway-owned (<c>ops:*</c> channels: publishes 403 and
    /// joins demand the operator policy), so a service by that name would have silently
    /// dead realtime yet its <c>realtime_allowed_origins</c> would still widen /hub CORS.
    /// </summary>
    private static readonly string[] ReservedServiceNames =
    {
        ReservedGatewayName,
        "hub",
        "internal",
        "ops",
    };

    /// <summary>
    /// True when <paramref name="name"/> is a reserved / gateway-owned service name
    /// (case-insensitive). Used both to gate the mutating endpoints and to exclude such
    /// names when folding <c>realtime_allowed_origins</c> into the /hub CORS set.
    /// </summary>
    public static bool IsReservedName(string name) =>
        ReservedServiceNames.Contains(name, StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex ServiceNameRegex();

    /// <summary>
    /// Map the §4.5 Management endpoints onto the authenticated <c>/mgmt</c> group.
    /// Read endpoints require an authenticated ops principal (OpsDeploy policy);
    /// lifecycle and manifest edits require OpsAdmin; deploy/rollback require
    /// OpsDeploy (satisfied by the CI machine token). Logs are OpsAdmin.
    /// </summary>
    public static IEndpointRouteBuilder MapManagementApi(this IEndpointRouteBuilder endpoints)
    {
        var mgmt = endpoints.MapGroup("/mgmt");

        mgmt.MapGet("/services", GetServicesAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        mgmt.MapGet("/instances", GetInstancesAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        mgmt.MapGet("/deploys", GetDeploysAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        mgmt.MapGet("/deploys/{id:int}", GetDeployAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);

        mgmt.MapGet("/services/{name}/logs", GetLogsAsync).RequireAuthorization(ManagementAuth.OpsAdminPolicy);

        mgmt.MapPost("/services/{name}/stop", StopAsync).RequireAuthorization(ManagementAuth.OpsAdminPolicy);
        mgmt.MapPost("/services/{name}/start", StartAsync).RequireAuthorization(ManagementAuth.OpsAdminPolicy);
        mgmt.MapPost("/services/{name}/restart", RestartAsync).RequireAuthorization(ManagementAuth.OpsAdminPolicy);
        mgmt.MapPut("/services/{name}", UpsertServiceAsync).RequireAuthorization(ManagementAuth.OpsAdminPolicy);
        mgmt.MapDelete("/services/{name}", DeleteServiceAsync).RequireAuthorization(ManagementAuth.OpsAdminPolicy);

        mgmt.MapPost("/services/{name}/deploy", DeployAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        mgmt.MapPost("/services/{name}/rollback", RollbackAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);

        return endpoints;
    }

    // ---------- reads ----------

    private static async Task<IResult> GetServicesAsync(
        IManifestStore manifests,
        IInstanceStatusStore instances,
        CancellationToken ct)
    {
        var services = await manifests.GetAllAsync(ct);
        var fleet = await instances.GetAllAsync(ct);

        var views = services
            .Select(m => ServiceView(m, FleetRollup.Compute(m.Name, fleet)))
            .ToList();

        return Results.Json(views);
    }

    private static async Task<IResult> GetInstancesAsync(
        IInstanceStatusStore instances,
        ReconcilerOptions options,
        CancellationToken ct)
    {
        var rows = await instances.GetAllAsync(ct);
        var cutoff = DateTimeOffset.UtcNow - options.InstanceStaleThreshold;

        var views = rows.Select(r => new
        {
            instanceId = r.InstanceId,
            privateIp = r.PrivateIp,
            publicIp = r.PublicIp,
            gatewayVer = r.GatewayVer,
            isLeader = r.IsLeader,
            heartbeatAt = r.HeartbeatAt,
            stale = r.HeartbeatAt < cutoff,
            services = InstanceServicesJson.Parse(r.Services).Select(s => new
            {
                name = s.Name,
                digest = s.Digest,
                state = s.State,
                startedAt = s.StartedAt,
                restarts = s.Restarts,
                hostPort = s.HostPort,
                // Convergence failure for this service on this instance, or null when
                // the last action succeeded (tech-spec §4.4).
                lastError = s.LastError,
                lastErrorAt = s.LastErrorAt,
            }),
        }).ToList();

        return Results.Json(views);
    }

    private static async Task<IResult> GetDeploysAsync(IDeployStore deploys, CancellationToken ct)
    {
        var rows = await deploys.ListAsync(ct);
        return Results.Json(rows.Select(DeployView).ToList());
    }

    private static async Task<IResult> GetDeployAsync(int id, IDeployStore deploys, CancellationToken ct)
    {
        var deploy = await deploys.GetAsync(id, ct);
        if (deploy is null)
        {
            return Results.NotFound(new { error = $"No deploy with id {id}." });
        }

        var children = await deploys.ListInstanceStatusesAsync(id, ct);
        return Results.Json(new
        {
            deploy = DeployView(deploy),
            instances = children.Select(c => new
            {
                deployId = c.DeployId,
                instanceId = c.InstanceId,
                status = c.Status,
                detail = c.Detail,
                updatedAt = c.UpdatedAt,
            }).ToList(),
        });
    }

    private static async Task<IResult> GetLogsAsync(
        string name,
        string? instance,
        int? tail,
        ILogStore logs,
        CancellationToken ct)
    {
        // Clamp the tail to a sane window; default 500 (tech-spec §4.5).
        var count = Math.Clamp(tail ?? 500, 1, 10_000);
        var lines = await logs.GetLogsAsync(name, instance, count, ct);
        return Results.Json(new
        {
            lines = lines.Select(l => new { ts = l.Ts, message = l.Message }).ToList(),
        });
    }

    // ---------- lifecycle (OpsAdmin) ----------

    private static async Task<IResult> StopAsync(
        string name,
        ClaimsPrincipal user,
        IManifestStore manifests,
        IDeployStore deploys,
        CancellationToken ct,
        bool force = false)
    {
        var manifest = await manifests.GetAsync(name, ct);
        if (manifest is null)
        {
            // No row to tear down: a reserved name is still blocked (it never had a
            // legitimate row), everything else is a plain 404.
            return IsReserved(name, out var reserved) ? reserved : NotFoundService(name);
        }

        // Stop is a teardown path, so a PRE-EXISTING reserved-named row (legal before the
        // reservation was added — the very row the reservation defends against) may be
        // stopped so its shadowing route can be retired. Reservation blocks creation, not
        // teardown of an already-present row.

        // Guardrail (tech-spec §4.5): stopping a health-check dependency requires an
        // explicit confirm flag, else 409.
        if (manifest.IncludeInHealth && !force)
        {
            return Results.Conflict(new
            {
                error = $"Service '{name}' participates in the aggregated health check; retry with ?force=true to stop it.",
            });
        }

        await MutateAsync(manifests, manifest, m => m.DesiredStatus = "stopped", user, ct);
        await AuditAsync(deploys, name, DeployAction.Stop, user, manifest.Digest, manifest.Digest, ct);

        return Results.Json(new { name, desiredStatus = "stopped", action = DeployAction.Stop });
    }

    private static async Task<IResult> StartAsync(
        string name,
        ClaimsPrincipal user,
        IManifestStore manifests,
        IDeployStore deploys,
        CancellationToken ct)
    {
        if (IsReserved(name, out var reserved))
        {
            return reserved;
        }

        var manifest = await manifests.GetAsync(name, ct);
        if (manifest is null)
        {
            return NotFoundService(name);
        }

        await MutateAsync(manifests, manifest, m => m.DesiredStatus = "running", user, ct);
        await AuditAsync(deploys, name, DeployAction.Start, user, manifest.Digest, manifest.Digest, ct);

        return Results.Json(new { name, desiredStatus = "running", action = DeployAction.Start });
    }

    private static async Task<IResult> RestartAsync(
        string name,
        ClaimsPrincipal user,
        IManifestStore manifests,
        IDeployStore deploys,
        CancellationToken ct)
    {
        if (IsReserved(name, out var reserved))
        {
            return reserved;
        }

        var manifest = await manifests.GetAsync(name, ct);
        if (manifest is null)
        {
            return NotFoundService(name);
        }

        // Restart = stamp restart_requested_at = now (tech-spec §4.5). Each instance's
        // reconciler then blue-green-recreates any container that started before this
        // stamp (same digest/tag, zero-downtime) and leaves already-recreated containers
        // alone, so a restart converges fleet-wide exactly once — no restart loop. It
        // also (re)asserts desired=running, so a restart on a stopped service starts it,
        // matching /start (documented in the README mgmt table).
        var now = DateTimeOffset.UtcNow;
        await MutateAsync(manifests, manifest, m =>
        {
            m.DesiredStatus = "running";
            m.RestartRequestedAt = now;
        }, user, ct);
        await AuditAsync(deploys, name, DeployAction.Restart, user, manifest.Digest, manifest.Digest, ct);

        return Results.Json(new { name, action = DeployAction.Restart });
    }

    private static async Task<IResult> UpsertServiceAsync(
        string name,
        UpsertServiceRequest request,
        ClaimsPrincipal user,
        IManifestStore manifests,
        IDeployStore deploys,
        CancellationToken ct)
    {
        if (IsReserved(name, out var reserved))
        {
            return reserved;
        }

        if (!ServiceNameRegex().IsMatch(name))
        {
            return Results.BadRequest(new { error = "Service name must match ^[a-z0-9-]+$." });
        }

        if (string.IsNullOrWhiteSpace(request.Image) || string.IsNullOrWhiteSpace(request.Tag))
        {
            return Results.BadRequest(new { error = "image and tag are required." });
        }

        if (request.Port is < 1 or > 65535)
        {
            return Results.BadRequest(new { error = "port must be in the range 1..65535." });
        }

        var existing = await manifests.GetAsync(name, ct);

        // realtime_auth_path (task #594) is a path on the service, appended to the
        // gateway-resolved base address for the auth callback. Tri-state on an edit
        // (see UpsertServiceRequest): a null/absent field PRESERVES the stored value so a
        // minimal re-upsert can never silently flip private channels to public; an empty
        // string CLEARS it; a non-empty value is validated (must be a rooted path so it
        // can never smuggle an absolute URL into the callback) and set.
        string? authPath;
        if (request.RealtimeAuthPath is null)
        {
            authPath = existing?.RealtimeAuthPath;
        }
        else
        {
            authPath = request.RealtimeAuthPath.Trim();
            if (authPath.Length == 0)
            {
                authPath = null;
            }
            else if (!authPath.StartsWith('/'))
            {
                return Results.BadRequest(new { error = "realtimeAuthPath must be a rooted path beginning with '/'." });
            }
        }

        // realtime_message_path (task #611) is a path on the service that receives messages
        // sent FROM its clients via the hub's SendToChannel. Same tri-state + rooted-path
        // validation as the auth path: null/absent PRESERVES the stored value (a minimal
        // re-upsert never silently disables full-duplex messaging), empty string CLEARS it,
        // a non-empty value must be a rooted path (so it can never smuggle an absolute URL
        // into the forward) and is set.
        string? messagePath;
        if (request.RealtimeMessagePath is null)
        {
            messagePath = existing?.RealtimeMessagePath;
        }
        else
        {
            messagePath = request.RealtimeMessagePath.Trim();
            if (messagePath.Length == 0)
            {
                messagePath = null;
            }
            else if (!messagePath.StartsWith('/'))
            {
                return Results.BadRequest(new { error = "realtimeMessagePath must be a rooted path beginning with '/'." });
            }
        }

        // realtime_allowed_origins (task #595) is the comma-separated set of exact
        // browser origins whose frontends may negotiate /hub for this service. Same
        // tri-state as the auth path: null/absent PRESERVES the stored origins (so a
        // minimal re-upsert can never silently wipe the hub CORS allow-list), empty
        // string CLEARS them, and a non-empty value is validated and canonicalized so the
        // dynamic hub CORS policy only ever stores absolute http(s) origins with no
        // path/wildcard (a wildcard would break the credentialed exact-match it relies on).
        string? allowedOrigins;
        if (request.RealtimeAllowedOrigins is null)
        {
            allowedOrigins = existing?.RealtimeAllowedOrigins;
        }
        else if (!RealtimeAllowedOrigins.TryNormalize(request.RealtimeAllowedOrigins, out allowedOrigins, out var badOrigin))
        {
            return Results.BadRequest(new
            {
                error = $"realtimeAllowedOrigins entry '{badOrigin}' must be an absolute http(s) origin (scheme://host[:port]) with no path or wildcard.",
            });
        }

        // A pinned digest is only valid for the exact image:tag it was resolved from.
        // If the edit changes the image or tag, the old digest is stale — clear it so
        // the next reconcile re-resolves the digest from the new tag (production bug
        // 2026-08-08: a tag change left the stale digest and every instance failed to
        // start image@<stale digest> on a loop). Editing only port/health/desired on an
        // unchanged image+tag keeps the pinned digest so a digest-pinned deploy is not
        // silently unpinned.
        var imageOrTagChanged = existing is not null
            && (!string.Equals(existing.Image, request.Image, StringComparison.Ordinal)
                || !string.Equals(existing.Tag, request.Tag, StringComparison.Ordinal));
        var manifest = new ServiceManifest
        {
            Name = name,
            Image = request.Image,
            Tag = request.Tag,
            // New services always start with a null digest; edits keep the pinned
            // digest only while image+tag are unchanged (see above).
            Digest = imageOrTagChanged ? null : existing?.Digest,
            Port = request.Port,
            // The same merge policy as the realtime fields (review finding: the
            // tri-state fix was applied per-field, not as an upsert-wide rule): a
            // minimal {image, tag, port} re-upsert must never silently restart a
            // stopped service, strip its secret ref, or flip health inclusion.
            DesiredStatus = string.IsNullOrWhiteSpace(request.DesiredStatus)
                ? existing?.DesiredStatus ?? "running"
                : request.DesiredStatus,
            // Tri-state: absent preserves, empty string clears, non-empty sets.
            EnvSecretRef = request.EnvSecretRef is null
                ? existing?.EnvSecretRef
                : (request.EnvSecretRef.Trim().Length == 0 ? null : request.EnvSecretRef.Trim()),
            RealtimeAuthPath = authPath,
            RealtimeMessagePath = messagePath,
            RealtimeAllowedOrigins = allowedOrigins,
            // realtime_presence (task #612) is a tri-state opt-in: absent/null PRESERVES the
            // stored flag so a minimal re-upsert never flips presence on or off, while an
            // explicit true/false sets it. Null is treated as false everywhere (the default),
            // so a service that never set it reads as opted-out.
            RealtimePresence = request.RealtimePresence ?? existing?.RealtimePresence,
            IncludeInHealth = request.IncludeInHealth ?? existing?.IncludeInHealth ?? true,
            UpdatedBy = ManagementAuth.ResolveUsername(user),
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        // Realtime publish token (task #593): preserve the token already on the row
        // across an edit, and mint one lazily on first create — or on the first upsert
        // of a pre-migration row that never had one. Write-only: ServiceView never
        // echoes it back, so it is exposed to the owner only through its container env.
        RealtimePublishToken.Ensure(manifest, existing);

        await manifests.UpsertAsync(manifest, ct);
        await AuditAsync(deploys, name, DeployAction.Upsert, user, existing?.Digest, manifest.Digest, ct);

        var body = ServiceView(manifest, FleetRollup.Compute(name, Array.Empty<InstanceStatus>()));
        return existing is null
            ? Results.Json(body, statusCode: StatusCodes.Status201Created)
            : Results.Json(body);
    }

    private static async Task<IResult> DeleteServiceAsync(
        string name,
        ClaimsPrincipal user,
        IManifestStore manifests,
        IDeployStore deploys,
        ProxyStateService proxyState,
        CancellationToken ct,
        bool force = false)
    {
        var manifest = await manifests.GetAsync(name, ct);
        if (manifest is null)
        {
            // No row to tear down: a reserved name is still blocked, everything else 404s.
            return IsReserved(name, out var reserved) ? reserved : NotFoundService(name);
        }

        // Delete is a teardown path (see StopAsync): a pre-existing reserved-named row may
        // be removed so its shadowing YARP route stops being generated. Reservation blocks
        // creation, not the teardown of a row that already exists.

        // Guardrail mirroring stop (tech-spec §4.5): removing a health-check
        // dependency requires an explicit confirm flag, else 409.
        if (manifest.IncludeInHealth && !force)
        {
            return Results.Conflict(new
            {
                error = $"Service '{name}' participates in the aggregated health check; retry with ?force=true to delete it.",
            });
        }

        // Purely a manifest-row operation: the reconciler treats the now-unmanifested
        // container as an orphan and stops/removes it on its next loop (tech-spec §4.3).
        await manifests.DeleteAsync(name, ct);

        // Audit the removal: from = the digest that was live, to = null (tech-spec §4.5, §8).
        await AuditAsync(deploys, name, DeployAction.Delete, user, manifest.Digest, null, ct);

        // Drop the service's route immediately on this serving instance so requests to
        // it 404 without waiting for a reconcile loop.
        await proxyState.RefreshRoutesAsync(ct);

        return Results.Json(new { name, action = DeployAction.Delete });
    }

    // ---------- deploy / rollback (OpsDeploy) ----------

    private static async Task<IResult> DeployAsync(
        string name,
        DeployRequest request,
        ClaimsPrincipal user,
        IManifestStore manifests,
        IDeployStore deploys,
        IImageRegistry registry,
        IChannelEventPublisher publisher,
        CancellationToken ct)
    {
        if (IsReserved(name, out var reserved))
        {
            return reserved;
        }

        if (string.IsNullOrWhiteSpace(request.Tag))
        {
            return Results.BadRequest(new { error = "tag is required." });
        }

        var manifest = await manifests.GetAsync(name, ct);
        if (manifest is null)
        {
            return NotFoundService(name);
        }

        string digest;
        try
        {
            // Resolve the digest ONCE (tech-spec §4.4): the fleet converges to this
            // exact image even if the tag later moves.
            digest = await registry.ResolveDigestAsync(manifest.Image, request.Tag, ct);
        }
        catch (ImageNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }

        var fromDigest = manifest.Digest;
        await MutateAsync(manifests, manifest, m =>
        {
            m.Tag = request.Tag;
            m.Digest = digest;
        }, user, ct);

        var record = await AuditAsync(
            deploys, name, DeployAction.Deploy, user, fromDigest, digest, ct, DeployStatus.InProgress);

        // Best-effort broadcast AFTER the manifest mutation has committed. Fire-and-
        // forget so a Redis blip can never turn an already-committed deploy into a 500;
        // the 202 below does not depend on the publish (tech-spec §4.5).
        BroadcastDeploy(publisher, record);

        return Results.Json(
            new { deployId = record.Id, service = name, tag = request.Tag, digest, status = record.Status },
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> RollbackAsync(
        string name,
        ClaimsPrincipal user,
        IManifestStore manifests,
        IDeployStore deploys,
        IChannelEventPublisher publisher,
        CancellationToken ct)
    {
        if (IsReserved(name, out var reserved))
        {
            return reserved;
        }

        var manifest = await manifests.GetAsync(name, ct);
        if (manifest is null)
        {
            return NotFoundService(name);
        }

        // Previous successful digest from history: the most recent completed deploy
        // whose target differs from what is live now (tech-spec §4.5).
        var history = await deploys.ListForServiceAsync(name, ct);
        var previous = history.FirstOrDefault(h =>
            h.Status == DeployStatus.Done
            && !string.IsNullOrEmpty(h.ToDigest)
            && !string.Equals(h.ToDigest, manifest.Digest, StringComparison.Ordinal));

        if (previous is null)
        {
            return Results.Conflict(new { error = $"No previous successful digest to roll '{name}' back to." });
        }

        var fromDigest = manifest.Digest;
        var toDigest = previous.ToDigest!;
        await MutateAsync(manifests, manifest, m => m.Digest = toDigest, user, ct);

        var record = await AuditAsync(
            deploys, name, DeployAction.Rollback, user, fromDigest, toDigest, ct, DeployStatus.InProgress);

        // Best-effort broadcast (see DeployAsync): the 202 must not depend on the publish.
        BroadcastDeploy(publisher, record);

        return Results.Json(
            new { deployId = record.Id, service = name, digest = toDigest, status = record.Status },
            statusCode: StatusCodes.Status202Accepted);
    }

    // ---------- helpers ----------

    private static bool IsReserved(string name, out IResult result)
    {
        foreach (var reserved in ReservedServiceNames)
        {
            if (string.Equals(name, reserved, StringComparison.OrdinalIgnoreCase))
            {
                result = Results.BadRequest(new
                {
                    error = $"'{reserved}' is a reserved name and cannot be used for a managed service.",
                });
                return true;
            }
        }

        result = Results.Empty;
        return false;
    }

    private static IResult NotFoundService(string name) =>
        Results.NotFound(new { error = $"No manifest entry for service '{name}'." });

    /// <summary>Apply a mutation, stamp the actor/time, and persist (tech-spec §4.4 updated_by/updated_at).</summary>
    private static Task MutateAsync(
        IManifestStore manifests,
        ServiceManifest manifest,
        Action<ServiceManifest> mutate,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        mutate(manifest);
        manifest.UpdatedBy = ManagementAuth.ResolveUsername(user);
        manifest.UpdatedAt = DateTimeOffset.UtcNow;
        return manifests.UpsertAsync(manifest, ct);
    }

    /// <summary>Write the audit / rollout ledger row for a mutating call (tech-spec §4.5, §8).</summary>
    private static Task<DeployHistory> AuditAsync(
        IDeployStore deploys,
        string service,
        string action,
        ClaimsPrincipal user,
        string? fromDigest,
        string? toDigest,
        CancellationToken ct,
        string status = DeployStatus.Done)
    {
        var now = DateTimeOffset.UtcNow;
        return deploys.AddAsync(new DeployHistory
        {
            Service = service,
            FromDigest = fromDigest,
            ToDigest = toDigest,
            Actor = ManagementAuth.ResolveUsername(user),
            Action = action,
            Status = status,
            StartedAt = now,
            FinishedAt = status == DeployStatus.InProgress ? null : now,
        }, ct);
    }

    /// <summary>
    /// Fire-and-forget the initiation <c>deploy</c> event on <c>ops:deploys</c> using
    /// the ChannelEvent envelope (tech-spec §4.2). At initiation the deploy is
    /// <c>in_progress</c>, so <c>finishedAt</c> and <c>error</c> are null; the
    /// reconciler publishes the terminal update once the fleet converges.
    /// </summary>
    private static void BroadcastDeploy(IChannelEventPublisher publisher, DeployHistory record) =>
        publisher.TryPublish(OpsDeploysChannel, "deploy", new
        {
            deployId = record.Id,
            service = record.Service,
            action = record.Action,
            fromDigest = record.FromDigest,
            toDigest = record.ToDigest,
            status = record.Status,
            finishedAt = (string?)null,
            error = (string?)null,
        });

    private static object ServiceView(ServiceManifest m, FleetRollup rollup)
    {
        // Most recent convergence error for this service across the fleet, or null
        // when no instance reports one (tech-spec §4.4, §4.5).
        object? latestError = rollup.LatestError is null
            ? null
            : new
            {
                instanceId = rollup.LatestError.InstanceId,
                message = rollup.LatestError.Message,
                at = rollup.LatestError.At,
            };

        return new
        {
            name = m.Name,
            image = m.Image,
            tag = m.Tag,
            digest = m.Digest,
            // Container-internal port only (what the app binds inside the container);
            // host ports are Docker-assigned and surfaced separately as hostPort.
            port = m.Port,
            // The actual Docker-assigned host port the service is published on across the
            // fleet (representative; null when no instance runs a container for it).
            hostPort = rollup.HostPort,
            desiredStatus = m.DesiredStatus,
            envSecretRef = m.EnvSecretRef,
            // Non-secret (unlike the publish token): null = public channels, non-null =
            // every JoinChannel for this service triggers its auth callback (task #594).
            realtimeAuthPath = m.RealtimeAuthPath,
            // Non-secret (task #611): null = full-duplex SendToChannel off for this service,
            // non-null = client messages are forwarded to this path on the service.
            realtimeMessagePath = m.RealtimeMessagePath,
            // Non-secret consumer-origin allowlist for /hub CORS (task #595); null when
            // the service contributes no origins.
            realtimeAllowedOrigins = m.RealtimeAllowedOrigins,
            // Non-secret presence opt-in (task #612): true = membership changes emit a
            // coalesced presence event to subscribers; null/false = off (the default).
            realtimePresence = m.RealtimePresence ?? false,
            includeInHealth = m.IncludeInHealth,
            updatedBy = m.UpdatedBy,
            updatedAt = m.UpdatedAt,
            // When a fleet-wide restart was last requested (null if never); instances
            // recreate any container older than this stamp (tech-spec §4.5).
            restartRequestedAt = m.RestartRequestedAt,
            fleet = new
            {
                runningOn = rollup.RunningOn,
                totalInstances = rollup.TotalInstances,
                digests = rollup.Digests,
                // Instances whose last reconcile action for this service failed, and the
                // most recent such error fleet-wide (tech-spec §4.4).
                errorOn = rollup.ErrorOn,
                latestError,
                // Instances whose reconciler has tripped the digest-drift circuit
                // breaker for this service (task #99): the pinned digest cannot be
                // satisfied from the tag and the reconciler has stopped replacing.
                degradedOn = rollup.DegradedOn,
            },
        };
    }

    private static object DeployView(DeployHistory d) => new
    {
        id = d.Id,
        service = d.Service,
        fromDigest = d.FromDigest,
        toDigest = d.ToDigest,
        actor = d.Actor,
        action = d.Action,
        status = d.Status,
        startedAt = d.StartedAt,
        finishedAt = d.FinishedAt,
        detail = d.Detail,
    };
}
