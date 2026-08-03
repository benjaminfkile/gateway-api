using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
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
        endpoints.MapHub<GatewayHub>("/hub");
        return endpoints;
    }

    /// <summary>
    /// Map the internal publish endpoint (tech-spec §4.2). Broadcasts the request
    /// payload to the channel's SignalR group as a message of type
    /// <c>{event}</c> and returns <c>202 Accepted</c>. The isolation middleware
    /// guarantees this is reachable only on the internal listener.
    /// </summary>
    public static IEndpointRouteBuilder MapInternalPublish(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/internal/publish", async (
            InternalPublishRequest request,
            IHubContext<GatewayHub> hub,
            CancellationToken ct) =>
        {
            await hub.Clients.Group(request.Channel).SendAsync(request.Event, request.Payload, ct);
            return Results.Accepted();
        });
        return endpoints;
    }
}
