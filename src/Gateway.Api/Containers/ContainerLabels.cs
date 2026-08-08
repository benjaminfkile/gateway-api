namespace Gateway.Api.Containers;

/// <summary>
/// Labels the gateway stamps onto every container it creates. They mark
/// ownership (so the reconciler only ever touches its own containers) and carry
/// the running digest / env hash so drift can be detected from a plain container
/// listing without re-inspecting the registry or the secrets store.
/// </summary>
public static class ContainerLabels
{
    /// <summary>Marks a container as managed by this gateway; value is always <c>true</c>.</summary>
    public const string Managed = "gateway.managed";

    /// <summary>The canonical service name the container belongs to.</summary>
    public const string Service = "gateway.service";

    /// <summary>The resolved image digest the container is running.</summary>
    public const string Digest = "gateway.digest";

    /// <summary>Hash of the environment the container was created with.</summary>
    public const string EnvHash = "gateway.env-hash";

    /// <summary>
    /// Legacy label: the fixed host port a container was published on by an older
    /// gateway. Host ports are now Docker-assigned (dynamic) and read from the
    /// container's actual binding, so this is no longer written — only read as a
    /// fallback for pre-existing fixed-port containers until they are replaced.
    /// </summary>
    public const string HostPort = "gateway.host-port";
}
