# gateway-api — Tech Spec

**Status:** Draft v1

---

## 1. Summary

**gateway-api** is a C#/.NET service that is not just a reverse proxy but the
**node agent** for the VM it runs on. It owns:

1. **Edge proxying** — HTTP + WebSocket routing to downstream service containers (YARP).
2. **Real-time hub** — a shared SignalR hub with a Redis backplane so downstream services never have to implement WebSocket handling.
3. **Node reconciliation** — installing box dependencies and running/upgrading downstream containers from a desired-state manifest (replaces large hand-rolled bootstrap scripts).
4. **Management plane** — a Cognito-protected (MFA required) API + dashboard that can do anything the agent can do: stop/start/restart/replace services, roll deploys, view status and logs.

### Goals
- Zero-ish downtime for downstream service deploys (seconds, no instance replacement).
- Instance user-data shrinks to ~10 lines; everything else is testable C# code.
- One WebSocket implementation, shared by all current and future apps.
- Full service lifecycle control from a browser dashboard, safely.
- Fleet-native: one instance or twenty behind a load balancer is the same operation.

### Non-goals
- Multi-region, Kubernetes, or ECS migration — this targets plain VMs (e.g. EC2 in an Auto Scaling group) running Docker.
- **Any change to any downstream application.** The gateway is infrastructure; apps are untouched.

### Design invariant — gateway transparency
No downstream application is ever *required* to know the gateway exists. Every
gateway facility is either transparent (proxying, health checks, WS passthrough)
or strictly opt-in (the real-time hub). Apps own their auth end-to-end; the
gateway performs **zero** authentication on application traffic. Consequence:
any service can be lifted out and put behind its own load balancer at any time
with no code change.

---

## 2. Motivation — what this replaces

The typical script-provisioned Docker-on-VM deployment has these problems:

| Concern | Status quo | Problem |
|---|---|---|
| Bootstrap | A few hundred lines of user-data bash | Untestable, only fails at boot, slow |
| Deploys | Push image → replace the whole instance | Minutes of churn for *any* service change |
| WebSockets | Every app implements its own socket handling | Reinvented per app |
| Service control | SSH + docker commands | Manual, error-prone, no audit trail |
| Ops auth | Shared password / bastion access | No MFA, no identity, no audit |

Reference deployment shape (AWS terms used throughout, but nothing is
AWS-exclusive in the design):

- DNS → internet-facing ALB (HTTPS, ACM cert) → target group (HTTP :80, health check `/api/health`) → Auto Scaling group of VMs.
- Containers pulled from a private registry (ECR), secrets from a secrets store (Secrets Manager), shared PostgreSQL and a small Redis.

---

## 3. Architecture overview

```
                    ┌─────────────────────────────────────────────┐
 ops.example.com    │  VM (ASG member; tiny user-data)            │
 (dashboard SPA) ─┐ │                                             │
                  │ │  gateway-api (systemd, host network)        │
 api.example.com  │ │  ┌───────────┬──────────┬────────────────┐  │
      │           └─▶ │  │ YARP      │ SignalR  │ Management API │  │
      ▼             │ │  │ proxy     │ hub      │ (Cognito+MFA)  │  │
    ALB ───────────▶│ │  ├───────────┴──────────┴────────────────┤  │
                    │ │  │ Node reconciler (Docker.DotNet)        │  │
                    │ │  └──────────────────┬─────────────────────┘  │
                    │ │       docker: app-net                        │
                    │ │  ┌──────────┐ ┌──────────┐ ┌─────────────┐   │
                    │ │  │ svc-a    │ │ svc-b    │ │ svc-c       │…  │
                    │ │  └──────────┘ └──────────┘ └─────────────┘   │
                    │ └─────────────────────────────────────────────┘
                    │
        ┌───────────┴───────────┐   ┌──────────────────┐
        │ PostgreSQL            │   │ Redis            │
        │ · desired-state       │   │ (pub/sub         │
        │ · deploy history      │   │  backplane,      │
        │ · leader election     │   │  cluster-mode    │
        │ · instance status     │   │  off)            │
        └───────────────────────┘   └──────────────────┘
```

The gateway process runs **on the host via systemd** (not in Docker) because it
manages Docker. Containers it starts use `--restart unless-stopped` so they
survive gateway restarts.

---

## 4. Components

