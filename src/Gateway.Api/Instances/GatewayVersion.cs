using System.Reflection;

namespace Gateway.Api.Instances;

/// <summary>
/// The gateway binary's version, reported in every <c>instance_status</c>
/// heartbeat (tech-spec §4.4 <c>gateway_ver</c>). Uses the assembly's
/// informational version (which carries the full SemVer + build metadata),
/// falling back to the plain assembly version.
/// </summary>
public static class GatewayVersion
{
    /// <summary>The running gateway version string.</summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = typeof(GatewayVersion).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // The SDK appends "+<commit-sha>" build metadata; keep the SemVer part.
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";
    }
}
