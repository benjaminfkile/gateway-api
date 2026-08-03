namespace Gateway.Api.Reconcile;

/// <summary>
/// Naming convention for blue-green candidates. During a replace the new
/// container runs as <c>{name}-green</c> alongside the still-serving canonical
/// <c>{name}</c>, then is promoted (renamed) to the canonical name once healthy
/// (tech-spec §7).
/// </summary>
public static class ReconcileNaming
{
    /// <summary>Suffix for a blue-green candidate container.</summary>
    public const string GreenSuffix = "-green";

    /// <summary>The green candidate name for a service.</summary>
    public static string GreenNameFor(string serviceName) => serviceName + GreenSuffix;

    /// <summary>Whether a container name is a transient blue-green candidate.</summary>
    public static bool IsGreen(string containerName) =>
        containerName.EndsWith(GreenSuffix, StringComparison.Ordinal);
}
