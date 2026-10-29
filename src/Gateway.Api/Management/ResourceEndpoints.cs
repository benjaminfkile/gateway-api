using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Gateway.Api.Auth;
using Gateway.Api.Data;
using Gateway.Api.Load;
using Gateway.Api.Manifest;
using Gateway.Api.RealTime;
using Gateway.Api.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Gateway.Api.Management;

/// <summary>Body of <c>PUT /mgmt/resources/fleet/size</c>.</summary>
public sealed record FleetSizeRequest(int? Size = null);

/// <summary>Body of <c>PUT /mgmt/resources/fleet/class</c>.</summary>
public sealed record FleetClassRequest(string? InstanceClass = null);

/// <summary>Body of <c>PUT /mgmt/resources/fleet/class/rollback</c>.</summary>
public sealed record FleetClassRollbackRequest(long? TemplateVersion = null);

/// <summary>Body of <c>PUT /mgmt/resources/cache/node-type</c>.</summary>
public sealed record CacheNodeTypeRequest(string? NodeType = null);

/// <summary>Body of <c>PUT /mgmt/resources/database/class</c>; <see cref="Confirm"/> must equal the instance identifier.</summary>
public sealed record DatabaseClassRequest(string? InstanceClass = null, string? Confirm = null);

/// <summary>
/// The Resources API under <c>/mgmt/resources</c> (docs/load.md section 9): reads need
/// OpsDeploy, every mutation OpsAdmin. Each mutation, in order: <c>503
/// resource_unconfigured</c> when the resource's configuration key is unset, <c>400</c>
/// on the body (<c>class_not_allowed</c> outside the allow list,
/// <c>confirm_mismatch</c> for the database), <c>409 run_active</c> while a load run is
/// non-terminal, then the resource's own conflicts (<c>refresh_in_progress</c>,
/// <c>downgrade_unsafe</c>). A change that reaches AWS writes a <c>resource_audit</c>
/// row and publishes <c>change</c> on <see cref="OpsResourcesChannel"/>. An AWS error
/// answers <c>502 { error: "aws_error", detail }</c>.
/// </summary>
public static class ResourceEndpoints
{
    /// <summary>Hub channel the dashboard listens on for resource events.</summary>
    public const string OpsResourcesChannel = "ops:resources";

    /// <summary>DI key of the <see cref="TimeProvider"/> the audit rows are stamped from.</summary>
    public const string ClockKey = "resources";

    public const string FleetResource = "fleet";
    public const string CacheResource = "cache";
    public const string DatabaseResource = "database";

    public const string SizeAction = "size";
    public const string ClassAction = "class";
    public const string ClassRollbackAction = "class_rollback";
    public const string RefreshEndedAction = "refresh_ended";
    public const string NodeTypeAction = "node_type";

    public const int DefaultAuditLimit = 50;
    public const int MaxAuditLimit = 500;

    /// <summary>The note a cache node type change answers with and audits.</summary>
    public const string CacheNote =
        "Realtime publishes are dropped while the backplane is degraded: the node type change replaces the cache nodes, "
        + "and events published while the backplane reconnects never reach the other instances.";

    /// <summary>The note a database class change answers with and audits; <c>{0}</c> is the instance identifier.</summary>
    public const string DatabaseNoteFormat =
        "The database instance {0} reboots to change class: every service listed loses its database connection until it is back.";

    private static readonly JsonSerializerOptions DetailJson = new(JsonSerializerDefaults.Web);

    /// <summary>Map the resources endpoints onto <c>/mgmt/resources</c>.</summary>
    public static IEndpointRouteBuilder MapResourceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var resources = endpoints.MapGroup("/mgmt/resources");

