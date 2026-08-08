using System.Security.Cryptography;
using System.Text;
using Gateway.Api.Instances;

namespace Gateway.Api.Legacy;

/// <summary>
/// Compatibility surface for downstream services written against the previous
/// gateway's local API. wmsfo-api polls <c>GET /api/about-me</c> (leader/identity,
/// gating its leader-only broadcast jobs) and <c>GET /api/ec2-launch/instances</c>
/// (fleet list) on the host at startup and blocks until they answer — so these
/// exist until those callers are migrated. Both require the legacy shared-secret
/// header <c>x-bk-gateway-key</c>, matched against <c>GATEWAY_LEGACY_KEY</c> in
/// constant time; with the key unconfigured the endpoints answer 503 and nothing
/// else changes. Response shapes mirror the old gateway exactly.
/// </summary>
public static class LegacyCompatEndpoints
{
    public const string KeyEnvVar = "GATEWAY_LEGACY_KEY";
    public const string HeaderName = "x-bk-gateway-key";

    public static IEndpointRouteBuilder MapLegacyCompat(this IEndpointRouteBuilder endpoints, IConfiguration configuration)
    {
        var key = Environment.GetEnvironmentVariable(KeyEnvVar) ?? configuration[KeyEnvVar];

        endpoints.MapGet("/api/about-me", async (
            HttpContext ctx,
            InstanceMetadataProvider metadata,
            ILeaderElection leaderElection,
            CancellationToken ct) =>
        {
            if (Reject(ctx, key) is { } rejection)
            {
                return rejection;
            }

            var identity = await metadata.GetAsync(ct);
            var amILeader = await leaderElection.TryAcquireAsync(ct);
            return Results.Json(new
            {
                amILeader,
                myInstanceId = identity.InstanceId,
                publicIp = identity.PublicIp,
                privateIp = identity.PrivateIp,
            });
        });

        endpoints.MapGet("/api/ec2-launch/instances", async (
            HttpContext ctx,
            IInstanceStatusStore instances,
            CancellationToken ct) =>
        {
            if (Reject(ctx, key) is { } rejection)
            {
                return rejection;
            }

            var rows = await instances.GetAllAsync(ct);
            return Results.Json(rows
                .OrderByDescending(r => r.HeartbeatAt)
                .Select((r, i) => new
                {
                    id = i + 1,
                    instance_id = r.InstanceId,
                    public_ip = r.PublicIp,
                    private_ip = r.PrivateIp,
                    is_leader = r.IsLeader,
                    launched_at = r.HeartbeatAt,
                })
                .ToList());
        });

        return endpoints;
    }

    /// <summary>Non-null result = request rejected (503 unconfigured, 401 missing, 403 wrong).</summary>
    private static IResult? Reject(HttpContext ctx, string? configuredKey)
    {
        if (string.IsNullOrEmpty(configuredKey))
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var presented = ctx.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(presented))
        {
            return Results.Json(new { error = $"Missing {HeaderName} header" }, statusCode: 401);
        }

        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(configuredKey);
        if (a.Length != b.Length || !CryptographicOperations.FixedTimeEquals(a, b))
        {
            return Results.Json(new { error = "Invalid credentials" }, statusCode: 403);
        }

        return null;
    }
}
