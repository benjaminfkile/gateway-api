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
| `GATEWAY_CORS_ORIGINS` | unset | Comma-separated origins allowed CORS access to `/mgmt/*` and `/hub` (the ops dashboard's origin). Unset → `/mgmt` gets no CORS; proxied application traffic is never CORS-handled either way. On `/hub` only, this static set is unioned with every manifest service's `realtimeAllowedOrigins` so a consumer app's frontend can negotiate a SignalR connection from its own domain (effective within ~30s of an upsert, no restart). |
| `GATEWAY_INTERNAL_BIND` | `0.0.0.0:8080` | Bind address of the internal listener hosting `POST /internal/publish`, `GET /internal/presence/{channel}`, and `GET /internal/leader` (never routed by the load balancer). |
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

### Container secrets (Secrets Manager)

A service's container environment is resolved from **AWS Secrets Manager at
container (re)create time** (tech-spec §8), never stored in the manifest or logs.
A manifest row's optional `env_secret_ref` (a secret **name or full ARN**, set via
`PUT /mgmt/services/{name}`) names a secret whose `SecretString` is a **flat JSON
object of string values**; that object becomes the container's env. An empty/unset
ref means an empty environment (the container runs with only its image defaults).

- **Rotation propagates via env drift.** The resolved env is folded into the
  service's env hash, so changing a secret's value changes the hash; the next
  reconcile loop sees the drift and **blue-green-replaces** the container onto the
  new env — no explicit restart. Rotation lands within ~`SecretCacheTtl` + one loop.
- **Caching:** resolved env is cached per ref for
  `Reconciler:SecretCacheTtl` (a `TimeSpan`, default `00:01:00` = **60s**) so a 30s
  fleet loop does not call Secrets Manager per service per loop. The cache is
  thread-safe and a **failed fetch is never cached**.
- **Failure isolation:** a missing secret, `AccessDenied`, or a non-JSON / non-flat
  `SecretString` fails **only that service's** reconcile action (surfaced through the
  same per-service `lastError` as any other failure) — it never takes down the loop
  or other services, and the existing container (if any) keeps serving. Error
  messages name the **ref only**; secret values never appear in logs or errors.
- **Laziness:** the AWS client is constructed only on the first non-empty ref, so a
  region-less dev box or an AWS-less CI runner still boots and passes host tests.

**IAM (instance role):** `secretsmanager:GetSecretValue` on the referenced secrets
(e.g. `arn:aws:secretsmanager:*:*:secret:/gateway/services/*`).

## Management API

All endpoints live under `/mgmt/*` on the public listener but require a Cognito
JWT with MFA (tech-spec §4.5); every mutating call is audit-logged to
`deploy_history` with the caller's username. Read endpoints and deploy/rollback
accept an `ops-deploy` token (the CI machine credential); lifecycle and manifest
edits require `ops-admin`.

| Endpoint | Action |
|---|---|
| `GET  /mgmt/services` | Manifest + per-service fleet rollup |
| `GET  /mgmt/instances` | Fleet list from `instance_status`, with each instance's node stats from its last heartbeat: `cpu`, `memory` and `disk` percent and `hubConnections`, each null when unread |
| `GET  /mgmt/deploys` · `GET /mgmt/deploys/{id}` | Deploy history + live per-instance rollout progress |
| `GET  /mgmt/services/{name}/logs` | Centralized logs for a service/instance |
| `POST /mgmt/services/{name}/stop` · `/start` | Set desired status (fleet converges) |
| `POST /mgmt/services/{name}/restart` | Stamp `restart_requested_at` → every instance rolling-recreates its container via blue-green (same digest/tag, old serves until green is ready), fleet-wide. A restart on a stopped service starts it (like `/start`). Allowed to `ops-admin` and to a token with the `mgmt/jobs` scope, so a scheduled job can bounce a service it depends on; stop, start and manifest edits stay admin only |
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

