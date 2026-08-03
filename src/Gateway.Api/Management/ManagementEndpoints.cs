using System.Security.Claims;
using System.Text.RegularExpressions;
using Gateway.Api.Auth;
using Gateway.Api.Data;
using Gateway.Api.Instances;
using Gateway.Api.Manifest;
using Gateway.Api.RealTime;
using Gateway.Api.Reconcile;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;

namespace Gateway.Api.Management;

/// <summary>Body of <c>POST /mgmt/services/{name}/deploy</c>.</summary>
public sealed record DeployRequest(string? Tag);

/// <summary>Body of <c>PUT /mgmt/services/{name}</c> (create/update a manifest entry).</summary>
public sealed record UpsertServiceRequest(
    string? Image,
    string? Tag,
    int Port,
    string? DesiredStatus,
    string? EnvSecretRef,
    bool IncludeInHealth);

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

    /// <summary>The gateway is never a managed service; its name is reserved (tech-spec §4.5).</summary>
    public const string ReservedGatewayName = "gateway";

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
        if (IsReserved(name, out var reserved))
        {
            return reserved;
        }

        var manifest = await manifests.GetAsync(name, ct);
        if (manifest is null)
        {
            return NotFoundService(name);
        }

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

        // Restart = touch updated_at to force the reconciler's env-drift replace path
        // (tech-spec §4.5); no field other than the audit stamp changes.
        await MutateAsync(manifests, manifest, _ => { }, user, ct);
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
        var manifest = new ServiceManifest
        {
            Name = name,
            Image = request.Image,
            Tag = request.Tag,
            // Preserve a previously-resolved digest across an edit; a deploy re-resolves it.
            Digest = existing?.Digest,
            Port = request.Port,
            DesiredStatus = string.IsNullOrWhiteSpace(request.DesiredStatus) ? "running" : request.DesiredStatus,
            EnvSecretRef = request.EnvSecretRef,
            IncludeInHealth = request.IncludeInHealth,
            UpdatedBy = ManagementAuth.ResolveUsername(user),
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await manifests.UpsertAsync(manifest, ct);
        await AuditAsync(deploys, name, DeployAction.Upsert, user, existing?.Digest, manifest.Digest, ct);

        var body = ServiceView(manifest, FleetRollup.Compute(name, Array.Empty<InstanceStatus>()));
        return existing is null
            ? Results.Json(body, statusCode: StatusCodes.Status201Created)
            : Results.Json(body);
    }

    // ---------- deploy / rollback (OpsDeploy) ----------

    private static async Task<IResult> DeployAsync(
        string name,
        DeployRequest request,
        ClaimsPrincipal user,
        IManifestStore manifests,
        IDeployStore deploys,
        IImageRegistry registry,
        IHubContext<GatewayHub> hub,
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

        await BroadcastAsync(hub, record, ct);

        return Results.Json(
            new { deployId = record.Id, service = name, tag = request.Tag, digest, status = record.Status },
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> RollbackAsync(
        string name,
        ClaimsPrincipal user,
        IManifestStore manifests,
        IDeployStore deploys,
        IHubContext<GatewayHub> hub,
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

        await BroadcastAsync(hub, record, ct);

        return Results.Json(
            new { deployId = record.Id, service = name, digest = toDigest, status = record.Status },
            statusCode: StatusCodes.Status202Accepted);
    }

    // ---------- helpers ----------

    private static bool IsReserved(string name, out IResult result)
    {
        if (string.Equals(name, ReservedGatewayName, StringComparison.OrdinalIgnoreCase))
        {
            result = Results.BadRequest(new
            {
                error = "The gateway is not a managed service and cannot be controlled from the dashboard.",
            });
            return true;
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

    private static Task BroadcastAsync(IHubContext<GatewayHub> hub, DeployHistory record, CancellationToken ct) =>
        hub.Clients.Group(OpsDeploysChannel).SendAsync("deploy", new
        {
            deployId = record.Id,
            service = record.Service,
            action = record.Action,
            fromDigest = record.FromDigest,
            toDigest = record.ToDigest,
            status = record.Status,
        }, ct);

    private static object ServiceView(ServiceManifest m, FleetRollup rollup) => new
    {
        name = m.Name,
        image = m.Image,
        tag = m.Tag,
        digest = m.Digest,
        port = m.Port,
        desiredStatus = m.DesiredStatus,
        envSecretRef = m.EnvSecretRef,
        includeInHealth = m.IncludeInHealth,
        updatedBy = m.UpdatedBy,
        updatedAt = m.UpdatedAt,
        fleet = new
        {
            runningOn = rollup.RunningOn,
            totalInstances = rollup.TotalInstances,
            digests = rollup.Digests,
        },
    };

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
