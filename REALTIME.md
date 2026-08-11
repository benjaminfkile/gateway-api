# Realtime guide for downstream services

This is the complete, self-contained guide for adding realtime features to an app
that runs behind the gateway. You do **not** need to read the gateway source to
follow it — everything the gateway promises your app is on this page.

Throughout, replace the placeholders with your own values:

- `https://gateway.example.com` — the gateway's public base URL (WebSockets use
  `wss://gateway.example.com/hub`).
- `chat-api` — the sample downstream service name. Your service name is whatever
  you registered in the manifest (`PUT /mgmt/services/{name}`); it is also your
  channel namespace.

> **Realtime is strictly opt-in.** A service that never publishes and never asks
> its clients to connect to `/hub` is unaffected by any of this.

---

## 1. Concepts

### Channels are `{service}:{topic}`

Every channel name has the form `{service}:{topic}` — for example
`chat-api:room-42` or `chat-api:notifications`. The part before the first `:` is
the **prefix**, and the prefix must be the name of a real manifest service. That
service **owns** the channel: only it may publish to `chat-api:*`, and CORS and
private-channel auth for `chat-api:*` are configured on the `chat-api` manifest
entry. **Your service name is your namespace** — you can invent any topic you
like under it, no registration per channel.

The prefix `ops` is reserved for the gateway's own dashboard (`ops:*` channels,
gated by an operator login). You cannot publish to `ops:*`, and the service
names `gateway`, `hub`, and `internal` are reserved and cannot be registered.

Channel names must be non-empty and contain a non-empty segment on each side of a
single `:` — a name with no `:`, an empty prefix, or an empty topic is rejected.

### The gateway fans out across all its instances

The gateway runs as a fleet of instances behind a load balancer. When you
publish once, the gateway's backplane (a Redis pub/sub bus) relays your event to
every gateway instance, so a subscriber reaches **any** instance and still gets
your event. You publish once; you do not care which instance a browser landed on.

### Delivery is fire-and-forget — AT MOST ONCE

This is the single most important property to design around:

- **At most once.** An event is delivered to whoever is connected and subscribed
  at that instant, and to nobody else.
- **No replay.** A client that was disconnected, still reconnecting, or had not
  yet re-joined the channel when you published simply never sees that event.
- **No ordering guarantee across reconnects.** After a drop-and-reconnect there
  is no promise that the client resumes where it left off, or that events arrive
  in the order you published them.
- **Best-effort.** If the backplane is degraded at the moment you publish, the
  event is silently dropped (see §4). Nothing is queued for later.

### The golden rule: events are hints, fetch is truth

Treat every realtime event as a **hint that something changed**, never as the
authoritative copy of the change. The source of truth is always your own HTTP
API. Concretely:

- On connect and after every reconnect, **fetch current state over HTTP** and
  render that. Then let events nudge you toward smaller follow-up fetches.
- Never accumulate application state purely from the event stream — a single
  missed event would leave you permanently wrong.
- An event's `data` is a convenience payload, not a guarantee. If it carries an
  id, the safe pattern is "event says record 42 changed → re-fetch record 42."

Design as if any given event may be missed, and reconcile via your API. If you
follow this rule, at-most-once delivery is an optimization, not a correctness
risk.

---

## 2. Receiving events in a browser