### 4.1 Edge proxy (YARP)
- `Yarp.ReverseProxy`, routes built **dynamically** from the desired-state manifest via `IProxyConfigProvider` (in-memory swap, no restart).
- Route rule: `/{service}/*` → `http://127.0.0.1:{hostPort}` — the container's host-published port. The gateway lives on the host (§3), outside the Docker network, so container DNS names are not resolvable for it; the reconciler publishes each service's port to the host instead. **Host ports are Docker-assigned, not manifest-derived:** the reconciler publishes the manifest `port` (container-internal only) with an *unassigned* host binding, Docker picks a unique ephemeral host port, and the reconciler records that actual port and points the route at it. This guarantees uniqueness (two containers of a service, or two services sharing an internal port, never contend) and removes any fixed host-port reservation. Manifest `port` is therefore purely the container-side contract.
- WebSocket passthrough works natively for any service that terminates its own sockets.
- Active health checks per cluster; unhealthy destinations are dropped from rotation automatically — this is the mechanism behind blue-green cutover.

### 4.2 Real-time hub (SignalR) — strictly opt-in
- One hub endpoint: `wss://api.example.com/hub`.
- Backplane: `AddStackExchangeRedis(...)` — multi-instance fan-out solved by the library. Use a dedicated Redis with cluster mode **off** (single endpoint, clean pub/sub semantics).
- **Opt-in only.** An app that wants gateway-managed real-time *may* publish events:
  `POST http://gateway:8080/internal/publish` `{ channel, event, payload }`
  (internal-only listener, reachable from the Docker network, never exposed via
  the load balancer). That publish call is the app *choosing* to be
  gateway-aware; nothing more is required of it — no webhooks, no callbacks, no SDK.
- Channel model: `{app}:{topic}` (e.g. `svc-a:updates`). Hub channels are
  **public broadcast** — the gateway performs no end-user auth (see design
  invariant). Suitable for public/live data feeds. An app needing private,
  per-user real-time terminates its own WebSockets, and the gateway proxies
  them transparently — such an app works unchanged behind its own load balancer.
- Client→server messages do not route through the hub: clients call the app's
  own (proxied) endpoints directly. The hub is one-way fan-out, app → clients.
- Exception: the `ops:*` channels used by the dashboard require a Cognito JWT
  at connect — that is the gateway authenticating **its own** dashboard client,
  not downstream traffic.

### 4.3 Node reconciler
- On boot (replaces user-data): ensure OS packages, Docker daemon config,
  metrics agent config, registry login (refreshed periodically), internal
  Docker network.
- Containers are started with the **`awslogs` log driver** (or equivalent
  centralized log driver) → log group `/gateway/services/{service}`, stream per
  instance. This enables the fleet-wide log viewer and post-mortem debugging
  after an instance is recycled.
- **Reconcile loop (every 30s ± jitter):** jitter prevents a large fleet from
  hitting the registry/DB in lockstep. Each loop the instance also **upserts
  its container inventory to `instance_status`** (heartbeat + per-service
  digest/state) so any instance can answer fleet-wide queries. Compare running
  containers (name, image digest, env hash, desired status) against the
  manifest; converge differences:
  - `running` → not running: blue-green start (see §7).
  - `stopped` → running: stop + remove container, remove YARP route.
  - digest/env drift → rolling replace (blue-green).
- Housekeeping: prune untagged images older than 48h; disk/mem watermarks
  emitted as metrics.
- Only mutates its **own** box. With >1 instance, every instance converges
  independently; the **leader** (Postgres advisory lock) is the only one that
  executes *fleet-wide* actions (e.g. marking a deploy record complete,
  pruning history).

### 4.4 Desired-state manifest (PostgreSQL)
Single source of truth, mutated only by the Management API, consumed by reconcilers.

