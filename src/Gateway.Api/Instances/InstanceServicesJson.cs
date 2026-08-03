using System.Text.Json;
using System.Text.Json.Serialization;
using Gateway.Api.Containers;

namespace Gateway.Api.Instances;

/// <summary>
/// Serialises this instance's container inventory into the <c>services</c> jsonb
/// payload of <c>instance_status</c> (tech-spec §4.4):
/// <c>[{name, digest, state, startedAt, restarts}]</c>. Every reconcile loop
/// rebuilds this from the live managed-container list so any instance can answer
/// fleet-wide queries.
/// </summary>
public static class InstanceServicesJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Build the <c>services</c> jsonb string from the managed containers.</summary>
    public static string Build(IReadOnlyList<ContainerInfo> containers)
    {
        var entries = containers
            .Select(c => new ServiceEntry(c.Name, c.Digest, c.State, c.StartedAt, c.Restarts))
            .ToList();

        return JsonSerializer.Serialize(entries, Options);
    }

    /// <summary>One element of the <c>services</c> jsonb array.</summary>
    private sealed record ServiceEntry(
        string Name,
        string? Digest,
        string State,
        DateTimeOffset? StartedAt,
        int Restarts);
}
