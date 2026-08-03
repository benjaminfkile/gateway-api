using Gateway.Api.RealTime;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Gateway.Api.Auth;

/// <summary>
/// Registration for the management-plane authentication and authorization
/// (tech-spec §5). Wires the Cognito JWT bearer scheme and the ops authorization
/// policies (<see cref="ManagementAuth.OpsAdminPolicy"/>,
/// <see cref="ManagementAuth.OpsDeployPolicy"/>) plus the real
/// <see cref="GatewayHub.OpsChannelPolicy"/> requirement for the dashboard's
/// <c>ops:*</c> hub channels.
/// <para>
/// The bearer scheme is registered unconditionally so the pipeline is identical
/// whether or not the plane is configured — but the JWKS-backed authority is only
/// set when <c>GATEWAY_COGNITO_AUTHORITY</c> is present. Unconfigured, no token can
/// validate; <c>/mgmt/*</c> is short-circuited to 503 by the plane guard before
/// authentication even runs. Options are resolved from <see cref="IConfiguration"/>
/// via DI (not read eagerly), so a test host can override the authority and signing
/// key.
/// </para>
/// </summary>
public static class ManagementAuthServiceCollectionExtensions
{
    public static IServiceCollection AddManagementAuthentication(this IServiceCollection services)
    {
        // Resolved lazily from the container's IConfiguration so late-bound test
        // configuration (WebApplicationFactory) is honored.
        services.TryAddSingleton(sp =>
            ManagementAuthOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer();

        // Configure the bearer scheme from the resolved ManagementAuthOptions. This
        // runs when the options are first materialized (at authentication time),
        // after the host — and any test overrides — has finished building config.
        services
            .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<ManagementAuthOptions>((jwt, auth) =>
            {
                // Keep Cognito's own claim names (cognito:groups, scope, token_use)
                // intact — the policies below read them verbatim.
                jwt.MapInboundClaims = false;

                if (auth.IsConfigured)
                {
                    // Authority drives OpenID metadata + JWKS discovery for signature
                    // validation. Only set when configured so an unconfigured gateway
                    // never reaches out to Cognito.
                    jwt.Authority = auth.Authority;
                }

                // Validate issuer, signature (via JWKS), and expiry (§5). Cognito
                // access tokens carry no `aud`, so audience is not validated; the
                // token_use=access requirement is enforced in OnTokenValidated below.
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = auth.IsConfigured,
                    ValidIssuer = auth.Authority,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                };

                jwt.Events = new JwtBearerEvents
                {
                    // SignalR's WebSocket transport cannot set an Authorization
                    // header, so its client passes the access token as the
                    // `access_token` query string. Honor it only on the hub path
                    // (§4.2) — this authenticates the gateway's own dashboard
                    // client and touches no downstream traffic.
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(accessToken)
                            && context.Request.Path.StartsWithSegments("/hub"))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        // Reject id tokens and anything that is not an access token
                        // (§5): the management plane accepts access tokens only.
                        var tokenUse = context.Principal?
                            .FindFirst(ManagementAuth.TokenUseClaim)?.Value;
                        if (!string.Equals(tokenUse, ManagementAuth.AccessTokenUse, StringComparison.Ordinal))
                        {
                            context.Fail("token_use must be 'access'.");
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        // Ops authorization policies (§5). Defined here — not in the realtime
        // module — so the ops:* channel gate shares the exact group requirement the
        // /mgmt endpoints use. AddAuthorization is additive across calls.
        services.AddAuthorization(authz =>
        {
            authz.AddPolicy(ManagementAuth.OpsAdminPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ManagementAuth.IsOpsAdmin(ctx.User)));

            authz.AddPolicy(ManagementAuth.OpsDeployPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ManagementAuth.CanDeploy(ctx.User)));

            // The dashboard's ops:* hub channels: authenticate the gateway's own
            // client and require a human ops group (§4.2). This replaces the
            // placeholder "any authenticated user" hook from the hub task.
            authz.AddPolicy(GatewayHub.OpsChannelPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => ManagementAuth.CanUseOpsChannel(ctx.User)));
        });

        return services;
    }
}