        resources.MapGet("", GetResourcesAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        resources.MapGet("/fleet/refresh", GetFleetRefreshAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);
        resources.MapGet("/audit", GetAuditAsync).RequireAuthorization(ManagementAuth.OpsDeployPolicy);

        resources.MapPut("/fleet/size", SetFleetSizeAsync).RequireAuthorization(ManagementAuth.OpsAdminPolicy);
        resources.MapPut("/fleet/class", SetFleetClassAsync).RequireAuthorization(ManagementAuth.OpsAdminPolicy);
        resources.MapPut("/fleet/class/rollback", RollbackFleetClassAsync).RequireAuthorization(ManagementAuth.OpsAdminPolicy);
        resources.MapPut("/cache/node-type", SetCacheNodeTypeAsync).RequireAuthorization(ManagementAuth.OpsAdminPolicy);
        resources.MapPut("/database/class", SetDatabaseClassAsync).RequireAuthorization(ManagementAuth.OpsAdminPolicy);

        return endpoints;
    }

    // ---------- reads (OpsDeploy) ----------

    private static async Task<IResult> GetResourcesAsync(
        ResourcesOptions options,
        LoadOptions load,
        FleetGroupResolver groups,
        IFleetScaler fleet,
        ICacheScaler cache,
        IDatabaseScaler database,
        ILoadStore loadStore,
        CancellationToken ct)
    {
        var group = await groups.GetAsync(ct);

        return Results.Json(new
        {
            runActive = await loadStore.GetActiveRunAsync(ct) is not null,
            fleet = group is null ? null : await FleetBlockAsync(options, fleet, group, ct),
            generators = string.IsNullOrWhiteSpace(load.GeneratorGroup) ? null : await GeneratorBlockAsync(fleet, load.GeneratorGroup, ct),
            cache = options.CacheReplicationGroup is { } cacheId ? await CacheBlockAsync(options, cache, cacheId, ct) : null,
            database = options.DatabaseInstance is { } dbId ? await DatabaseBlockAsync(options, database, dbId, ct) : null,
        });
    }

    private static async Task<object> FleetBlockAsync(ResourcesOptions options, IFleetScaler fleet, string group, CancellationToken ct)
    {
        FleetGroupSize? size = null;
        FleetLaunchTemplate? template = null;
        FleetRefresh? newest = null;
        string? error = null;
        try
        {
            size = await fleet.DescribeGroupAsync(group, ct);
            if (size is null)
            {
                error = $"The fleet group {group} does not exist.";
            }
            else
            {
                if (options.FleetTemplate is { } name)
                {
                    template = await fleet.DescribeLaunchTemplateAsync(name, ct);
                    error = template is null ? $"The fleet launch template {name} does not exist." : null;
                }

                newest = (await fleet.DescribeInstanceRefreshesAsync(group, ct: ct)).FirstOrDefault();
            }
        }
        catch (Exception ex) when (IsScalerError(ex))
        {
            error = ex.Message;
        }

        return new
        {
            group,
            min = size?.Min,
            desired = size?.Desired,
            max = size?.Max,
            inService = size?.InService,
            instanceClass = template?.InstanceType,
            templateVersion = template?.DefaultVersion,
            refreshInProgress = newest is { IsTerminal: false },
            classes = Sorted(options.FleetClasses),
            maxSize = options.FleetMaxSize,
            error,
        };
    }

    private static async Task<object> GeneratorBlockAsync(IFleetScaler fleet, string group, CancellationToken ct)
    {
        FleetGroupSize? size = null;
        string? error = null;
        try
        {
            size = await fleet.DescribeGroupAsync(group, ct);
            error = size is null ? $"The generator group {group} does not exist." : null;
        }
        catch (Exception ex) when (IsScalerError(ex))
        {
            error = ex.Message;
        }

        return new { group, desired = size?.Desired, inService = size?.InService, max = size?.Max, error };
    }

