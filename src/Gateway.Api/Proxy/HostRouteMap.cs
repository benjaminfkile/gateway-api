namespace Gateway.Api.Proxy;

/// <summary>
/// Host-based routing table (tech-spec §4.1): domains that front a single
/// service directly, e.g. <c>wmsfo-api.com</c> → the <c>wmsfo-api</c> service
/// with <b>bare</b> paths (no <c>/{service}</c> prefix, nothing stripped).
/// Configured via <c>GATEWAY_HOST_ROUTES</c> as comma-separated
/// <c>host=service</c> pairs; unset → path-prefix routing only. The domain must
/// resolve to the load balancer and be covered by its certificate — this map
/// only teaches the gateway what the Host header means.
/// </summary>
public sealed class HostRouteMap
{
    public const string EnvVar = "GATEWAY_HOST_ROUTES";

    private readonly IReadOnlyDictionary<string, string> _hostToService;

    public HostRouteMap(IReadOnlyDictionary<string, string> hostToService)
    {
        _hostToService = hostToService;
    }

    public static HostRouteMap Empty { get; } =
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>All hosts mapped to <paramref name="service"/>.</summary>
    public IReadOnlyList<string> HostsFor(string service) =>
        _hostToService
            .Where(kvp => string.Equals(kvp.Value, service, StringComparison.Ordinal))
            .Select(kvp => kvp.Key)
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();

    /// <summary>Parse from the environment (env var wins, then config). Malformed pairs are skipped.</summary>
    public static HostRouteMap FromConfiguration(IConfiguration configuration)
    {
        var raw = Environment.GetEnvironmentVariable(EnvVar) ?? configuration[EnvVar];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Empty;
        }

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0 || eq == pair.Length - 1)
            {
                continue;
            }

            map[pair[..eq].Trim()] = pair[(eq + 1)..].Trim();
        }

        return new HostRouteMap(map);
    }
}
