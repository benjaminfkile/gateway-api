using Gateway.Api.Data;
using Gateway.Api.RealTime;

namespace Gateway.Api.Tests;

/// <summary>
/// Unit coverage for the per-service publish token helper (task #593): url-safe
/// random generation, the lazy Ensure lifecycle (mint on create, preserve on edit),
/// and constant-time matching that never accepts an absent stored token.
/// </summary>
public class RealtimePublishTokenTests
{
    [Fact]
    public void Generate_IsUrlSafe_AndUnique()
    {
        var a = RealtimePublishToken.Generate();
        var b = RealtimePublishToken.Generate();

        Assert.NotEqual(a, b);
        // Url-safe base64: no '+', '/', or '=' padding — safe in a header / env value.
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
        // 32 bytes of entropy encode to at least 40 url-safe chars.
        Assert.True(a.Length >= 40);
    }

    [Fact]
    public void Ensure_Create_MintsToken()
    {
        var manifest = new ServiceManifest { Name = "svc-a" };

        RealtimePublishToken.Ensure(manifest, existing: null);

        Assert.False(string.IsNullOrEmpty(manifest.RealtimePublishToken));
    }

    [Fact]
    public void Ensure_Edit_PreservesExistingToken()
    {
        var manifest = new ServiceManifest { Name = "svc-a", RealtimePublishToken = null };
        var existing = new ServiceManifest { Name = "svc-a", RealtimePublishToken = "kept" };

        RealtimePublishToken.Ensure(manifest, existing);

        Assert.Equal("kept", manifest.RealtimePublishToken);
    }

    [Fact]
    public void Ensure_PreMigrationRow_MintsOnUpsert()
    {
        // Both the incoming write and the stored row lack a token (a row from before
        // the column existed): a fresh one is minted.
        var manifest = new ServiceManifest { Name = "svc-a", RealtimePublishToken = null };
        var existing = new ServiceManifest { Name = "svc-a", RealtimePublishToken = null };

        RealtimePublishToken.Ensure(manifest, existing);

        Assert.False(string.IsNullOrEmpty(manifest.RealtimePublishToken));
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abcd", false)]
    [InlineData("abc", "xyz", false)]
    [InlineData("", "abc", false)]
    [InlineData(null, "abc", false)]
    [InlineData("abc", null, false)]
    [InlineData(null, null, false)]
    public void Matches_ComparesTokens(string? presented, string? stored, bool expected)
    {
        Assert.Equal(expected, RealtimePublishToken.Matches(presented, stored));
    }
}