    private static async Task<object> CacheBlockAsync(ResourcesOptions options, ICacheScaler cache, string id, CancellationToken ct)
    {
        CacheReplicationGroup? group = null;
        string? error = null;
        try
        {
            group = await cache.DescribeReplicationGroupAsync(id, ct);
            error = group is null ? $"The replication group {id} does not exist." : null;
        }
        catch (Exception ex) when (IsScalerError(ex))
        {
            error = ex.Message;
        }

        return new { id, nodeType = group?.NodeType, status = group?.Status, nodeTypes = Sorted(options.CacheNodeTypes), error };
    }

    private static async Task<object> DatabaseBlockAsync(ResourcesOptions options, IDatabaseScaler database, string id, CancellationToken ct)
    {
        DatabaseInstanceState? instance = null;
        string? error = null;
        try
        {
            instance = await database.DescribeInstanceAsync(id, ct);
            error = instance is null ? $"The database instance {id} does not exist." : null;
        }
        catch (Exception ex) when (IsScalerError(ex))
        {
            error = ex.Message;
        }

        return new
        {
            id,
            instanceClass = instance?.InstanceClass,
            status = instance?.Status,
            maxConnections = instance?.MaxConnections,
            connectionsInUse = instance?.ConnectionsInUse,
            documentedMaxConnections = DatabaseClassConnections.For(instance?.InstanceClass),
            classes = Sorted(options.DatabaseClasses)
                .Select(c => new { instanceClass = c, documentedMaxConnections = DatabaseClassConnections.For(c) })
                .ToList(),
            error,
        };
    }

    private static Task<IResult> GetFleetRefreshAsync(FleetGroupResolver groups, IFleetScaler fleet, CancellationToken ct) =>
        GuardAsync(async () =>
        {
            if (await groups.GetAsync(ct) is not { } group)
            {
                return Unconfigured(FleetResource);
            }

            var newest = (await fleet.DescribeInstanceRefreshesAsync(group, ct: ct)).FirstOrDefault();
            return Results.Json(new
            {
                group,
                refreshId = newest?.RefreshId,
                status = newest?.Status,
                percentComplete = newest?.PercentComplete,
                inProgress = newest is { IsTerminal: false },
            });
        });

    private static async Task<IResult> GetAuditAsync(int? limit, IResourceAuditStore audit, CancellationToken ct)
    {
        var take = limit ?? DefaultAuditLimit;
        if (take < 1 || take > MaxAuditLimit)
        {
            return Invalid("limit", $"limit must be in the range 1..{MaxAuditLimit}.");
        }

        var rows = await audit.ListAsync(take, ct: ct);
        return Results.Json(rows.Select(AuditView).ToList());
    }

    // ---------- fleet mutations (OpsAdmin) ----------

    private static Task<IResult> SetFleetSizeAsync(
        FleetSizeRequest request,
        ClaimsPrincipal user,
        ResourcesOptions options,
        FleetGroupResolver groups,
        IFleetScaler fleet,
        ILoadStore loadStore,
        IResourceAuditStore audit,
        IChannelEventPublisher publisher,
        [FromKeyedServices(ClockKey)] TimeProvider clock,
        CancellationToken ct) =>
        GuardAsync(async () =>
        {
            if (await groups.GetAsync(ct) is not { } group)
            {
                return Unconfigured(FleetResource);
            }

            if (request.Size is not { } size || size < 1 || size > options.FleetMaxSize)
            {
                return Invalid("size", $"size is required and must be in the range 1..{options.FleetMaxSize}.");
            }

            if (await loadStore.GetActiveRunAsync(ct) is not null)
            {
                return RunActive();
            }

            if (await FleetRefreshes.InFlightAsync(fleet, group, ct) is { } inFlight)
            {
                return RefreshInProgress(inFlight);
            }

            var before = await fleet.DescribeGroupAsync(group, ct);
            if (before is null)
            {
                return NotFound($"The fleet group {group} does not exist.");
            }

            // Never desired alone: the group's target-tracking policy scales an idle
            // fleet back in within minutes when only desired is raised.
            await fleet.SetGroupSizeAsync(group, size, size, size, ct);

            var after = new FleetGroupSize(size, size, size, before.InService);
            await RecordAsync(audit, publisher, clock, user, FleetResource, SizeAction,
                SizeText(before), SizeText(after), new { group }, ct);

            return Results.Json(new { group, min = size, desired = size, max = size }, statusCode: StatusCodes.Status202Accepted);
        });

