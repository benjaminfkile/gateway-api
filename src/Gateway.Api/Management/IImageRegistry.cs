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
}

/// <summary>Thrown when a requested image tag cannot be found in the registry.</summary>
public sealed class ImageNotFoundException : Exception
{
    public ImageNotFoundException(string message) : base(message)
    {
    }
}
