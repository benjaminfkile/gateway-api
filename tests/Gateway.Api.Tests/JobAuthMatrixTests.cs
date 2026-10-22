using System.Net;
using System.Text;
using Gateway.Api.Management;

namespace Gateway.Api.Tests;

/// <summary>
/// The authorization matrix for every <c>/mgmt/jobs</c> route (docs/jobs.md
/// section 4): anonymous is 401 everywhere; reads need OpsDeploy; mutations need
/// OpsJobs (ops-admin or the <c>mgmt/jobs</c> scope, never ops-deploy); the run log
/// needs OpsAdmin. Each caller gets a fresh host so a mutation cannot change what
/// the next caller sees.
/// </summary>
public class JobAuthMatrixTests
{
    public enum Access
    {
        Read,
        Mutate,
        Admin,
    }

    public static TheoryData<string, string, string?, Access> Routes => new()
    {
        { "GET", "/mgmt/jobs", null, Access.Read },
        { "GET", "/mgmt/jobs/nightly", null, Access.Read },
        { "GET", "/mgmt/jobs/nightly/runs", null, Access.Read },
        { "GET", "/mgmt/jobs/nightly/runs/1", null, Access.Read },
        { "GET", "/mgmt/jobs/nightly/script/versions", null, Access.Read },
        { "GET", "/mgmt/jobs/nightly/script/versions/1", null, Access.Read },
        { "GET", "/mgmt/jobs/nightly/runs/1/log", null, Access.Admin },
        { "PUT", "/mgmt/jobs/nightly", """{"timeoutSeconds":120}""", Access.Mutate },
        { "PUT", "/mgmt/jobs/fresh", """{"timeZone":"UTC","script":"echo hi"}""", Access.Mutate },
        { "DELETE", "/mgmt/jobs/nightly", null, Access.Mutate },
        { "POST", "/mgmt/jobs/nightly/run", null, Access.Mutate },
        { "POST", "/mgmt/jobs/nightly/enable", null, Access.Mutate },
        { "POST", "/mgmt/jobs/nightly/disable", null, Access.Mutate },
        { "POST", "/mgmt/jobs/hourly/runs/2/cancel", null, Access.Mutate },
    };

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task EveryRoute_EnforcesItsPolicy(string method, string path, string? json, Access access)
    {
        var callers = new (string Who, string? Token, bool Allowed)[]
        {
            ("anonymous", null, false),
            ("ops-deploy", ManagementApiFactory.DeployToken(), access == Access.Read),
            ("mgmt/jobs scope", ManagementApiFactory.JobsToken(), access == Access.Mutate),
            ("ops-admin", ManagementApiFactory.AdminToken(), true),
        };

        foreach (var (who, token, allowed) in callers)
        {
            await using var host = new JobTestHost();
            await host.SeedAsync(
                JobTestHost.Job("nightly"),
                JobTestHost.Version("nightly", 1, "echo hi"),
                JobTestHost.Job("hourly"),
                JobTestHost.Run(1, "nightly", JobRunStatus.Succeeded),
                JobTestHost.Run(2, "hourly", JobRunStatus.Queued));

            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (json is not null)
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            var response = await host.Client(token).SendAsync(request);
            var label = $"{method} {path} as {who}: {(int)response.StatusCode}";

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
