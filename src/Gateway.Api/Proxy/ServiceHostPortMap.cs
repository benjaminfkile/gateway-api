using System.Collections.Concurrent;
using Gateway.Api.Containers;
using Gateway.Api.Reconcile;

namespace Gateway.Api.Proxy;

/// <summary>
/// Container-truth map of canonical service name → the host port its running
/// managed container is <b>actually</b> published on.
/// <para>
/// Docker port bindings are immutable after container creation: when a blue-green
/// candidate started on a side port is promoted (renamed) to the canonical name it
/// keeps that side port. The manifest port is therefore only the <i>container-side</i>
/// contract — the gateway must forward traffic (and health-probe) to the port the
/// container is truly bound to, or every request 502s after a successful deploy
/// (this bug, tech-spec §7).
/// </para>
/// <para>
/// The reconciler keeps this map current from the container inventory (each loop and
/// at the moments a container is started, promoted, or removed); on startup it is
/// primed from the inventory so a promoted-green container keeps receiving traffic
/// across a gateway restart. <see cref="ProxyStateService"/> and
/// <see cref="Health.HttpHealthProber"/> resolve through it, falling back to the
/// manifest port only when a service has no recorded container port.
/// </para>
/// </summary>
public sealed class ServiceHostPortMap
{
    private readonly ConcurrentDictionary<string, int> _ports = new(StringComparer.Ordinal);

    /// <summary>Record the actual host port a service's canonical container is bound to.</summary>
    public void Set(string service, int hostPort) => _ports[service] = hostPort;

    /// <summary>Forget a service's port (its container was removed).</summary>
    public void Remove(string service) => _ports.TryRemove(service, out _);

    /// <summary>Try to read a service's actual host port.</summary>
    public bool TryGet(string service, out int hostPort) => _ports.TryGetValue(service, out hostPort);

    /// <summary>
    /// Rebuild the whole map from a fresh container inventory snapshot. Only
    /// canonical containers with a known host port are recorded; transient
    /// <c>-green</c> candidates are ignored (the reconciler owns their lifecycle),
    /// and services no longer present are dropped.
    /// </summary>
    public void ReplaceFrom(IEnumerable<ContainerInfo> containers)
    {
        var snapshot = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in containers)
        {
            if (ReconcileNaming.IsGreen(c.Name))
            {
                continue;
            }

            if (c.HostPort is int port)
            {
                snapshot[c.Name] = port;
            }
        }

        foreach (var key in _ports.Keys.ToList())
        {
            if (!snapshot.ContainsKey(key))
            {
                _ports.TryRemove(key, out _);
            }
        }

        foreach (var kvp in snapshot)
        {
            _ports[kvp.Key] = kvp.Value;
        }
    }
}