The gateway hosts one SignalR hub at `/hub`. Use the official
[`@microsoft/signalr`](https://www.npmjs.com/package/@microsoft/signalr) client.

### Connect

```js
import * as signalR from "@microsoft/signalr";

const connection = new signalR.HubConnectionBuilder()
  .withUrl("https://gateway.example.com/hub")
  .withAutomaticReconnect()
  .build();
```

Public channels require **no** credential at connect time — the hub performs no
end-user authentication for downstream channels (private channels are authorized
per-join, see §3).

### One client method: `ChannelEvent`

The gateway sends exactly **one** SignalR client method, named `ChannelEvent`.
Its single argument is an envelope:

```json
{ "channel": "chat-api:room-42", "event": "messagePosted", "data": { "...": "..." } }
```

- `channel` — the channel the event was published to.
- `event` — your event name (whatever you published it under).
- `data` — your payload, passed through verbatim.

Register one handler and route on the envelope — you do **not** register a
handler per event type:

```js
connection.on("ChannelEvent", (envelope) => {
  const { channel, event, data } = envelope;
  if (channel === "chat-api:room-42" && event === "messagePosted") {
    // treat as a hint — reconcile via your HTTP API (see the golden rule)
  }
});
```

### Subscribe: `JoinChannel` / `LeaveChannel`

Group membership is what makes you receive a channel's events. Join after the
connection starts:

```js
await connection.start();
await connection.invoke("JoinChannel", "chat-api:room-42");
// later:
await connection.invoke("LeaveChannel", "chat-api:room-42");
```

`JoinChannel` throws (the invoke promise rejects) if the channel name is
malformed, if the prefix is not a known service, or if the channel is private and
authorization is denied (see §3). Catch it and surface a sensible UI state rather
than letting it go unhandled.

### Re-join after every reconnect

**Channel membership lives on the connection id and dies with it.** When the
transport drops and `@microsoft/signalr` reconnects, it establishes a **new**
connection id — and the new connection is a member of **no** channels. Any
groups you joined on the old connection are gone. If you do not re-join, the
connection stays open and healthy but silently receives nothing.

Re-join on every (re)connect, and — per the golden rule — re-fetch state over
HTTP at the same time, because events published during the gap are lost:

```js
async function joinAll() {
  await connection.invoke("JoinChannel", "chat-api:room-42");
  await refreshFromHttpApi(); // events are hints; fetch is truth
}

connection.onreconnected(joinAll); // fires after each automatic reconnect
await connection.start();
await joinAll();                    // and once for the initial connection
```

The gateway can also drop you from a **single** private channel mid-connection, without the
transport reconnecting, when your delegated-auth allow lapses — it sends a `channelEvicted`
event on that channel (see §3). Handle it the same way: re-join that channel with a fresh
credential and re-fetch its state over HTTP.

### CORS prerequisite

A browser will only be allowed to open the WebSocket to `/hub` if its page origin
is on your service's allow-list. Set `realtime_allowed_origins` on your manifest
entry (API field `realtimeAllowedOrigins` on `PUT /mgmt/services/{name}`) to a
comma-separated list of **exact** origins:

```
realtimeAllowedOrigins = "https://app.example.com,https://admin.example.com"
```

Rules the gateway enforces on this list:

- Each entry must be an absolute `http`/`https` origin: `scheme://host[:port]`
  with **no path, query, fragment, userinfo, or wildcard**. Default ports
  (`80`/`443`) may be omitted.
- Matching is exact and byte-for-byte against the browser's `Origin` header
  (a wildcard is rejected outright, because the hub uses credentialed CORS which
  forbids `*`).
- A manifest CORS change takes effect within a short cache window — no gateway
  restart needed.
- **Upsert is tri-state on this field.** Omitting `realtimeAllowedOrigins` from a
  `PUT` **preserves** the stored allow-list — a minimal re-upsert of only
  `{image, tag, port}` (e.g. from a pre-phase-2 CI pipeline) never silently wipes
  it. Send an **empty string** to explicitly clear it; send a non-empty value to
  replace it.

If your origin is not listed, the browser's preflight/negotiate fails before any
hub method runs (see the FAQ).

---

## 3. Public vs private channels

**Channels are public by default.** Anyone who can reach `/hub` from an allowed
origin can `JoinChannel("chat-api:anything")` and receive its events. That is the
right model for public/live feeds.

To make **all** of your service's channels private, set `realtime_auth_path` on
your manifest entry (API field `realtimeAuthPath` on `PUT /mgmt/services/{name}`)
to a rooted path on your own service, e.g. `/realtime/authorize`. Opting in flips
every `chat-api:*` channel to delegated auth: each join is authorized by **your**
service, not the gateway.

Like `realtimeAllowedOrigins`, this field is **tri-state on upsert**: omitting
`realtimeAuthPath` from a `PUT` **preserves** the stored path (so a minimal
`{image, tag, port}` re-upsert can never silently flip your private channels back
to public), an **empty string** clears it (channels become public again), and a
non-empty rooted path replaces it.

### The delegated-auth callback contract

When a client joins a private channel, the gateway calls your `realtime_auth_path`
and admits the join only if you allow it. Exact contract:

- **Request.** The gateway sends `POST {your service}{realtime_auth_path}` with a
  JSON body:

  ```json
  { "channel": "chat-api:room-42", "credential": "<opaque>", "connectionId": "<signalr-connection-id>" }
  ```

- **The credential is yours.** It is whatever your app handed to your own client
  to present on join (see below). **The gateway never inspects or validates it** —
  it forwards it verbatim. Its meaning is entirely up to you (a signed token, a
  session id, anything).
- **Response to allow.** Reply **HTTP 200** with `{ "allow": true }`. You may add
  an `"identity"` string (`{ "allow": true, "identity": "user-123" }`); it is
  carried with the decision for future use.
- **Anything else is a deny.** A non-200 status, a 200 with `{ "allow": false }`,
  a malformed body, an **unreachable service**, or **no answer within 2 seconds**
  all deny the join. Fail-closed by design.
- **The client presents a credential with `JoinPrivateChannel`.** The plain
  one-argument `JoinChannel("chat-api:room-42")` still works against a private
  channel but arrives at your callback with a `null` credential (so you can deny
  it). To pass a credential, call the two-argument method:

  ```js
  await connection.invoke("JoinPrivateChannel", "chat-api:room-42", myCredential);
  ```

  (SignalR binds hub methods by exact argument count, which is why there are two
  method names rather than one optional parameter.)

- **A denied join throws** a single generic error on the client
  (`"Not authorized to join this channel."`) that reveals nothing about why or
  whether the channel exists.
- **Caching and eviction.** An *allow* is cached per `(connection, channel)` for a finite
  window (~15 minutes). That window is now the **lifetime of access**, not just of re-join
  authorization: once an allow lapses, the gateway's periodic sweep (about once a minute)
  **removes that connection from the channel** and sends it a `channelEvicted` event (see
  below) so a well-behaved client re-joins with a fresh credential. So an admitted client
  whose allow is not renewed stops receiving a private channel's events within roughly the
  allow window plus one sweep interval — you no longer have to force a disconnect to cut off
  access. Your callback must work idempotently and expect to be called again (after an
  eviction, on any reconnect, or when a client presents a new credential); a reconnect gets a
  new connection id and re-authorizes from scratch. If you must cut a client off *faster*
  than the sweep, revoke on your side AND force the disconnect (the gateway also closes a
  connection when its Cognito-level token expires, but your app-level credential is opaque to
  it). A *deny* is cached only briefly (~10s) and is keyed to the exact credential that was
  rejected: a client that immediately retries with a **different, now-valid** credential (the
  normal token-refresh flow) reaches your callback again rather than being blocked for the
  deny window.
- **The `channelEvicted` event.** When the gateway evicts your connection from a channel it
  sends a normal `ChannelEvent` envelope on that channel with `event: "channelEvicted"` and
  `data: { channel, reason }`:

  ```json
  { "channel": "chat-api:room-42", "event": "channelEvicted",
    "data": { "channel": "chat-api:room-42", "reason": "auth_expired" } }
  ```

  `reason` is `"auth_expired"` (your allow lapsed — re-join with a fresh credential) or
  `"service_removed"` (the channel's owning service was removed from the manifest — re-joining
  will fail until it returns). The resilient client's rejoin machinery handles the common
  `auth_expired` case automatically; if you route the envelope yourself, treat `channelEvicted`
  as "you are no longer subscribed" and re-join (with a fresh credential) rather than assuming
  you still receive the channel. Public channels are never evicted (they have no auth to
  expire), and the dashboard's `ops:*` channels are unaffected (they are Cognito-gated, not
  delegated-auth).

