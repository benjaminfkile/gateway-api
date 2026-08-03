using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Gateway.Api.RealTime;

/// <summary>
/// The shared real-time hub (tech-spec §4.2). One hub, mapped at <c>/hub</c>, that
/// every downstream app can fan events out through so none has to implement its
/// own WebSocket handling. Clients subscribe to <c>{app}:{topic}</c> channels via
/// <see cref="JoinChannel"/> / <see cref="LeaveChannel"/>, which map onto SignalR
/// groups; a matching group broadcast (from <c>POST /internal/publish</c> or an
/// <see cref="IHubContext{THub}"/>) reaches every subscriber.
/// <para>
/// The hub does <b>no</b> end-user authentication (design invariant, §1): channels
/// are public broadcast. The sole exception is the dashboard's <c>ops:*</c>
/// channels, which require an authenticated connection. That check runs through
/// the <see cref="OpsChannelPolicy"/> authorization policy — a hook that is
/// satisfied by an authenticated principal today and becomes a real Cognito-JWT
/// gate in the auth task. Until then, anonymous <c>ops:*</c> joins are rejected.
/// </para>
/// </summary>
public sealed class GatewayHub : Hub
{
    /// <summary>Authorization policy gating <c>ops:*</c> channel joins.</summary>
    public const string OpsChannelPolicy = "OpsChannel";

    /// <summary>Prefix marking the authenticated dashboard channels.</summary>
    public const string OpsChannelPrefix = "ops:";

    private readonly IAuthorizationService _authorization;

    public GatewayHub(IAuthorizationService authorization)
    {
        _authorization = authorization;
    }

    /// <summary>
    /// Subscribe the caller to a <c>{app}:{topic}</c> channel. Public for every
    /// channel except <c>ops:*</c>, which requires the connection to satisfy the
    /// <see cref="OpsChannelPolicy"/>. Throws <see cref="HubException"/> on an
    /// invalid channel name or an unauthorized <c>ops:*</c> join.
    /// </summary>
    public async Task JoinChannel(string channel)
    {
        ValidateChannel(channel);
        await AuthorizeChannelAsync(channel);
        await Groups.AddToGroupAsync(Context.ConnectionId, channel);
    }

    /// <summary>Unsubscribe the caller from a channel.</summary>
    public async Task LeaveChannel(string channel)
    {
        ValidateChannel(channel);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, channel);
    }

    /// <summary>True for channel names in the authenticated <c>ops:*</c> namespace.</summary>
    public static bool IsOpsChannel(string channel) =>
        channel.StartsWith(OpsChannelPrefix, StringComparison.Ordinal);

    private async Task AuthorizeChannelAsync(string channel)
    {
        if (!IsOpsChannel(channel))
        {
            return;
        }

        var user = Context.User ?? new ClaimsPrincipal(new ClaimsIdentity());
        var result = await _authorization.AuthorizeAsync(user, resource: null, OpsChannelPolicy);
        if (!result.Succeeded)
        {
            throw new HubException(
                $"Channel '{channel}' requires an authenticated connection.");
        }
    }

    /// <summary>
    /// Enforce the <c>{app}:{topic}</c> shape. Kept deliberately permissive — the
    /// hub is opt-in infrastructure, not an app-level validator — but a missing
    /// <c>:</c> separator or empty segment is rejected so a bad name can never
    /// collide with the <c>ops:</c> namespace check.
    /// </summary>
    private static void ValidateChannel(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
        {
            throw new HubException("Channel name is required.");
        }

        var separator = channel.IndexOf(':');
        if (separator <= 0 || separator == channel.Length - 1)
        {
            throw new HubException(
                $"Channel '{channel}' must be in '{{app}}:{{topic}}' form.");
        }
    }
}