    private static Task<IResult> SetFleetClassAsync(
        FleetClassRequest request,
        ClaimsPrincipal user,
        ResourcesOptions options,
        FleetGroupResolver groups,
        IFleetScaler fleet,
        ILoadStore loadStore,
        IResourceAuditStore audit,
        IChannelEventPublisher publisher,
        [FromKeyedServices(ClockKey)] TimeProvider clock,
        CancellationToken ct) =>
        GuardAsync(async () =>
        {
            if (options.FleetTemplate is not { } template || await groups.GetAsync(ct) is not { } group)
            {
                return Unconfigured(FleetResource);
            }

            var instanceClass = request.InstanceClass?.Trim();
            if (string.IsNullOrEmpty(instanceClass) || !options.FleetClasses.Contains(instanceClass))
            {
                return ClassNotAllowed("instanceClass", instanceClass, options.FleetClasses);
            }

            if (await loadStore.GetActiveRunAsync(ct) is not null)
            {
                return RunActive();
            }

            if (await FleetRefreshes.InFlightAsync(fleet, group, ct) is { } inFlight)
            {
                return RefreshInProgress(inFlight);
            }

            var current = await fleet.DescribeLaunchTemplateAsync(template, ct);
            if (current is null)
            {
                return NotFound($"The fleet launch template {template} does not exist.");
            }

            var size = await fleet.DescribeGroupAsync(group, ct);
            if (size is null)
            {
                return NotFound($"The fleet group {group} does not exist.");
            }

            // A new version from $Default with only InstanceType changed, made the
            // default before the refresh starts so the replacements launch from it.
            var version = await fleet.CreateLaunchTemplateVersionAsync(template, instanceClass, ct);
            await fleet.SetDefaultLaunchTemplateVersionAsync(template, version, ct);

            return await StartRefreshAsync(fleet, audit, publisher, clock, user, ClassAction, group, size,
                TemplateText(current.InstanceType, current.DefaultVersion), TemplateText(instanceClass, version),
                version, current.DefaultVersion, ct);
        });

    private static Task<IResult> RollbackFleetClassAsync(
        FleetClassRollbackRequest request,
        ClaimsPrincipal user,
        ResourcesOptions options,
        FleetGroupResolver groups,
        IFleetScaler fleet,
        ILoadStore loadStore,
        IResourceAuditStore audit,
        IChannelEventPublisher publisher,
        [FromKeyedServices(ClockKey)] TimeProvider clock,
        CancellationToken ct) =>
        GuardAsync(async () =>
        {
            if (options.FleetTemplate is not { } template || await groups.GetAsync(ct) is not { } group)
            {
                return Unconfigured(FleetResource);
            }

            if (request.TemplateVersion is not { } version || version < 1)
            {
                return Invalid("templateVersion", "templateVersion is required and must be a version number.");
            }

            if (await loadStore.GetActiveRunAsync(ct) is not null)
            {
                return RunActive();
            }

            if (await FleetRefreshes.InFlightAsync(fleet, group, ct) is { } inFlight)
            {
                return RefreshInProgress(inFlight);
            }

            var target = await fleet.DescribeLaunchTemplateVersionAsync(template, version, ct);
            if (target is null)
            {
                return Results.BadRequest(new
                {
                    error = "version_not_found",
                    field = "templateVersion",
                    detail = $"The fleet launch template has no version {version}.",
                });
            }

            var current = await fleet.DescribeLaunchTemplateAsync(template, ct);
            var size = await fleet.DescribeGroupAsync(group, ct);
            if (current is null || size is null)
            {
                return NotFound(current is null
                    ? $"The fleet launch template {template} does not exist."
                    : $"The fleet group {group} does not exist.");
            }

            await fleet.SetDefaultLaunchTemplateVersionAsync(template, version, ct);

            return await StartRefreshAsync(fleet, audit, publisher, clock, user, ClassRollbackAction, group, size,
                TemplateText(current.InstanceType, current.DefaultVersion), TemplateText(target.InstanceType, version),
                version, current.DefaultVersion, ct);
        });