### Worked example: a minimal Express auth endpoint

```js
// chat-api: private-channel authorization callback
// Registered in the manifest as realtime_auth_path = "/realtime/authorize"
const express = require("express");
const app = express();
app.use(express.json());

app.post("/realtime/authorize", (req, res) => {
  const { channel, credential, connectionId } = req.body;

  // `credential` is whatever YOUR client presented via JoinPrivateChannel.
  // Validate it however your app wants (verify a signed token, look up a
  // session, check the user may see this channel's topic, etc.).
  const user = verifyMyOwnCredential(credential); // your logic
  const roomId = channel.split(":")[1];

  if (user && userMaySee(user, roomId)) {
    return res.status(200).json({ allow: true, identity: user.id });
  }
  // Anything that is not 200 { allow: true } denies. Be quick — you have 2s.
  return res.status(200).json({ allow: false });
});
```

The gateway reaches this endpoint over the internal Docker network the same way
it health-checks your container, so you do not expose it publicly or route it
yourself — just handle the path inside your app.

---

## 4. Publishing from your container

Your container publishes events by POSTing to the gateway's **internal listener**,
which is reachable from the Docker bridge network only and is never exposed
through the load balancer.

### The request

```
POST http://gateway:8080/internal/publish
X-Gateway-Realtime-Token: <your token>
Content-Type: application/json

{ "channel": "chat-api:room-42", "event": "messagePosted", "payload": { "id": 91234 } }
```

