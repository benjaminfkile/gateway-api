using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Api.Data;
using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
using Gateway.Api.Reconcile;
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

        /// <summary>
        /// When set, HttpHealthProber sees a reconciler-enabled ReconcilerOptions —
        /// so a service with no canonical container port reports down instead of
        /// falling back to a manifest-port probe (task #98, incident 2026-08-17).
        /// </summary>
        public bool ReconcilerEnabled { get; set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IManifestStore>();
                services.AddSingleton<IManifestStore>(Store);

                services.RemoveAll<IServiceAddressResolver>();
                services.AddSingleton<IServiceAddressResolver, LoopbackAddressResolver>();

                if (ReconcilerEnabled)
                {
                    services.RemoveAll<ReconcilerOptions>();
                    services.AddSingleton(new ReconcilerOptions { Enabled = true });
                }
            });
        }

        public ServiceHostPortMap HostPorts =>
            Services.GetRequiredService<ServiceHostPortMap>();
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
    public async Task ReconcilerMode_NoContainerPort_ReportsServiceDown_WithoutProbingManifestPort()
    {
        // task #98 / incident 2026-08-17: in reconciler mode the health prober
        // must NEVER fall back to the manifest port. That host port is a
        // Docker-assigned ephemeral that could belong to a completely different
        // service (a stranger returning 200 to /api/health would silently mark
        // this service 'up' — the incident's blind spot). No canonical container
        // port recorded → the probe short-circuits and reports the service down.
        await using var factory = new HealthTestFactory { ReconcilerEnabled = true };
        // Manifest port 8080 happens to be plausibly listening on many dev boxes;
        // the point is the prober must not touch it at all.
        await factory.Store.UpsertAsync(RunningManifest("svc-a", 8080));
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("up", doc.GetProperty("gateway").GetString());
        var svc = doc.GetProperty("services").GetProperty("svc-a");
        Assert.Equal("down", svc.GetProperty("status").GetString());
        // Unreachable (no probe attempted at all) → both HTTP fields are null.
        Assert.Equal(JsonValueKind.Null, svc.GetProperty("httpStatus").ValueKind);
        Assert.Equal(JsonValueKind.Null, svc.GetProperty("responseTimeMs").ValueKind);
    }

    [Fact]
    public async Task ReconcilerMode_ContainerPortRecorded_ProbesRealPort_ReportsUp()
    {
        // Complement: once the host-port map has the canonical container's port,
        // the health prober targets that real port and reports up on a 2xx.
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new HealthTestFactory { ReconcilerEnabled = true };
        // Manifest port is intentionally NOT the downstream port; only the map
        // entry drives the probe.
        await factory.Store.UpsertAsync(RunningManifest("svc-a", 65_500));
        factory.HostPorts.Set("svc-a", downstream.Port);

        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await response.Content.ReadFromJsonAsync<JsonElement>();
        var svc = doc.GetProperty("services").GetProperty("svc-a");
        Assert.Equal("up", svc.GetProperty("status").GetString());
        Assert.Equal(200, svc.GetProperty("httpStatus").GetInt32());
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