    // The template's $Default already moved: raise the max, start the refresh, and
    // audit either way. The watcher finds the refresh through the audit row's detail.
    private static async Task<IResult> StartRefreshAsync(
        IFleetScaler fleet,
        IResourceAuditStore audit,
        IChannelEventPublisher publisher,
        TimeProvider clock,
        ClaimsPrincipal user,
        string action,
        string group,
        FleetGroupSize size,
        string before,
        string after,
        long templateVersion,
        long previousVersion,
        CancellationToken ct)
    {
        FleetRefreshes.Started started;
        try
        {
            started = await FleetRefreshes.StartAsync(fleet, group, size, ct);
        }
        catch (Exception ex) when (IsScalerError(ex))
        {
            await RecordAsync(audit, publisher, clock, user, FleetResource, action, before, after,
                new { group, templateVersion, previousVersion, refreshError = ex.Message }, ct);
            return Results.Json(new
            {
                error = "aws_error",
                detail = $"The launch template default is now version {templateVersion}, but the instance refresh did not start: {ex.Message}",
                templateVersion,
            }, statusCode: StatusCodes.Status502BadGateway);
        }

        await RecordAsync(audit, publisher, clock, user, FleetResource, action, before, after,
            new RefreshDetail(group, started.RefreshId, started.RestoreMax, templateVersion, previousVersion), ct);
        publisher.TryPublish(OpsResourcesChannel, "refresh", new { refreshId = started.RefreshId, status = "Pending", percentComplete = 0 });

        return Results.Json(new { templateVersion, refreshId = started.RefreshId }, statusCode: StatusCodes.Status202Accepted);
    }

    // ---------- cache and database mutations (OpsAdmin) ----------

    private static Task<IResult> SetCacheNodeTypeAsync(
        CacheNodeTypeRequest request,
        ClaimsPrincipal user,
        ResourcesOptions options,
        ICacheScaler cache,
        ILoadStore loadStore,
        IResourceAuditStore audit,
        IChannelEventPublisher publisher,
        [FromKeyedServices(ClockKey)] TimeProvider clock,
        CancellationToken ct) =>
        GuardAsync(async () =>
        {
            if (options.CacheReplicationGroup is not { } id)
            {
                return Unconfigured(CacheResource);
            }

            var nodeType = request.NodeType?.Trim();
            if (string.IsNullOrEmpty(nodeType) || !options.CacheNodeTypes.Contains(nodeType))
            {
                return ClassNotAllowed("nodeType", nodeType, options.CacheNodeTypes);
            }

            if (await loadStore.GetActiveRunAsync(ct) is not null)
            {
                return RunActive();
            }

            var before = await cache.DescribeReplicationGroupAsync(id, ct);
            if (before is null)
            {
                return NotFound($"The replication group {id} does not exist.");
            }

            await cache.ModifyNodeTypeAsync(id, nodeType, ct);

            await RecordAsync(audit, publisher, clock, user, CacheResource, NodeTypeAction,
                before.NodeType, nodeType, new { id, note = CacheNote }, ct);

            return Results.Json(new { id, nodeType, note = CacheNote }, statusCode: StatusCodes.Status202Accepted);
        });

