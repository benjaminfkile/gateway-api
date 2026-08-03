using System.Security.Claims;

namespace Gateway.Api.Auth;

/// <summary>
/// Names and claim conventions for the management-plane authentication scheme
/// (tech-spec §5). The ops dashboard and <c>/mgmt/*</c> endpoints are the
/// <b>only</b> authenticated surfaces; application traffic is never inspected
/// (design invariant, §1).
/// <para>
/// Authorization is expressed as Cognito access-token claims: human tokens carry
/// group membership in <c>cognito:groups</c>; the CI machine token (a
/// <c>client_credentials</c> grant) carries the resource-server scope
/// <c>mgmt/deploy</c> in the space-delimited <c>scope</c> claim.
/// </para>
/// </summary>
public static class ManagementAuth
{
    /// <summary>Full-control ops role: manifest edits, stop/start, deploy, rollback.</summary>
    public const string OpsAdminPolicy = "OpsAdmin";

    /// <summary>Deploy/rollback role, also satisfied by the CI <c>mgmt/deploy</c> scope.</summary>
    public const string OpsDeployPolicy = "OpsDeploy";

    /// <summary>Claim type carrying Cognito group membership.</summary>
    public const string GroupsClaim = "cognito:groups";

    /// <summary>Claim type carrying the space-delimited OAuth scopes.</summary>
    public const string ScopeClaim = "scope";

    /// <summary>Claim type Cognito uses to distinguish access vs id tokens.</summary>
    public const string TokenUseClaim = "token_use";

    /// <summary>The only token type the management plane accepts.</summary>
    public const string AccessTokenUse = "access";

    /// <summary>Cognito group granting full ops-admin control.</summary>
    public const string OpsAdminGroup = "ops-admin";

    /// <summary>Cognito group granting deploy/rollback only.</summary>
    public const string OpsDeployGroup = "ops-deploy";

    /// <summary>Resource-server scope granting the CI machine token deploy rights.</summary>
    public const string DeployScope = "mgmt/deploy";

    /// <summary>True when <paramref name="user"/> is a member of the named Cognito group.</summary>
    public static bool IsInGroup(ClaimsPrincipal user, string group) =>
        user.Claims.Any(c =>
            string.Equals(c.Type, GroupsClaim, StringComparison.Ordinal)
            && string.Equals(c.Value, group, StringComparison.Ordinal));

    /// <summary>True when the space-delimited <c>scope</c> claim contains <paramref name="scope"/>.</summary>
    public static bool HasScope(ClaimsPrincipal user, string scope)
    {
        foreach (var claim in user.FindAll(ScopeClaim))
        {
            if (claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(s => string.Equals(s, scope, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Satisfies <see cref="OpsAdminPolicy"/>: member of <c>ops-admin</c>.</summary>
    public static bool IsOpsAdmin(ClaimsPrincipal user) => IsInGroup(user, OpsAdminGroup);

    /// <summary>
    /// Satisfies <see cref="OpsDeployPolicy"/>: an <c>ops-admin</c> or <c>ops-deploy</c>
    /// human, or a CI client-credentials token bearing the <c>mgmt/deploy</c> scope.
    /// </summary>
    public static bool CanDeploy(ClaimsPrincipal user) =>
        IsOpsAdmin(user) || IsInGroup(user, OpsDeployGroup) || HasScope(user, DeployScope);

    /// <summary>
    /// Whether <paramref name="user"/> may connect to the dashboard's <c>ops:*</c>
    /// hub channels — a human ops user (admin or deploy). The CI scope alone does
    /// not grant dashboard access; CI only calls the deploy endpoint.
    /// </summary>
    public static bool CanUseOpsChannel(ClaimsPrincipal user) =>
        IsOpsAdmin(user) || IsInGroup(user, OpsDeployGroup);
}