- **Endpoint:** `POST /internal/publish` on the internal listener
  (`http://gateway:8080` on the Docker network by default). It is not on the
  public `/hub` host.
- **Header:** `X-Gateway-Realtime-Token`. The gateway injects your service's
  publish token into your container as the environment variable
  `GATEWAY_REALTIME_TOKEN` — read it from the environment and send it verbatim.
  The gateway compares it in constant time.
- **Body fields:**
  - `channel` — a `chat-api:{topic}` channel your service owns.
  - `event` — your event name; delivered to clients as the envelope's `event`.
  - `payload` — opaque JSON, passed through untouched.
- **Note the field rename on the wire.** You send `payload`; the browser receives
  it as `data`. A publish of `{ channel, event, payload }` arrives at the client
  as the envelope `{ channel, event, data }` (§2). Same bytes, different key.
- **Success:** `202 Accepted`, no body.

### Owner-only rule

You may publish **only** to channels whose prefix is your own service name. The
gateway resolves the channel prefix to the owning manifest service and requires
your token to match **that** service's token. Publishing to another service's
prefix returns `403`. Common `403` causes:

- The prefix is not a known service.
- Your service has no publish token yet (it is minted on first upsert — re-upsert
  the service to generate one, then read the refreshed `GATEWAY_REALTIME_TOKEN`).
- The `X-Gateway-Realtime-Token` header is missing or wrong.

### `ops:*` is off-limits

`ops:*` channels belong to the gateway's own dashboard and can **never** be
published through `/internal/publish` — any such request is `403` regardless of
the token presented.

### Publishes are dropped while the backplane is degraded

The internal publish path is **best-effort**. If the gateway's backplane is
degraded at the moment you publish, the event is **silently dropped** — you may
still get a `202`, and nothing is queued or retried. This is deliberate: a
backplane blip must never turn your already-committed state change into an error.

**Design for it.** Never make a publish the only way a client learns about a
change. Publish *after* you have durably committed the change, and rely on your
clients' reconcile-via-HTTP behavior (the golden rule) to close any gap. A missed
publish should degrade to "the UI updates a few seconds late on the next fetch,"
never to "the UI is permanently wrong."

---

## 5. Receiving messages from clients (full-duplex)

Everything above is one-directional: your container publishes, browsers receive.
The hub can also carry messages the **other** way — from a connected client up to
your service — without the gateway learning any of your app's semantics. This is
opt-in per service and off by default.

### Turn it on: `realtime_message_path`

Set `realtime_message_path` on your manifest entry (API field
`realtimeMessagePath` on `PUT /mgmt/services/{name}`) to a **rooted path** on your
service that will receive client messages:

```
realtimeMessagePath = "/realtime/message"
```