```sql
service_manifest (
  name            text primary key,      -- 'svc-a'
  image           text not null,         -- registry repo
  tag             text not null,         -- 'latest' | pinned
  digest          text,                  -- resolved sha256 (set on deploy)
  port            int  not null,          -- container-internal port ONLY (containerPort); Docker assigns the host port dynamically
  desired_status  text not null,         -- 'running' | 'stopped'
  env_secret_ref  text,                  -- optional secrets-store reference
  include_in_health boolean not null,
  -- dev variants are their own explicit rows (e.g. 'svc-a-dev'), not a
  -- derived flag: the dashboard can stop/deploy dev independently of prod
  updated_by      text not null,         -- Cognito username
  updated_at      timestamptz not null
)
deploy_history (id, service, from_digest, to_digest, actor, action, status, started_at, finished_at, detail jsonb)

-- fleet awareness (written by every reconciler loop; rows stale > 90s = instance gone)
instance_status (
  instance_id   text primary key,
  private_ip    text, public_ip text,
  gateway_ver   text,
  is_leader     boolean,
  services      jsonb,                 -- [{name, digest, state, startedAt, restarts}]
  heartbeat_at  timestamptz not null
)
-- per-instance rollout progress for a deploy ("17/20 converged")
deploy_instance_status (deploy_id, instance_id, status, detail, updated_at,
                        primary key (deploy_id, instance_id))
```

- CI integration: the build pipeline pushes the image, then calls one
  Management API endpoint (machine credential, see §5) —
  `POST /mgmt/services/{name}/deploy {tag}` — which resolves the digest and
  updates the manifest. Reconcilers do the rest. CI needs no infrastructure
  permissions beyond registry push + that one API call.

### 4.5 Management API
All endpoints under `/mgmt/*`, served on the public listener but **require a
Cognito JWT with MFA** (§5). Every call is audit-logged with the Cognito
username.

All state-returning endpoints are **fleet-aware**: they read `instance_status` /
Postgres, never just local Docker, so it doesn't matter which instance the load
balancer routes the dashboard's request to.

| Endpoint | Action |
|---|---|
| `GET  /mgmt/services` | Manifest + fleet rollup per service ("running on 20/20, digest abc123 on 20/20") |
| `GET  /mgmt/instances` | Fleet list from `instance_status` (heartbeats, versions, leader, per-instance services) |
| `POST /mgmt/services/{name}/stop` · `/start` · `/restart` | Set desired status — **all** instances converge |
| `POST /mgmt/services/{name}/deploy` | `{tag}` → resolve digest once, update manifest → every instance blue-greens locally |
| `POST /mgmt/services/{name}/rollback` | Redeploy previous digest from history (fleet-wide, same mechanism) |
| `PUT  /mgmt/services/{name}` | Create/update manifest entry (add a new app from the dashboard) |
| `DELETE /mgmt/services/{name}` | Remove a service from the manifest; the reconciler stops/removes the now-orphaned container next loop. `?force=true` required for a health-check dependency (mirrors stop) |
| `GET  /mgmt/services/{name}/logs?instance={id}&tail=500` | Logs from the centralized log store — works for any instance regardless of which one serves the request, and survives instance replacement |
| `GET  /mgmt/deploys` · `GET /mgmt/deploys/{id}` | History + live per-instance rollout progress (`deploy_instance_status`) |
| `WS   /hub` (`ops` channel) | Live status pushes to the dashboard — Redis backplane means events from *any* instance reach the dashboard's single connection |

Guardrails: `stop` (and `delete`) on a health-check dependency requires a
confirm flag (`?force=true`); the gateway itself is **not** in the manifest and
cannot be stopped from the dashboard (its lifecycle = systemd + instance
replacement).

### 4.6 Dashboard (ops UI)
A separate SPA (any static host) at e.g. `ops.example.com`:

| Concern | Choice |
|---|---|
| Build | **Vite + React + TypeScript**, `tsc --noEmit && vite build` |
| UI kit | **MUI (Material Design)** — `@mui/material` + `@mui/icons-material`, Emotion |
| Theme | MUI theme with light/dark mode provider + toggle |
| Routing | `react-router-dom` |
| Auth | `amazon-cognito-identity-js` wrapped in a small client: login page, auth context, **`SOFTWARE_TOKEN_MFA` challenge step** (TOTP prompt after password) and first-login TOTP enrollment (QR via `associateSoftwareToken`) |
| API layer | axios client with a request interceptor attaching `Authorization: Bearer <token>`; per-resource modules (`servicesApi`, `deploysApi`, `instancesApi`, `logsApi`) |
| Live updates | `@microsoft/signalr` client on the `ops:*` channels, wrapped in hooks (`useFleetStatus`, `useDeployProgress`) |
| Log viewer | `@xterm/xterm` + fit addon console panel |
| Tests | vitest + @testing-library/react + axios-mock-adapter |
| Env | `VITE_API_BASE_URL` (gateway origin), `VITE_COGNITO_POOL_ID`, `VITE_COGNITO_CLIENT_ID`; local dev proxies `/mgmt` to a local gateway |