## Scheduled jobs

A job is a named script with an optional five-field cron schedule, evaluated in
the job's IANA time zone. When it is due, or when an operator presses run, the
fleet runs the script once as a short-lived container from a runner image and
records the result as a run. One run at a time per job; a run has a timeout. The
design is [`docs/jobs.md`](docs/jobs.md); the job model, the store, the
management API, the `ops:jobs` events and the executor are all built.

State lives in three tables (tech-spec §4.4): `job` (the current script, schedule,
image, timeout, `next_run_at`), `job_script_version` (every script body with its
sha256) and `job_run` (the run ledger, kept after a job is deleted).
`next_run_at` is recomputed with Cronos on every upsert, enable and disable, and
is null when the job is disabled or has no schedule.

| Endpoint | Policy | Action |
|---|---|---|
| `GET /mgmt/jobs` | OpsDeploy | Every job with schedule, next run, last run, enabled, script version. Never the script body |
| `GET /mgmt/jobs/{name}` | OpsDeploy | One job including its script |
| `PUT /mgmt/jobs/{name}` | OpsJobs | Create (`201`, needs `timeZone` and `script`) or update (`200`). Body `{ schedule?, timeZone?, script?, image?, tag?, envSecretRef?, timeoutSeconds?, enabled? }`; an absent field preserves, an empty string clears `schedule` and `envSecretRef`. A changed script bumps `scriptVersion` and adds a history row. `400 { error, field }` on a bad name, cron, zone, timeout (30..21600, default 3600), script (non-empty, at most 256 KiB) or empty image/tag |
| `DELETE /mgmt/jobs/{name}` | OpsJobs | `204`. `409 { error: "run_active" }` while a run is queued or running unless `?force=true`, which cancels it first. Run history stays |
| `POST /mgmt/jobs/{name}/run` | OpsJobs | Queue a manual run: `202 { runId }`, `409 { error: "run_active" }` when one is active. Allowed on a disabled job |
| `POST /mgmt/jobs/{name}/enable` and `/disable` | OpsJobs | Flip `enabled`, recompute `next_run_at` |
| `GET /mgmt/jobs/{name}/runs?limit=50&before={id}` | OpsDeploy | Runs newest first |
| `GET /mgmt/jobs/{name}/runs/{id}` | OpsDeploy | One run |
| `POST /mgmt/jobs/{name}/runs/{id}/cancel` | OpsJobs | `202`: a queued run becomes `cancelled` at once, a running run gets `cancelRequestedAt` and is killed by its instance. `409` when already finished |
| `GET /mgmt/jobs/{name}/runs/{id}/log?tail=500` | OpsAdmin | `[{ ts, message }]` from log group `/gateway/jobs/{name}`, stream `run-{id}` |
| `GET /mgmt/jobs/{name}/script/versions` | OpsDeploy | `[{ version, sha256, updatedBy, updatedAt }]`, newest first |
| `GET /mgmt/jobs/{name}/script/versions/{v}` | OpsDeploy | `{ version, script }` |

**OpsJobs policy:** a member of `ops-admin`, or a client-credentials token
carrying the scope `mgmt/jobs`. A job script runs with the instance role, so
`ops-deploy` and the CI `mgmt/deploy` scope do not qualify; a repository whose CI
should push its script uses a separate client an operator creates with
`mgmt/jobs`. Every mutation records the caller in `updated_by` or the run's
`actor`.

**Defaults:** `Jobs:DefaultImage` (default `gateway-job-runner`, a placeholder to
point at the runner repository in your registry) and `Jobs:DefaultTag` (default
`latest`) fill in `image` and `tag` when a create omits them.

**Logs:** a run writes to CloudWatch log group `/gateway/jobs/{name}`, stream
`run-{id}`, read through the same log store as service logs. The group is made by
the awslogs driver and gets the same 30-day retention as service groups the first
time an instance uses it. The `json-file` escape hatch (`GATEWAY_LOG_DRIVER`)
applies to job containers exactly as to services.

