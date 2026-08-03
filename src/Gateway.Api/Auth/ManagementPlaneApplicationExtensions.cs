using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Api.Auth;

/// <summary>
/// Pipeline and endpoint wiring for the management plane (tech-spec §5).
/// </summary>
public static class ManagementPlaneApplicationExtensions
{
    /// <summary>Body returned by the guard when the plane is unconfigured.</summary>
    public const string NotConfiguredMessage = "management plane not configured";

    /// <summary>
    /// Short-circuit every <c>/mgmt/*</c> request to <c>503</c> when the plane is
    /// unconfigured (tech-spec §5): with no Cognito authority the management API is
    /// closed, never open. Runs before authentication so the response does not
    /// depend on a token. Non-<c>/mgmt</c> traffic (proxy, health, hub) is
    /// untouched — the transparency invariant holds.
    /// </summary>
    public static IApplicationBuilder UseManagementPlaneGuard(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<ManagementAuthOptions>();

        return app.Use(async (context, next) =>
        {
            if (!options.IsConfigured
                && context.Request.Path.StartsWithSegments("/mgmt"))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync(NotConfiguredMessage);
                return;
            }

            await next();
        });
    }

    /// <summary>
    /// Map the management plane's authenticated surface (tech-spec §5). The
    /// fleet-aware endpoints themselves are built in a later task; this establishes
    /// the authenticated <c>/mgmt</c> group and a small identity endpoint so the
    /// dashboard can confirm which role its token carries. Every endpoint here
    /// requires a valid Cognito access token — no token yields <c>401</c> (or
    /// <c>503</c> when the plane is unconfigured, via the guard above).
    /// </summary>
    public static IEndpointRouteBuilder MapManagementPlane(this IEndpointRouteBuilder endpoints)
    {
        var mgmt = endpoints.MapGroup("/mgmt");

        // Identity introspection: available to any authenticated ops principal
        // (admins, deployers, and the CI deploy token) via the OpsDeploy policy.
        mgmt.MapGet("/whoami", (ClaimsPrincipal user) => Results.Json(new
        {
            name = user.Identity?.Name
                ?? user.FindFirst("username")?.Value
                ?? user.FindFirst("client_id")?.Value,
            groups = user.FindAll(ManagementAuth.GroupsClaim).Select(c => c.Value).ToArray(),
            isOpsAdmin = ManagementAuth.IsOpsAdmin(user),
            canDeploy = ManagementAuth.CanDeploy(user),
        })).RequireAuthorization(ManagementAuth.OpsDeployPolicy);

        return endpoints;
    }
}
