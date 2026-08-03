using Microsoft.Extensions.Logging;

namespace Gateway.Api.Instances;

/// <summary>
/// Resolves this instance's <see cref="InstanceIdentity"/> by trying an ordered
/// list of <see cref="IInstanceMetadata"/> sources and taking the first that
/// applies — in production <c>[Ec2, Env]</c>, so IMDS wins on EC2 and the
/// environment fallback is <b>auto-selected when IMDS is unreachable</b>
/// (tech-spec §4.4). The resolved identity is cached: instance identity is stable
/// for the life of the process, and the heartbeat runs it every reconcile loop.
/// </summary>
public sealed class InstanceMetadataProvider
{
    private readonly IReadOnlyList<IInstanceMetadata> _sources;
    private readonly ILogger<InstanceMetadataProvider>? _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InstanceIdentity? _cached;

    public InstanceMetadataProvider(
        IEnumerable<IInstanceMetadata> sources,
        ILogger<InstanceMetadataProvider>? logger = null)
    {
        _sources = sources.ToList();
        _logger = logger;
    }

    /// <summary>
    /// Resolve (and cache) this instance's identity. Falls through the sources in
    /// order; if every source declines it returns a machine-name identity so the
    /// heartbeat always has a primary key.
    /// </summary>
    public async Task<InstanceIdentity> GetAsync(CancellationToken ct = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            foreach (var source in _sources)
            {
                var identity = await source.TryResolveAsync(ct);
                if (identity is not null)
                {
                    _logger?.LogInformation(
                        "Resolved instance identity {InstanceId} via {Source}.",
                        identity.InstanceId, source.GetType().Name);
                    return _cached = identity;
                }
            }

            // No source applied — extremely unlikely (the env source never declines),
            // but keep a stable id so the heartbeat can still write a row.
            return _cached = new InstanceIdentity(Environment.MachineName, null, null);
        }
        finally
        {
            _gate.Release();
        }
    }
}
