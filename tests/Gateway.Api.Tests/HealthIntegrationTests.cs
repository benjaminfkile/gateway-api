using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Api.Data;
using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.Tests;

/// <summary>
/// End-to-end test of <c>/api/health</c> through the full gateway host using the
/// real <see cref="Gateway.Api.Health.HttpHealthProber"/> (no fake) against a
/// real localhost downstream server, plus an unreachable service to prove the
/// aggregate stays 200 when a probe genuinely fails on the wire.
/// </summary>
public class HealthIntegrationTests
{
    /// <summary>Points probes/routes at localhost rather than container DNS.</summary>
    private sealed class LoopbackAddressResolver : IServiceAddressResolver
    {
        public string Resolve(ServiceManifest manifest) => $"http://127.0.0.1:{manifest.Port}";
    }

    private sealed class HealthTestFactory : WebApplicationFactory<Program>
    {
        public InMemoryManifestStore Store { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IManifestStore>();
                services.AddSingleton<IManifestStore>(Store);

                services.RemoveAll<IServiceAddressResolver>();
                services.AddSingleton<IServiceAddressResolver, LoopbackAddressResolver>();
            });
        }
    }

    private static ServiceManifest RunningManifest(string name, int port) => new()
    {
        Name = name,
        Image = $"registry/{name}",
        Tag = "latest",
        Port = port,
        DesiredStatus = "running",
        IncludeInHealth = true,
        UpdatedBy = "test",
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public async Task Health_ReportsUp_ForRealDownstreamServer()
    {
        // DownstreamTestServer answers 200 at /api/health (it echoes any path).
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new HealthTestFactory();
        await factory.Store.UpsertAsync(RunningManifest("svc-a", downstream.Port));
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("up", doc.GetProperty("gateway").GetString());
        var svc = doc.GetProperty("services").GetProperty("svc-a");
        Assert.Equal("up", svc.GetProperty("status").GetString());
        Assert.Equal(200, svc.GetProperty("httpStatus").GetInt32());
        Assert.True(svc.GetProperty("responseTimeMs").GetInt64() >= 0);
    }

    [Fact]
    public async Task Health_Returns200_WhenRealServiceUnreachable()
    {
        // A running, health-participating service with nothing listening behind it.
        await using var factory = new HealthTestFactory();
        await factory.Store.UpsertAsync(RunningManifest("svc-dead", 59_998));
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        // A dead downstream must not take down the gateway's own check.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("up", doc.GetProperty("gateway").GetString());
        var svc = doc.GetProperty("services").GetProperty("svc-dead");
        Assert.Equal("down", svc.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, svc.GetProperty("httpStatus").ValueKind);
    }
}
