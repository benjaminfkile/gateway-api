namespace Gateway.Api.Instances;

/// <summary>
/// Environment-variable fallback source for instance identity, used off-EC2
/// (dev boxes, other clouds) when IMDS is unreachable. Reads
/// <c>GATEWAY_INSTANCE_ID</c> / <c>GATEWAY_PRIVATE_IP</c> / <c>GATEWAY_PUBLIC_IP</c>.
/// <para>
/// This source always resolves (it never returns <c>null</c>) so the fleet
/// heartbeat always has a stable id: when <c>GATEWAY_INSTANCE_ID</c> is unset it
/// falls back to the machine name.
/// </para>
/// </summary>
public sealed class EnvInstanceMetadata : IInstanceMetadata
{
    /// <summary>Environment variable naming this instance.</summary>
    public const string InstanceIdVar = "GATEWAY_INSTANCE_ID";

    /// <summary>Environment variable carrying the private IP.</summary>
    public const string PrivateIpVar = "GATEWAY_PRIVATE_IP";

    /// <summary>Environment variable carrying the public IP.</summary>
    public const string PublicIpVar = "GATEWAY_PUBLIC_IP";

    private readonly Func<string, string?> _readVar;
    private readonly Func<string> _machineName;

    public EnvInstanceMetadata()
        : this(Environment.GetEnvironmentVariable, () => Environment.MachineName)
    {
    }

    /// <summary>Testable constructor: injects the variable reader and machine-name source.</summary>
    public EnvInstanceMetadata(Func<string, string?> readVar, Func<string> machineName)
    {
        _readVar = readVar;
        _machineName = machineName;
    }

    public Task<InstanceIdentity?> TryResolveAsync(CancellationToken ct = default)
    {
        var instanceId = Trimmed(_readVar(InstanceIdVar)) ?? _machineName();
        var privateIp = Trimmed(_readVar(PrivateIpVar));
        var publicIp = Trimmed(_readVar(PublicIpVar));

        return Task.FromResult<InstanceIdentity?>(
            new InstanceIdentity(instanceId, privateIp, publicIp));
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
