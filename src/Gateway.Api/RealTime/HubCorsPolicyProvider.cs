using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Gateway.Api.RealTime;

/// <summary>
/// CORS policy provider that resolves the dynamic, manifest-driven <c>/hub</c> policy
/// (task #595) and delegates every other named policy — notably the strict static
/// <c>ops</c> dashboard policy used on <c>/mgmt</c> — to the framework default.
/// <para>
/// The <c>/hub</c> policy is built per request from <see cref="HubCorsOriginCache"/>
/// (a cached, short-TTL union of the static ops origins and each service's
/// <c>realtime_allowed_origins</c>). It uses <c>SetIsOriginAllowed</c> against the
/// resolved set so the decision is always an <b>exact</b> origin match — never a
/// wildcard — which is what <c>AllowCredentials</c> (required by SignalR) demands.
/// Resolving here, rather than in a fixed <c>AddCors</c> policy, is what lets a manifest
/// CORS change take effect without a gateway restart: <c>GetPolicyAsync</c> runs on
/// every preflight and reads the current cache snapshot.
/// </para>
/// </summary>
public sealed class HubCorsPolicyProvider : ICorsPolicyProvider
{
    /// <summary>The policy name applied via <c>UseCors</c> on the <c>/hub</c> branch.</summary>
    public const string PolicyName = "hub";

    private readonly DefaultCorsPolicyProvider _fallback;

    public HubCorsPolicyProvider(IOptions<CorsOptions> options)
    {
        _fallback = new DefaultCorsPolicyProvider(options);
    }

    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        if (!string.Equals(policyName, PolicyName, StringComparison.Ordinal))
        {
            return await _fallback.GetPolicyAsync(context, policyName);
        }

        var cache = context.RequestServices.GetRequiredService<HubCorsOriginCache>();
        var origins = await cache.GetAllowedOriginsAsync(context.RequestAborted);

        return new CorsPolicyBuilder()
            .AllowAnyHeader()
            .AllowAnyMethod()
            // Exact-match only (origins is a normalized, wildcard-free set); combined
            // with AllowCredentials the middleware echoes the exact request Origin, so a
            // credentialed SignalR negotiate succeeds and no wildcard is ever emitted.
            .SetIsOriginAllowed(origins.Contains)
            .AllowCredentials()
            .Build();
    }
}
