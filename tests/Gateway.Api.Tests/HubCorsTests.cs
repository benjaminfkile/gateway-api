using System.Net;
using Gateway.Api.Data;
using Gateway.Api.Manifest;
using Gateway.Api.RealTime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.Tests;

/// <summary>
/// The /hub CORS policy is dynamic and manifest-driven (task #595): it allows the
/// static GATEWAY_CORS_ORIGINS ops-dashboard origins PLUS the union of every manifest
/// service's realtime_allowed_origins, so a consumer app's browser on its own domain
/// can pass the /hub/negotiate preflight. The widened set applies to /hub only — /mgmt
/// keeps the strict static dashboard policy — and a manifest change takes effect after
/// the cache TTL with no restart (the clock is controlled here so that is deterministic).
/// </summary>
public class HubCorsTests
{
    private const string OpsOrigin = "https://ops.example.com";
    private const string ChatOrigin = "https://chat.example.com";
    private const string NewOrigin = "https://new.example.com";

    private sealed class HubCorsFactory : WebApplicationFactory<Program>
    {
        public InMemoryManifestStore Manifest { get; } = new();
        public ManualTimeProvider Clock { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("GATEWAY_CORS_ORIGINS", OpsOrigin);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IManifestStore>();
                services.AddSingleton<IManifestStore>(Manifest);

                // Swap the shared manifest snapshot cache for one on a controllable clock
                // (the TTL lives there now), then rebuild the /hub origin cache off it,
                // keeping the same static origins the host would have derived from
                // GATEWAY_CORS_ORIGINS.
                services.RemoveAll<ManifestSnapshotCache>();
                services.AddSingleton(sp => new ManifestSnapshotCache(
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    ManifestSnapshotCache.DefaultTtl,
                    Clock));
                services.RemoveAll<HubCorsOriginCache>();
                services.AddSingleton(sp => new HubCorsOriginCache(
                    sp.GetRequiredService<ManifestSnapshotCache>(),
                    new[] { OpsOrigin }));
            });
        }

        public Task AddServiceAsync(string name, string? allowedOrigins) =>
            Manifest.UpsertAsync(new ServiceManifest
            {
                Name = name,
                Image = $"registry/{name}",
                Tag = "latest",
                Port = 8080,
                DesiredStatus = "running",
                RealtimeAllowedOrigins = allowedOrigins,
                UpdatedBy = "seed",
                UpdatedAt = DateTimeOffset.UnixEpoch,
            });
    }

    private static HttpRequestMessage Preflight(string path, string origin, string method = "POST")
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        return request;
    }

    [Fact]
    public async Task ServiceOrigin_PassesHubNegotiatePreflight()
    {
        await using var factory = new HubCorsFactory();
        await factory.AddServiceAsync("svc-a", ChatOrigin);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("/hub/negotiate", ChatOrigin));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(ChatOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        // Credentials must stay on for SignalR, and the origin echoed is exact (no wildcard).
        Assert.Equal("true", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
    }

    [Fact]
    public async Task StaticOpsOrigin_StillPassesHubPreflight()
    {
        await using var factory = new HubCorsFactory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("/hub/negotiate", OpsOrigin));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(OpsOrigin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task UnknownOrigin_RejectedOnHub()
    {
        await using var factory = new HubCorsFactory();
        await factory.AddServiceAsync("svc-a", ChatOrigin);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("/hub/negotiate", "https://evil.example.com"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ReservedNameOrigins_NotFoldedIntoHubCors()
    {
        // FINDING 2b: a reserved / gateway-owned name (here "ops") must never widen /hub
        // CORS. Such a row can exist only if it predates the reservation (the API blocks
        // creating it), but its realtime_allowed_origins still must not grant credentialed
        // hub access for realtime that can never work.
        await using var factory = new HubCorsFactory();
        await factory.AddServiceAsync("ops", ChatOrigin);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("/hub/negotiate", ChatOrigin));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ServiceOrigin_RejectedOnManagementPlane()
    {
        // The widened set applies to /hub only; /mgmt keeps the strict static ops policy,
        // so a service's consumer origin is not honoured there.
        await using var factory = new HubCorsFactory();
        await factory.AddServiceAsync("svc-a", ChatOrigin);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("/mgmt/services", ChatOrigin, method: "GET"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task NewlyUpsertedOrigin_BecomesEffectiveAfterTtl_WithoutRestart()
    {
        await using var factory = new HubCorsFactory();
        await factory.AddServiceAsync("svc-a", ChatOrigin);
        using var client = factory.CreateClient();

        // Prime the cache snapshot at the current (frozen) clock: svc-a's origin is in,
        // the not-yet-added one is out.
        var primed = await client.SendAsync(Preflight("/hub/negotiate", ChatOrigin));
        Assert.Equal(ChatOrigin, Assert.Single(primed.Headers.GetValues("Access-Control-Allow-Origin")));

        // A manifest change lands, but within the TTL the cached snapshot still rejects it.
        await factory.AddServiceAsync("svc-b", NewOrigin);
        var withinTtl = await client.SendAsync(Preflight("/hub/negotiate", NewOrigin));
        Assert.False(withinTtl.Headers.Contains("Access-Control-Allow-Origin"));

        // Advance past the TTL: the next preflight refreshes and the new origin is allowed —
        // no gateway restart.
        factory.Clock.Now += HubCorsOriginCache.DefaultTtl + TimeSpan.FromSeconds(1);
        var afterTtl = await client.SendAsync(Preflight("/hub/negotiate", NewOrigin));
        Assert.Equal(NewOrigin, Assert.Single(afterTtl.Headers.GetValues("Access-Control-Allow-Origin")));
    }
}
