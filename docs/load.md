# Load runs

Design for the gateway's load feature: a page on the dashboard that ramps a
fleet of fake users against any service behind the gateway, shows where things
break, and can never leave generators running. The gateway steers one pre-made
generator Auto Scaling group and its own fleet group; it never creates
infrastructure. The companion Resources page scales what already exists. This
document is the contract for the build tasks at the end.

Status: designed 2026-10-29, building v1. Built and verified so far: task 0 (wmsfo-api `tools/loadtest`, one-user run against dev passed) and task 1 (runner image with k6 1.8.2 and the load mode, smoke passed). The first scenario is an anonymous
WMSFO tracker visitor on the dev stack; the first generator group is one
t4g.small. Phase 2 (the signed-in flow, bigger generators, CloudWatch-backed
fleet metrics, the 100k ladder) is listed in section 13 and is not part of v1.
Part 1 built (build plan item 2): the data model and the `AddLoad` migration,
`ILoadStore` (EF and no-database), the `/mgmt/load` API with the OpsLoad policy,
the two generator endpoints, pre-flight behind `ILoadPreflight` with
`IDatabaseHeadroom` and the `IFleetScaler` seam, the `ops:load` events and the
`Load` options. No scheduler yet: a started run stays `queued`.
Resources built (build plan item 3): `IFleetScaler`, `ICacheScaler` and
`IDatabaseScaler` with AWS SDK implementations registered when their keys are
set (null ones otherwise), the `/mgmt/resources` API, the `resource_audit` table
(migration `AddResourceAudit`), the `ops:resources` events and the fleet refresh
watcher; `generator_group` and `fleet_class` now read through the AWS fleet
scaler.

## 1. What a load run is