- Same validation as `realtime_auth_path`: it must begin with `/` (a bare path, not
  an absolute URL), and it is **tri-state on upsert** — omitting the field
  **preserves** the stored value, an **empty string** clears it (turning the
  feature off), and a non-empty value sets it. A minimal `{image, tag, port}`
  re-upsert never silently disables it.
- Not a secret: it is returned by `GET /mgmt/services`.
- While it is null, any client `SendToChannel` to one of your channels is rejected.

### The client method: `SendToChannel`

```js
await connection.invoke("SendToChannel", "chat-api:room-42", "typing", { userId: 7 });
```

`SendToChannel(channel, event, data)` asks the gateway to hand your service one
message. The gateway checks, **in order**:

1. **Membership.** The connection must already be joined to `channel` (via
   `JoinChannel`/`JoinPrivateChannel`) and not have left it. A send to a channel
   you never joined is rejected with the same generic "not authorized" message a
   denied join uses — join first.
2. **Opt-in.** The channel's owning service must have `realtime_message_path`
   configured, or the send is rejected with a distinct "does not accept client
   messages" error.
3. **Rate limit.** Each connection has a token-bucket budget (default **10
   messages/second, burst 20**, shared across *all* its channels). Over budget is
   rejected with a throttled error — back off and retry.
4. **Size.** The payload rides SignalR's 32 KB receive cap (§7). Keep it small.

If all four pass, the gateway POSTs the message to your `realtime_message_path`.

### What your service receives

The gateway `POST`s this JSON to your `realtime_message_path` (reached on the
Docker network exactly as the auth callback is — no public exposure):

```
POST http://<your-service>/realtime/message
Content-Type: application/json

{
  "channel": "chat-api:room-42",
  "event": "typing",
  "data": { "userId": 7 },
  "connectionId": "abc123…",
  "identity": "user-7"
}
```

- `channel`, `event`, `data` — exactly what the client passed to `SendToChannel`.
- `connectionId` — the sender's hub connection id (opaque; useful for correlation).
- `identity` — the string **your own auth callback** returned when this connection
  joined the channel (§3). It is `null` for public channels (no auth callback ran).
  This is how you know *who* sent the message without the gateway ever
  understanding your credentials.

**Delivery is fire-and-forget toward the client.** The gateway ignores your
response **body**; it only looks at the status code, with a **5-second** timeout.
A `2xx` means "accepted" and the client's `invoke` resolves. A **non-2xx or a
timeout** is logged and surfaced to the sending client as a thrown hub error, so
the sender knows delivery failed — but nothing is retried.

### The gateway never broadcasts your client's message — you do

This is the crucial rule. When a client calls `SendToChannel`, the gateway hands
the message **only to your service**. It does **not** fan it out to the channel's
other subscribers. If you want other clients to see it, **you** publish it back
out via `POST /internal/publish` (§4) — after you have validated, stored, and
shaped it however your app needs.

This keeps the gateway ignorant of your semantics and puts you in control: you
decide what is persisted, what is rebroadcast, to which channel, and in what form.

### Worked example: a chat message round-trip

1. **Client sends.** A browser in room 42 invokes
   `SendToChannel("chat-api:room-42", "message", { text: "hi" })`.
2. **Gateway forwards.** The gateway POSTs
   `{ channel: "chat-api:room-42", event: "message", data: { text: "hi" },
   connectionId, identity: "user-7" }` to your `realtime_message_path`.
3. **Your service decides.** Your handler authenticates via `identity`, validates
   and **persists** the message (assigning it an id, timestamp, etc.), and returns
   `202`. The client's `invoke` resolves.
4. **Your service fans out.** To let everyone in the room see it, your service now
   `POST`s to `/internal/publish`:

   ```
   POST http://gateway:8080/internal/publish
   X-Gateway-Realtime-Token: <your token>

   { "channel": "chat-api:room-42", "event": "messagePosted",
     "payload": { "id": 91234, "from": "user-7" } }
   ```

5. **Everyone receives.** Every subscriber of `chat-api:room-42` (including the
   original sender) gets the `ChannelEvent` envelope and — per the golden rule —
   re-fetches the message by id.

The inbound leg (`SendToChannel` → your service) and the outbound leg (your
service → `/internal/publish` → all clients) are deliberately separate. The
gateway never short-circuits them into a direct client-to-client broadcast.

