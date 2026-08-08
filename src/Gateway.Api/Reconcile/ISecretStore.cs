using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace Gateway.Api.Reconcile;

/// <summary>
/// Thin fetch seam over a secrets store (tech-spec §8): resolve a secret reference
/// to its raw <c>SecretString</c>. Behind an interface so the build/test box — which
/// has no AWS access — substitutes a fake, and so <see cref="SecretsManagerEnvProvider"/>
/// can be unit-tested without a real Secrets Manager client. The production
/// implementation is <see cref="SecretsManagerSecretStore"/> over AWSSDK.SecretsManager.
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Fetch the <c>SecretString</c> for <paramref name="reference"/> (a secret name or
    /// full ARN). Throws <see cref="SecretResolutionException"/> when the secret is
    /// missing or has no string value; any other store/permission failure (e.g.
    /// AccessDenied) propagates. The returned value is a secret and must never be logged.
    /// </summary>
    Task<string> GetSecretStringAsync(string reference, CancellationToken ct = default);
}

/// <summary>
/// <see cref="ISecretStore"/> over AWS Secrets Manager (AWSSDK.SecretsManager).
/// Resolves a service's <c>env_secret_ref</c> — a secret name or full ARN — to its
/// <c>SecretString</c> at container (re)create time (tech-spec §8). Registered lazily
/// in production (behind a <c>Func&lt;&gt;</c>, like the ECR auth provider) so a
/// region-less box or the AWS-less CI runner still boots — the client is only
/// constructed on the first non-empty ref.
/// </summary>
public sealed class SecretsManagerSecretStore : ISecretStore
{
    private readonly IAmazonSecretsManager _client;

    public SecretsManagerSecretStore(IAmazonSecretsManager client)
    {
        _client = client;
    }

    public async Task<string> GetSecretStringAsync(string reference, CancellationToken ct = default)
    {
        GetSecretValueResponse response;
        try
        {
            response = await _client.GetSecretValueAsync(
                new GetSecretValueRequest { SecretId = reference }, ct);
        }
        catch (ResourceNotFoundException)
        {
            // Name the ref, never a value — the secret simply does not exist.
            throw new SecretResolutionException($"secret '{reference}' was not found in Secrets Manager");
        }

        // A binary-only secret has no SecretString; we only support string secrets.
        return response.SecretString
            ?? throw new SecretResolutionException(
                $"secret '{reference}' has no SecretString value (binary secrets are not supported)");
    }
}

/// <summary>
/// Raised when a service's environment cannot be resolved from its
/// <c>env_secret_ref</c> — a missing secret or a <c>SecretString</c> that is not a
/// flat JSON object of string values (tech-spec §8). The message always names the
/// ref and <b>never</b> echoes any secret value, so it is safe to surface through the
/// per-service <c>lastError</c> plumbing and logs.
/// </summary>
public sealed class SecretResolutionException : Exception
{
    public SecretResolutionException(string message) : base(message)
    {
    }

    public SecretResolutionException(string message, Exception inner) : base(message, inner)
    {
    }
}
