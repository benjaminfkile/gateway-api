using System.Net;
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
/// Integration tests for the manifest-driven YARP proxy. The gateway runs under
/// <see cref="WebApplicationFactory{TEntryPoint}"/> with an in-memory manifest
/// store; routes are forwarded to a real Kestrel downstream server on localhost.
/// </summary>
public class ProxyIntegrationTests
{
    /// <summary>Points routes at the localhost test server rather than container DNS.</summary>
    private sealed class LoopbackAddressResolver : IServiceAddressResolver
    {
        public string Resolve(ServiceManifest manifest) => $"http://127.0.0.1:{manifest.Port}";
    }

    private sealed class ProxyTestFactory : WebApplicationFactory<Program>
    {
        // Captured so tests can mutate the manifest at runtime and refresh routes.
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

        public Task RefreshRoutesAsync() =>
            Services.GetRequiredService<ProxyStateService>().RefreshRoutesAsync();
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
    public async Task ProxiesRequest_PreservingRemainderOfPathAndQuery()
    {
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new ProxyTestFactory();
        await factory.Store.UpsertAsync(RunningManifest("svc-a", downstream.Port));

        var client = factory.CreateClient();

        var response = await client.GetAsync("/svc-a/orders/42?status=open&page=2");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        // The '/svc-a' prefix is stripped; the app sees only its own path + query.
        Assert.Equal("/orders/42?status=open&page=2", body);
    }

    [Fact]
    public async Task ProxiesRootOfService()
    {
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new ProxyTestFactory();
        await factory.Store.UpsertAsync(RunningManifest("svc-a", downstream.Port));

        var client = factory.CreateClient();

        var response = await client.GetAsync("/svc-a/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NonManifestPath_Returns404()
    {
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new ProxyTestFactory();
        await factory.Store.UpsertAsync(RunningManifest("svc-a", downstream.Port));

        var client = factory.CreateClient();

        var response = await client.GetAsync("/svc-b/anything");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StoppedService_HasNoRoute_Returns404()
    {
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new ProxyTestFactory();
        var stopped = RunningManifest("svc-a", downstream.Port);
        stopped.DesiredStatus = "stopped";
        await factory.Store.UpsertAsync(stopped);

        var client = factory.CreateClient();

        var response = await client.GetAsync("/svc-a/orders");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ManifestChange_TakesEffect_AfterRefresh_WithoutRestart()
    {
        await using var first = await DownstreamTestServer.StartAsync();
        await using var second = await DownstreamTestServer.StartAsync();
        await using var factory = new ProxyTestFactory();
        await factory.Store.UpsertAsync(RunningManifest("svc-a", first.Port));

        var client = factory.CreateClient();

        // svc-b is not in the manifest yet → 404.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/svc-b/ping")).StatusCode);

        // Add svc-b and refresh routes on the *same* running gateway.
        await factory.Store.UpsertAsync(RunningManifest("svc-b", second.Port));
        await factory.RefreshRoutesAsync();

        // svc-a still works, svc-b now routes — no restart.
        var aResponse = await client.GetAsync("/svc-a/ping");
        Assert.Equal(HttpStatusCode.OK, aResponse.StatusCode);
        Assert.Equal("/ping", await aResponse.Content.ReadAsStringAsync());

        var bResponse = await client.GetAsync("/svc-b/ping");
        Assert.Equal(HttpStatusCode.OK, bResponse.StatusCode);
        Assert.Equal("/ping", await bResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SetDesiredStatusStopped_ThenRefresh_RemovesRoute()
    {
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new ProxyTestFactory();
        await factory.Store.UpsertAsync(RunningManifest("svc-a", downstream.Port));

        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/svc-a/ping")).StatusCode);

        await factory.Store.SetDesiredStatusAsync("svc-a", "stopped");
        await factory.RefreshRoutesAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/svc-a/ping")).StatusCode);
    }

    [Fact]
    public async Task ProxiedRoute_HasNoAuthChallenge()
    {
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new ProxyTestFactory();
        await factory.Store.UpsertAsync(RunningManifest("svc-a", downstream.Port));

        var client = factory.CreateClient();

        // No Authorization header supplied. A gateway that authenticated app
        // traffic would answer 401; the design invariant forbids that.
        var response = await client.GetAsync("/svc-a/secret");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
