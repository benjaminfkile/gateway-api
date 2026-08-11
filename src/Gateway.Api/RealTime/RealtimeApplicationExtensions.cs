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
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Channel))
            {
                return Results.BadRequest(new { error = "channel is required." });
            }

            // Reject a malformed channel with the SAME shape rule joins enforce (task #608
            // finding 3): a name like "svc-a" or "svc-a:" would pass ownership + token
            // checks and 202, then broadcast into a group ValidateChannel guarantees is
            // permanently empty — silent event loss. 400 before it can be published.
            if (!GatewayHub.IsValidChannel(request.Channel))
            {
                return Results.BadRequest(new
                {
                    error = $"Channel '{request.Channel}' must be in '{{service}}:{{topic}}' form.",
                });
            }

            // ops:* is gateway-owned and never publishable through the HTTP passthrough,
            // regardless of any token presented. Gateway-internal ops events ride
            // IChannelEventPublisher directly, bypassing this endpoint entirely.
            if (GatewayHub.IsOpsChannel(request.Channel))
            {
                return Results.Json(
                    new { error = $"Channel '{request.Channel}' is gateway-owned and cannot be published via /internal/publish." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var prefix = GatewayHub.PrefixOf(request.Channel);
            var owner = await ownership.ResolveAsync(prefix, ct);
            if (owner is null)
            {
                return Results.Json(
                    new { error = $"Channel '{request.Channel}' has no owning service; '{prefix}' is not a known service." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            if (string.IsNullOrEmpty(owner.PublishToken))
            {
                // Pre-migration row (or a service upserted before this column existed):
                // no token has been minted yet, so no publish can be authorized. The
                // next upsert of the service generates one (tech-spec §4.2, task #593).
                return Results.Json(
                    new { error = $"Service '{prefix}' has no realtime publish token yet; re-upsert the service to generate one before publishing." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var presented = context.Request.Headers[RealtimePublishToken.Header].ToString();
            if (!RealtimePublishToken.Matches(presented, owner.PublishToken))
            {
                return Results.Json(
                    new { error = $"The {RealtimePublishToken.Header} header does not authorize publishing to '{request.Channel}'." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            await publisher.PublishAsync(request.Channel, request.Event, request.Payload, ct);
            return Results.Accepted();
        });
        return endpoints;
    }
}
