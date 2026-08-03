using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Api.Data;
using Gateway.Api.Health;
using Gateway.Api.Manifest;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.Tests;

/// <summary>
/// Endpoint-level tests for the aggregated <c>/api/health</c>, driven by a fake
/// <see cref="IHealthProber"/> so probe outcomes (up / down / timeout) are
/// deterministic and need no network. A real-server end-to-end path is covered
/// separately in <see cref="HealthIntegrationTests"/>.
/// </summary>
public class HealthEndpointTests
{
    /// <summary>Deterministic prober: returns a preconfigured result per service.</summary>
    private sealed class FakeHealthProber : IHealthProber
    {
        private readonly ConcurrentDictionary<string, ServiceProbeResult> _results = new(StringComparer.Ordinal);

        /// <summary>How long each probe pretends to take; used to observe parallelism.</summary>
        public TimeSpan Delay { get; set; } = TimeSpan.Zero;

        private int _inFlight;
        public int MaxConcurrent { get; private set; }

        public void Set(string name, ServiceProbeResult result) => _results[name] = result;

        public async Task<ServiceProbeResult> ProbeAsync(ServiceManifest manifest, CancellationToken ct = default)
        {
            var current = Interlocked.Increment(ref _inFlight);
            // Track the peak overlap so a test can prove probes run concurrently.
            MaxConcurrent = Math.Max(MaxConcurrent, current);
            try
            {
                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, ct);
                }

                return _results.TryGetValue(manifest.Name, out var r)
                    ? r
                    : ServiceProbeResult.Unreachable();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private sealed class HealthTestFactory : WebApplicationFactory<Program>
    {
        public InMemoryManifestStore Store { get; } = new();
        public FakeHealthProber Prober { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IManifestStore>();
                services.AddSingleton<IManifestStore>(Store);

                services.RemoveAll<IHealthProber>();
                services.AddSingleton<IHealthProber>(Prober);
            });
        }
    }

    private static ServiceManifest HealthyManifest(string name, bool includeInHealth = true, string desiredStatus = "running") => new()
    {
        Name = name,
        Image = $"registry/{name}",
        Tag = "latest",
        Port = 8080,
        DesiredStatus = desiredStatus,
        IncludeInHealth = includeInHealth,
        UpdatedBy = "test",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static int PropertyCount(JsonElement obj) => obj.EnumerateObject().Count();

    private static async Task<JsonElement> GetHealthAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/health");
        // The gateway must always answer 200, regardless of downstream health.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("up", doc.GetProperty("gateway").GetString());
        Assert.True(doc.TryGetProperty("timestamp", out _), "timestamp must be present");
        return doc;
    }

    [Fact]
    public async Task Health_EmptyManifest_GatewayUp_NoServices()
    {
        await using var factory = new HealthTestFactory();
        var client = factory.CreateClient();

        var doc = await GetHealthAsync(client);

        Assert.Equal(0, PropertyCount(doc.GetProperty("services")));
    }

    [Fact]
    public async Task Health_AllServicesUp()
    {
        await using var factory = new HealthTestFactory();
        await factory.Store.UpsertAsync(HealthyManifest("svc-a"));
        await factory.Store.UpsertAsync(HealthyManifest("svc-b"));
        factory.Prober.Set("svc-a", ServiceProbeResult.Up(200, 12));
        factory.Prober.Set("svc-b", ServiceProbeResult.Up(204, 7));
        var client = factory.CreateClient();

        var doc = await GetHealthAsync(client);
        var services = doc.GetProperty("services");

        Assert.Equal(2, PropertyCount(services));
        var a = services.GetProperty("svc-a");
        Assert.Equal("up", a.GetProperty("status").GetString());
        Assert.Equal(200, a.GetProperty("httpStatus").GetInt32());
        Assert.Equal(12, a.GetProperty("responseTimeMs").GetInt64());
        Assert.Equal("up", services.GetProperty("svc-b").GetProperty("status").GetString());
    }

    [Fact]
    public async Task Health_OneServiceDown_GatewayStaysUpAnd200()
    {
        await using var factory = new HealthTestFactory();
        await factory.Store.UpsertAsync(HealthyManifest("svc-a"));
        await factory.Store.UpsertAsync(HealthyManifest("svc-b"));
        factory.Prober.Set("svc-a", ServiceProbeResult.Up(200, 5));
        // A downstream that answered but with a 5xx is 'down' with its status kept.
        factory.Prober.Set("svc-b", ServiceProbeResult.Down(503, 9));
        var client = factory.CreateClient();

        var doc = await GetHealthAsync(client); // asserts gateway=up + 200 inside
        var services = doc.GetProperty("services");

        Assert.Equal("up", services.GetProperty("svc-a").GetProperty("status").GetString());
        var b = services.GetProperty("svc-b");
        Assert.Equal("down", b.GetProperty("status").GetString());
        Assert.Equal(503, b.GetProperty("httpStatus").GetInt32());
        Assert.Equal(9, b.GetProperty("responseTimeMs").GetInt64());
    }

    [Fact]
    public async Task Health_Timeout_ReportedAsDownWithNulls()
    {
        await using var factory = new HealthTestFactory();
        await factory.Store.UpsertAsync(HealthyManifest("svc-a"));
        // A timed-out / unreachable service: down, with null http status + timing.
        factory.Prober.Set("svc-a", ServiceProbeResult.Unreachable());
        var client = factory.CreateClient();

        var doc = await GetHealthAsync(client);
        var a = doc.GetProperty("services").GetProperty("svc-a");

        Assert.Equal("down", a.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, a.GetProperty("httpStatus").ValueKind);
        Assert.Equal(JsonValueKind.Null, a.GetProperty("responseTimeMs").ValueKind);
    }

    [Fact]
    public async Task Health_ExcludesNonHealthAndStoppedServices()
    {
        await using var factory = new HealthTestFactory();
        await factory.Store.UpsertAsync(HealthyManifest("svc-in"));
        await factory.Store.UpsertAsync(HealthyManifest("svc-out", includeInHealth: false));
        await factory.Store.UpsertAsync(HealthyManifest("svc-stopped", desiredStatus: "stopped"));
        factory.Prober.Set("svc-in", ServiceProbeResult.Up(200, 3));
        factory.Prober.Set("svc-out", ServiceProbeResult.Up(200, 3));
        factory.Prober.Set("svc-stopped", ServiceProbeResult.Up(200, 3));
        var client = factory.CreateClient();

        var doc = await GetHealthAsync(client);
        var services = doc.GetProperty("services");

        Assert.Equal(1, PropertyCount(services));
        Assert.True(services.TryGetProperty("svc-in", out _));
        Assert.False(services.TryGetProperty("svc-out", out _));
        Assert.False(services.TryGetProperty("svc-stopped", out _));
    }

    [Fact]
    public async Task Health_ProbesRunInParallel()
    {
        await using var factory = new HealthTestFactory();
        for (var i = 0; i < 4; i++)
        {
            var name = $"svc-{i}";
            await factory.Store.UpsertAsync(HealthyManifest(name));
            factory.Prober.Set(name, ServiceProbeResult.Up(200, 1));
        }

        // Each probe blocks briefly; if they ran serially peak concurrency is 1.
        factory.Prober.Delay = TimeSpan.FromMilliseconds(150);
        var client = factory.CreateClient();

        await GetHealthAsync(client);

        Assert.True(factory.Prober.MaxConcurrent > 1,
            $"probes should overlap, but peak concurrency was {factory.Prober.MaxConcurrent}");
    }
}
