using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Gateway.Api.RealTime;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gateway.Api.Tests;

/// <summary>
/// The deploy broadcast is best-effort and runs AFTER the manifest mutation has
/// committed (tech-spec §4.5): a Redis backplane blip must never turn an
/// already-committed deploy into a 500. Wires the REAL
/// <see cref="ChannelEventPublisher"/> over a hub that always throws and asserts the
/// endpoint still returns 202.
/// </summary>
public class DeployBroadcastResilienceTests
{
    [Fact]
    public async Task Deploy_ReturnsAccepted_EvenWhenThePublishThrows()
    {
        await using var factory = new ManagementApiFactory();
        await factory.WithDbAsync(async db =>
        {
            db.ServiceManifests.Add(ManagementTestData.Manifest("svc-a", digest: "sha256:old", tag: "v1"));
            await db.SaveChangesAsync();
        });

        // Real publisher, but its hub always throws — simulates a backplane outage on
        // the request path after the commit. TryPublish must swallow it.
        var throwingHub = new FakeGatewayHubContext
        {
            SendError = new InvalidOperationException("backplane down"),
        };

        var customized = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IChannelEventPublisher>();
                services.AddSingleton<IChannelEventPublisher>(
                    new ChannelEventPublisher(throwingHub, NullLogger<ChannelEventPublisher>.Instance));
            });
        });

        var client = customized.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ManagementApiFactory.DeployToken());

        var response = await client.PostAsJsonAsync("/mgmt/services/svc-a/deploy", new { tag = "v2" });

        // The publish faulted, but the committed deploy is still a success.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }
}
