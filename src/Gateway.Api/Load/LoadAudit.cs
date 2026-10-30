using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway.Api.Data;
using Gateway.Api.Management;
using Gateway.Api.RealTime;

namespace Gateway.Api.Load;

/// <summary>
/// The <c>resource_audit</c> rows of the load feature (docs/load.md sections 3 and 8):
/// the orphan guard writes one each time it sets the generator group to 0, and
/// <c>GET /mgmt/load/audit</c> lists them with the older rows the table already holds.
/// </summary>
public static class LoadAudit
{
    /// <summary>Hub channel the orphan guard publishes its <c>change</c> event on.</summary>
    public const string OpsResourcesChannel = "ops:resources";

    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;

    private static readonly JsonSerializerOptions DetailJson = new(JsonSerializerDefaults.Web);

    /// <summary>Write an audit row and publish <c>change</c> on <see cref="OpsResourcesChannel"/>.</summary>
    public static async Task RecordAsync(
        IResourceAuditStore audit,
        IChannelEventPublisher publisher,
        TimeProvider clock,
        string actor,
        string resource,
        string action,
        string? before,
        string? after,
        object? detail,
        CancellationToken ct)
    {
        await audit.AddAsync(new ResourceAudit
        {
            At = clock.GetUtcNow(),
            Actor = actor,
            Resource = resource,
            Action = action,
            Before = before,
            After = after,
            Detail = detail is null ? null : JsonSerializer.Serialize(detail, DetailJson),
        }, ct);

        publisher.TryPublish(OpsResourcesChannel, "change", new { resource, action, by = actor, before, after });
    }

    /// <summary>One row as the audit route answers it: <c>{ id, at, actor, resource, action, before, after, detail }</c>.</summary>
    public static object View(ResourceAudit row) => new
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
}
