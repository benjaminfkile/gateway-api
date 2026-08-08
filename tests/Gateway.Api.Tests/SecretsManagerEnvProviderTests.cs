using Gateway.Api.Data;
using Gateway.Api.Reconcile;

namespace Gateway.Api.Tests;

/// <summary>
/// Unit tests for <see cref="SecretsManagerEnvProvider"/> (tech-spec §8) over a fake
/// <see cref="ISecretStore"/> — no AWS. Covers the empty-ref no-op, the flat-JSON happy
/// path, per-ref TTL caching (honoured within the window, refreshed after, failures not
/// cached), and descriptive ref-only errors for invalid/non-flat secrets and a missing
/// secret. Every error assertion also checks that no secret value leaks into the message.
/// </summary>
public class SecretsManagerEnvProviderTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private static ServiceManifest Manifest(string? envSecretRef) => new()
    {
        Name = "svc-a",
        Image = "registry/svc-a",
        Tag = "latest",
        Digest = "sha256:v1",
        Port = 8080,
        DesiredStatus = "running",
        IncludeInHealth = true,
        UpdatedBy = "test",
        UpdatedAt = DateTimeOffset.UnixEpoch,
        EnvSecretRef = envSecretRef,
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyRef_ReturnsEmptyEnv_WithoutTouchingStore(string? reference)
    {
        var store = new FakeSecretStore();
        var provider = new SecretsManagerEnvProvider(store, Ttl);

        var env = await provider.GetEnvAsync(Manifest(reference));

        Assert.Empty(env);
        Assert.Equal(0, store.Calls); // never constructs/uses the AWS-backed store
    }

    [Fact]
    public async Task HappyPath_ParsesFlatJsonObject()
    {
        var store = new FakeSecretStore();
        store.Secrets["svc-a-secret"] = "{\"TOKEN\":\"abc\",\"DB_URL\":\"postgres://h/db\"}";
        var provider = new SecretsManagerEnvProvider(store, Ttl);

        var env = await provider.GetEnvAsync(Manifest("svc-a-secret"));

        Assert.Equal(2, env.Count);
        Assert.Equal("abc", env["TOKEN"]);
        Assert.Equal("postgres://h/db", env["DB_URL"]);
    }

    [Fact]
    public async Task Ref_AcceptsFullArn()
    {
        const string arn = "arn:aws:secretsmanager:us-east-1:123456789012:secret:svc-a/env-AbCdEf";
        var store = new FakeSecretStore();
        store.Secrets[arn] = "{\"K\":\"v\"}";
        var provider = new SecretsManagerEnvProvider(store, Ttl);

        var env = await provider.GetEnvAsync(Manifest(arn));

        Assert.Equal("v", env["K"]);
    }

    [Fact]
    public async Task Cache_HonouredWithinTtl_ThenRefreshedAfter()
    {
        var store = new FakeSecretStore();
        store.Secrets["svc-a-secret"] = "{\"TOKEN\":\"v1\"}";
        var time = new ManualTimeProvider();
        var provider = new SecretsManagerEnvProvider(store, Ttl, time);

        var first = await provider.GetEnvAsync(Manifest("svc-a-secret"));
        var second = await provider.GetEnvAsync(Manifest("svc-a-secret"));
        Assert.Equal("v1", first["TOKEN"]);
        Assert.Equal("v1", second["TOKEN"]);
        Assert.Equal(1, store.Calls); // second read served from cache

        // Rotate the value; still within TTL → still cached.
        store.Secrets["svc-a-secret"] = "{\"TOKEN\":\"v2\"}";
        time.Now += TimeSpan.FromSeconds(59);
        Assert.Equal("v1", (await provider.GetEnvAsync(Manifest("svc-a-secret")))["TOKEN"]);
        Assert.Equal(1, store.Calls);

        // Past the TTL → refetch picks up the rotated value.
        time.Now += TimeSpan.FromSeconds(2);
        Assert.Equal("v2", (await provider.GetEnvAsync(Manifest("svc-a-secret")))["TOKEN"]);
        Assert.Equal(2, store.Calls);
    }

    [Fact]
    public async Task FailedFetch_IsNotCached()
    {
        var store = new FakeSecretStore();
        var attempts = 0;
        store.Handler = _ =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new SecretResolutionException("transient");
            }

            return "{\"TOKEN\":\"ok\"}";
        };
        var provider = new SecretsManagerEnvProvider(store, Ttl);

        await Assert.ThrowsAsync<SecretResolutionException>(() => provider.GetEnvAsync(Manifest("svc-a-secret")));

        // A failed fetch must not be cached, so the next call actually retries the store.
        var env = await provider.GetEnvAsync(Manifest("svc-a-secret"));
        Assert.Equal("ok", env["TOKEN"]);
        Assert.Equal(2, store.Calls);
    }

    [Fact]
    public async Task MissingSecret_ThrowsDescriptive_NamingRef()
    {
        var store = new FakeSecretStore(); // svc-a-secret not configured
        var provider = new SecretsManagerEnvProvider(store, Ttl);

        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => provider.GetEnvAsync(Manifest("svc-a-secret")));

        Assert.Contains("svc-a-secret", ex.Message);
    }

    [Fact]
    public async Task NonJson_Throws_NamingRef_NotValue()
    {
        var store = new FakeSecretStore();
        store.Secrets["svc-a-secret"] = "not-a-json-secret-value";
        var provider = new SecretsManagerEnvProvider(store, Ttl);

        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => provider.GetEnvAsync(Manifest("svc-a-secret")));

        Assert.Contains("svc-a-secret", ex.Message);
        Assert.DoesNotContain("not-a-json-secret-value", ex.Message);
    }

    [Fact]
    public async Task NonObjectRoot_Throws_NamingRef()
    {
        var store = new FakeSecretStore();
        store.Secrets["svc-a-secret"] = "[\"a\",\"b\"]"; // valid JSON, but an array
        var provider = new SecretsManagerEnvProvider(store, Ttl);

        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => provider.GetEnvAsync(Manifest("svc-a-secret")));

        Assert.Contains("svc-a-secret", ex.Message);
    }

    [Fact]
    public async Task NestedObjectValue_Throws_NamingRefAndKey_NotValue()
    {
        var store = new FakeSecretStore();
        store.Secrets["svc-a-secret"] = "{\"TOKEN\":{\"nested\":\"supersecret\"}}";
        var provider = new SecretsManagerEnvProvider(store, Ttl);

        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => provider.GetEnvAsync(Manifest("svc-a-secret")));

        Assert.Contains("svc-a-secret", ex.Message);
        Assert.Contains("TOKEN", ex.Message);            // the offending key is named
        Assert.DoesNotContain("supersecret", ex.Message); // its value never is
    }

    [Fact]
    public async Task NonStringScalarValue_Throws_NamingRefAndKey()
    {
        var store = new FakeSecretStore();
        store.Secrets["svc-a-secret"] = "{\"PORT\":8080}"; // number, not a string
        var provider = new SecretsManagerEnvProvider(store, Ttl);

        var ex = await Assert.ThrowsAsync<SecretResolutionException>(
            () => provider.GetEnvAsync(Manifest("svc-a-secret")));

        Assert.Contains("svc-a-secret", ex.Message);
        Assert.Contains("PORT", ex.Message);
    }
}