    private static Task<IResult> SetDatabaseClassAsync(
        DatabaseClassRequest request,
        ClaimsPrincipal user,
        ResourcesOptions options,
        LoadOptions load,
        IDatabaseScaler database,
        IManifestStore manifests,
        ILoadStore loadStore,
        IResourceAuditStore audit,
        IChannelEventPublisher publisher,
        [FromKeyedServices(ClockKey)] TimeProvider clock,
        CancellationToken ct) =>
        GuardAsync(async () =>
        {
            if (options.DatabaseInstance is not { } id)
            {
                return Unconfigured(DatabaseResource);
            }

            var instanceClass = request.InstanceClass?.Trim();
            if (string.IsNullOrEmpty(instanceClass) || !options.DatabaseClasses.Contains(instanceClass))
            {
                return ClassNotAllowed("instanceClass", instanceClass, options.DatabaseClasses);
            }

            if (!string.Equals(request.Confirm, id, StringComparison.Ordinal))
            {
                return Results.BadRequest(new
                {
                    error = "confirm_mismatch",
                    field = "confirm",
                    detail = "Type the database instance identifier to confirm.",
                });
            }

            if (await loadStore.GetActiveRunAsync(ct) is not null)
            {
                return RunActive();
            }

            var before = await database.DescribeInstanceAsync(id, ct);
            if (before is null)
            {
                return NotFound($"The database instance {id} does not exist.");
            }

            var downgrade = CheckDowngrade(before.InstanceClass, instanceClass, before.ConnectionsInUse, load.DbDowngradeMarginPercent);
            if (downgrade is not null)
            {
                return downgrade;
            }

            await database.ModifyInstanceClassAsync(id, instanceClass, ct);

            var services = (await manifests.GetAllAsync(ct))
                .Select(s => s.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            var note = string.Format(System.Globalization.CultureInfo.InvariantCulture, DatabaseNoteFormat, id);

            await RecordAsync(audit, publisher, clock, user, DatabaseResource, ClassAction,
                before.InstanceClass, instanceClass, new { id, services, note }, ct);

            return Results.Json(new { id, instanceClass, services, note }, statusCode: StatusCodes.Status202Accepted);
        });

    /// <summary>
    /// The downgrade rule of docs/load.md section 9. A change is a downgrade when the
    /// target's documented <c>max_connections</c> (<see cref="DatabaseClassConnections"/>)
    /// is lower than the current class's, or when either class is missing from the
    /// table. A downgrade is allowed only when the target's documented maximum is at
    /// least the connections in use plus <paramref name="marginPercent"/> percent;
    /// unknown connections in use or an unknown target refuse it. Null when allowed,
    /// else the <c>409 downgrade_unsafe</c> answer.
    /// </summary>
    public static IResult? CheckDowngrade(string? currentClass, string targetClass, int? connectionsInUse, int marginPercent)
    {
        var currentMax = DatabaseClassConnections.For(currentClass);
        var targetMax = DatabaseClassConnections.For(targetClass);

        var isDowngrade = targetMax is null || currentMax is null || targetMax < currentMax;
        if (!isDowngrade)
        {
            return null;
        }

        var safe = targetMax is { } max
            && connectionsInUse is { } inUse
            && (long)max * 100 >= (long)inUse * (100 + marginPercent);
        if (safe)
        {
            return null;
        }

        var why = targetMax is null
            ? $"{targetClass} is not in the documented max_connections table, so it is not a safe downgrade."
            : connectionsInUse is null
                ? "Connections in use are unknown, so a smaller class cannot be checked."
                : $"{targetClass} documents max_connections {targetMax}, which does not exceed the {connectionsInUse} connections in use by {marginPercent}%.";

        return Results.Json(new
        {
            error = "downgrade_unsafe",
            detail = why,
            currentMaxConnections = currentMax,
            targetMaxConnections = targetMax,
            connectionsInUse,
            marginPercent,
        }, statusCode: StatusCodes.Status409Conflict);
    }

    // ---------- shared ----------

    /// <summary>The detail of a fleet class change or rollback whose refresh started.</summary>
    public sealed record RefreshDetail(string Group, string RefreshId, int RestoreMax, long TemplateVersion, long PreviousVersion);

    /// <summary>Read a <see cref="RefreshDetail"/> back from an audit row, or null when the row carries none.</summary>
    public static RefreshDetail? ReadRefreshDetail(ResourceAudit row)
    {
        if (string.IsNullOrEmpty(row.Detail))
        {
            return null;
        }

        try
        {
            var detail = JsonSerializer.Deserialize<RefreshDetail>(row.Detail, DetailJson);
            return detail is { RefreshId.Length: > 0, Group.Length: > 0 } ? detail : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Write the audit row of a change and publish <c>change</c> on <see cref="OpsResourcesChannel"/>.</summary>
    public static async Task RecordAsync(
        IResourceAuditStore audit,
        IChannelEventPublisher publisher,
        TimeProvider clock,
        ClaimsPrincipal? user,
        string resource,
        string action,
        string? before,
        string? after,
        object? detail,
        CancellationToken ct)
    {
        var by = user is null ? FleetRefreshWatcher.Actor : ManagementAuth.ResolveUsername(user);
        await audit.AddAsync(new ResourceAudit
        {
            At = clock.GetUtcNow(),
            Actor = by,
            Resource = resource,
            Action = action,
            Before = before,
            After = after,
            Detail = detail is null ? null : JsonSerializer.Serialize(detail, DetailJson),
        }, ct);

        publisher.TryPublish(OpsResourcesChannel, "change", new { resource, action, by, before, after });
    }

    private static object AuditView(ResourceAudit row) => new
    {
        id = row.Id,
        at = row.At,
        actor = row.Actor,
        resource = row.Resource,
        action = row.Action,
        before = row.Before,
        after = row.After,
        detail = ParseDetail(row.Detail),
    };

    private static JsonNode? ParseDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(detail);
        }
        catch (JsonException)
        {
            return JsonValue.Create(detail);
        }
    }

    // Maps the scaler failures every mutation shares: the null scaler and AWS errors.
    private static async Task<IResult> GuardAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (NotSupportedException ex)
        {
            return Results.Json(new { error = "resource_unconfigured", detail = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (AmazonServiceException ex)
        {
            return Results.Json(new { error = "aws_error", detail = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static bool IsScalerError(Exception ex) => ex is NotSupportedException or AmazonServiceException;

    private static string SizeText(FleetGroupSize size) => $"min {size.Min}, desired {size.Desired}, max {size.Max}";

    private static string TemplateText(string? instanceType, long version) => $"{instanceType ?? "unset"} (version {version})";

    private static List<string> Sorted(IReadOnlySet<string> values) => values.OrderBy(v => v, StringComparer.Ordinal).ToList();

    private static IResult Unconfigured(string resource) =>
        Results.Json(new { error = "resource_unconfigured", detail = $"The {resource} resource is not configured on this gateway." },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult ClassNotAllowed(string field, string? value, IReadOnlySet<string> allowed) =>
        Results.BadRequest(new
        {
            error = "class_not_allowed",
            field,
            detail = $"{(string.IsNullOrEmpty(value) ? "No class" : value)} is not in the allow list.",
            allowed = Sorted(allowed),
        });

    private static IResult RunActive() => Results.Conflict(new { error = "run_active" });

    private static IResult RefreshInProgress(FleetRefresh refresh) =>
        Results.Conflict(new { error = "refresh_in_progress", refreshId = refresh.RefreshId, status = refresh.Status, percentComplete = refresh.PercentComplete });

    private static IResult NotFound(string detail) => Results.NotFound(new { error = "resource_not_found", detail });

    private static IResult Invalid(string field, string error) => Results.BadRequest(new { error, field });
}