A scenario is a script plus parameters, registered with the gateway and owned
by the application it tests (a WMSFO scenario lives in the wmsfo-api
repository, a future wisper scenario in wisper's). A run names a scenario, a
target service, a number of generators, the virtual users each generator
carries, a ramp and a hold. When the operator presses Start, the fleet leader
checks pre-flight, optionally scales the fleet group, scales the generator group
to the requested count, hands each generator its share through a check-in,
collects a stats line from each every five seconds while the run ramps and
holds, then scales the generator group back to zero, restores the fleet, and
freezes the record. Every exit path passes through "generator group to zero".

Compared with a job: many containers on instances outside the fleet instead of
one container on the leader; a clock the run cannot outlive; stats coming back
while it runs. A load run reuses the job patterns (script versions, the runner
image, leader-claimed runs, the CI client) and none of the Jobs UI.

Not in scope: creating or deleting stacks, instances, roles or groups; running
more than one load run at a time; retries.

## 2. Data model

EF Core migration `AddLoad`, snake_case like the rest.

```sql
load_scenario (
  name                    text primary key,       -- ^[a-z0-9][a-z0-9-]{1,62}$
  repository              text not null,          -- owner/name of the repo that owns the script
  commit_sha              text,                   -- the commit the script was pushed from, when known
  script                  text not null,          -- the k6 script body, <= 256 KiB
  script_version          int  not null,          -- bumps when the script body changes
  image                   text not null,          -- runner repository, default from config
  tag                     text not null,          -- default 'latest'
  params_default          text not null,          -- JSON object of strings, '{}' when none
  max_vus_per_generator   int  not null,          -- the most users one generator may carry, 1..100000
  last_validation_run_id  bigint,                 -- the newest validation run that succeeded
  updated_by              text not null,
  updated_at              timestamptz not null
)
load_scenario_version (scenario, version, script, sha256, updated_by, updated_at,
                       primary key (scenario, version))
load_run (
  id                    bigint generated always as identity primary key,
  scenario              text not null,            -- not a foreign key: history outlives a deleted scenario
  scenario_version      int  not null,
  target                text not null,            -- manifest service name the scenario is aimed at
  validation            boolean not null,         -- true for the one-generator, one-user validation run
  actor                 text not null,            -- Cognito username or client id
  status                text not null,            -- see section 3
  generators            int  not null,            -- 1..Load:MaxGenerators
  vus_per_generator     int  not null,            -- 1..scenario.max_vus_per_generator
  ramp_seconds          int  not null,            -- 0..7200
  hold_seconds          int  not null,            -- 0..10800
  params                text not null,            -- JSON object of strings, params_default merged with the run's overrides
  fleet_size_requested  int,                      -- null: leave the fleet alone
  fleet_size_before     int,                      -- the fleet group's desired size when the run started
  restore_fleet         boolean not null,
  hard_stop_at          timestamptz not null,     -- started_at + ramp + hold + 10 min, at most started_at + 4 h
  token_hash            text not null,            -- sha256 of the run token the generators present
  checked_in            int  not null,            -- generators that have checked in
  checkins              text not null default '{}', -- JSON object of instance id to assigned index
  queued_at             timestamptz not null,
  started_at            timestamptz,              -- when the status left queued
  ramp_started_at       timestamptz,
  finished_at           timestamptz,
  stop_requested_at     timestamptz,
  stop_reason           text,                     -- operator | hard_clock | lost | leader_restart | failed
  verdict               text,                     -- one line the operator writes afterwards, <= 500 chars
  detail                text,                     -- last error or note, <= 2 KiB
  peaks                 text not null,            -- JSON: the maximum of every numeric stat seen, '{}' at first
  timeline              text not null             -- JSON array of { at, status, by, note }
)
load_run_stat (
  id          bigint generated always as identity primary key,
  run_id      bigint not null,
  source      text not null,                      -- 'generator' or 'fleet'
  index       int,                                -- generator index; null for fleet samples
  at          timestamptz not null,
  body        text not null                       -- the stats line as received, <= 64 KiB
)
-- index on (run_id, at)
-- at most one non-terminal run (Postgres only, a constant-expression partial index)
create unique index ux_load_run_active on load_run ((true))
  where status not in ('succeeded', 'stopped', 'expired', 'failed', 'lost')
```

`checkins` is what makes a repeated check-in answer the same index: the
check-in update sets `checked_in = n + 1` and adds the instance to `checkins`
conditional on `checked_in` still being `n`, so the previous value is the
assigned index and a losing concurrent check-in reads again.

The run token is 32 random bytes, base64url, minted at Start, shown to nobody,
written into the generator spec secret (section 5) and compared by hash on
every check-in and stats line. It is useless after the run finishes.

## 3. Run lifecycle

```
queued -> fleet_up -> generators_up -> ramping -> holding -> draining -> succeeded
                                                                     -> stopped | expired | failed | lost
```

| Status | Meaning | Leaves when |
|---|---|---|
| `queued` | Start accepted; pre-flight passed at Start | the leader claims it (one load run at a time: a second Start answers `409 run_active`) |
| `fleet_up` | fleet group set to `fleet_size_requested` (skipped when null) | every fleet instance has a fresh heartbeat and `GET /api/health` on the proxy answers 200 for the target service |
| `generators_up` | generator spec secret written, generator group set to `generators` | `checked_in == generators` (section 5) |
| `ramping` | generators are raising virtual users over `ramp_seconds` | `ramp_seconds` after `ramp_started_at` |
| `holding` | steady state | `hold_seconds` after the ramp ends |
| `draining` | generator group set to 0; fleet restored when `restore_fleet` | the generator group reports zero instances in service and the fleet refresh (if any) is done |
| terminal | `succeeded`, `stopped` (operator), `expired` (hard clock), `failed` (pre-flight at claim, scaling error, check-in timeout), `lost` (no generator stats for 60 s during ramp or hold, or a leader restart found a run it cannot re-attach) | never |

Every path into `draining` sets the generator group's desired capacity to 0
first and records the reason in `stop_reason`. The leader re-evaluates every
non-terminal run on every tick (10 s), so a crash between two steps is retried
rather than forgotten. The hard clock is absolute (`hard_stop_at`), written at
Start, and cannot be extended: a longer run is a new run.

Leader duties, every tick:

1. Claim the oldest `queued` run with a conditional update when no run is
   non-terminal.
2. Drive the claimed run through the states above, one step per tick, reading
   the two groups' desired and in-service counts through `IFleetScaler`.
3. Expire a run past `hard_stop_at`; mark a run `lost` when ramp or hold has
   had no generator stats line for 60 s.
4. Reconcile on gaining leadership: a non-terminal run with no driver on this
   instance is driven to `draining` with reason `leader_restart` unless it is
   in `ramping` or `holding` and stats are still arriving, in which case it is
   adopted and continues.
5. Guard: when no run is non-terminal and the generator group's desired
   capacity is above 0, set it to 0 and write an audit row (reason
   `orphan_guard`). This is the backstop for anything that slipped.

Only the leader drives load runs; there is nothing for the other instances to
do. Stats and check-ins arrive on whichever instance the load balancer picks
and are written to the database, where the leader reads them.

## 4. Scenario contract

A scenario is a k6 script. The generator runs the runner image (section 7),
which runs k6 against the script with this environment:

| Variable | Meaning |
|---|---|
| `GATEWAY_LOAD_RUN_ID` | The run's id. |
| `GATEWAY_LOAD_SCENARIO` | Scenario name. |
| `GATEWAY_LOAD_SCRIPT` | The script body, base64 of UTF-8 (decoded by the entrypoint, like a job script). |
| `GATEWAY_LOAD_INDEX`, `GATEWAY_LOAD_COUNT` | This generator's zero-based index and the generator count. |
| `GATEWAY_LOAD_VUS` | Virtual users this generator owns. |
| `GATEWAY_LOAD_RAMP_SECONDS`, `GATEWAY_LOAD_HOLD_SECONDS` | The run's ramp and hold. The script shapes its stages from these. |
| `GATEWAY_LOAD_PARAMS` | The run's params as a JSON object of strings. |
| `GATEWAY_LOAD_TARGET` | The target manifest service name. |
| `GATEWAY_LOAD_STATS_URL`, `GATEWAY_LOAD_TOKEN` | Where the entrypoint's reporter posts stats and the run token. The script never reads these; the reporter in the entrypoint does. |

Inside k6 these are `__ENV.GATEWAY_LOAD_*`. The script must:

- define its load with a `ramping-vus` executor whose stages are `[{ duration: RAMP, target: VUS }, { duration: HOLD, target: VUS }]`, so the gateway's ramp and hold are the script's;
- set `summaryTrendStats` to `["avg", "min", "med", "max", "p(95)", "p(99)"]` for the end-of-run summary; note that k6's REST API, which the reporter reads, carries `p(90)` and `p(95)` for a trend and no `p(99)`, so the page draws `p(95)` as the high percentile;
- keep every custom metric name in `snake_case` with a unit suffix where one applies (`event_lag_ms`, `poll_ms`, `event_gap_ms`, `ws_errors`);
- read every URL and name from `__ENV.GATEWAY_LOAD_PARAMS`, never from a literal, so the same script runs against dev and prod;
- never carry a credential in the script text: secrets, when phase 2 needs them, arrive through the run's secret reference.

A validation run (`validation = true`, one generator, one virtual user, ramp 0,
hold `Load:ValidationHoldSeconds`, default 300) is an ordinary run whose success
is recorded on the scenario as `last_validation_run_id`; the script may read
`GATEWAY_LOAD_VUS == 1` to tighten its checks. A scenario whose script changed
since its last validation must be validated again before a non-validation run
(pre-flight, section 6).

### Stats line

The runner's reporter posts one line every five seconds per generator to
`GATEWAY_LOAD_STATS_URL` with header `X-Gateway-Load-Token`:

```json
{ "runId": 41, "index": 0, "at": "2026-11-05T02:14:05Z", "vus": 480,
  "metrics": {
    "vus":          { "type": "gauge",   "value": 480 },
    "ws_sessions":  { "type": "counter", "count": 480, "rate": 0 },
    "event_lag_ms": { "type": "trend",   "avg": 41, "min": 12, "med": 38, "max": 410, "p(90)": 95, "p(95)": 120 },
    "event_gap_ms": { "type": "trend",   "avg": 1001, "min": 940, "med": 1000, "max": 1310, "p(90)": 1040, "p(95)": 1080 },
    "seq_regressions": { "type": "counter", "count": 0, "rate": 0 },
    "ws_errors":    { "type": "counter", "count": 1, "rate": 0 }
  } }
```

`runId`, `index`, `at` and `vus` are required; `metrics` is whatever k6
reports, in k6's own metric shapes, copied through by the reporter without
interpretation. The gateway stores the line, updates `peaks` (the maximum of
every numeric leaf, keyed `metric.field`), and republishes a fleet-wide
aggregate on `ops:load` (counters summed, gauges summed, trends merged by the
worst value of each field). The page draws whatever metric names arrive; a
wisper scenario can report queue depth where WMSFO reports event lag.

The stats answer is `200 { "action": "continue" }` or `200 { "action": "stop" }`.
On `stop` the reporter sends k6 SIGTERM (graceful stop), posts one final line,
and exits; the generator instance is terminated by the group's scale-in
regardless.

### Fleet samples

While a run is not terminal the leader writes one `fleet` stats row every 10 s
from what the gateway already knows: for every instance in `instance_status`
with a fresh heartbeat, the node stats the Node stats page shows (cpu, memory,
disk, the instance's hub connection count) and, for the target service, the
member count of channel `<target>:location` from the presence registry when the
scenario's params name a `channel`, else nothing. Load balancer, Redis and
database metrics from CloudWatch are phase 2.

## 5. Generators

The generator group is created once, by an operator, from the CloudFormation
template `deploy/load-generators.yaml` (placeholders only; the real values are
stack parameters). It holds: an Auto Scaling group with min 0 and a configured
max, one launch template (Amazon Linux 2023 arm64, the instance type a
parameter), a security group with no inbound rule, an instance role that may
pull from the runner's ECR repository, write the log group, and read one
secret, and the log group `/gateway/load` with 14-day retention. The gateway
reads the group's name from `Load:GeneratorGroup` and the secret's name from
`Load:GeneratorSpecSecret`. v1 is one t4g.small with max 2.

At Start the leader writes the generator spec secret:

```json
{ "runId": 41, "gatewayBaseUrl": "https://<gateway-domain>", "token": "<run token>",
  "image": "<account>.dkr.ecr.<region>.amazonaws.com/gateway-job-runner", "tag": "latest" }
```

The launch template's user data, on boot: read the secret through the instance
role, log in to ECR, pull `image:tag`, then `POST {gatewayBaseUrl}/mgmt/load/runs/{runId}/checkin`
with header `X-Gateway-Load-Token` and body `{ "instanceId": "i-..." }`. The
gateway answers, on the public listener with the token as the only credential:

- `200 { index, count, vus, rampSeconds, holdSeconds, scenario, target, script, params, statsUrl }` (`script` is the plain script body; the user data base64-encodes it into `GATEWAY_LOAD_SCRIPT` for the runner, and `statsUrl` is built from the request's forwarded scheme and host)
  when the run is in `generators_up` and `checked_in < generators`; `index` is
  assigned in check-in order and `checked_in` is incremented in the same
  conditional update, so two instances never share an index. A repeat
  check-in from the same `instanceId` answers the same assignment while the run
  is in `generators_up`, `ramping` or `holding`. `script` is the plain script
  body of the run's scenario version (the user data base64-encodes it into
  `GATEWAY_LOAD_SCRIPT`); `statsUrl` is built from the request's host and
  `X-Forwarded-Proto`.
- `409 { error: "run_full" }` or `409 { error: "run_not_accepting" }`
  otherwise; the user data then shuts the instance down (the group will
  replace it only if desired capacity still says so, and the leader lowers it
  when the run leaves `generators_up`).

The user data then runs the container with the environment of section 4 and
`--log-driver awslogs` to group `/gateway/load`, stream `run-{id}-{index}`. A
generator that has not checked in within `Load:CheckInTimeout` (default 5 min)
of entering `generators_up` fails the run (`failed`, detail `checkin_timeout`).

The user data never carries a credential: the secret is the only input, and
the instance role is the only way to read it.

## 6. Pre-flight

Evaluated by `GET /mgmt/load/preflight?scenario=&target=&generators=&vus=&fleet=`
for the page, and again at claim time by the leader (a red check at claim
fails the run with the check's name in `detail`). Each check answers
`{ name, status: green | amber | red, message, fix }`; Start is refused by the
page while any check is red, and by the API with `409 preflight_failed`.

| Check | Green when | Red says | v1 |
|---|---|---|---|
| `generator_group` | desired 0, no instance in service, `generators <= max` | a previous run left instances, or the group is smaller than the run | yes |
| `fleet_healthy` | every fleet instance has a fresh heartbeat and the target service is running on each | name the stale instance | yes |
| `target_known` | the target is a manifest service | not a service | yes |
| `scenario_validated` | `last_validation_run_id` is set and its run's `scenario_version` equals the current version (skipped for a validation run) | validate first | yes |
| `presence_off` | the target service's `realtime_presence` is false | a join batch would broadcast to every member | yes |
| `database_headroom` | `max_connections - in use >= fleet nodes * Load:DbConnectionsPerNode + Load:DbConnectionMargin` on the gateway's own database connection | name the numbers; fix: the database card on Resources | yes |
| `fleet_class` | the fleet launch template's `$Default` instance type is not a burstable family (`t2`, `t3`, `t3a`, `t4g`) | amber, not red, in v1 (the first runs are small and the fleet is t4g.small) | amber |
| `target_confirmed` | the page sent `confirm` equal to the target name | type the target to confirm; production is allowed | yes |
| `file_descriptors`, `backplane`, `spend` | phase 2 | | no |

## 7. Runner image

The job runner image (`gateway-job-runner`) gains k6 (the release binary for
the image's architecture, pinned by version in the Dockerfile) and a second
entrypoint mode. When `GATEWAY_LOAD_RUN_ID` is set the entrypoint:

1. decodes `GATEWAY_LOAD_SCRIPT` to `/job/scenario.js` (exit 64 on a missing or
   broken script, as for jobs) and removes it from the environment;
2. starts `k6 run --address 127.0.0.1:6565 --quiet /job/scenario.js` in the
   background with the `GATEWAY_LOAD_*` environment passed through;
3. runs the reporter: every five seconds, `GET http://127.0.0.1:6565/v1/metrics`
   from k6's REST API, builds the stats line of section 4 from it with jq, and
   posts it to `GATEWAY_LOAD_STATS_URL` with `X-Gateway-Load-Token`; on
   `{ "action": "stop" }` sends k6 SIGTERM, waits for it, posts one last line
   and exits 0; a post failure is logged and skipped, never fatal;
4. exits with k6's exit code when k6 ends on its own.

Without `GATEWAY_LOAD_RUN_ID` the entrypoint behaves exactly as today. The
image stays public and credential-free.

## 8. Management API

Under `/mgmt/load`, public listener, Cognito like everything else, except the
two generator endpoints, which take the run token.

| Endpoint | Policy | Action |
|---|---|---|
| `GET /mgmt/load/scenarios` | OpsDeploy | Every scenario with version, repository, commit, validation state, max users per generator. Never the script body. |
| `GET /mgmt/load/scenarios/{name}` | OpsDeploy | One scenario with the script body. |
| `PUT /mgmt/load/scenarios/{name}` | OpsLoad | Create or update. Body `{ repository?, commitSha?, script?, image?, tag?, paramsDefault?, maxVusPerGenerator? }`. A create requires `repository`, `script` and `maxVusPerGenerator`. A changed script bumps `script_version`, writes a history row, and clears `last_validation_run_id`. Validation: name pattern, script size, params a flat JSON object of strings, range. |
| `DELETE /mgmt/load/scenarios/{name}` | OpsLoad | `204`; `409 run_active` while a run of it is non-terminal. History rows stay. |
| `GET /mgmt/load/scenarios/{name}/script/versions` and `/script/versions/{v}` | OpsDeploy | As jobs. |
| `GET /mgmt/load/preflight?...` | OpsDeploy | Section 6. |
| `POST /mgmt/load/runs` | OpsLoad | Start. Body `{ scenario, target, confirm, generators, vusPerGenerator, rampSeconds, holdSeconds, params?, fleetSize?, restoreFleet, validation }`. `202 { runId }`; `409 run_active` when a run is non-terminal; `409 preflight_failed { checks }`; `400 { error, field }`. |
| `GET /mgmt/load/runs?limit=50&before={id}` | OpsDeploy | Runs newest first, without stats. |
| `GET /mgmt/load/runs/{id}` | OpsDeploy | One run with timeline and peaks. |
| `GET /mgmt/load/runs/{id}/stats?from=&to=&source=&index=&step=` | OpsDeploy | Stats rows, optionally downsampled to one row per `step` seconds (the maximum of each numeric leaf within the bucket). |
| `GET /mgmt/load/runs/{id}/stats.csv` | OpsDeploy | The same, flattened one column per numeric leaf. |
| `POST /mgmt/load/runs/{id}/stop` | OpsLoad | `202`; stamps `stop_requested_at`, the leader drives to `draining` with reason `operator` on its next tick. `409` when terminal. |
| `PUT /mgmt/load/runs/{id}/verdict` | OpsLoad | `{ verdict }` on a terminal run. |
| `DELETE /mgmt/load/runs/{id}` | OpsLoad | Remove a terminal run and its stats. |
| `POST /mgmt/load/runs/{id}/checkin` | run token | Section 5. |
| `POST /mgmt/load/runs/{id}/stats` | run token | Section 4. Rejected with `401` on a bad token, `409` when the run is terminal (the reporter treats `409` as `stop`). Body at most 64 KiB. |
| `GET /mgmt/load/runs/{id}/log?index=&tail=500` | OpsAdmin | Lines from group `/gateway/load`, stream `run-{id}-{index}`. |

**OpsLoad policy**: `ops-admin`, or a client-credentials token carrying scope
`mgmt/load`. A scenario script runs on generators with the generator role,
which can read one secret and nothing else, so the bar is lower than deploy;
Start also scales the fleet, so it stays with admins and a dedicated client. The
CI client that pushes scenario scripts on a repository push is a separate
client an operator creates, as for jobs.

The run token endpoints are not behind Cognito. They are rate limited per
source address to 5 requests per second, answer the same `401` for a wrong
token and an unknown run, and log nothing about the token.

Built as: the rate limit is the gateway's own `TokenBucket` (the building block of
the realtime limits), one bucket per source address, 5 per second with a burst
of 10, instance-local, answering `429 { error: "rate_limited" }` with
`Retry-After`. The source address is the last `X-Forwarded-For` entry (the
address the load balancer saw), else the connection's remote address. The body
cap answers `413 { error: "body_too_large" }`. The token check is the sha256 of
the `X-Gateway-Load-Token` header compared with `token_hash` by
`CryptographicOperations.FixedTimeEquals`; an unknown run is compared against a
dummy hash so both cases cost the same. The order is rate limit, body cap,
token, then the run's state.

Details of the built API:

- `GET /mgmt/load/preflight` answers `{ ok, checks }`; `ok` is false while any
  check is red. `confirm` and `validation` are query parameters beside the ones
  above; `generators` and `vus` default to 1.
- `POST /mgmt/load/runs` checks in this order: the body (`400 { error, field }`,
  an unknown scenario is `field: scenario`), `503 load_unconfigured`,
  `409 run_active`, `409 preflight_failed { checks }`. For `validation: true` the
  gateway sets generators 1, users 1, ramp 0 and hold `Load:ValidationHoldSeconds`
  and ignores the sent values. `restoreFleet` defaults to true. `hard_stop_at` is
  the Start time plus ramp, hold and 10 minutes, at most plus 4 hours.
- Stats lines: `runId` must equal the path id, `index` must be in
  `0..generators-1`, `at` an ISO 8601 time, `vus` a non-negative number and
  `metrics`, when present, an object. Peaks take every numeric leaf under
  `metrics`. The aggregate is `{ generators, vus, metrics }` over the newest
  received line of each generator: counter and gauge fields summed, trend fields
  the maximum except `min` (the minimum), rate metrics summing `passes` and
  `fails` and recomputing `rate`, any other type the maximum per field. A
  terminal run answers `409 { error: "run_terminal" }`.
- `stats?step=` buckets per source, index and `step` seconds aligned to the Unix
  epoch; `stats.csv` has `at,source,index` then one column per numeric leaf
  (dotted paths, arrays by position, sorted).
- Stop answers `409 run_finished` on a terminal run; a second stop keeps the
  first stamp. Verdict and run delete answer `409 run_active` on a non-terminal
  run. A scenario re-created after a delete continues its version numbers after
  the kept history.

## 9. Resources API

Under `/mgmt/resources`, policy `OpsAdmin` for every mutation, `OpsDeploy` for
reads. Each resource has a configured allow list of classes; a class outside
the list is `400 class_not_allowed`. Every mutation is refused with
`409 run_active` while a load run is non-terminal, writes an audit row, and
publishes on `ops:resources`.

| Endpoint | Action |
|---|---|
| `GET /mgmt/resources` | `{ fleet: { group, min, desired, max, inService, instanceClass, templateVersion, refreshInProgress }, generators: { group, desired, inService, max }, cache: { id, nodeType, status }, database: { id, instanceClass, status, maxConnections, connectionsInUse } }`. Each block is null when its configuration key is unset. |
| `PUT /mgmt/resources/fleet/size` | `{ size }`: sets min, max and desired to `size` on the fleet group (never desired alone: the group's target-tracking policy scales an idle fleet back in within minutes when only desired is raised). `202`; progress through `ops:fleet` heartbeats. |
| `PUT /mgmt/resources/fleet/class` | `{ instanceClass }`: creates a launch template version from `$Default` with only `InstanceType` changed, sets it as `$Default`, starts an instance refresh with the deploy preferences (min healthy 100, max healthy 150, warmup 300 s, max raised by one and restored), `202 { templateVersion, refreshId }`. `409 refresh_in_progress` while any refresh is in flight. `GET /mgmt/resources/fleet/refresh` reports it. |
| `PUT /mgmt/resources/fleet/class/rollback` | `{ templateVersion }`: sets that version as `$Default` and starts a refresh. |
| `PUT /mgmt/resources/cache/node-type` | `{ nodeType }`: modifies the backplane replication group's node type, apply immediately. `202`. The response and the audit row say that publishes are dropped while the backplane is degraded. |
| `PUT /mgmt/resources/database/class` | `{ instanceClass, confirm }`: `confirm` must equal the instance identifier. Class only, never storage or engine. A downgrade is refused unless the target class's documented `max_connections` exceeds connections in use by `Load:DbDowngradeMarginPercent` (default 25). Apply immediately. `202`. The response lists the manifest services (every one: they share the instance) and says the instance reboots. |
| `GET /mgmt/resources/audit?limit=50` | `[{ at, actor, resource, action, before, after, detail }]`. |

Configuration:

| Key | Meaning |
|---|---|
| `Resources:FleetGroup` | The fleet Auto Scaling group name (read from the instance's `aws:autoscaling:groupName` tag when unset). |
| `Resources:FleetTemplate` | The fleet launch template name or id. |
| `Resources:FleetClasses` | Allowed fleet classes, comma separated. |
| `Resources:CacheReplicationGroup`, `Resources:CacheNodeTypes` | The backplane replication group and its allowed node types. |
| `Resources:DatabaseInstance`, `Resources:DatabaseClasses` | The database instance identifier and its allowed classes. |

Everything in this section goes through `IFleetScaler`, `ICacheScaler` and
`IDatabaseScaler` interfaces with AWS SDK implementations and fakes for the
tests. The load scheduler uses `IFleetScaler` for both groups.

Details of the built API:

- Registration: `AwsFleetScaler` when `Resources:FleetGroup`,
  `Resources:FleetTemplate` or `Load:GeneratorGroup` is set, `ElastiCacheScaler`
  when `Resources:CacheReplicationGroup` is set, `RdsDatabaseScaler` when
  `Resources:DatabaseInstance` is set; the null scaler, which refuses every call,
  otherwise. `Resources:FleetMaxSize` (default 50) bounds `fleet/size`. When
  `Resources:FleetGroup` is unset and `Resources:FleetTemplate` is set, the group
  is read from the instance's `aws:autoscaling:groupName` tag through IMDS
  (instance metadata tags must be on).
- Order of the answers on a mutation: `503 resource_unconfigured`, `400` on the
  body (`class_not_allowed`, `confirm_mismatch`, `version_not_found`, a size
  outside `1..Resources:FleetMaxSize`), `409 run_active`, `409
  refresh_in_progress` (fleet size, class and rollback, while the group's newest
  refresh is in flight whoever started it), `409 downgrade_unsafe`. An AWS error
  is `502 { error: "aws_error", detail }`. Only a change that reaches AWS writes
  an audit row; a class change whose refresh fails to start writes one with the
  error, since `$Default` already moved, and puts the max straight back.
- `GET /mgmt/resources` adds `runActive`, the allow lists (`classes`,
  `nodeTypes`), the fleet `maxSize`, the database `documentedMaxConnections`,
  and an `error` per block for a failed AWS read. The database block's
  `maxConnections` is the parameter group's value when it is a plain number, else
  null; `connectionsInUse` is `pg_stat_activity` on the gateway's connection.
- `fleet/class`, in order: create the version from `$Default` with only
  `InstanceType` changed, make it `$Default`, raise the group's max by one, start
  the refresh with min healthy 100, max healthy 150, warmup 300 s. Rollback skips
  the create and does the rest. The audit row's `detail` holds `{ group,
  refreshId, restoreMax, templateVersion, previousVersion }`.
- The refresh watcher (a hosted service, every 30 s, the fresh leader only, or
  any instance with the reconciler disabled) finds the gateway's refreshes
  through those audit rows, publishes `refresh`, and when one ends (any terminal
  status, or Auto Scaling no longer knows it) sets the max back to `restoreMax`
  (never below min or desired) and writes a `refresh_ended` row. Keeping the
  pending refresh in the database lets a successor finish the job when the
  refresh replaces the instance that started it.
- `GET /mgmt/resources/audit` rows carry `id`, and `detail` as a JSON object.

Documented `max_connections`, the table the downgrade rule reads
(`DatabaseClassConnections` in code). Operator-maintained: each value is the RDS
Postgres default `LEAST({DBInstanceClassMemory/9531392}, 5000)` over the class's
nominal memory, so the live value is a little lower. A class missing here is
unknown and never a safe downgrade target; add its row before adding it to
`Resources:DatabaseClasses`.

| Classes | Memory | `max_connections` |
|---|---|---|
| `db.t3.micro`, `db.t4g.micro` | 1 GiB | 112 |
| `db.t3.small`, `db.t4g.small` | 2 GiB | 225 |
| `db.t3.medium`, `db.t4g.medium` | 4 GiB | 450 |
| `db.t3.large`, `db.t4g.large`, `db.m6g.large`, `db.m7g.large` | 8 GiB | 901 |
| `db.t3.xlarge`, `db.t4g.xlarge`, `db.m6g.xlarge`, `db.m7g.xlarge`, `db.r6g.large`, `db.r7g.large` | 16 GiB | 1802 |
| `db.t3.2xlarge`, `db.t4g.2xlarge`, `db.m6g.2xlarge`, `db.m7g.2xlarge`, `db.r6g.xlarge`, `db.r7g.xlarge` | 32 GiB | 3604 |
| `db.m6g.4xlarge`, `db.m7g.4xlarge`, `db.r6g.2xlarge`, `db.r7g.2xlarge` | 64 GiB | 5000 |

## 10. Real-time

Two ops channels beside `ops:jobs`, same gate:

- `ops:load`: `scenario` `{ name, action: upserted | deleted, by }`; `run`
  `{ runId, scenario, status, actor, generators, vusPerGenerator, startedAt, finishedAt, stopReason }`
  on every status change; `stats` `{ runId, at, aggregate }` after every stored
  generator line, where `aggregate` is the fleet-wide merge of the newest line
  from every generator; `fleet` `{ runId, at, sample }` after every fleet
  sample.
- `ops:resources`: `change` `{ resource, action, by, before, after }` and
  `refresh` `{ refreshId, status, percentComplete }` while a fleet refresh runs.

The global banner (section 11) is driven by `ops:load` `run` events and by
`GET /mgmt/resources` on load.

## 11. Dashboard

Two drawer entries. `Load` at `/load` between Jobs and Instances; `Resources`
at `/resources` after Node stats. The Jobs page is untouched.

**Load page**, three views:

- **Runs list** (default): status chip, scenario, target, generators times
  users, started, duration, peak of `event_lag_ms.p(95)` when present,
  verdict, with New run, Validate, Stop, Open and Delete.
- **Run**: the form (scenario, target with a typed confirm field, generators,
  users each with the computed total, ramp and hold in minutes, fleet size with
  "leave alone" default, restore fleet, params as key and value rows seeded
  from `paramsDefault`), the pre-flight list refreshed as the form changes
  (debounced `GET /mgmt/load/preflight`), Start disabled while any check is
  red; once started, the same screen becomes the live view: a status strip
  with the state, elapsed, hard stop time and Stop; tiles for total users,
  every top-level counter and the `p(95)` of every trend from the aggregate;
  a chart of users and the first trend over the run; a fleet panel with one
  row per instance (connections, cpu, memory) and the channel member count;
  the timeline. A finished run shows the same view from stored stats, with
  Export CSV and the verdict field.
- **Scenarios**: name, repository, commit, version, max users per generator,
  validation state, with Edit (script in a monospace field, params default
  rows), Validate (starts a validation run) and Delete.

**Resources page**: four cards from `GET /mgmt/resources`, each with its
current state and one control: fleet size (number, apply), fleet class (select
from the allow list, apply, refresh progress bar, rollback to the previous
version), cache node type (select, apply, with the degradation warning in the
confirm dialog), database class (select, typed instance identifier, the list of
affected services and the reboot warning in the confirm dialog). Every control
is disabled with a reason while a load run is non-terminal or a refresh is in
flight. The audit list sits below the cards.

**Global banner**: on every page, red, whenever a run is non-terminal or the
generator group is above zero: "Load run {id} {status}, generators {n}, hard
stop {time}" with a Stop button, driven by `ops:load` and a poll every 30 s.

Tests in the repo's style: api module tests with the axios mock, page and
dialog tests with testing-library, the hub mock for live events.

## 12. Operations

- IAM, inline policy on the gateway instance role: `autoscaling:SetDesiredCapacity`,
  `UpdateAutoScalingGroup`, `StartInstanceRefresh`, `CancelInstanceRefresh`,
  `DescribeInstanceRefreshes`, `DescribeAutoScalingGroups` on the two groups by
  tag; `ec2:CreateLaunchTemplateVersion`, `ModifyLaunchTemplate`,
  `DescribeLaunchTemplateVersions`, `DescribeLaunchTemplates` on the fleet
  template; `elasticache:ModifyReplicationGroup`, `DescribeReplicationGroups`,
  `DescribeCacheClusters` on the backplane; `rds:ModifyDBInstance`,
  `DescribeDBInstances` on the one instance and `rds:DescribeDBParameters` on its
  parameter group; `secretsmanager:PutSecretValue` on
  the generator spec secret. Operator step.
- The generator stack, the spec secret (created with a placeholder value), the
  `mgmt/load` scope on the ops pool's resource server, and the
  `gateway-ci-load` client: operator steps.
- Alarm outside the gateway: `GroupInServiceInstances` on the generator group
  above 0 for 5 hours notifies the ops address, for the case where the gateway
  itself is down.
- Metrics: `LoadRunCount` by scenario and status, `LoadRunDuration`,
  `LoadGeneratorStats` (lines received), in the gateway namespace through the
  same EMF path as jobs.
- Deploying this feature is a gateway release: ASG refresh, the usual rule.
  The dashboard and the runner image deploy separately.

Configuration:

| Key | Default | Meaning |
|---|---|---|
| `Load:GeneratorGroup` | unset | Generator Auto Scaling group name; unset disables Start with `503 load_unconfigured` |
| `Load:GeneratorSpecSecret` | unset | Secrets Manager name of the generator spec secret |
| `Load:MaxGenerators` | `2` | Most generators a run may ask for (never above the group's max) |
| `Load:CheckInTimeout` | `00:05:00` | How long `generators_up` waits for every generator |
| `Load:StatsStaleAfter` | `00:01:00` | Ramp or hold with no generator line for this long marks the run `lost` |
| `Load:ValidationHoldSeconds` | `300` | Hold of a validation run |
| `Load:DbConnectionsPerNode` | `20` | Per-node database connections the headroom check assumes |
| `Load:DbConnectionMargin` | `10` | Spare connections the headroom check requires |
| `Load:DbDowngradeMarginPercent` | `25` | Resources: a smaller database class must exceed connections in use by this much |
| `Load:FleetSampleInterval` | `00:00:10` | How often the leader writes a fleet sample |
| `Load:DefaultImage`, `Load:DefaultTag` | as jobs | Runner image a scenario gets when PUT omits them |

## 13. Phase 2 (not in v1)

The signed-in flow and test people; CloudWatch-backed fleet samples (load
balancer, Redis, database); the `file_descriptors`, `backplane` and `spend`
pre-flight checks; bigger generator types and a mixed instances policy with
spot; `Load:MaxGenerators` raised; the capacity ladder to 100k.

## 14. Build plan

Flat tasks, posted to grunt one at a time, each verified before the next is
posted. Nothing touches the fleet until an operator merges main.

0. **wmsfo-api: scenario v1.** `tools/loadtest/`: a k6 script for an anonymous
   tracker visitor (raw websocket SignalR handshake and `JoinChannel`, one
   location event per second measured as `event_lag_ms` from `publishedAt`,
   `event_gap_ms` and `seq_regressions` (seq values are not consecutive on the hub: every stored fix of the event takes one, spare beacons included, so only a repeat or a step back is a fault), CDN poll of the live object at the poll cadence for the poll
   fraction, keepalive pings, re-join on `channelEvicted`), offline tests with
   a fake socket, README. The signed-in pick is phase 2.
1. **gateway-job-runner: k6 and the load mode.** Section 7, smoke test with
   a two-metric scenario against a fake stats intake.
2. **gateway-api: load model, store, API, events, docs.** Sections 2, 4
   (intake side), 5 (check-in side), 6 (the checks that need no AWS: target,
   scenario, presence, database headroom; the group checks through
   `IFleetScaler` with a fake), 8, 10. No scheduler yet: a run stays `queued`.
3. **gateway-api: resources.** Section 9 end to end with the scaler
   interfaces, AWS implementations and fakes; the `fleet_class` and
   `generator_group` pre-flight checks become real.
4. **gateway-api: load scheduler.** Section 3, the fleet samples of section 4,
   the generator spec secret of section 5, metrics, and the README and
   tech-spec rows for everything. Depends on 2 and 3.
5. **gateway-api-admin: Load and Resources pages, banner.** Section 11.
6. **Operator steps and release.** Section 12, then runner main, gateway main
   and the ASG refresh, dashboard main; then the validation run and the first
   small runs from the page.

## 15. Rules for every task

Every repository is public: no account ids, hosts, ARNs, pool ids, secret
values or LAN addresses anywhere, tests included (invent placeholders). No em
dash or en dash in any file. Comments describe the code as it is. Fakes over
real services in tests: no Docker, Postgres, AWS or network on the build box.
Do not change deploy, rollback, reconcile, jobs or realtime behaviour for
services. Keep the existing style of each repo (records, snake_case columns,
MUI, vitest).
