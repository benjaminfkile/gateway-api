using Gateway.Api.Data;

namespace Gateway.Api.Proxy;

/// <summary>
/// Maps a manifest entry to the base address YARP forwards to. In production a
/// service is reached at <c>http://{name}:{port}</c> — the container's DNS name
/// on the internal Docker network is the service name (tech-spec §4.1). This is
/// a seam so integration tests can point routes at a real localhost test server
/// instead of a container DNS name.
/// </summary>
public interface IServiceAddressResolver
{
    string Resolve(ServiceManifest manifest);

    /// <summary>
    /// Resolve the base address for an explicit container name and port — used by
    /// the reconciler to reach a blue-green candidate (<c>{name}-green</c> on its
    /// side port) before it is promoted to the canonical name. Defaults to the
    /// same container-DNS form as <see cref="Resolve(ServiceManifest)"/>.
    /// </summary>
    string Resolve(string name, int port) => $"http://{name}:{port}";
}

/// <summary>
/// Default resolver: <c>http://{name}:{port}</c>, i.e. the container DNS name on
/// the internal Docker network.
/// </summary>
public sealed class ContainerDnsAddressResolver : IServiceAddressResolver
{
    public string Resolve(ServiceManifest manifest) => Resolve(manifest.Name, manifest.Port);

    public string Resolve(string name, int port) => $"http://{name}:{port}";
}
