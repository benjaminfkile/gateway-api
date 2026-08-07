using Docker.DotNet.Models;
using Gateway.Api.Management;

namespace Gateway.Api.Containers;

/// <summary>
/// Supplies per-pull registry credentials for the Docker Engine API. A CLI
/// <c>docker login</c> only writes the CLI's config file — API-driven pulls
/// (<c>POST /images/create</c>) authenticate exclusively via the
/// <c>X-Registry-Auth</c> header on each request, so the reconciler's runtime
/// must attach credentials itself or private-registry pulls fail with
/// "no basic auth credentials".
/// </summary>
public interface IRegistryAuthProvider
{
    /// <summary>Credentials for pulling <paramref name="image"/>, or null for anonymous.</summary>
    Task<AuthConfig?> GetAuthAsync(string image, CancellationToken ct = default);
}

/// <summary>Anonymous pulls only — public images and tests.</summary>
public sealed class NullRegistryAuthProvider : IRegistryAuthProvider
{
    public Task<AuthConfig?> GetAuthAsync(string image, CancellationToken ct = default) =>
        Task.FromResult<AuthConfig?>(null);
}

/// <summary>
/// ECR-backed provider: for images hosted in ECR (registry host contains
/// <c>.ecr.</c>), fetches an authorization token via <see cref="IImageRegistry"/>
/// and caches it — ECR tokens are valid for 12 hours; we refresh well before
/// that. Non-ECR images pull anonymously.
/// </summary>
public sealed class EcrRegistryAuthProvider : IRegistryAuthProvider
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(6);

    private readonly IImageRegistry _registry;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private AuthConfig? _cached;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;

    public EcrRegistryAuthProvider(IImageRegistry registry, TimeProvider? time = null)
    {
        _registry = registry;
        _time = time ?? TimeProvider.System;
    }

    public async Task<AuthConfig?> GetAuthAsync(string image, CancellationToken ct = default)
    {
        if (!IsEcrImage(image))
        {
            return null;
        }

        if (_cached is not null && _time.GetUtcNow() - _cachedAt < TokenLifetime)
        {
            return _cached;
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (_cached is null || _time.GetUtcNow() - _cachedAt >= TokenLifetime)
            {
                var credentials = await _registry.GetCredentialsAsync(ct);
                _cached = new AuthConfig
                {
                    Username = credentials.Username,
                    Password = credentials.Password,
                    ServerAddress = credentials.Endpoint,
                };
                _cachedAt = _time.GetUtcNow();
            }

            return _cached;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>True when the image reference's registry host is an ECR endpoint.</summary>
    public static bool IsEcrImage(string image)
    {
        var slash = image.IndexOf('/');
        if (slash <= 0)
        {
            return false;
        }

        var host = image[..slash];
        return host.Contains(".ecr.", StringComparison.OrdinalIgnoreCase)
            && host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase);
    }
}
