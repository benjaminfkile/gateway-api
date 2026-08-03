using Gateway.Api.Data;
using Gateway.Api.Manifest;

namespace Gateway.Api.Health;

/// <summary>
/// Builds the aggregated health document (tech-spec §4.1): it selects the
/// manifest entries that opt into health (<c>IncludeInHealth</c>) and are meant
/// to be running (<c>DesiredStatus == "running"</c>), probes them all in
/// parallel via <see cref="IHealthProber"/>, and folds the results into an
/// <see cref="AggregateHealthReport"/>. The gateway status is always <c>"up"</c>:
/// a down downstream is reported but never fails the gateway's own response.
/// </summary>
public sealed class HealthAggregator
{
    private readonly IManifestStore _manifestStore;
    private readonly IHealthProber _prober;

    public HealthAggregator(IManifestStore manifestStore, IHealthProber prober)
    {
        _manifestStore = manifestStore;
        _prober = prober;
    }

    public async Task<AggregateHealthReport> BuildAsync(CancellationToken ct = default)
    {
        var manifests = await _manifestStore.GetAllAsync(ct);

        var probeTargets = manifests
            .Where(m => m.IncludeInHealth
                && string.Equals(m.DesiredStatus, "running", StringComparison.Ordinal))
            .ToList();

        // All probes run concurrently; each is individually bounded and never
        // throws, so the aggregate completes even when services are unreachable.
        var probes = probeTargets
            .Select(async m => (m.Name, Result: await _prober.ProbeAsync(m, ct)))
            .ToList();

        var results = await Task.WhenAll(probes);

        var services = new Dictionary<string, ServiceHealth>(StringComparer.Ordinal);
        foreach (var (name, result) in results)
        {
            services[name] = new ServiceHealth(result.Status, result.HttpStatus, result.ResponseTimeMs);
        }

        return new AggregateHealthReport("up", DateTimeOffset.UtcNow, services);
    }
}
