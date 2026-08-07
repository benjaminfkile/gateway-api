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
| `GATEWAY_INTERNAL_BIND` | `0.0.0.0:8080` | Bind address of the internal listener hosting `POST /internal/publish` (never routed by the load balancer). |
| `GATEWAY_INSTANCE_ID` | unset | Instance identity fallback when EC2 IMDS is unreachable (local dev). |
| `GATEWAY_PRIVATE_IP` | unset | Private IP fallback for local dev. |
| `GATEWAY_PUBLIC_IP` | unset | Public IP fallback for local dev. |

`ASPNETCORE_URLS` controls the public listener as usual; under systemd,
`NOTIFY_SOCKET`/`WATCHDOG_USEC` are provided by the unit for the watchdog
self-check.

### Database migrations (applied automatically)

When `GATEWAY_DB_CONNECTION` is set the gateway **owns its schema and applies
pending EF Core migrations on boot** — there is no manual `dotnet ef database
update` step. This runs before the reconciler, heartbeat, or any endpoint that
reads the schema, so pointing a fresh instance at an empty database just works.

- **Fleet-safe:** when several instances boot at once (ASG scale-out) a Postgres
  advisory lock (a distinct key from leader election) serializes migration —
  exactly one instance migrates while the rest wait, then proceed.
- **Resilient:** if the database is not yet reachable, migration is retried with
  exponential backoff for a bounded window; if it is still failing the process
  exits non-zero so systemd (`Restart=always`) restarts it, rather than serving
  with a configured-but-unmigrated database.

Tune the retry window via the `Migration` configuration section
(`Migration:MaxWait`, `Migration:InitialBackoff`, `Migration:MaxBackoff`,
`Migration:BackoffFactor`); the defaults give a ~2 minute window. Authoring new
migrations still uses the EF Core tools (`dotnet ef migrations add …`) against
`GatewayDbContextFactory`; applying them is automatic.

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