Layout: app shell (drawer nav) with pages — **Services** (fleet grid:
status/digest/uptime per service with start·stop·restart·deploy·rollback
actions + confirm dialogs), **Deploys** (history + live per-instance
convergence), **Instances** (fleet list, heartbeats, leader badge), **Logs**
(xterm panel, service + instance pickers), **Node stats**. Structure:
`src/api`, `src/components`, `src/contexts`, `src/hooks`, `src/pages`,
`src/theme`.

---

## 5. Authentication — Cognito + MFA (ops plane ONLY)

**Scope: the ops dashboard and `/mgmt/*` endpoints. Nothing else.** Application
traffic passes through the proxy untouched — headers, cookies, and tokens are
forwarded as-is and the gateway never inspects them. Downstream apps
authenticate their own users exactly as they do today; moving an app behind its
own load balancer changes nothing about its auth.

- **Dedicated user pool** for ops (never shared with any application's user pool — blast-radius isolation for infra admin).
  - Self-signup **disabled**; users created by admin only.
  - **MFA: REQUIRED, TOTP (software token)**. No SMS (SIM-swap risk, cost).
  - Password policy: 12+ chars; advanced security features ON (compromised-credential blocking).
  - App client: SPA client (no secret, PKCE) for the dashboard.
- **Machine credential for CI:** separate app client using `client_credentials` grant against a resource server scope `mgmt/deploy` — CI gets deploy-only, no dashboard powers.
- **Gateway-side validation:** standard JWT bearer middleware against the pool's JWKS; requires `token_use=access`, the pool issuer, and — for human tokens — an MFA-performed check. Role claim via Cognito groups: `ops-admin` (full) vs `ops-deploy` (deploy/rollback only).
- Session: access token 60 min, refresh 30 days, revocation enabled.

---

## 6. Tech stack

| Concern | Choice |
|---|---|
| Runtime | .NET 10 LTS, ASP.NET Core, publish self-contained single-file (build for your host arch, e.g. `linux-arm64` for Graviton) |
| Proxy | YARP (`Yarp.ReverseProxy`) |
| Real-time | SignalR + `Microsoft.AspNetCore.SignalR.StackExchangeRedis` |
| Docker control | `Docker.DotNet` via `unix:///var/run/docker.sock` |
| DB | `Npgsql` + EF Core (migrations own the manifest/status schema) |
| Cloud SDKs | secrets store, registry, metrics, object storage as needed |
| Auth | `Microsoft.AspNetCore.Authentication.JwtBearer` against Cognito |
| Process | systemd unit, `Restart=always`, `WatchdogSec` wired to a self-check |
| Logs | Serilog → centralized log store (`/gateway/{instance}`) |

New user-data (complete):
```bash
#!/bin/bash
set -euo pipefail
dnf install -y docker awscli amazon-cloudwatch-agent
systemctl enable --now docker
aws s3 cp s3://<artifact-bucket>/gateway-api-latest.tar.gz /opt/gateway/ && tar -xzf ... -C /opt/gateway
cp /opt/gateway/gateway-api.service /etc/systemd/system/ && systemctl enable --now gateway-api
```
(Gateway binary versions live in a small object-storage bucket.)

---

## 7. Deploy flows & zero-downtime

**Downstream service (the common case — seconds, zero drop, any fleet size):**
1. Manifest digest changes (CI or dashboard) — a single Postgres write, whether the fleet is 1 instance or 20.
2. Each instance's reconciler notices on its next (jittered) loop and runs the same local sequence independently: pull image, start `svc-a-green` (Docker assigns it a unique ephemeral host port), container health + HTTP readiness poll at that assigned port.
3. YARP config swap on that instance: destination flips to green (in-flight requests drain via YARP's graceful destination removal).
4. Old container stopped/removed after drain timeout (30s), then green is renamed to the canonical name; instance reports `converged` to `deploy_instance_status`.
   - **Ports are container-truth, not manifest-derived.** Docker port bindings are
     fixed at container creation, so a promoted green keeps the **Docker-assigned
     host port** it was started on — a rename does **not** move it to the manifest
     port. The manifest `port` is therefore only the *container-side* contract. The
     reconciler learns each managed container's actual host-published port by
     inspecting it after start (surfaced on `ContainerInfo.HostPort`), and the proxy
     + health prober resolve each destination to that real port, falling back to the
     manifest port only when no container port is known. On startup the route table
     is rebuilt from the running container inventory for the same reason, so a
     promoted-green container keeps receiving traffic across a gateway restart. A
     container serving on its assigned host port with the correct digest/env is fully
     converged and never triggers a replace. Pre-existing containers from an older
     gateway that used fixed host ports keep working unchanged (container-truth
     routing handles arbitrary ports) and adopt an ephemeral port on their next
     replace — no migration step is required.
5. Because every instance keeps its old container serving until its own green is healthy, **fleet capacity never dips** — no wave orchestration needed; convergence completes fleet-wide within ~1–2 min of the click. The **leader** marks the `deploy_history` row complete when all live instances report converged (or flags `partial` listing stragglers).
6. Failure on an instance = automatic local abort, old container stays live there, dashboard shows exactly which instances are on which digest; `rollback` is one click and uses the identical mechanism.

**Event scale-out (e.g. 20 instances):** scaling-group desired-capacity change only. Each new box boots the tiny user-data, reconciler converges from the manifest, registers in `instance_status`, and joins the SignalR backplane. A deploy issued mid-scale-out is safe: instances converge to whatever the manifest says whenever they arrive.

**The gateway itself (rare):** new artifact to object storage → rolling instance replacement (e.g. ASG instance refresh at 100% min-healthy). The load balancer covers the cutover; SignalR clients auto-reconnect and re-join groups.

**Migration note:** old and new versions of a service run side by side briefly
during convergence. Ship DB migrations expand/contract style (compatible with
both versions).

---

## 8. Security notes

- `/internal/*` listener bound to the Docker bridge interface only; never routed by the load balancer.
- Management plane: Cognito JWT + MFA + group authz + full audit trail (actor, action, before/after digest).
- Docker socket = root-equivalent: the gateway is the *only* privileged process; downstream containers run unprivileged, no socket mounts.
- Secrets: containers get env from the secrets store at (re)create time via the reconciler — secret values never stored in the manifest or logs.
- Keep the database non-public; restrict by security group / network policy.

---

## 9. Observability

- Metrics: reconciler convergence duration/failures, per-service restart counts, hub connection count, publish throughput, YARP 5xx/latency per route.
- Alarms: load-balancer target health, `ReconcileFailed > 0`, `ServiceCrashLooping` (restart count > 3 in 5 min).
- Structured logs with `deployId` correlation across reconciler/proxy/hub.
- Log retention: 30 days on all gateway/service log groups, set by the reconciler at group creation.

---

## 10. Build plan

| Phase | Deliverable | Exit criteria |
|---|---|---|
| **0** | Repo scaffold, systemd packaging, artifact bucket, CI build | Binary boots on a dev box, `/api/health` works |
| **1** | YARP proxy + static manifest; Postgres schema (EF Core migrations) | Shadow instance passes all routes incl. WS passthrough |
| **2** | Node reconciler (boot + converge); tiny user-data | Fresh instance self-provisions with zero user-data logic |
| **3** | SignalR hub + Redis backplane + internal publish contract | Test app pushes events via hub on 2 instances |
| **4** | Cognito pool + Management API + audit | CI deploys via API; CI infra permissions revoked |
| **5** | Dashboard SPA | Stop/start/deploy/rollback/logs from browser w/ MFA |
| **6** | Cutover from legacy gateway; migrate service deploy pipelines | Legacy image unused for 2 weeks → archive |

Rollback at any phase: revert the launch template / instance image to the
previous version and roll the fleet.

---

## 11. Resolved decisions

1. **EF Core** (not Dapper) for the manifest/status layer; migrations own the schema.
2. **No end-user auth in the gateway** (design invariant, §1). Hub channels are public broadcast; the only authenticated hub surface is the dashboard's `ops:*` channels (Cognito). Apps needing private real-time terminate their own WebSockets behind transparent passthrough.
3. Dashboard is a standalone statically-hosted SPA on its own subdomain.
4. `-dev` variants are explicit manifest rows; dev is stopped/deployed independently of prod.
5. 30-day log retention on all gateway-created log groups, set at group creation.
