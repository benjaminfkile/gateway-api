namespace Gateway.Api.Management;

/// <summary>
/// Resolves an image reference (<c>repo:tag</c>) to its immutable content digest
/// (tech-spec §4.4, §4.5). A deploy resolves the digest <b>once</b> in the
/// Management API and writes it to the manifest, so every reconciler in the fleet
/// converges to exactly the same image even if the tag later moves.
/// <para>
/// Behind an interface so the build/test box — which has no AWS access — can
/// substitute a fake. The production implementation is <see cref="EcrImageRegistry"/>
/// over AWSSDK.ECR.
/// </para>
/// </summary>
public interface IImageRegistry
{
    /// <summary>
    /// Resolve the sha256 digest of <paramref name="image"/>:<paramref name="tag"/>.
    /// Throws <see cref="ImageNotFoundException"/> when the tag does not exist.
    /// </summary>
    Task<string> ResolveDigestAsync(string image, string tag, CancellationToken ct = default);

    /// <summary>
    /// Fetch short-lived credentials for a <c>docker login</c> against the registry
    /// (tech-spec §4.3: registry login refreshed periodically). The node bootstrap's
    /// registry-login step uses these to authenticate the Docker daemon so the
    /// reconciler can pull private images. Credentials rotate (ECR tokens last ~12h),
    /// which is why the bootstrap refreshes them on a timer.
    /// </summary>
    Task<RegistryCredentials> GetCredentialsAsync(CancellationToken ct = default);
}

/// <summary>
/// A registry username/password pair plus the endpoint they authenticate against,
/// suitable for <c>docker login --username &lt;user&gt; --password-stdin &lt;endpoint&gt;</c>.
/// For ECR the username is always <c>AWS</c> and the password is a rotating token.
/// </summary>
public sealed record RegistryCredentials(string Username, string Password, string Endpoint);

/// <summary>Thrown when a requested image tag cannot be found in the registry.</summary>
public sealed class ImageNotFoundException : Exception
{
    public ImageNotFoundException(string message) : base(message)
    {
    }
}
