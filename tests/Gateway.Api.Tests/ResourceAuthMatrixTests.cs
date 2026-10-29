using System.Net;
using System.Text;

namespace Gateway.Api.Tests;

/// <summary>
/// The authorization matrix for every <c>/mgmt/resources</c> route (docs/load.md
/// section 9): anonymous is 401 everywhere; reads need OpsDeploy; every mutation needs
/// OpsAdmin, so neither ops-deploy, the deploy scope, the jobs scope nor the
/// <c>mgmt/load</c> client can change a resource. Each caller gets a fresh host so a
/// mutation cannot change what the next caller sees.
/// </summary>
public class ResourceAuthMatrixTests
{
    public static TheoryData<string, string, string?, bool> Routes => new()
    {
        { "GET", "/mgmt/resources", null, false },
        { "GET", "/mgmt/resources/fleet/refresh", null, false },
        { "GET", "/mgmt/resources/audit", null, false },
        { "PUT", "/mgmt/resources/fleet/size", """{"size":3}""", true },
        { "PUT", "/mgmt/resources/fleet/class", """{"instanceClass":"m7g.large"}""", true },
        { "PUT", "/mgmt/resources/fleet/class/rollback", """{"templateVersion":4}""", true },
        { "PUT", "/mgmt/resources/cache/node-type", """{"nodeType":"cache.m7g.large"}""", true },
        { "PUT", "/mgmt/resources/database/class", """{"instanceClass":"db.m7g.large","confirm":"gateway-db-placeholder"}""", true },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryRoute_EnforcesItsPolicy(string method, string path, string? json, bool mutation)
    {
        var callers = new (string Who, string? Token, bool Allowed)[]
        {
            ("anonymous", null, false),
            ("ops-deploy", ManagementApiFactory.DeployToken(), !mutation),
            ("mgmt/deploy scope", ManagementApiFactory.CiToken(), !mutation),
            ("mgmt/jobs scope", ManagementApiFactory.JobsToken(), false),
            ("mgmt/load scope", ManagementApiFactory.LoadToken(), false),
            ("no role", ManagementApiFactory.NoRoleToken(), false),
            ("ops-admin", ManagementApiFactory.AdminToken(), true),
        };

        foreach (var (who, token, allowed) in callers)
        {
            await using var host = new ResourceTestHost();

            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            var response = await host.Client(token).SendAsync(request);
            var label = $"{method} {path} as {who}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}";

            if (token is null)
            {
                Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, label);
            }
            else if (allowed)
            {
                Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted, label);
            }
            else
            {
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, label);
                Assert.Empty(await host.AuditAsync());
                Assert.DoesNotContain(host.Scaler.CallLog, c => !c.StartsWith("describe", StringComparison.Ordinal));
            }
        }
    }
}
