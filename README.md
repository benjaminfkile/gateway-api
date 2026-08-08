# gateway-api

A self-hosted application gateway that is also the **node agent** for the VM it
runs on: YARP edge proxy, opt-in SignalR real-time hub, Docker reconciler
driven by a Postgres desired-state manifest, and a Cognito-protected (MFA)
management API + ops dashboard. Deploy downstream services in seconds with
blue-green cutover — no instance replacement — whether you run one box or a
fleet of twenty behind a load balancer.

**Read the tech spec first: [`docs/tech-spec.md`](docs/tech-spec.md).** The
design invariant in §1 (downstream applications must never be required to be
gateway-aware) is non-negotiable.

## Layout

- `src/Gateway.Api` — the gateway (ASP.NET Core, .NET 10, publishes self-contained single-file)
- `tests/Gateway.Api.Tests` — xUnit test suite

## Build & run

```bash
dotnet build
dotnet run --project src/Gateway.Api   # serves /api/health
dotnet test
```

### Proxy-only dev mode (no database)

With `GATEWAY_DB_CONNECTION` unset the gateway boots with an in-memory manifest
seeded from the `Manifest` section of `appsettings.Development.json`, so you can
develop the proxy without Postgres, Docker, or AWS:

```json
{
  "Manifest": [
    {
      "Name": "svc-a",
      "Image": "registry/svc-a",
      "Tag": "latest",
      "Port": 3001,
      "DesiredStatus": "running",
      "IncludeInHealth": true,
      "UpdatedBy": "dev",
      "UpdatedAt": "2026-01-01T00:00:00Z"
    }
  ]
}
```

Requests to `/svc-a/*` proxy to `http://127.0.0.1:3001`; `/mgmt/*` returns 503
until a Cognito authority is configured.

`Port` is the **container-internal port only** — the port the app binds inside its
container (Kubernetes `containerPort` semantics), never a fixed host port. In
production the reconciler publishes it with an unassigned host binding and Docker
picks a unique ephemeral host port; the gateway forwards to that real, dynamic
port (surfaced as `hostPort` on `GET /mgmt/services`). Because host ports are
Docker-assigned, two services may share the same `Port`. In proxy-only dev mode
there is no reconciler, so the gateway falls back to `Port` as the host port —
run your app locally on it.

## Configuration

All settings are environment variables (or equivalent configuration keys); every
feature degrades gracefully when its variable is unset, so a bare
`dotnet run` always boots.

