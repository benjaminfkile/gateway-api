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
/// <param name="Port">
/// The container-internal port the app binds inside the container (Kubernetes
/// <c>containerPort</c> semantics). It is <b>never</b> a fixed host port: the
/// runtime publishes it with an unassigned host binding and Docker picks a unique
/// ephemeral host port, so two services may share the same internal port and
/// blue-green candidates never contend for a host port (tech-spec §4.1, §7).
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
    IReadOnlyDictionary<string, string> EnvVars,
    string Network,
    LogDriverConfig LogConfig,
    string? EnvHash);