### Execution

`JobSchedulerService` runs on every instance when a database is configured and
ticks every `Jobs:TickInterval`. Leadership is the answer the reconciler's last
loop published (the same in-process snapshot `GET /internal/leader` reads); the
scheduler never runs the election itself, and treats an answer older than
`Reconciler:InstanceStaleThreshold` as not leader.

Leader duties, each tick:

1. **Claim due scheduled runs.** For each enabled job with `next_run_at <= now`
   and no queued or running run, insert a `queued` run (trigger `schedule`,
   actor `schedule`) and advance `next_run_at` to the next occurrence after now,
   in one transaction conditional on `next_run_at` being unchanged. A job that
   missed several slots (the fleet was down, or its previous run was still
   active) runs once.
2. **Mark lost runs.** A `running` run whose instance has no `instance_status`
   heartbeat within the stale threshold becomes `lost` with a detail line.
3. **Claim queued runs**, oldest first, with one conditional update (`queued` to
   `running`, `instance_id` = this instance, `started_at` = now), no more than
   `Jobs:MaxConcurrent` minus the runs this instance already has running. Two
   leaders ticking together start a run once: only one conditional update wins.

Own duties, every tick on every instance:

- **Drive** each run this instance owns on a background task: read the job's env
  from `envSecretRef` (the same Secrets Manager provider and cache services use),
  add `GATEWAY_JOB_NAME`, `GATEWAY_JOB_RUN_ID`, `GATEWAY_JOB_TRIGGER` and
  `GATEWAY_JOB_SCRIPT` (the script, base64 of UTF-8), pull `image:tag` and record
  the digest, then run container `job-{name}-{runId}` on the internal network with
  no exposed or published port, no restart policy, the `Jobs:MemoryLimitBytes`
  memory limit and only the labels `gateway.job` and `gateway.job-run` (never
  `gateway.managed`, and the reconciler's inventory and orphan handling skip any
  container with the job label). The container is removed when it ends.
- **Finalise:** exit 0 is `succeeded`, any other exit `failed`, the job timeout
  kills the container and records `timed_out`, a cancel request kills it and
  records `cancelled`. `exit_code`, `finished_at` and the last 20 output lines (in
  `detail`, at most 2 KiB) are stored and a `run` event goes out on `ops:jobs`.
- **Cancel:** `cancel_requested_at` is polled for every driven run each tick.
- **Re-attach:** a gateway that stops mid-run leaves the container running. On
  the next process's first tick, a `running` run this instance owns that it did
  not start itself is re-attached through its `gateway.job-run` label and
  finalised with the container's real exit code; with no container left it is
  `lost`.

| Case | Behaviour |
|---|---|
| Secret missing or not flat JSON | `failed` before start, detail names the ref (never a value), nothing executes |
| Image pull fails | `failed`, detail carries the registry error |
| Script exits non-zero | `failed` with the exit code and the tail |
| Timeout | Container killed, `timed_out` |
| Leader dies mid-run | The run's own instance finishes it; if that instance is gone the leader marks it `lost` |
| Gateway restarts mid-run | The container continues; the same instance re-attaches by label on its first tick |
| Two leaders for one tick | The claim is a conditional update; one wins |
| Job deleted mid-run | `409` unless forced; forced cancels first |

**Metrics:** every finished run records `JobRunCount` (dimensions `Job`,
`Status`) and `JobRunDuration` in seconds (dimension `Job`) on the in-process
meter `Gateway.Jobs` and as an embedded-metric-format document sent to the
CloudWatch agent's EMF listener, which the bootstrap step enables, so both land
in the gateway's metric namespace. An alarm on `failed`, `timed_out` and `lost`
counts is an operator choice per job.