| Variable | Default | Purpose |
|---|---|---|
| `GATEWAY_DB_CONNECTION` | unset | Postgres connection string. Set → EF Core migrations are applied automatically on boot (see below), then DB-backed features (manifest, fleet status, deploy history, leader election) are active. Unset → in-memory manifest, all DB features inactive, zero DB traffic. |
| `GATEWAY_RECONCILER_ENABLED` | `false` | Enable the node reconciler (requires the Docker socket). |
| `GATEWAY_BOOTSTRAP_ENABLED` | `false` | Run the idempotent node-bootstrap pipeline at startup. |
| `GATEWAY_COGNITO_AUTHORITY` | unset | Cognito issuer URL for the management plane. Unset → `/mgmt/*` returns 503. |
| `GATEWAY_REDIS_ENDPOINT` | unset | Redis endpoint for the SignalR backplane. Unset → hub runs without a backplane (single instance). |
| `GATEWAY_REDIS_SSL` | `true` | TLS for the Redis backplane connection. |
| `GATEWAY_CORS_ORIGINS` | unset | Comma-separated origins allowed CORS access to `/mgmt/*` and `/hub` (the ops dashboard's origin). Unset → no CORS; proxied application traffic is never CORS-handled either way. |
| `GATEWAY_INTERNAL_BIND` | `0.0.0.0:8080` | Bind address of the internal listener hosting `POST /internal/publish` (never routed by the load balancer). |
| `GATEWAY_INSTANCE_ID` | unset | Instance identity fallback when EC2 IMDS is unreachable (local dev). |
| `GATEWAY_PRIVATE_IP` | unset | Private IP fallback for local dev. |
| `GATEWAY_PUBLIC_IP` | unset | Public IP fallback for local dev. |
| `GATEWAY_LOG_DRIVER` | unset | Container log-driver escape hatch. Set to `json-file` to log downstream containers to local rotating files (dev box without AWS); unset → `awslogs` to CloudWatch (see [Container logs](#container-logs-cloudwatch) below). |
| `AWS_REGION` | unset | Region for the `awslogs` driver; falls back to `AWS_DEFAULT_REGION`, then the instance's own region (IMDS). |

`ASPNETCORE_URLS` controls the public listener as usual; under systemd,
`NOTIFY_SOCKET`/`WATCHDOG_USEC` are provided by the unit for the watchdog
self-check.

Leader election is heartbeat-derived and reuses the `instance_status` staleness
threshold — `Reconciler:InstanceStaleThreshold` (a `TimeSpan`, default `00:01:30`
= 90s; see [Leader election](#leader-election-heartbeat-derived) below).

### Database migrations (applied automatically)

When `GATEWAY_DB_CONNECTION` is set the gateway **owns its schema and applies
pending EF Core migrations on boot** — there is no manual `dotnet ef database
update` step. This runs before the reconciler, heartbeat, or any endpoint that
reads the schema, so pointing a fresh instance at an empty database just works.

- **Fleet-safe:** when several instances boot at once (ASG scale-out) a Postgres
  advisory lock serializes migration — exactly one instance migrates while the rest
  wait, then proceed. (This lock is only used for migration; leader election uses
  none — see below.)
- **Resilient:** if the database is not yet reachable, migration is retried with
  exponential backoff for a bounded window; if it is still failing the process
  exits non-zero so systemd (`Restart=always`) restarts it, rather than serving
  with a configured-but-unmigrated database.

Tune the retry window via the `Migration` configuration section
(`Migration:MaxWait`, `Migration:InitialBackoff`, `Migration:MaxBackoff`,
`Migration:BackoffFactor`); the defaults give a ~2 minute window. Authoring new
migrations still uses the EF Core tools (`dotnet ef migrations add …`) against
`GatewayDbContextFactory`; applying them is automatic.

### Leader election (heartbeat-derived)

With more than one instance, every box converges its own containers, but the
**leader** additionally runs fleet-wide duties (pruning stale `instance_status`
rows, marking deploys complete). Leadership is **not** a lock: the leader is simply
the live instance with the **lowest `instance_id`** (ordinal compare) among the
`instance_status` rows whose `heartbeat_at` is within
`Reconciler:InstanceStaleThreshold` (default **90s**). No extra table, no advisory
lock, no session state.

- Each reconcile loop an instance **upserts its own heartbeat first, then evaluates
  leadership from a fresh read** — so a booting instance sees itself and takes
  leadership when it is the lowest live id. The `is_leader` flag on its own row
  reflects that evaluation, so the dashboard leader badge follows automatically.
- A hard-killed leader (EC2 terminate, kernel panic) simply stops heartbeating; its
  row ages out and drops from the candidate set, so the next-lowest live id takes
  over within **~1 reconcile loop after the stale threshold** — there is no zombie
  Postgres session that could pin leadership for hours.
- The leader-only duties are **idempotent**, so strict mutual exclusion is
  unnecessary: a brief **dual-leader overlap** during a transition is harmless and
  tolerated by design. Liveness matters more than exclusivity.

### Container logs (CloudWatch)

Every downstream service container the reconciler starts logs to CloudWatch via the
Docker `awslogs` driver (tech-spec §4.3, §9), so the dashboard log viewer works
fleet-wide and logs survive instance replacement:

- **Group per service:** `/gateway/services/{service}` — e.g. `svc-a` →
  `/gateway/services/svc-a`.
- **Stream per instance:** the stream name is this instance's id (the same id used
  in `instance_status` and the `?instance=` log query param), so the viewer scopes
  a service's logs to one box exactly. A blue-green candidate (`{service}-green`)
  logs to its **service's** group/stream, so its output lands in the canonical group
  and survives promotion.
- **Region:** `AWS_REGION` → `AWS_DEFAULT_REGION` → the instance's own region (IMDS).
- **Group creation + retention:** the driver sets `awslogs-create-group=true` so the
  group is created on first write; because that cannot set retention, the reconciler
  sets **30-day retention once per group** (`PutRetentionPolicy`) after a start that
  may have created it. This runs on every instance (not leader-only) and tolerates a
  lagging IAM grant (`AccessDenied`) with a warning — logs still ship regardless.

**Driver precedence** (highest wins): `GATEWAY_LOG_DRIVER` env >
`Reconciler:LogDriver:Driver` config > `awslogs` default. Set either to `json-file`
to force local rotating-file logging (`max-size=10m,max-file=3`) on a dev box
without AWS — no CloudWatch group is created and no retention call is made.

**IAM (instance role):** the awslogs *writer* (the Docker driver) needs
`logs:CreateLogGroup`, `logs:CreateLogStream`, and `logs:PutLogEvents`; the
reconciler additionally needs `logs:PutRetentionPolicy` for retention. The log
*viewer* path (`GET /mgmt/services/{name}/logs`) needs `logs:GetLogEvents` and
`logs:FilterLogEvents`. Scope these to `arn:aws:logs:*:*:log-group:/gateway/services/*`.

## Management API

All endpoints live under `/mgmt/*` on the public listener but require a Cognito
JWT with MFA (tech-spec §4.5); every mutating call is audit-logged to
`deploy_history` with the caller's username. Read endpoints and deploy/rollback
accept an `ops-deploy` token (the CI machine credential); lifecycle and manifest
edits require `ops-admin`.

| Endpoint | Action |
|---|---|
| `GET  /mgmt/services` | Manifest + per-service fleet rollup |
| `GET  /mgmt/instances` | Fleet list from `instance_status` |
| `GET  /mgmt/deploys` · `GET /mgmt/deploys/{id}` | Deploy history + live per-instance rollout progress |
| `GET  /mgmt/services/{name}/logs` | Centralized logs for a service/instance |
| `POST /mgmt/services/{name}/stop` · `/start` · `/restart` | Set desired status (fleet converges) |
| `POST /mgmt/services/{name}/deploy` · `/rollback` | Resolve digest and roll the fleet |
| `PUT  /mgmt/services/{name}` | Create/update a manifest entry |
| `DELETE /mgmt/services/{name}` | Remove a service from the manifest — the reconciler stops/removes the now-orphaned container on its next loop. `?force=true` is required when the service participates in the aggregated health check (mirrors `stop`) |

### Convergence errors are visible

Each reconcile loop records the outcome of the most recent action per service: a
failure stamps `lastError`/`lastErrorAt` on that service's entry in the instance's
`instance_status` services JSON, and a subsequent success clears them. A service
that is desired running but whose start keeps failing has no container, yet still
gets an entry (`state: "absent"`) carrying the error — so a stale-digest start that
loops on `No such image` is visible through the API instead of hiding in journald.

- `GET /mgmt/services` — each service's `fleet` rollup adds:
  - `errorOn` — number of instances whose entry for the service carries a `lastError`.
  - `latestError` — the most recent error fleet-wide as `{ instanceId, message, at }`,
    or `null` when no instance reports one.
- `GET /mgmt/instances` — each per-instance service entry adds `lastError` (trimmed
  to ~300 chars) and `lastErrorAt`, both `null` when the last action succeeded.

Older rows written before these fields existed parse unchanged (the fields default
to `null`).

## CI

`.github/workflows/ci.yml`:

- **test** — every push and pull request: restore, build, and run the full test
  suite on .NET 10. The suite needs no Postgres, Docker, or AWS (all external
  systems are faked), so forks and fresh clones pass out of the box.
- **package** — pushes to `main` only, after tests pass: runs
  `scripts/package.sh` to produce the self-contained `linux-arm64` tarball and
  uploads it as a workflow artifact. If the `ARTIFACT_BUCKET` repo variable and
  AWS credentials secrets are configured, the tarball is also synced to S3
  (including a `gateway-api-latest.tar.gz` alias pulled by instance user-data);
  otherwise that step is skipped and CI stays green.

## Packaging & deploy

The gateway runs on the host via systemd (it manages Docker) and provisions the
box itself — no hand-rolled user-data bash (tech-spec §2, §4.3).

- `scripts/package.sh [version]` — `dotnet publish` a self-contained single-file
  `linux-arm64` binary and bundle it with the unit + install script into
  `artifacts/gateway-api-<version>-linux-arm64.tar.gz`.
- `deploy/gateway-api.service` — systemd unit (`Restart=always`, `WatchdogSec`
  wired to an app self-check, config via `EnvironmentFile=/etc/gateway-api/env`).
- `deploy/install.sh` — unpack-and-install (idempotent) on the box.
- `docs/user-data.example.sh` — the complete ~10-line instance user-data (§6).

Node bootstrap (idempotent, off unless `GATEWAY_BOOTSTRAP_ENABLED=true`) writes
Docker log-rotation config, ensures the internal network, logs in to the registry
(refreshed on a timer), and writes the CloudWatch agent config — all behind an
`ILinuxHost` seam so it is unit-tested against a fake host.

## Status

Core build complete through §10 Phase 5 equivalents: proxy, health, reconciler,
fleet status + leader election, real-time hub, management plane, packaging, CI.
See [`docs/tech-spec.md`](docs/tech-spec.md) for the design; the ops dashboard
lives at [gateway-api-admin](https://github.com/benjaminfkile/gateway-api-admin).
