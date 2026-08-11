using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Api.RealTime;

/// <summary>
/// Pipeline and endpoint wiring for the real-time hub and the internal publish
/// listener (tech-spec §4.2, §8).
/// </summary>
public static class RealtimeApplicationExtensions
{
    /// <summary>
    /// Add the internal listener as a second Kestrel endpoint alongside the
    /// public URLs, preserving any already-configured public bind. The internal
    /// address comes from <c>GATEWAY_INTERNAL_BIND</c> (default
    /// <c>0.0.0.0:8080</c>). Both listeners share one pipeline; the isolation
    /// middleware keeps their surfaces disjoint by local port.
    /// </summary>
    public static WebApplicationBuilder AddGatewayInternalListener(this WebApplicationBuilder builder)
    {
        var options = InternalListenerOptions.FromConfiguration(builder.Configuration);

        var urls = new List<string>();
        var configured = builder.Configuration["urls"] ?? builder.Configuration["ASPNETCORE_URLS"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            urls.AddRange(configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        if (urls.Count == 0)
        {
            // Kestrel's own default public bind when nothing is configured.
            urls.Add("http://localhost:5000");
        }

        if (!urls.Contains(options.Url, StringComparer.OrdinalIgnoreCase))
        {
            urls.Add(options.Url);
        }

        builder.WebHost.UseUrls(urls.ToArray());
        return builder;
    }

    /// <summary>
    /// Keep the two listeners' surfaces disjoint (tech-spec §8): <c>/internal/*</c>
    /// is served only on the internal port and 404s on the public one, and the
    /// internal port serves nothing but <c>/internal/*</c>. Keyed on the
    /// connection's local port so the load balancer can never reach the publish
    /// endpoint. Register this before the endpoints so a mismatched request short
    /// -circuits to 404.
    /// </summary>
    public static IApplicationBuilder UseInternalListenerIsolation(this IApplicationBuilder app)
    {
        var internalPort = app.ApplicationServices
            .GetRequiredService<InternalListenerOptions>().Port;

        return app.Use(async (context, next) =>
        {
            var isInternalPath = context.Request.Path.StartsWithSegments("/internal");
            var onInternalPort = context.Connection.LocalPort == internalPort;

            if (isInternalPath != onInternalPort)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next();
        });
    }

    /// <summary>Map the real-time hub at <c>/hub</c> (tech-spec §4.2).</summary>
    public static IEndpointRouteBuilder MapGatewayHub(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<GatewayHub>("/hub", options =>
        {
            // ops:* channel auth runs only inside JoinChannel, so without this an
            // expired-token connection that already joined ops:* would keep
            // receiving privileged events until it happened to disconnect. Closing
            // the connection at token expiry forces the dashboard to reconnect,
            // which pulls a fresh Cognito token from its accessTokenFactory — a
            // clean close is exactly the signal that refresh flow expects.
            options.CloseOnAuthenticationExpiration = true;
        });
        return endpoints;
    }

    /// <summary>
    /// Map the internal publish endpoint (tech-spec §4.2, task #593). A downstream
    /// container publishes to its own <c>{service}:{topic}</c> channels only: the
    /// request must carry header <c>X-Gateway-Realtime-Token</c> matching the publish
    /// token of the manifest service that owns the target channel's prefix, and the
    /// token is constant-time compared. On success the payload is broadcast to the
    /// channel's SignalR group inside the ChannelEvent envelope (method
    /// <c>ChannelEvent</c>, <c>{ channel, event, data }</c>) and the call returns
    /// <c>202 Accepted</c>. Rejected with <c>403</c> when the prefix owns to no
    /// service, its token has not been minted yet (pre-migration row), or the token
    /// does not match. <c>ops:*</c> channels are gateway-owned and never publishable
    /// through this HTTP path — gateway-internal events go through
    /// <see cref="IChannelEventPublisher"/> directly. The isolation middleware
    /// guarantees this endpoint is reachable only on the internal listener.
    /// </summary>
    public static IEndpointRouteBuilder MapInternalPublish(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/internal/publish", async (
            InternalPublishRequest request,
            HttpContext context,
            IChannelEventPublisher publisher,
            IChannelOwnershipResolver ownership,
            PublishRateLimiter throttle,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Channel))
            {
                return Results.BadRequest(new { error = "channel is required." });
            }

            var (owner, failure) = await AuthorizeOwnerAsync(
                request.Channel, "publishing to", context, ownership, ct);
            if (failure is not null)
            {
                return failure;
            }

            // Per-service publish throttle (task #611, item 4): a token bucket keyed on the
            // owning service caps its publish volume so one chatty service cannot exhaust the
            // hub for everyone. Checked only after the token authorizes the caller, so an
            // unauthorized request (already 403'd above) never consumes the service's budget.
            // Over budget → 429 with Retry-After (whole seconds, floored at 1).
            if (!throttle.TryTake(owner!.Service, out var retryAfter))
            {
                var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
                context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Results.Json(
                    new { error = $"Publish rate limit exceeded for service '{owner.Service}'; retry after {seconds}s." },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }

            await publisher.PublishAsync(request.Channel, request.Event, request.Payload, ct);
            return Results.Accepted();
        });
        return endpoints;
    }

    /// <summary>
    /// Map the owner presence API (tech-spec §4.2, task #612):
    /// <c>GET /internal/presence/{channel}</c>. Returns who is currently present in the
    /// channel plus a count, so a chat/collaboration backend can render "who is online"
    /// without keeping its own connection bookkeeping. Guarded by the <b>same</b>
    /// owner-token check as <c>/internal/publish</c>: the request must carry
    /// <c>X-Gateway-Realtime-Token</c> matching the publish token of the manifest service
    /// that owns the channel's prefix (constant-time compared). Unlike the coalesced
    /// presence <i>event</i>, this read is available regardless of the service's
    /// <c>realtime_presence</c> opt-in — it is the owner's own token-gated data, not a
    /// broadcast to every subscriber. <c>ops:*</c> is gateway-owned and never queryable
    /// here. The isolation middleware keeps this reachable only on the internal listener.
    /// </summary>
    public static IEndpointRouteBuilder MapInternalPresence(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/internal/presence/{channel}", async (
            string channel,
            HttpContext context,
            IChannelOwnershipResolver ownership,
            IPresenceRegistry presence,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(channel))
            {
                return Results.BadRequest(new { error = "channel is required." });
            }

            var (_, failure) = await AuthorizeOwnerAsync(
                channel, "reading presence for", context, ownership, ct);
            if (failure is not null)
            {
                return failure;
            }

            var members = await presence.ListAsync(channel, ct);
            return Results.Json(new
            {
                channel,
                count = members.Count,
                members = members
                    .Select(m => new { connectionId = m.ConnectionId, identity = m.Identity, joinedAt = m.JoinedAt })
                    .ToArray(),
            });
        });
        return endpoints;
    }

    /// <summary>
    /// The ONE owner-token authorization sequence every internal owner-scoped endpoint
    /// shares (review finding: /internal/publish and /internal/presence each carried a
    /// byte-for-byte copy; a third endpoint would have made a third). Validates the
    /// channel's shape (the same rule joins enforce — a name like <c>svc-a:</c> would
    /// otherwise pass ownership and token checks yet name a group that can never have
    /// members), refuses gateway-owned <c>ops:*</c>, resolves the owning service, and
    /// constant-time-matches the presented <c>X-Gateway-Realtime-Token</c>.
    /// Returns either the authorized owner or the failure <see cref="IResult"/> to
    /// short-circuit with; <paramref name="action"/> phrases the 403 (e.g. "publishing
    /// to", "reading presence for").
    /// </summary>
    private static async Task<(ChannelOwner? Owner, IResult? Failure)> AuthorizeOwnerAsync(
        string channel,
        string action,
        HttpContext context,
        IChannelOwnershipResolver ownership,
        CancellationToken ct)
    {
        if (!GatewayHub.IsValidChannel(channel))
        {
            return (null, Results.BadRequest(new
            {
                error = $"Channel '{channel}' must be in '{{service}}:{{topic}}' form.",
            }));
        }

        // ops:* is gateway-owned and never exposed to any service token, on any surface.
        if (GatewayHub.IsOpsChannel(channel))
        {
            return (null, Results.Json(
                new { error = $"Channel '{channel}' is gateway-owned and does not allow {action} it via the internal API." },
                statusCode: StatusCodes.Status403Forbidden));
        }

        var prefix = GatewayHub.PrefixOf(channel);
        var owner = await ownership.ResolveAsync(prefix, ct);
        if (owner is null)
        {
            return (null, Results.Json(
                new { error = $"Channel '{channel}' has no owning service; '{prefix}' is not a known service." },
                statusCode: StatusCodes.Status403Forbidden));
        }

        if (string.IsNullOrEmpty(owner.PublishToken))
        {
            // Pre-migration row (or a service upserted before this column existed): no
            // token has been minted yet, so nothing can be authorized. The next upsert
            // of the service generates one (tech-spec §4.2, task #593).
            return (null, Results.Json(
                new { error = $"Service '{prefix}' has no realtime token yet; re-upsert the service to generate one." },
                statusCode: StatusCodes.Status403Forbidden));
        }

        var presented = context.Request.Headers[RealtimePublishToken.Header].ToString();
        if (!RealtimePublishToken.Matches(presented, owner.PublishToken))
        {
            return (null, Results.Json(
                new { error = $"The {RealtimePublishToken.Header} header does not authorize {action} '{channel}'." },
                statusCode: StatusCodes.Status403Forbidden));
        }

        return (owner, null);
    }
}