| Key | Default | Meaning |
|---|---|---|
| `Jobs:TickInterval` | `00:00:10` | Time between scheduler ticks |
| `Jobs:MaxConcurrent` | `4` | Most runs one instance drives at once; the rest wait in `queued` |
| `Jobs:MemoryLimitBytes` | `1073741824` | Memory limit of a job container (1 GiB) |
| `Jobs:MetricsEndpoint` | `127.0.0.1:25888` | CloudWatch agent EMF listener for run metrics (UDP); empty turns the send off |
| `Jobs:MetricsNamespace` | `Gateway` | CloudWatch namespace of the run metrics |
| `Jobs:DefaultImage`, `Jobs:DefaultTag` | see above | Image and tag a create gets when it omits them |

**Real-time:** the `ops:jobs` hub channel (same gate as `ops:deploys`) carries
`job` `{ name, action, by }` on upsert, delete, enable and disable, and `run`
`{ runId, job, status, trigger, actor, instanceId, startedAt, finishedAt,
exitCode }` on every run status change. Publishing is best-effort: a backplane
failure never fails the request.

## Load runs

A load run ramps a fleet of k6 generators against a service behind the gateway,
collects their stats every five seconds, and can never outlive its hard clock.
The design is [`docs/load.md`](docs/load.md): the load model, the store, the
management API, the two generator endpoints, pre-flight, the `ops:load` events
and the load scheduler that drives a run on the fleet leader.

A load run never touches the fleet. The only AWS resource the gateway steers is
the generator Auto Scaling group, through `IGeneratorGroupScaler`: it reads the
group's desired, max and in-service counts and sets its desired capacity
(`AwsGeneratorGroupScaler` once `Load:GeneratorGroup` is set, default credential
chain and region from the environment; `NullGeneratorGroupScaler`, which refuses
every call, otherwise). The fleet Auto Scaling group and its launch template, the
ElastiCache backplane and the RDS instance are sized by hand in the AWS console;
a `Resources:*` key left in the environment is ignored.

**IAM (instance role)** for load runs: `autoscaling:DescribeAutoScalingGroups`
and `autoscaling:SetDesiredCapacity` on the generator group,
`secretsmanager:PutSecretValue` on the generator spec secret, and the ECR and
CloudWatch Logs rights the gateway already holds (the generator log route reads
group `/gateway/load`). Nothing else: an inline policy that still grants launch
template, ElastiCache or RDS rights can be trimmed by hand.

State lives in four tables (tech-spec §4.4): `load_scenario` (the current k6
script, repository, image, default params, users per generator, last validation
run), `load_scenario_version` (every script body with its sha256, kept after a
delete), `load_run` (the run ledger with its token hash, check-ins, peaks and
timeline; `spec_token` holds the token from Start until the leader has written
the spec secret) and `load_run_stat` (every stats line, indexed on `(run_id, at)`).

