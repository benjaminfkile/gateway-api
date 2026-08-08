using System.Collections.Concurrent;
using Gateway.Api.Containers;
using Gateway.Api.Reconcile;

namespace Gateway.Api.Proxy;

/// <summary>
/// Container-truth map of canonical service name → the host port its running
/// managed container is <b>actually</b> published on.
/// <para>
/// Host ports are Docker-assigned (dynamic) and immutable after container creation:
/// when a blue-green candidate started on its ephemeral host port is promoted
/// (renamed) to the canonical name it keeps that port. The manifest port is only the
/// <i>container-side</i> contract — the gateway must forward traffic (and
/// health-probe) to the port the container is truly bound to, or every request 502s
/// after a successful deploy (tech-spec §7).
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

    private static readonly IReadOnlySet<string> None =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Rebuild the whole map from a fresh container inventory snapshot. Only
    /// canonical containers with a known host port are recorded; transient
    /// <c>-green</c> candidates are ignored (the reconciler owns their lifecycle),
    /// and services no longer present are dropped.
    /// </summary>
    public void ReplaceFrom(IEnumerable<ContainerInfo> containers) => ReconcileFrom(containers, None);

    /// <summary>
    /// Reconcile the map against a fresh container inventory (container-truth),
    /// leaving services in <paramref name="skip"/> untouched — those are mid
    /// blue-green swap, so their canonical port is deliberately overridden and must
    /// not be clobbered (tech-spec §7, requirement #2). Returns <c>true</c> when any
    /// recorded port was added, changed, or removed, so the caller can rebuild YARP
    /// routes <b>only</b> on a real change — a steady fleet churns nothing
    /// (requirement #3). This is what heals the map when a container's published
    /// port changes outside a reconciler action — Docker restart-policy recovery,
    /// manual intervention, or a crash-looper that finally stabilized (production
    /// 2026-08-08) — within one loop and with no gateway restart (requirements #1, #4).
    /// </summary>
    public bool ReconcileFrom(IEnumerable<ContainerInfo> containers, IReadOnlySet<string> skip)
    {
        var snapshot = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in containers)
        {
            if (ReconcileNaming.IsGreen(c.Name) || skip.Contains(c.Name))
            {
                continue;
            }

            if (c.HostPort is int port)
            {
                snapshot[c.Name] = port;
            }
        }

        var changed = false;

        // Drop services whose canonical container has vanished (a swap-in-progress
        // service is never dropped: it is in skip and its old container is still up).
        foreach (var key in _ports.Keys.ToList())
        {
            if (skip.Contains(key) || snapshot.ContainsKey(key))
            {
                continue;
            }

            if (_ports.TryRemove(key, out _))
            {
                changed = true;
            }
        }

        // Add or update from container truth; only a genuine move counts as a change.
        foreach (var kvp in snapshot)
        {
            if (_ports.TryGetValue(kvp.Key, out var existing) && existing == kvp.Value)
            {
                continue;
            }

            _ports[kvp.Key] = kvp.Value;
            changed = true;
        }

        return changed;
    }
}