---

## 6. Presence: who is in a channel

Presence answers "who is currently in this channel" without your service keeping
its own connection bookkeeping. It has two independent surfaces: an **owner API**
you pull on demand, and optional **presence events** the gateway broadcasts to a
channel's subscribers on every membership change. The registry underneath is
workload-agnostic — the gateway tracks it for every channel; you choose how to
consume it.

### The identity string comes from your auth callback

Each presence row is `{ connectionId, identity, joinedAt }`. `identity` is exactly
the opaque string your **delegated-auth callback** returned when it admitted the
join (§3 — the `identity` field of `{ "allow": true, "identity": "..." }`). For a
**public** channel (no `realtime_auth_path`) or an `ops:*` channel there is no
callback, so `identity` is `null`. The gateway never invents or parses it — it is
your value, echoed back to you.

### Owner API: `GET /internal/presence/{channel}`

Pull the current members of one of your channels from the internal listener:

```
GET http://gateway:8080/internal/presence/chat-api:room-42
X-Gateway-Realtime-Token: <your token>
```

Guarded by the **same** owner-token check as `/internal/publish` (§4): the
`X-Gateway-Realtime-Token` header must match the publish token of the service that
owns the channel's prefix. `ops:*` channels are gateway-owned and never queryable.
A `200` returns:

```json
{
  "channel": "chat-api:room-42",
  "count": 2,
  "members": [
    { "connectionId": "abc123", "identity": "user-7", "joinedAt": "2026-08-11T12:00:00Z" },
    { "connectionId": "def456", "identity": "user-9", "joinedAt": "2026-08-11T12:00:03Z" }
  ]
}
```

This read is available **regardless** of the presence-event opt-in below — it is
your own token-gated data, not a broadcast. Use it to render "who's online" on
first load and to **reconcile** whenever you need ground truth.

### Presence events (opt-in): `event: "presence"`

When your service opts in, the gateway broadcasts a `presence` event **on the same
channel** every time membership changes:

```jsonc
// inside the standard ChannelEvent envelope: { channel, event: "presence", data }
{
  "channel": "chat-api:room-42",
  "count": 3,
  "joined": [ { "connectionId": "def456", "identity": "user-9" } ],
  "left":   [ "abc123" ]
}
```

`count` is the channel's current member count; `joined` lists the connections that
arrived (with their identity) and `left` the connection ids that departed, since
the last event.

**Opt-in per service.** Off by default. A presence event on a channel is delivered
to **every subscriber**, so it exposes connection ids and any identity you attached
to the whole channel audience. You must consciously enable it by setting the
`realtime_presence` flag on your manifest entry (tri-state on upsert, like the
other realtime fields: absent preserves, `true`/`false` sets). Leave it off unless
your channel's subscribers are entitled to see each other. The owner API is
unaffected either way.

**Coalescing.** Membership changes are collapsed per channel behind a short (~1s)
window: a burst of joins/leaves emits **one** `presence` event carrying all the
deltas, not one event per change. A connection that joins and leaves inside the
same window nets out and is never announced.

### Single-instance vs Redis

- **Single instance (default, `GATEWAY_REDIS_ENDPOINT` unset).** Presence is an
  in-process map — one instance sees every connection, so its view is complete.
  Rows are removed the moment a connection disconnects.
- **Multi-instance (Redis backplane configured).** Presence is unioned across the
  fleet in Redis, so the owner API and the count span every instance. Because a
  connection is pinned to one instance, a crashed instance's rows are aged out by a
  background reaper within a short staleness window (they may briefly linger before
  the sweep).

### Presence is best-effort

Presence events are **at-most-once**, exactly like every other hub event (§1): a
degraded backplane, a reconnect, or a lost frame can drop one, and there is no
replay. Treat the events as fast hints and **the owner API as the truth** — render
who's-online from `GET /internal/presence/{channel}` on load and after any gap, and
let `presence` events drive live updates in between. Never treat a missed `left`
event as proof a user is still present; reconcile against the API.

---

## 7. Limits and operational notes

