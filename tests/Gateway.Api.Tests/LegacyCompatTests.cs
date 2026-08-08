using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gateway.Api.Tests;

/// <summary>
/// The old gateway served a small local API that wmsfo-api still calls at
/// startup (blocking until it answers): /api/about-me and
/// /api/ec2-launch/instances, guarded by the x-bk-gateway-key shared secret.
/// These verify the compat surface matches that contract.
/// </summary>
public class LegacyCompatTests
{
    private const string Key = "legacy-secret";

    private static WebApplicationFactory<Program> Factory(string? key = Key) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (key is not null)
            {
                builder.UseSetting(Gateway.Api.Legacy.LegacyCompatEndpoints.KeyEnvVar, key);
            }
        });

    private static HttpRequestMessage Get(string path, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (key is not null)
        {
            request.Headers.Add("x-bk-gateway-key", key);
        }
        return request;
    }

    [Fact]
    public async Task AboutMe_WithValidKey_ReturnsLegacyShape()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Get("/api/about-me", Key));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("amILeader", out _));
        Assert.True(body.TryGetProperty("myInstanceId", out _));
        Assert.True(body.TryGetProperty("publicIp", out _));
        Assert.True(body.TryGetProperty("privateIp", out _));
    }

    [Fact]
    public async Task Instances_WithValidKey_ReturnsArray()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Get("/api/ec2-launch/instances", Key));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
    }

    [Fact]
    public async Task MissingKey_Returns401_WrongKey_Returns403()
    {
        await using var factory = Factory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.SendAsync(Get("/api/about-me", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.SendAsync(Get("/api/about-me", "wrong"))).StatusCode);
    }

    [Fact]
    public async Task UnconfiguredKey_Returns503()
    {
        await using var factory = Factory(key: null);
        using var client = factory.CreateClient();

        var response = await client.SendAsync(Get("/api/about-me", Key));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
