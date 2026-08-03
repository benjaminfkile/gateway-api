using Gateway.Api.Management;

namespace Gateway.Api.Tests;

/// <summary>
/// In-memory <see cref="IImageRegistry"/> for tests: the build box has no AWS/ECR
/// access. Returns a deterministic digest per <c>image:tag</c> (so a deploy of the
/// same tag resolves the same digest), records every resolve call, and can be told
/// to fail a specific tag to exercise the not-found path.
/// </summary>
public sealed class FakeImageRegistry : IImageRegistry
{
    private readonly object _gate = new();

    /// <summary>Every (image, tag) resolve requested, in order.</summary>
    public List<(string Image, string Tag)> Resolved { get; } = new();

    /// <summary>Explicit digest overrides keyed by tag; falls back to a derived digest.</summary>
    public Dictionary<string, string> DigestsByTag { get; } = new(StringComparer.Ordinal);

    /// <summary>Tags that should be treated as missing (resolve throws).</summary>
    public HashSet<string> MissingTags { get; } = new(StringComparer.Ordinal);

    /// <summary>Credentials returned by <see cref="GetCredentialsAsync"/>; settable to simulate rotation.</summary>
    public RegistryCredentials Credentials { get; set; } =
        new("AWS", "fake-token", "https://123456789012.dkr.ecr.us-east-1.amazonaws.com");

    /// <summary>When set, <see cref="GetCredentialsAsync"/> throws it (simulates no-registry / unreachable).</summary>
    public Exception? CredentialsError { get; set; }

    /// <summary>Number of times credentials were requested.</summary>
    public int CredentialRequests { get; private set; }

    public Task<RegistryCredentials> GetCredentialsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            CredentialRequests++;
            if (CredentialsError is not null)
            {
                throw CredentialsError;
            }

            return Task.FromResult(Credentials);
        }
    }

    public Task<string> ResolveDigestAsync(string image, string tag, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Resolved.Add((image, tag));

            if (MissingTags.Contains(tag))
            {
                throw new ImageNotFoundException($"Image '{image}:{tag}' not found.");
            }

            var digest = DigestsByTag.TryGetValue(tag, out var d)
                ? d
                : $"sha256:{tag}-digest";
            return Task.FromResult(digest);
        }
    }
}
