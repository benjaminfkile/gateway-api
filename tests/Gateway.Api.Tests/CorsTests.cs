using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gateway.Api.Tests;

/// <summary>
/// The ops dashboard is served from its own origin, so /mgmt/* and /hub need
/// CORS for exactly the origins in GATEWAY_CORS_ORIGINS — and nothing else
/// (proxied application traffic is never touched; unset → plane stays closed).
/// </summary>
public class CorsTests
{
    private static WebApplicationFactory<Program> FactoryWithOrigins(string? origins) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (origins is not null)
            {
                builder.UseSetting("GATEWAY_CORS_ORIGINS", origins);
            }
        });

    private static HttpRequestMessage Preflight(string path, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        return request;
    }

    [Fact]
    public async Task PreflightFromConfiguredOriginIsAllowedOnManagementPlane()
    {
        await using var factory = FactoryWithOrigins("https://ops.example.com");
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("/mgmt/services", "https://ops.example.com"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            "https://ops.example.com",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal(
            "true",
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Credentials")));
    }

    [Fact]
    public async Task PreflightFromUnknownOriginGetsNoCorsHeaders()
    {
        await using var factory = FactoryWithOrigins("https://ops.example.com");
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("/mgmt/services", "https://evil.example.com"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ProxiedApplicationPreflightIsAnsweredWithWildcard()
    {
        // Legacy parity: the old gateway ran `cors()` in front of every proxied
        // route; frontends on other origins depend on it.
        await using var factory = FactoryWithOrigins(null);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("/svc-a/anything", "https://some-frontend.example.com"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
    }

    [Fact]
    public async Task ProxiedApplicationResponseGetsWildcardOnlyWhenDownstreamSetNone()
    {
        // The gateway's own /api/health stands in for a downstream that emits
        // no CORS headers of its own: a cross-origin GET gets the wildcard.
        await using var factory = FactoryWithOrigins(null);
        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Origin", "https://some-frontend.example.com");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task WithoutConfiguredOriginsManagementPlaneGetsNoCorsHeaders()
    {
        await using var factory = FactoryWithOrigins(null);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Preflight("/mgmt/services", "https://ops.example.com"));

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
