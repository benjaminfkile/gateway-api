using Gateway.Api.Data;

namespace Gateway.Api.Proxy;

/// <summary>
/// Maps a manifest entry to the base address YARP forwards to. The gateway runs
/// on the host via systemd (tech-spec §3), not inside the Docker network, so
/// container DNS names do not resolve for it; the reconciler publishes every
/// service container's port to the host (tech-spec §4.1), and services are
/// reached at <c>http://127.0.0.1:{port}</c>. This is a seam so integration
/// tests can point routes at a test server on a different address.
/// </summary>
public interface IServiceAddressResolver
{
    string Resolve(ServiceManifest manifest);

    /// <summary>
    /// Resolve the base address for an explicit container name and port — used by
    /// the reconciler to reach a blue-green candidate (<c>{name}-green</c> on its
    /// Docker-assigned host port) before it is promoted to the canonical name. The
    /// port is the host-published port, so the name does not participate in the
    /// default loopback form.
    /// </summary>
    string Resolve(string name, int port) => $"http://127.0.0.1:{port}";
}

/// <summary>
/// Default resolver: <c>http://127.0.0.1:{port}</c> — the host-published port of
/// the service container, reachable from the host-resident gateway process.
/// </summary>
public sealed class HostLoopbackAddressResolver : IServiceAddressResolver
{
    public string Resolve(ServiceManifest manifest) => Resolve(manifest.Name, manifest.Port);

    public string Resolve(string name, int port) => $"http://127.0.0.1:{port}";
}
