using Microsoft.Extensions.Primitives;
using Yarp.ReverseProxy.Configuration;

namespace Gateway.Api.Proxy;

/// <summary>
/// In-memory <see cref="IProxyConfigProvider"/> whose route/cluster set is
/// swapped at runtime — no process restart (tech-spec §4.1). Each
/// <see cref="Update"/> publishes a fresh <see cref="IProxyConfig"/> and signals
/// the previous config's change token so YARP re-reads the routes.
/// </summary>
public sealed class ManifestProxyConfigProvider : IProxyConfigProvider
{
    private volatile ManifestProxyConfig _config =
        new(Array.Empty<RouteConfig>(), Array.Empty<ClusterConfig>());

    public IProxyConfig GetConfig() => _config;

    /// <summary>Publish a new route/cluster set and trigger a YARP reload.</summary>
    public void Update(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
    {
        var old = _config;
        _config = new ManifestProxyConfig(routes, clusters);
        old.SignalChange();
    }

    private sealed class ManifestProxyConfig : IProxyConfig
    {
        private readonly CancellationTokenSource _cts = new();

        public ManifestProxyConfig(IReadOnlyList<RouteConfig> routes, IReadOnlyList<ClusterConfig> clusters)
        {
            Routes = routes;
            Clusters = clusters;
            ChangeToken = new CancellationChangeToken(_cts.Token);
        }

        public IReadOnlyList<RouteConfig> Routes { get; }

        public IReadOnlyList<ClusterConfig> Clusters { get; }

        public IChangeToken ChangeToken { get; }

        public void SignalChange() => _cts.Cancel();
    }
}