### Current limits

- **Message size: 32 KB.** The hub keeps SignalR's default maximum receive
  message size (32,768 bytes). This caps both `SendToChannel` payloads (§5) and,
  by symmetry, what you should publish. Keep payloads small — remember they are
  hints, so prefer sending an id the client re-fetches over embedding a large
  object.
- **Client message rate (`SendToChannel`): 10 msg/s, burst 20, per connection.**
  A token bucket shared across *all* a connection's channels; over budget throws a
  throttled error (§5). Tunable per gateway via `GATEWAY_REALTIME_MSG_RATE` /
  `GATEWAY_REALTIME_MSG_BURST`.
- **Publish rate (`/internal/publish`): 50/s, burst 100, per service.** A token
  bucket keyed on the owning service; over budget returns `429` with a
  `Retry-After` header. Tunable via `GATEWAY_REALTIME_PUBLISH_RATE` /
  `GATEWAY_REALTIME_PUBLISH_BURST`. Still self-limit genuinely chatty event
  sources — the throttle is a backstop, not a queue.
- **Private-channel join auth callbacks** are additionally capped per
  connection+channel (a handful of attempts per ~10s window, plus at most one
  in-flight callback per connection), so a credential-guessing loop cannot hammer
  your auth endpoint through the gateway.
- **Rate limits are per gateway instance.** A connection lives on exactly one
  instance and a publish is limited by whichever instance served it, so the
  buckets above are instance-local (not fleet-wide) — sized generously so a
  well-behaved app never notices.
- **Presence is opt-in and best-effort.** "Who's online" is a first-class
  capability now (§6): pull the owner API `GET /internal/presence/{channel}` any
  time, and optionally enable coalesced `presence` events with the
  `realtime_presence` manifest flag. Events are at-most-once — reconcile against
  the API, never treat a missed `left` as ground truth.

### Recommended polling-floor pattern for critical UI

Because delivery is at-most-once with no replay, **any UI that must not silently
go stale needs a polling floor** underneath the event stream. The gateway's own
dashboard uses exactly this shape and you should mirror it:

- The gateway continuously emits a periodic **heartbeat** event on its `ops:fleet`
  channel. The dashboard treats the *arrival* of heartbeats as a liveness signal:
  events keep the view fresh in real time, but the client also **polls the HTTP
  API on a slow floor** (e.g. every 15–30 seconds) regardless of events.
- Adopt the same **heartbeat-liveness** approach for your own critical views:
  1. Render from an HTTP fetch on connect and after every reconnect.
  2. Let `ChannelEvent`s drive fast, targeted refreshes in between.
  3. Keep a slow background poll running as a floor, so even a run of missed
     events (or a quiet degraded backplane) self-heals within one poll interval.
  4. Optionally publish your own periodic heartbeat event so clients can detect a
     stalled stream and fall back to the poll sooner.

Real-time is the fast path; the poll is the safety net. Together they give you a
live UI that can never get permanently stuck.

### FAQ

**Why did my `JoinChannel` throw?**
One of: the channel name is malformed (must be `{service}:{topic}`, both segments
non-empty); the prefix is not a registered service; the channel is `ops:*` and
your connection is not an authenticated operator; or the channel is private
(`realtime_auth_path` is set) and your auth callback denied the join (returned
anything other than `200 { "allow": true }`, or did not answer within 2 seconds).
Denied private-channel joins always surface the same generic message.

**Why do events stop after a reconnect?**
Channel membership is bound to the connection id and is discarded when the
connection drops. After an automatic reconnect you have a brand-new connection id
that belongs to no channels. Re-join every channel in `onreconnected` (and on the
initial `start`), and re-fetch state over HTTP because anything published during
the gap was not delivered.

**Why does my origin fail preflight?**
The browser origin is not in your service's `realtime_allowed_origins`. The hub
uses credentialed CORS with **exact** origin matching, so the entry must match
your page's origin byte-for-byte (`scheme://host[:port]`, no path, no trailing
slash, no wildcard). Add the exact origin to your manifest entry; the change
takes effect within a short cache window without restarting the gateway.
