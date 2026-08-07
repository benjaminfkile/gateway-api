using Gateway.Api.Containers;
using Gateway.Api.Management;

namespace Gateway.Api.Tests;

/// <summary>
/// The Docker Engine API authenticates pulls per request — the reconciler's
/// runtime must attach ECR credentials itself (a CLI `docker login` is not
/// consulted). These tests cover ECR detection, anonymous fallback, and token
/// caching in <see cref="EcrRegistryAuthProvider"/>.
/// </summary>
public class RegistryAuthProviderTests
{
    private sealed class CountingRegistry : IImageRegistry
    {
        public int CredentialCalls;

        public Task<string> ResolveDigestAsync(string image, string tag, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<RegistryCredentials> GetCredentialsAsync(CancellationToken ct = default)
        {
            CredentialCalls++;
            return Task.FromResult(new RegistryCredentials("AWS", "token-" + CredentialCalls, "https://123.dkr.ecr.us-east-1.amazonaws.com"));
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private const string EcrImage = "123456789012.dkr.ecr.us-east-1.amazonaws.com/team/app";

    [Fact]
    public async Task EcrImageGetsCredentials()
    {
        var registry = new CountingRegistry();
        var provider = new EcrRegistryAuthProvider(registry);

        var auth = await provider.GetAuthAsync(EcrImage);

        Assert.NotNull(auth);
        Assert.Equal("AWS", auth!.Username);
        Assert.Equal("token-1", auth.Password);
        Assert.Equal(1, registry.CredentialCalls);
    }

    [Fact]
    public async Task NonEcrImagePullsAnonymously()
    {
        var registry = new CountingRegistry();
        var provider = new EcrRegistryAuthProvider(registry);

        Assert.Null(await provider.GetAuthAsync("nginx"));
        Assert.Null(await provider.GetAuthAsync("registry.example.com/team/app"));
        Assert.Equal(0, registry.CredentialCalls);
    }

    [Fact]
    public async Task TokenIsCachedWithinLifetimeAndRefreshedAfter()
    {
        var registry = new CountingRegistry();
        var time = new ManualTimeProvider();
        var provider = new EcrRegistryAuthProvider(registry, time);

        await provider.GetAuthAsync(EcrImage);
        await provider.GetAuthAsync(EcrImage);
        Assert.Equal(1, registry.CredentialCalls);

        time.Now += TimeSpan.FromHours(7);
        var refreshed = await provider.GetAuthAsync(EcrImage);
        Assert.Equal(2, registry.CredentialCalls);
        Assert.Equal("token-2", refreshed!.Password);
    }

    [Theory]
    [InlineData("123456789012.dkr.ecr.us-east-1.amazonaws.com/team/app", true)]
    [InlineData("public.ecr.aws/team/app", false)]
    [InlineData("docker.io/library/nginx", false)]
    [InlineData("nginx", false)]
    public void EcrDetectionMatchesPrivateEcrHostsOnly(string image, bool expected) =>
        Assert.Equal(expected, EcrRegistryAuthProvider.IsEcrImage(image));
}
