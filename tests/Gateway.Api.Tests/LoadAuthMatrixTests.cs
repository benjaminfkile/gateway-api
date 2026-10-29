using System.Net;
using System.Text;
using Gateway.Api.Data;

namespace Gateway.Api.Tests;

/// <summary>
/// The authorization matrix for every Cognito-gated <c>/mgmt/load</c> route
/// (docs/load.md section 8): anonymous is 401 everywhere; reads need OpsDeploy;
/// mutations need OpsLoad (ops-admin or the <c>mgmt/load</c> scope, never ops-deploy
/// or the jobs scope); the generator log needs OpsAdmin. Each caller gets a fresh host
/// so a mutation cannot change what the next caller sees.
/// </summary>
public class LoadAuthMatrixTests
{
    public enum Access
    {
        Read,
        Mutate,
        Admin,
    }

    private const string StartBody =
        """{"scenario":"tracker","target":"svc-a","confirm":"svc-a","generators":1,"vusPerGenerator":10,"rampSeconds":0,"holdSeconds":60}""";

    public static TheoryData<string, string, string?, Access, bool> Routes => new()
    {
        { "GET", "/mgmt/load/scenarios", null, Access.Read, false },
        { "GET", "/mgmt/load/scenarios/tracker", null, Access.Read, false },
        { "GET", "/mgmt/load/scenarios/tracker/script/versions", null, Access.Read, false },
        { "GET", "/mgmt/load/scenarios/tracker/script/versions/1", null, Access.Read, false },
        { "GET", "/mgmt/load/preflight?scenario=tracker&target=svc-a", null, Access.Read, false },
        { "GET", "/mgmt/load/runs", null, Access.Read, false },
        { "GET", "/mgmt/load/runs/1", null, Access.Read, false },
        { "GET", "/mgmt/load/runs/1/stats", null, Access.Read, false },
        { "GET", "/mgmt/load/runs/1/stats.csv", null, Access.Read, false },
        { "GET", "/mgmt/load/runs/1/log", null, Access.Admin, false },
        { "PUT", "/mgmt/load/scenarios/tracker", """{"maxVusPerGenerator":100}""", Access.Mutate, false },
        { "PUT", "/mgmt/load/scenarios/fresh", """{"repository":"example-org/example-app","script":"export default function () {}","maxVusPerGenerator":10}""", Access.Mutate, false },
        { "DELETE", "/mgmt/load/scenarios/tracker", null, Access.Mutate, false },
        { "POST", "/mgmt/load/runs", StartBody, Access.Mutate, false },
        { "POST", "/mgmt/load/runs/3/stop", null, Access.Mutate, true },
        { "PUT", "/mgmt/load/runs/1/verdict", """{"verdict":"held at 200 users"}""", Access.Mutate, false },
        { "DELETE", "/mgmt/load/runs/1", null, Access.Mutate, false },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryRoute_EnforcesItsPolicy(string method, string path, string? json, Access access, bool activeRun)
    {
        var callers = new (string Who, string? Token, bool Allowed)[]
        {
            ("anonymous", null, false),
            ("ops-deploy", ManagementApiFactory.DeployToken(), access == Access.Read),
            ("mgmt/deploy scope", ManagementApiFactory.CiToken(), access == Access.Read),
            ("mgmt/jobs scope", ManagementApiFactory.JobsToken(), false),
            ("mgmt/load scope", ManagementApiFactory.LoadToken(), access == Access.Mutate),
            ("ops-admin", ManagementApiFactory.AdminToken(), true),
        };

        foreach (var (who, token, allowed) in callers)
        {
            await using var host = new LoadTestHost();
            await host.SeedTargetAsync();
            host.SeedReadyScenario();
            host.Store.Seed(LoadTestHost.Run(1, LoadRunStatus.Succeeded));
            if (activeRun)
            {
                host.Store.Seed(LoadTestHost.Run(3, LoadRunStatus.Queued));
            }

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
                Assert.True(response.IsSuccessStatusCode, label);
            }
            else
            {
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, label);
            }
        }
    }
}
