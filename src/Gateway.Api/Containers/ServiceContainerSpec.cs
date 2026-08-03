namespace Gateway.Api.Containers;

/// <summary>
/// Everything required to create and start a single downstream service container
/// (tech-spec §4.3, §7). Built by the reconciler from a manifest entry plus the
/// service's resolved environment. Secret values live only in <see cref="EnvVars"/>
/// at (re)create time and are never persisted to the manifest or logs.
/// </summary>
/// <param name="Name">Container name — the canonical service name, or <c>{name}-green</c> for a blue-green candidate.</param>
/// <param name="Image">Registry image reference (repository).</param>
/// <param name="Digest">Resolved image digest to run; the container is created from <c>{image}@{digest}</c> when set.</param>
/// <param name="Port">The port the app listens on inside the container.</param>
/// <param name="SidePort">
/// Alternate host port to publish for a blue-green candidate so it does not
/// collide with the still-serving old container's port. Null for a canonical
/// container, which publishes <see cref="Port"/>.
/// </param>
/// <param name="EnvVars">Environment variables (including resolved secrets) for the container.</param>
/// <param name="Network">Internal Docker network the container joins.</param>
/// <param name="LogConfig">Log driver configuration (awslogs in production).</param>
/// <param name="EnvHash">
/// Hash of <see cref="EnvVars"/>, stored as a label so a later reconcile can
/// detect env drift and trigger a blue-green replace.
/// </param>
public sealed record ServiceContainerSpec(
    string Name,
    string Image,
    string? Digest,
    int Port,
    int? SidePort,
    IReadOnlyDictionary<string, string> EnvVars,
    string Network,
    LogDriverConfig LogConfig,
    string? EnvHash);
