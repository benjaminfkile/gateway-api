# bk-gateway-v2 — Tech Spec

**Status:** Draft v1 · 2026-08-02
**Owner:** Ben
**Repo (planned):** `bk-gateway-v2` (C# / .NET)

---

## 1. Summary

Rebuild the Node.js `bk-gateway-api` as a C#/.NET service that is not just a reverse
proxy but the **node agent** for the EC2 instance it runs on. It owns:

1. **Edge proxying** — HTTP + WebSocket routing to downstream service containers (replaces http-proxy-middleware with YARP).
2. **Real-time hub** — a shared SignalR hub with a Redis backplane so downstream services never implement WebSocket handling again.
3. **Node reconciliation** — installing box dependencies and running/upgrading downstream containers from a desired-state manifest (replaces the 200-line user-data script).
4. **Management plane** — a Cognito-protected (MFA required) API + dashboard that can do anything the agent can do: stop/start/restart/replace services, roll deploys, view status and logs.

### Goals
- Zero-ish downtime for downstream service deploys (seconds, no instance refresh).
- User-data shrinks to ~10 lines; everything else is testable C# code.
- One WebSocket implementation, shared by all current and future apps.
- Full service lifecycle control from a browser dashboard, safely.

### Non-goals
- Multi-region, Kubernetes, or ECS migration.
- Changing the ALB/ASG topology (stays: Route53 → ALB → ASG instances).
- Replacing Postgres-backed leader election (kept, simplified).
- **Any change to any downstream application.** This project touches only the gateway.

### Design invariant — gateway transparency
No downstream application is ever *required* to know the gateway exists. Every
gateway facility is either transparent (proxying, health checks, WS passthrough)
or strictly opt-in (the real-time hub). Apps own their auth end-to-end
(file-manager-api pattern); the gateway performs **zero** authentication on
application traffic. Consequence: any service can be lifted out and put behind
its own load balancer at any time with no code change.

---

## 2. Current state (v1) — what we're replacing

| Concern | Today | Problem |
|---|---|---|
| Proxy | Node/Express + http-proxy-middleware | Fine, but tied to Node rewrite anyway |
| WebSockets | Raw `ws` upgrade passthrough per service | Every app re-implements socket handling |
| Bootstrap | 216-line user-data bash script (LT v100) | Untestable, only fails at boot, slow |
| Deploys | Push to main → full ASG instance refresh | ~15–25 min for *any* service change |
| Service control | SSH + docker commands | Manual, error-prone |
| Auth on ops routes | Password-hash middleware | No MFA, no identity, no audit |

Existing AWS resources that carry over:
- **Account/region:** 719766734490 / us-east-1
- **ALB:** `bk-gateway-api-lb` (HTTPS :443, ACM) → TG `bk-gateway-api-tg` (HTTP :80, HC `/api/health`)
- **ASG:** `bk-gateway-api-asg` (t4g.medium, ARM — publish `linux-arm64`)
- **DB:** `bk-db` (RDS Postgres 17)
- **Redis backplane:** `bk-backplane-redis` — `master.bk-backplane-redis.z6bv8v.use1.cache.amazonaws.com:6379`, TLS, cluster-mode off, SG `bk-backplane-redis-sg` (sg-0c1cfdeaff7660b12)
- **ECR:** `benkile/*` repos
- **Secrets Manager:** app + DB secrets pattern (reuse `redis_host`/`redis_port` keys already reserved in v1 secrets)

---

## 3. Architecture overview

```
                    ┌─────────────────────────────────────────────┐
 admin.benkile.com  │  EC2 instance (ASG, LT: tiny user-data)     │
 (dashboard SPA) ─┐ │                                             │
                  │ │  bk-gateway-v2 (systemd, host network)      │
 api.benkile.com  │ │  ┌───────────┬──────────┬────────────────┐  │
      │           └─▶ │  │ YARP      │ SignalR  │ Management API │  │
      ▼             │ │  │ proxy     │ hub      │ (Cognito+MFA)  │  │
    ALB ───────────▶│ │  ├───────────┴──────────┴────────────────┤  │
                    │ │  │ Node reconciler (Docker.DotNet)        │  │
                    │ │  └──────────────────┬─────────────────────┘  │
                    │ │       docker: app-net                        │
                    │ │  ┌──────────┐ ┌──────────┐ ┌─────────────┐   │
                    │ │  │portfolio │ │ wmsfo    │ │file-manager │…  │
                    │ │  └──────────┘ └──────────┘ └─────────────┘   │
                    │ └─────────────────────────────────────────────┘
                    │
        ┌───────────┴───────────┐   ┌──────────────────┐
        │ bk-db (Postgres)      │   │ bk-backplane-    │
        │ · desired-state       │   │ redis (pub/sub   │
        │ · deploy history      │   │ backplane)       │
        │ · leader election     │   └──────────────────┘
        └───────────────────────┘
```

The gateway process runs **on the host via systemd** (not in Docker) because it
manages Docker. Containers it starts use `--restart unless-stopped` so they
survive gateway restarts.

---

## 4. Components

### 4.1 Edge proxy (YARP)
- `Yarp.ReverseProxy`, routes built **dynamically** from the desired-state manifest via `IProxyConfigProvider` (in-memory swap, no restart).
- Route rule: `/{service}/*` → `http://{container}:{port}` on `app-net`, same as v1 serviceMap semantics including `-dev` variants.
- WebSocket passthrough works natively for any service that still terminates its own sockets (migration path for 3gix-style apps until they adopt the hub).
- Active health checks per cluster; unhealthy destinations are dropped from rotation automatically — this is the mechanism behind blue-green cutover.

### 4.2 Real-time hub (SignalR) — strictly opt-in
- One hub endpoint: `wss://api.benkile.com/hub`.
- Backplane: `AddStackExchangeRedis("master.bk-backplane-redis...:6379,ssl=true")` — multi-instance fan-out solved by the library.
- **Opt-in only.** An app that wants gateway-managed real-time *may* publish events:
  `POST http://bk-gateway:8080/internal/publish` `{ channel, event, payload }`
  (internal-only listener, reachable from `app-net`, never exposed via ALB).
  That publish call is the app *choosing* to be gateway-aware; nothing more is
  required of it — no webhooks, no callbacks, no gateway SDK.
- Channel model: `{app}:{topic}` (e.g. `wmsfo:flights`). Hub channels are
  **public broadcast** — the gateway performs no end-user auth (see design
  invariant). Suitable for public/live data feeds. An app needing private,
  per-user real-time terminates its own WebSockets exactly as today, and the
  gateway proxies them transparently (YARP native WS passthrough) — such an app
  works unchanged behind its own load balancer.
- Client→server messages do not route through the hub: clients call the app's
  own (proxied) endpoints directly. The hub is one-way fan-out, app → clients.
- Exception: the `ops:*` channels used by the dashboard require a Cognito JWT
  at connect — that is the gateway authenticating **its own** dashboard client,
  not downstream traffic.

### 4.3 Node reconciler
- On boot (replaces user-data): ensure dnf packages, Docker daemon config, CloudWatch agent config, IMDS iptables rules, ECR login (refresh every 6h), `app-net` network.
- Containers are started with the **`awslogs` log driver** → log group `/bk-services/{service}`, stream per instance (v1 used local json-file only; this is what enables the fleet-wide log viewer and post-mortem debugging after an instance is recycled).
- **Reconcile loop (every 30s ± jitter):** jitter (v1's `jitter.ts` pattern) prevents a 20-instance fleet from hitting ECR/Postgres in lockstep. Each loop the instance also **upserts its container inventory to `instance_status`** (heartbeat + per-service digest/state) so any instance can answer fleet-wide queries. Compare running containers (name, image digest, env hash, desired status) against the manifest; converge differences:
  - `running` → not running: blue-green start (see §7).
  - `stopped` → running: stop + remove container, remove YARP route.
  - digest/env drift → rolling replace (blue-green).
- Housekeeping: `docker image prune` for untagged layers older than 48h; disk/mem watermarks emitted as CloudWatch metrics.
- Only mutates its **own** box. With >1 instance, every instance converges independently; the **leader** (Postgres advisory lock, replaces v1's heartbeat tables) is the only one that executes *fleet-wide* actions (e.g. marking a deploy record complete, pruning history).

### 4.4 Desired-state manifest (Postgres)
Single source of truth, mutated only by the Management API, consumed by reconcilers.

```sql
service_manifest (
  name            text primary key,      -- 'wmsfo-api'
  image           text not null,         -- ECR repo
  tag             text not null,         -- 'latest' | pinned
  digest          text,                  -- resolved sha256 (set on deploy)
  port            int  not null,
  desired_status  text not null,         -- 'running' | 'stopped'
  env_secret_arn  text,                  -- optional Secrets Manager ARN
  include_in_health boolean not null,
  -- dev variants are their own explicit rows (e.g. 'wmsfo-api-dev'), not a
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

- CI change: GitHub Actions no longer touches the ASG. It builds/pushes the image, then calls one Management API endpoint (machine credential, see §5) — `POST /mgmt/services/{name}/deploy {tag}` — which resolves the digest and updates the manifest. Reconcilers do the rest.

### 4.5 Management API
All endpoints under `/mgmt/*`, served on the same public listener but **require a Cognito JWT with MFA** (§5). Every call is audit-logged to `deploy_history`/`audit_log` with the Cognito username.

All state-returning endpoints are **fleet-aware**: they read `instance_status` /
Postgres, never just local Docker, so it doesn't matter which instance the ALB
routes the dashboard's request to.

| Endpoint | Action |
|---|---|
| `GET  /mgmt/services` | Manifest + fleet rollup per service ("running on 20/20, digest abc123 on 20/20") |
| `GET  /mgmt/instances` | Fleet list from `instance_status` (heartbeats, versions, leader, per-instance services) |
| `POST /mgmt/services/{name}/stop` · `/start` · `/restart` | Set desired status — **all** instances converge |
| `POST /mgmt/services/{name}/deploy` | `{tag}` → resolve digest once, update manifest → every instance blue-greens locally |
| `POST /mgmt/services/{name}/rollback` | Redeploy previous digest from history (fleet-wide, same mechanism) |
| `PUT  /mgmt/services/{name}` | Create/update manifest entry (add a new app from the dashboard) |
| `GET  /mgmt/services/{name}/logs?instance={id}&tail=500` | Logs from **CloudWatch Logs** — works for any instance regardless of which one serves the request, and survives instance replacement |
| `GET  /mgmt/deploys` · `GET /mgmt/deploys/{id}` | History + live per-instance rollout progress (`deploy_instance_status`) |
| `WS   /hub` (`ops` channel) | Live status pushes to the dashboard — Redis backplane means events from *any* instance reach the dashboard's single connection |

Guardrails: `stop` on the gateway's own health-check dependencies prompts a
confirm flag (`?force=true`); the gateway itself is **not** in the manifest and
cannot be stopped from the dashboard (its lifecycle = systemd + ASG refresh).

### 4.6 Dashboard (`bk-ops-dashboard`)
Hosted on Vercel at **`ops.benkile.com`** (new CNAME; `admin.benkile.com` stays
with the portfolio admin). Stack follows the house conventions established in
`portfolio-v6-admin` (primary reference) and `wisp-dashboard`:

| Concern | Choice | Ported from |
|---|---|---|
| Build | **Vite 7 + React 19 + TypeScript**, `tsc --noEmit && vite build` | portfolio-v6-admin |
| UI kit | **MUI v9** (`@mui/material` + `@mui/icons-material`, Emotion) — Material Design | both |
| Theme | MUI theme with light/dark + `ThemeModeProvider` / `ThemeToggle` | portfolio-v6-admin `src/theme/` |
| Routing | `react-router-dom` | portfolio-v6-admin |
| Auth | `amazon-cognito-identity-js` wrapped in `lib/cognitoClient`, `AuthContext` + `LoginPage` — **extended with the `SOFTWARE_TOKEN_MFA` challenge step** (TOTP prompt after password) and first-login TOTP enrollment (QR via `associateSoftwareToken`) | portfolio-v6-admin `src/contexts/AuthContext.tsx` |
| API layer | axios `apiClient` with request interceptor attaching `Authorization: Bearer <token>`; per-resource modules (`servicesApi`, `deploysApi`, `instancesApi`, `logsApi`) | portfolio-v6-admin `src/api/` |
| Live updates | `@microsoft/signalr` client on the `ops:*` channels, wrapped in hooks (`useFleetStatus`, `useDeployProgress`) | wisp-dashboard `useEventStream` pattern |
| Log viewer | `@xterm/xterm` + fit addon console panel | wisp-dashboard `ConsolePanel` |
| Tests | vitest + @testing-library/react + axios-mock-adapter | portfolio-v6-admin |
| Env | `VITE_API_BASE_URL` (gateway origin), `VITE_COGNITO_POOL_ID`, `VITE_COGNITO_CLIENT_ID`; local dev proxies `/mgmt` to a local gateway | portfolio-v6-admin |

Layout: `AppShell` (drawer nav) with pages — **Services** (fleet grid:
status/digest/uptime per service with start·stop·restart·deploy·rollback
actions + confirm dialogs), **Deploys** (history + live per-instance
convergence), **Instances** (fleet list, heartbeats, leader badge), **Logs**
(xterm panel, service + instance pickers, CloudWatch-backed), **Node stats**.
Structure mirrors portfolio-v6-admin: `src/api`, `src/components`,
`src/contexts`, `src/hooks`, `src/pages`, `src/theme`.

---

## 5. Authentication — Cognito + MFA (ops plane ONLY)

**Scope: the ops dashboard and `/mgmt/*` endpoints. Nothing else.** Application
traffic passes through the proxy untouched — headers, cookies, and tokens are
forwarded as-is and the gateway never inspects them. Downstream apps
authenticate their own users exactly as they do today (file-manager-api
pattern); moving an app behind its own load balancer changes nothing about its
auth.

- **New dedicated user pool `bk-ops-up`** (do not reuse the six existing app pools — blast radius isolation for infra admin).
  - Self-signup **disabled**; users created by admin only.
  - **MFA: REQUIRED, TOTP (software token)**. No SMS (SIM-swap risk, cost).
  - Password policy: 12+ chars; advanced security features ON (compromised-credential blocking).
  - App client: SPA client (no secret, PKCE) for the dashboard.
- **Machine credential for CI:** separate app client using `client_credentials` grant against a resource server scope `mgmt/deploy` — GitHub Actions gets deploy-only, no dashboard powers.
- **Gateway-side validation:** standard JWT bearer middleware against the pool's JWKS; requires `token_use=access`, the pool issuer, and — for human tokens — an `amr`/auth-event check that MFA was performed. Role claim via Cognito group `ops-admin` (full) vs `ops-deploy` (deploy/rollback only).
- Session: access token 60 min, refresh 30 days, revocation enabled.

---

## 6. Tech stack

| Concern | Choice |
|---|---|
| Runtime | .NET 10 LTS, ASP.NET Core, publish self-contained `linux-arm64` single-file |
| Proxy | YARP (`Yarp.ReverseProxy`) |
| Real-time | SignalR + `Microsoft.AspNetCore.SignalR.StackExchangeRedis` |
| Docker control | `Docker.DotNet` via `unix:///var/run/docker.sock` |
| DB | `Npgsql` + EF Core (migrations own the manifest/status schema) |
| AWS | `AWSSDK.SecretsManager`, `AWSSDK.ECR`, `AWSSDK.CloudWatch`, `AWSSDK.S3` |
| Auth | `Microsoft.AspNetCore.Authentication.JwtBearer` against Cognito |
| Process | systemd unit, `Restart=always`, `WatchdogSec` wired to a self-check |
| Logs | Serilog → CloudWatch Logs (`/bk-gateway-v2/{instance}`) |

New user-data (complete):
```bash
#!/bin/bash
set -euo pipefail
dnf install -y docker awscli amazon-cloudwatch-agent
systemctl enable --now docker
aws s3 cp s3://bk-gateway-artifacts/bk-gateway-v2-latest.tar.gz /opt/bkgw/ && tar -xzf ... -C /opt/bkgw
cp /opt/bkgw/bk-gateway-v2.service /etc/systemd/system/ && systemctl enable --now bk-gateway-v2
```
(Gateway binary versions live in a new small S3 bucket `bk-gateway-artifacts`.)

---

## 7. Deploy flows & zero-downtime

**Downstream service (the common case — seconds, zero drop, any fleet size):**
1. Manifest digest changes (CI or dashboard) — a single Postgres write, whether the fleet is 1 instance or 20.
2. Each instance's reconciler notices on its next (jittered) loop and runs the same local sequence independently: pull image, start `wmsfo-api-green` on a side port, `docker` health + HTTP readiness poll.
3. YARP config swap on that instance: destination flips to green (in-flight requests drain via YARP's graceful destination removal).
4. Old container stopped/removed after drain timeout (30s), renamed green → canonical name; instance reports `converged` to `deploy_instance_status`.
5. Because every instance keeps its old container serving until its own green is healthy, **fleet capacity never dips** — no wave orchestration needed; convergence completes fleet-wide within ~1–2 min of the click. The **leader** marks the `deploy_history` row complete when all live instances report converged (or flags `partial` listing stragglers).
6. Failure on an instance = automatic local abort, old container stays live there, dashboard shows exactly which instances are on which digest; `rollback` is one click and uses the identical mechanism.

**Event scale-out (e.g. 20 instances):** ASG desired-capacity change only. Each new box boots the tiny user-data, reconciler converges from the manifest, registers in `instance_status`, and joins the SignalR backplane. A deploy issued mid-scale-out is safe: instances converge to whatever the manifest says whenever they arrive.

**Gateway itself (rare):** new tarball to S3 → ASG instance refresh (existing `MinHealthyPercentage: 100` flow). ALB covers the cutover; SignalR clients auto-reconnect to the new instance and re-join groups (client-side standard behavior).

**Instance replacement/scale-out:** new box boots, reconciler reads manifest, converges to full stack with zero human input.

---

## 8. Security notes

- `/internal/*` listener bound to the Docker bridge interface only; never routed by ALB.
- Management plane: Cognito JWT + MFA + group authz + full audit trail (actor, action, before/after digest).
- Docker socket = root-equivalent: the gateway is the *only* privileged process; downstream containers run unprivileged, no socket mounts.
- Secrets: containers get env from Secrets Manager at (re)create time via reconciler — secrets never stored in manifest or logs.
- Keep RDS `PubliclyAccessible` flip-off as a standing TODO (SG-restricted today).

---

## 9. Observability

- Metrics: reconciler convergence duration/failures, per-service restart counts, hub connection count, publish throughput, YARP 5xx/latency per route → CloudWatch (`BkGateway` namespace).
- Alarms to migrate: keep existing ALB/ASG alarms; add `ReconcileFailed > 0` and `ServiceCrashLooping` (restart count > 3 in 5 min).
- Structured logs with `deployId` correlation across reconciler/proxy/hub.

---

## 10. Migration plan

| Phase | Deliverable | Exit criteria |
|---|---|---|
| **0** | Repo scaffold, systemd packaging, S3 artifact bucket, CI build (arm64) | Binary boots on a dev box, `/api/health` parity |
| **1** | YARP proxy + static manifest parity with v1 serviceMap; Postgres schema | Shadow instance passes all v1 routes incl. WS passthrough |
| **2** | Node reconciler (boot + converge); new tiny user-data; LT version | Fresh instance self-provisions with zero user-data logic |
| **3** | SignalR hub + Redis backplane + internal publish contract | Test app pushes/receives via hub on 2 instances |
| **4** | Cognito pool + Management API + audit | CI deploys via API; ASG perms removed from GitHub |
| **5** | Dashboard SPA | Stop/start/deploy/rollback/logs from browser w/ MFA |
| **6** | Cutover: ASG → v2 LT default; retire v1 repo; migrate wmsfo/portfolio deploy pipelines | v1 image unused for 2 weeks → archive |

Rollback at any phase: flip LT default back to v100 and instance-refresh.

---

## 11. Open questions

1. ~~EF Core vs Dapper~~ — **Resolved: EF Core** (2026-08-02).
2. ~~Hub auth handshake~~ — **Resolved: none** (2026-08-02): the gateway performs no end-user auth of any kind (design invariant, §1). Hub channels are public broadcast; the only authenticated hub surface is the dashboard's `ops:*` channels (Cognito). Apps needing private real-time keep terminating their own WebSockets behind transparent passthrough — no app changes, ever.
3. ~~Dashboard host~~ — **Resolved: `ops.benkile.com`** (2026-08-02), new Vercel project + CNAME.
4. ~~`-dev` variants~~ — **Resolved: explicit manifest rows** (2026-08-02); dev is stopped/deployed independently of prod from the dashboard.
5. ~~Log retention~~ — **Resolved: 30-day retention** (2026-08-02) on all `/bk-services/*` and `/bk-gateway-v2/*` log groups, set by the reconciler at group creation. Revisit only if CloudWatch spend becomes noticeable.

*(All open questions resolved — spec is ready for Phase 0.)*
