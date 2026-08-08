using System.Net;
using Gateway.Api.Data;
using Gateway.Api.Manifest;
using Gateway.Api.Proxy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Api.Tests;

/// <summary>
/// Host-based routes (GATEWAY_HOST_ROUTES): a domain fronting a single service
/// forwards bare paths to that service — no /{service} prefix, nothing stripped.
/// </summary>
public class HostRouteTests
{
    private sealed class LoopbackAddressResolver : IServiceAddressResolver
    {
        public string Resolve(ServiceManifest manifest) => $"http://127.0.0.1:{manifest.Port}";
    }

    private sealed class HostRouteFactory : WebApplicationFactory<Program>
    {
        public InMemoryManifestStore Store { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(HostRouteMap.EnvVar, "svc-a.example.com=svc-a");
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
    public async Task MappedHost_ForwardsBarePathUnchanged()
    {
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new HostRouteFactory();
        await factory.Store.UpsertAsync(RunningManifest("svc-a", downstream.Port));
        await factory.RefreshRoutesAsync();
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/sponsors?year=2026");
        request.Headers.Host = "svc-a.example.com";
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // The downstream must see the bare path exactly as the browser sent it.
        Assert.Equal("/api/sponsors?year=2026", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UnmappedHost_StillGets404ForBarePaths()
    {
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new HostRouteFactory();
        await factory.Store.UpsertAsync(RunningManifest("svc-a", downstream.Port));
        await factory.RefreshRoutesAsync();
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/sponsors");
        request.Headers.Host = "unmapped.example.com";
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PathPrefixRouting_StillWorksAlongsideHostRoutes()
    {
        await using var downstream = await DownstreamTestServer.StartAsync();
        await using var factory = new HostRouteFactory();
        await factory.Store.UpsertAsync(RunningManifest("svc-a", downstream.Port));
        await factory.RefreshRoutesAsync();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/svc-a/api/sponsors");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api/sponsors", await response.Content.ReadAsStringAsync());
    }
}
