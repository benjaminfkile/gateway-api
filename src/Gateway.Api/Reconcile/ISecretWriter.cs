using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace Gateway.Api.Reconcile;

/// <summary>
/// Write seam over a secrets store, the counterpart of <see cref="ISecretStore"/>: put a
/// new <c>SecretString</c> on a secret that already exists. The load scheduler writes
/// the generator spec secret through it (docs/load.md section 5). Behind an interface so
/// the build/test box, which has no AWS access, substitutes a fake. The production
/// implementation is <see cref="SecretsManagerSecretWriter"/>.
/// </summary>
public interface ISecretWriter
{
    /// <summary>
    /// Store <paramref name="value"/> as the current <c>SecretString</c> of
    /// <paramref name="reference"/> (a secret name or full ARN). The value is a secret:
    /// an implementation never logs it and never puts it in an exception message.
    /// </summary>
    Task PutSecretStringAsync(string reference, string value, CancellationToken ct = default);
}

/// <summary>
/// <see cref="ISecretWriter"/> over AWS Secrets Manager: <c>PutSecretValue</c> on a
/// secret an operator created (the gateway never creates one). Needs
/// <c>secretsmanager:PutSecretValue</c> on that secret. The client is resolved through a
/// <c>Func&lt;&gt;</c> so a region-less box only builds it on the first write.
/// </summary>
public sealed class SecretsManagerSecretWriter : ISecretWriter
{
    private readonly Func<IAmazonSecretsManager> _client;

    public SecretsManagerSecretWriter(Func<IAmazonSecretsManager> client)
    {
        _client = client;
    }

    public async Task PutSecretStringAsync(string reference, string value, CancellationToken ct = default)
    {
        try
        {
            await _client().PutSecretValueAsync(
                new PutSecretValueRequest { SecretId = reference, SecretString = value }, ct);
        }
        catch (ResourceNotFoundException)
        {
            // Name the ref, never the value.
            throw new SecretResolutionException($"secret '{reference}' was not found in Secrets Manager");
        }
    }
}
