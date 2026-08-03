namespace Gateway.Api.Auth;

/// <summary>
/// Environment-driven configuration for the management-plane JWT bearer scheme
/// (tech-spec §5). The authority is the Cognito user-pool issuer URL, from
/// <c>GATEWAY_COGNITO_AUTHORITY</c>; its <c>/.well-known/openid-configuration</c>
/// yields the JWKS used for signature validation.
/// <para>
/// When the authority is unset the management plane is <b>not configured</b>: the
/// gateway still boots and serves all transparent traffic, but every
/// <c>/mgmt/*</c> request answers <c>503</c> rather than being left open (§5).
/// </para>
/// </summary>
public sealed class ManagementAuthOptions
{
    /// <summary>Environment variable / config key holding the Cognito issuer URL.</summary>
    public const string AuthorityEnvVar = "GATEWAY_COGNITO_AUTHORITY";

    /// <summary>The Cognito issuer URL, or <c>null</c> when the plane is unconfigured.</summary>
    public string? Authority { get; init; }

    /// <summary>True once an authority is present and the plane can validate tokens.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Authority);

    /// <summary>
    /// Read the authority (env var wins, then config). Absent by default so the
    /// gateway boots without any Cognito dependency; <c>/mgmt/*</c> then 503s.
    /// </summary>
    public static ManagementAuthOptions FromConfiguration(IConfiguration configuration)
    {
        var authority = Environment.GetEnvironmentVariable(AuthorityEnvVar)
            ?? configuration[AuthorityEnvVar];

        return new ManagementAuthOptions
        {
            Authority = string.IsNullOrWhiteSpace(authority) ? null : authority,
        };
    }
}
