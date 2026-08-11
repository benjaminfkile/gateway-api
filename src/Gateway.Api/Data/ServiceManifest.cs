namespace Gateway.Api.Data;

/// <summary>
/// Desired-state row for a single downstream service (tech-spec §4.4).
/// Source of truth for what each reconciler converges toward; mutated only by
/// the Management API.
/// </summary>
public class ServiceManifest
{
    /// <summary>Service name, e.g. 'svc-a'. Primary key.</summary>
    public string Name { get; set; } = default!;

    /// <summary>Registry repository for the image.</summary>
    public string Image { get; set; } = default!;

    /// <summary>Image tag, 'latest' or a pinned value.</summary>
    public string Tag { get; set; } = default!;

    /// <summary>Resolved sha256 digest, set on deploy. Null until first resolved.</summary>
    public string? Digest { get; set; }

    /// <summary>Container port exposed on the internal Docker network.</summary>
    public int Port { get; set; }

    /// <summary>Desired lifecycle state: 'running' or 'stopped'.</summary>
    public string DesiredStatus { get; set; } = "running";

    /// <summary>Optional secrets-store reference for the container's env.</summary>
    public string? EnvSecretRef { get; set; }

    /// <summary>
    /// Per-service secret that authorizes publishing to this service's real-time
    /// channels (channel prefix == <see cref="Name"/>). A cryptographically random,
    /// url-safe token generated lazily the first time the row is created or upserted
    /// without one (tech-spec §4.2). Injected into the managed container as
    /// <c>GATEWAY_REALTIME_TOKEN</c> and required — constant-time compared — on the
    /// <c>X-Gateway-Realtime-Token</c> header of <c>POST /internal/publish</c>.
    /// <para>
    /// Write-only from the API's perspective: never surfaced in
    /// <c>GET /mgmt/services</c>, like a secret. Null only on pre-migration rows that
    /// have not been upserted since this column was added; publishes to such a
    /// service's channels are rejected until the next upsert mints one.
    /// </para>
    /// </summary>
    public string? RealtimePublishToken { get; set; }

    /// <summary>
    /// Optional path on the service that delegates real-time channel authorization to
    /// the service itself (tech-spec §4.2, task #594 — the Pusher/Ably auth-delegation
    /// pattern). Null means this service's channels are <b>public</b>: any
    /// <c>JoinChannel</c> for a <c>{Name}:{topic}</c> channel is allowed as today.
    /// Non-null means every join triggers an auth callback — the gateway POSTs
    /// <c>{ channel, credential, connectionId }</c> to this path on the service
    /// (resolved through the same host-loopback + learned-host-port mechanism the
    /// health prober uses) and only a <c>200 { allow: true }</c> admits the join. The
    /// gateway never parses the credential; it is an opaque string the service alone
    /// understands. Unlike the publish token this is not a secret, so it is returned by
    /// <c>GET /mgmt/services</c> and settable via the upsert endpoint.
    /// </summary>
    public string? RealtimeAuthPath { get; set; }

    /// <summary>
    /// Optional path on the service that receives messages sent FROM its connected
    /// clients through the hub (tech-spec §4.2, task #611 — the full-duplex companion to
    /// <see cref="RealtimeAuthPath"/>). Null means this feature is <b>off</b> for the
    /// service: a client's <c>SendToChannel</c> to any <c>{Name}:{topic}</c> channel is
    /// rejected. Non-null opts the service in — every <c>SendToChannel</c> the gateway
    /// accepts is POSTed as <c>{ channel, event, data, connectionId, identity }</c> to
    /// this path on the service (resolved through the same host-loopback + learned-host-port
    /// mechanism the auth callback and health prober use). The gateway never broadcasts the
    /// message itself: if the owner wants fan-out it publishes via <c>/internal/publish</c>.
    /// Like the auth path this is not a secret, so it is returned by
    /// <c>GET /mgmt/services</c> and settable via the upsert endpoint (same tri-state
    /// semantics: absent preserves, empty string clears, non-empty sets a rooted path).
    /// </summary>
    public string? RealtimeMessagePath { get; set; }

    /// <summary>
    /// Optional comma-separated list of exact browser origins (e.g.
    /// <c>https://chat.example.com,https://app.example.com</c>) that this service's
    /// frontend negotiates its SignalR connection from (tech-spec §4.2, task #595).
    /// Null/empty means the service contributes no origins. Each entry is an absolute
    /// <c>http</c>/<c>https</c> origin with no path, query, fragment, or wildcard —
    /// validated at upsert and rejected otherwise. The gateway folds the union of every
    /// service's origins (plus the static <c>GATEWAY_CORS_ORIGINS</c> ops-dashboard
    /// origins) into the dynamic CORS policy on <c>/hub</c> only — never <c>/mgmt</c> —
    /// so a consumer app's browser can pass the <c>/hub/negotiate</c> preflight. Not a
    /// secret: returned by <c>GET /mgmt/services</c> and settable via upsert.
    /// </summary>
    public string? RealtimeAllowedOrigins { get; set; }

    /// <summary>
    /// Whether this service has opted in to real-time <b>presence events</b> on its
    /// channels (tech-spec §4.2, task #612). Nullable/tri-state on an upsert: null means
    /// "not set" and is treated as <b>false</b> everywhere (the default), so a minimal
    /// re-upsert never flips it and a pre-migration row reads as off. When true, every
    /// membership change on a <c>{Name}:{topic}</c> channel emits a coalesced
    /// <c>presence</c> event ({ channel, count, joined, left }) to that channel's
    /// subscribers. Off by default because a presence event on a public channel leaks
    /// connection ids (and any owner-supplied identity) to every subscriber, so the owner
    /// must consciously choose it. The <c>GET /internal/presence/{channel}</c> owner API
    /// works regardless of this flag — it is the owner's own token-gated read, not a
    /// broadcast. Not a secret: returned by <c>GET /mgmt/services</c> and settable via upsert.
    /// </summary>
    public bool? RealtimePresence { get; set; }

    /// <summary>Whether this service participates in the aggregated health check.</summary>
    public bool IncludeInHealth { get; set; }

    /// <summary>Cognito username of the last mutator.</summary>
    public string UpdatedBy { get; set; } = default!;

    /// <summary>Timestamp of the last mutation.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// When a fleet-wide restart was last requested for this service, or null if never
    /// (tech-spec §4.5). A running container whose <c>StartedAt</c> predates this stamp
    /// is stale and gets a blue-green recreate; a container started after it satisfies
    /// the request and never triggers a restart loop. Set by <c>POST
    /// /mgmt/services/{name}/restart</c>; the digest/tag are unchanged.
    /// </summary>
    public DateTimeOffset? RestartRequestedAt { get; set; }
}
