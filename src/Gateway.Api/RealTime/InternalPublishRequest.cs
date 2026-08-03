using System.Text.Json;

namespace Gateway.Api.RealTime;

/// <summary>
/// Body of <c>POST /internal/publish</c> (tech-spec §4.2): an app choosing to be
/// gateway-aware asks the hub to fan <paramref name="Payload"/> out to every
/// subscriber of <paramref name="Channel"/> as a message of type
/// <paramref name="Event"/>. The payload is passed through verbatim as opaque
/// JSON — the gateway never inspects it.
/// </summary>
/// <param name="Channel">Target <c>{app}:{topic}</c> channel (SignalR group).</param>
/// <param name="Event">SignalR message name delivered to clients.</param>
/// <param name="Payload">Opaque JSON payload broadcast to the group.</param>
public sealed record InternalPublishRequest(
    string Channel,
    string Event,
    JsonElement Payload);