| Endpoint | Policy | Action |
|---|---|---|
| `GET /mgmt/load/scenarios` | OpsDeploy | Every scenario with version, repository, commit, validation state, users per generator. Never the script body |
| `GET /mgmt/load/scenarios/{name}` | OpsDeploy | One scenario including its script |
| `PUT /mgmt/load/scenarios/{name}` | OpsLoad | Create (`201`, needs `repository`, `script`, `maxVusPerGenerator`) or update (`200`). Body `{ repository?, commitSha?, script?, image?, tag?, paramsDefault?, maxVusPerGenerator? }`. A changed script bumps `scriptVersion`, adds a history row and clears `lastValidationRunId`. `400 { error, field }` on a bad name, repository (`owner/name`), commit, script (non-empty, at most 256 KiB), params (a flat object of strings) or users (1..100000) |
| `DELETE /mgmt/load/scenarios/{name}` | OpsLoad | `204`; `409 run_active` while a run of it is non-terminal. History and runs stay |
| `GET /mgmt/load/scenarios/{name}/script/versions` and `/script/versions/{v}` | OpsDeploy | `[{ version, sha256, updatedBy, updatedAt }]` newest first, and `{ version, script }` |
| `GET /mgmt/load/preflight?scenario=&target=&confirm=&generators=&vus=&validation=` | OpsDeploy | `{ ok, checks: [{ name, status, message, fix }] }` |
| `POST /mgmt/load/runs` | OpsLoad | Start. Body `{ scenario, target, confirm, generators, vusPerGenerator, rampSeconds, holdSeconds, params?, validation? }`. `202 { runId }`; `400 { error, field }`; `503 load_unconfigured` when `Load:GeneratorGroup` is unset; `409 run_active` while any run is non-terminal; `409 preflight_failed { checks }` when a check is red |
| `GET /mgmt/load/runs?limit=50&before={id}` | OpsDeploy | Runs newest first, with peaks, without timeline or stats |
| `GET /mgmt/load/runs/{id}` | OpsDeploy | One run with timeline, peaks and check-ins |
| `GET /mgmt/load/runs/{id}/stats?from=&to=&source=&index=&step=` | OpsDeploy | Stats rows; `step` downsamples to one row per bucket, the maximum of each numeric leaf |
| `GET /mgmt/load/runs/{id}/stats.csv` | OpsDeploy | The same rows, one column per numeric leaf |
| `POST /mgmt/load/runs/{id}/stop` | OpsLoad | `202`: stamps `stopRequestedAt` (the first request wins). `409 run_finished` when terminal |
| `PUT /mgmt/load/runs/{id}/verdict` | OpsLoad | `{ verdict }` (at most 500 characters, empty clears) on a terminal run; `409 run_active` otherwise |
| `DELETE /mgmt/load/runs/{id}` | OpsLoad | `204`: removes a terminal run and its stats; `409 run_active` otherwise |
| `POST /mgmt/load/runs/{id}/checkin` | run token | `{ instanceId }` to `200 { index, count, vus, rampSeconds, holdSeconds, scenario, target, script, params, statsUrl }`, `409 run_full` or `409 run_not_accepting` |
| `POST /mgmt/load/runs/{id}/stats` | run token | A generator stats line: `200 { action: "continue" \| "stop" }`, `409 run_terminal` |
| `GET /mgmt/load/runs/{id}/log?index=0&tail=500` | OpsAdmin | `[{ ts, message }]` from log group `/gateway/load`, stream `run-{id}-{index}` |
| `GET /mgmt/load/generators` | OpsDeploy | `{ group, desired, inService, max }` of the generator group, read through the generator group scaler, for the dashboard banner. `503 load_unconfigured` when `Load:GeneratorGroup` is unset, `404 generator_group_not_found`, `502 generator_group_unreadable` |
| `GET /mgmt/load/audit?limit=50` | OpsDeploy | `[{ id, at, actor, resource, action, before, after, detail }]` from `resource_audit`, newest first, `limit` 1..500 (`400 { error, field }` outside it): the orphan guard's rows, and the rows earlier releases wrote |

**OpsLoad policy:** a member of `ops-admin`, or a client-credentials token
carrying the scope `mgmt/load`. Start scales the generator group, so `ops-deploy`, the CI
`mgmt/deploy` scope and the `mgmt/jobs` scope do not qualify; a repository whose
CI pushes its scenario script uses a separate client an operator creates with
`mgmt/load`.

**Run token:** Start mints 32 random bytes (base64url) and stores only their
sha256 in `token_hash`. The check-in and stats endpoints are anonymous and take
the token in `X-Gateway-Load-Token`: the hash comparison is constant time, a
wrong token and an unknown run answer the same `401`, the body is capped at
64 KiB (`413`), and each source address (the last `X-Forwarded-For` entry, else
the connection) gets 5 requests per second with a burst of 10 (`429` with
`Retry-After`).

