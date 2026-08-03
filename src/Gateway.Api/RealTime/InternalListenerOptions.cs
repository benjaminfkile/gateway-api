namespace Gateway.Api.RealTime;

/// <summary>
/// Binding for the internal-only publish listener (tech-spec §4.2, §8). The
/// gateway exposes a second Kestrel endpoint — reachable from the Docker network
/// but <b>never</b> routed by the load balancer — that hosts exactly one endpoint,
/// <c>POST /internal/publish</c>. The address comes from
/// <c>GATEWAY_INTERNAL_BIND</c> (default <c>0.0.0.0:8080</c>).
/// <para>
/// <see cref="Port"/> is the discriminator the isolation middleware uses to keep
/// <c>/internal/*</c> off the public listener and everything else off the internal
/// one — matched on the connection's local port.
/// </para>
/// </summary>
public sealed class InternalListenerOptions
{
    /// <summary>Environment variable / config key holding the internal bind address.</summary>
    public const string BindEnvVar = "GATEWAY_INTERNAL_BIND";

    /// <summary>Default internal bind: reachable on the Docker network, off the load balancer.</summary>
    public const string DefaultBind = "0.0.0.0:8080";

    /// <summary>The raw <c>host:port</c> bind string.</summary>
    public required string Bind { get; init; }

    /// <summary>Host portion of <see cref="Bind"/> (e.g. <c>0.0.0.0</c>).</summary>
    public required string Host { get; init; }

    /// <summary>Port the internal listener binds — the isolation discriminator.</summary>
    public required int Port { get; init; }

    /// <summary>The internal listener's URL, for Kestrel address binding.</summary>
    public string Url => $"http://{Host}:{Port}";

    /// <summary>
    /// Parse the internal bind from configuration (env var wins, then config,
    /// then the default). Splits on the final colon so IPv4 <c>host:port</c> forms
    /// parse cleanly.
    /// </summary>
    public static InternalListenerOptions FromConfiguration(IConfiguration configuration)
    {
        var bind = Environment.GetEnvironmentVariable(BindEnvVar)
            ?? configuration[BindEnvVar]
            ?? DefaultBind;

        var separator = bind.LastIndexOf(':');
        if (separator <= 0 || separator == bind.Length - 1
            || !int.TryParse(bind[(separator + 1)..], out var port))
        {
            throw new InvalidOperationException(
                $"{BindEnvVar} must be 'host:port' (got '{bind}').");
        }

        return new InternalListenerOptions
        {
            Bind = bind,
            Host = bind[..separator],
            Port = port,
        };
    }
}
