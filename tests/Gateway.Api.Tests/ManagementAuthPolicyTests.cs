using System.Security.Claims;
using Gateway.Api.Auth;
using Microsoft.Extensions.Configuration;

namespace Gateway.Api.Tests;

/// <summary>
/// Unit coverage for the ops authorization semantics (tech-spec §5), expressed as
/// the claim-reading helpers the policies use. Keeps the group/scope truth table
/// close to the assertions without needing a running server.
/// </summary>
public class ManagementAuthPolicyTests
{
    private static ClaimsPrincipal Principal(string[]? groups = null, string? scope = null)
    {
        var claims = new List<Claim>();
        foreach (var g in groups ?? Array.Empty<string>())
        {
            claims.Add(new Claim(ManagementAuth.GroupsClaim, g));
        }

        if (scope is not null)
        {
            claims.Add(new Claim(ManagementAuth.ScopeClaim, scope));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    [Fact]
    public void OpsAdmin_OnlyForOpsAdminGroup()
    {
        Assert.True(ManagementAuth.IsOpsAdmin(Principal(new[] { ManagementAuth.OpsAdminGroup })));
        Assert.False(ManagementAuth.IsOpsAdmin(Principal(new[] { ManagementAuth.OpsDeployGroup })));
        Assert.False(ManagementAuth.IsOpsAdmin(Principal(scope: ManagementAuth.DeployScope)));
        Assert.False(ManagementAuth.IsOpsAdmin(Principal()));
    }

    [Fact]
    public void CanDeploy_ForAdminDeployGroupOrCiScope()
    {
        Assert.True(ManagementAuth.CanDeploy(Principal(new[] { ManagementAuth.OpsAdminGroup })));
        Assert.True(ManagementAuth.CanDeploy(Principal(new[] { ManagementAuth.OpsDeployGroup })));
        Assert.True(ManagementAuth.CanDeploy(Principal(scope: ManagementAuth.DeployScope)));
        // A different scope must not grant deploy.
        Assert.False(ManagementAuth.CanDeploy(Principal(scope: "mgmt/read openid")));
        Assert.False(ManagementAuth.CanDeploy(Principal()));
    }

    [Fact]
    public void DeployScope_MatchesWithinSpaceDelimitedList()
    {
        Assert.True(ManagementAuth.HasScope(
            Principal(scope: $"openid {ManagementAuth.DeployScope} profile"),
            ManagementAuth.DeployScope));
        // A scope that merely contains the string as a substring must not match.
        Assert.False(ManagementAuth.HasScope(
            Principal(scope: "mgmt/deployment"),
            ManagementAuth.DeployScope));
    }

    [Fact]
    public void OpsChannel_ForHumanOpsGroupsOnly_NotCiScope()
    {
        Assert.True(ManagementAuth.CanUseOpsChannel(Principal(new[] { ManagementAuth.OpsAdminGroup })));
        Assert.True(ManagementAuth.CanUseOpsChannel(Principal(new[] { ManagementAuth.OpsDeployGroup })));
        // CI's deploy scope does not grant dashboard channel access.
        Assert.False(ManagementAuth.CanUseOpsChannel(Principal(scope: ManagementAuth.DeployScope)));
        Assert.False(ManagementAuth.CanUseOpsChannel(Principal()));
    }

    [Fact]
    public void Options_Unconfigured_WhenAuthorityUnset()
    {
        var options = ManagementAuthOptions.FromConfiguration(
            new ConfigurationBuilder().Build());

        Assert.False(options.IsConfigured);
        Assert.Null(options.Authority);
    }

    [Fact]
    public void Options_Configured_WhenAuthoritySet()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ManagementAuthOptions.AuthorityEnvVar] = "https://issuer.example/pool",
            })
            .Build();

        var options = ManagementAuthOptions.FromConfiguration(config);

        Assert.True(options.IsConfigured);
        Assert.Equal("https://issuer.example/pool", options.Authority);
    }
}