**Check-in** hands out generator indexes in order with a conditional update on
`checked_in`, so two instances never share one; a repeated check-in from the
same instance gets its earlier index back. **Stats** lines are stored as
received, raise the run's `peaks` (the maximum of every numeric leaf, keyed
`metric.field`), and are merged into a fleet-wide aggregate over the newest line
of each generator (counters and gauges summed, trends by the worst value). The
answer is `stop` once a stop is requested or the run is draining.

**Pre-flight** checks, each `green`, `amber` or `red`: `generator_group` (the
generator group is empty and its max fits the run, read through `IGeneratorGroupScaler`),
`fleet_healthy` (every `instance_status` row heartbeats and runs the target),
`target_known`, `scenario_validated` (the last validation ran the current
script version; skipped for a validation run), `presence_off` (the target's
`realtime_presence` is off) and `target_confirmed` (`confirm` equals the
target). A load test is meant to find resource limits, so pre-flight does not
check database, cache or fleet capacity.

### Load scheduler

`LoadSchedulerService` drives a run from `queued` to a terminal status. It ticks
every `Load:TickInterval` (10 s) after the migration gate, acts only while
`ILeaderStatus` holds a fresh leader answer (the job scheduler's seam), and takes
one step per tick. Every status change is one conditional update on the run's
status and timeline, appends a timeline entry by `gateway` and publishes `run` on
`ops:load`, so two leaders in the same tick cannot both act.

| Status | The leader | Leaves when |
|---|---|---|
| `queued` | claims it (one run at a time) and runs pre-flight again with `confirm` = target | claimed; a red check drains it with reason `failed`, `detail` = the check name |
| `generators_up` | writes the generator spec secret once, then sets only the generator group's desired to `generators` | every generator checked in (`ramp_started_at` stamped); `failed`/`checkin_timeout` after `Load:CheckInTimeout` |
| `ramping` | | `ramp_seconds` after `ramp_started_at` |
| `holding` | | `hold_seconds` after it began |
| `draining` | keeps the generator group at 0 | the generator group has zero in service, then `succeeded` (no reason), `stopped` (operator), `expired` (hard_clock), `failed` or `lost` (lost, leader_restart) |

**Exits and guards:** in `generators_up`, `ramping` and `holding`
the leader drains a run past `hard_stop_at` (`hard_clock`), a ramping or holding
run whose newest generator line is older than `Load:StatsStaleAfter` (`lost`),
and a run with `stopRequestedAt` (`operator`). Every path into `draining` sets
the generator group's desired to 0 first. With no run non-terminal, a generator
group above desired 0 is set to 0, audited as `generators`/`orphan_guard` in
`resource_audit` (listed by `GET /mgmt/load/audit`) and published as `change` on
`ops:resources`. On gaining leadership (also after a process restart), a
non-terminal run this process is not driving is adopted when it is in
`generators_up`, ramping or holding with fresh stats, or already draining;
anything else drains with `leader_restart`. A generator group or secret call that
throws is retried on the next tick; after `Load:ScaleRetryLimit` failed ticks in a row the run fails, with the
generator group set to 0 regardless. A succeeded validation run records itself
as the scenario's `lastValidationRunId`.

**Generator spec secret:** written through `ISecretWriter` (Secrets Manager
`PutSecretValue`, so the gateway role needs it on that secret) to
`Load:GeneratorSpecSecret` as `{ runId, gatewayBaseUrl, token, image, tag }`, with
`gatewayBaseUrl` from `Load:GatewayBaseUrl` and the image and tag of the
scenario. The value is never logged or published.

**Fleet samples:** every `Load:FleetSampleInterval` while a run is non-terminal
the leader stores one `fleet` stats row `{ at, instances: [{ instanceId, leader,
heartbeatAt, gatewayVersion, runningServices, cpu, memory, disk, hubConnections }],
channelMembers }` from the instances with a fresh heartbeat (cpu, memory, disk and
hub connections are null: the heartbeat row does not carry them yet) and the
presence count of the run's `params.channel` (null without one), and publishes
`fleet` on `ops:load`.

**Metrics:** `ILoadMetrics` sends `LoadRunCount` (dimensions `Scenario`,
`Status`) and `LoadRunDuration` (seconds, `Scenario`) when a run ends, and
`LoadGeneratorStats` (`Scenario`) for every stored generator line, through the
same EMF path and namespace as the job metrics (`Jobs:MetricsEndpoint`,
`Jobs:MetricsNamespace`).

| Key | Default | Meaning |
|---|---|---|
| `Load:GeneratorGroup` | unset | Generator Auto Scaling group name; unset answers Start with `503 load_unconfigured` |
| `Load:GeneratorSpecSecret` | unset | Secrets Manager name of the generator spec secret |
| `Load:MaxGenerators` | `2` | Most generators a run may ask for |
| `Load:CheckInTimeout` | `00:05:00` | How long `generators_up` waits for every generator |
| `Load:StatsStaleAfter` | `00:01:00` | Ramp or hold with no generator line for this long marks the run `lost` |
| `Load:ValidationHoldSeconds` | `300` | Hold of a validation run |
| `Load:FleetSampleInterval` | `00:00:10` | How often the leader writes a fleet sample |
| `Load:TickInterval` | `00:00:10` | How often the load scheduler ticks |
| `Load:GatewayBaseUrl` | unset | Public origin the generators reach the gateway at, e.g. `https://gateway.example.com`; written into the spec secret, and a run fails in `generators_up` while it is unset |
| `Load:ScaleRetryLimit` | `6` | Failed ticks in a row of a run's scaling or secret step before the run fails |
| `Load:DefaultImage`, `Load:DefaultTag` | `Jobs:DefaultImage`, `Jobs:DefaultTag` | Runner image and tag a scenario gets when PUT omits them |

**Real-time:** the `ops:load` hub channel (same gate as `ops:deploys`) carries
`scenario` `{ name, action: upserted | deleted, by }`, `run` `{ runId, scenario,
status, actor, generators, vusPerGenerator, startedAt, finishedAt, stopReason }`
on Start, on a stop request and on every status change, `stats` `{ runId, at,
aggregate }` after every stored generator line, and `fleet` `{ runId, at, sample }`
after every fleet sample. The `ops:resources` channel carries `change`
`{ resource, action, by, before, after }` when the orphan guard writes its audit
row. Publishing is best-effort: a backplane failure never fails the request.

## Internal listener (containers only)

A second Kestrel endpoint bound from `GATEWAY_INTERNAL_BIND` (default `0.0.0.0:8080`)
hosts the routes downstream containers call into the gateway on. It is reachable
only from the Docker bridge network of the instance it runs on — never routed by
the load balancer — and the isolation middleware 404s these paths on the public
listener. Every endpoint below is gated by `X-Gateway-Realtime-Token` (constant-time
compared through the same token→service resolver, one place); the management
(Cognito) auth path is not accepted on this listener.

| Endpoint | Auth | Action |
|---|---|---|
| `POST /internal/publish` | Owner's publish token | Broadcast `{ channel, event, payload }` to the channel's SignalR group. `202` on success; `403` cross-service or on `ops:*`; `429` over the per-service publish budget. |
| `GET  /internal/presence/{channel}` | Owner's publish token | Pull the current members of one of your channels: `{ channel, count, members: [{ connectionId, identity, joinedAt }] }`. |
| `GET  /internal/leader` | Any registered service's publish token | Is the answering gateway's instance the fleet leader right now: `{ instanceId, isLeader, leaderInstanceId, evaluatedAt }`. Reads the reconciler's cached snapshot — no DB, no election. Before the first loop `isLeader=false` with null `leaderInstanceId`/`evaluatedAt`; a booting instance never claims leadership through this endpoint. Leadership may briefly overlap on a transition, so downstream leader-gated duties must be idempotent (see REALTIME.md §7). |

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
