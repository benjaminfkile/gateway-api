# Load runs

Design for the gateway's load feature: a page on the dashboard that ramps a
fleet of fake users against any service behind the gateway, shows where things
break, and can never leave generators running. The gateway steers one pre-made
generator Auto Scaling group and nothing else; it never creates infrastructure,
and the fleet, the backplane and the database are sized by hand in the AWS
console. This document is the contract for the build tasks at the end.

Status: v1 live since 2026-10-29. This document describes the gateway as built:
load runs that steer only the generator group and never the fleet (lifecycle
`queued`, `generators_up`, `ramping`, `holding`, `draining`, then a terminal
status), the six pre-flight checks of section 6, the resource audit read at
`GET /mgmt/load/audit`, and no Resources API (section 9). The runner image and
the dashboard are released; the first scenario (`wmsfo-tracker`, an anonymous
WMSFO tracker visitor on the dev stack) passed its validation run (one
generator, one user, five minutes) end to end. The first generator group is one
t4g.small. Known gaps: fleet samples carry no node stats yet (the heartbeat rows
have none), and `Load:DefaultImage` should be the full registry path. Phase 2
(the signed-in flow, bigger generators, CloudWatch-backed fleet metrics, the
100k ladder) is listed in section 13 and is not part of v1.

## 1. What a load run is

A scenario is a script plus parameters, registered with the gateway and owned
by the application it tests (a WMSFO scenario lives in the wmsfo-api
repository, a future wisper scenario in wisper's). A run names a scenario, a
target service, a number of generators, the virtual users each generator
carries, a ramp and a hold. When the operator presses Start, the fleet leader
checks pre-flight, scales the generator group to the requested count, hands each
generator its share through a check-in, collects a stats line from each every
five seconds while the run ramps and holds, then scales the generator group back
to zero and freezes the record. Every exit path passes through "generator group
to zero". A run never reads or changes the fleet group.

Compared with a job: many containers on instances outside the fleet instead of
one container on the leader; a clock the run cannot outlive; stats coming back
while it runs. A load run reuses the job patterns (script versions, the runner
image, leader-claimed runs, the CI client) and none of the Jobs UI.

Not in scope: creating or deleting stacks, instances, roles or groups; sizing
the fleet, the backplane or the database (done by hand in the console); running
more than one load run at a time; retries.

## 2. Data model

EF Core migrations `AddLoad`, `AddResourceAudit`, `AddLoadSpecToken` and
`RemoveLoadFleet` build this schema, snake_case like the rest.

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
  hard_stop_at          timestamptz not null,     -- started_at + ramp + hold + 10 min, at most started_at + 4 h
  token_hash            text not null,            -- sha256 of the run token the generators present
  spec_token            text,                     -- the run token itself until the leader writes the spec secret, then null
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
resource_audit (
  id        bigint generated always as identity primary key,
  at        timestamptz not null,
  actor     text not null,                        -- 'gateway' for the orphan guard
  resource  text not null,                        -- 'generators' for the orphan guard
  action    text not null,                        -- 'orphan_guard'
  before    text,                                 -- e.g. 'desired 2, in service 1'
  after     text,                                 -- 'desired 0'
  detail    text                                  -- JSON object, e.g. { group, reason }
)
-- index on (resource, id)
```

`checkins` is what makes a repeated check-in answer the same index: the
check-in update sets `checked_in = n + 1` and adds the instance to `checkins`
conditional on `checked_in` still being `n`, so the previous value is the
assigned index and a losing concurrent check-in reads again.

The run token is 32 random bytes, base64url, minted at Start, shown to nobody,
written into the generator spec secret (section 5) and compared by hash on
every check-in and stats line. It is useless after the run finishes. Start keeps
the token in `spec_token` because the leader that writes the secret may be another
instance; the leader clears it in the same conditional update that records the
secret as written (and on any exit), so the token sits on the row only between
Start and `generators_up`.

`resource_audit` is the gateway's record of the changes it made to an AWS
resource on its own: one row each time the orphan guard (section 3) sets the
generator group to 0. Rows written by earlier releases (fleet, cache and
database changes) stay as history; `GET /mgmt/load/audit` lists them all.

## 3. Run lifecycle

```
queued -> generators_up -> ramping -> holding -> draining -> succeeded
                                                         -> stopped | expired | failed | lost
```

| Status | Meaning | Leaves when |
|---|---|---|
| `queued` | Start accepted; pre-flight passed at Start | the leader claims it (one load run at a time: a second Start answers `409 run_active`) |
| `generators_up` | generator spec secret written, generator group set to `generators` | `checked_in == generators` (section 5) |
| `ramping` | generators are raising virtual users over `ramp_seconds` | `ramp_seconds` after `ramp_started_at` |
| `holding` | steady state | `hold_seconds` after the ramp ends |
| `draining` | generator group set to 0 | the generator group reports zero instances in service |
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
   the generator group's desired and in-service counts through
   `IGeneratorGroupScaler`. Nothing in a run reads or sets the fleet group.
3. Expire a run past `hard_stop_at`; mark a run `lost` when ramp or hold has
   had no generator stats line for 60 s.
4. Reconcile on gaining leadership: a non-terminal run with no driver on this
   instance is adopted and continues when it is in `generators_up` (a state
   check any leader can carry on), or in `ramping` or `holding` with stats still
   arriving, or already `draining`; anything else is driven to `draining` with
   reason `leader_restart`.
5. Guard: when no run is non-terminal and the generator group's desired
   capacity is above 0, set it to 0 and write an audit row (reason
   `orphan_guard`). This is the backstop for anything that slipped.

Built as (`LoadSchedulerService`):

- The tick runs only while `ILeaderStatus` holds a fresh leader answer (as the job
  scheduler) and `Load:GeneratorGroup` is set, after the migration gate. In order:
  the guard when no run is non-terminal (audit row `generators`/`orphan_guard`,
  actor `gateway`, a `change` on `ops:resources`); the claim of the queued run,
  straight to `generators_up`, followed by pre-flight with `confirm` set to the
  target (a red check drains the run with reason `failed` and the check's name in
  `detail`); the gaining-leadership rule for a run this process is not driving
  (adopted in `generators_up` and `draining`, and when ramping or holding with
  fresh stats; otherwise drained with `leader_restart`); then one step; then the
  fleet sample.
- Exits are checked first in `generators_up`, `ramping` and `holding`:
  `hard_clock` past `hard_stop_at`, `lost` when ramping or holding and the newest
  generator line (or the ramp start) is older than `Load:StatsStaleAfter`, and
  `operator` once `stop_requested_at` is set.
- `generators_up` writes the spec secret and clears `spec_token` (once), then sets
  only the generator group's desired to `generators`; it leaves for `ramping`
  (stamping `ramp_started_at`) once `checked_in == generators`, and drains with
  `failed`/`checkin_timeout` when `Load:CheckInTimeout` has passed since it was
  entered. An unset `Load:GeneratorSpecSecret` or `Load:GatewayBaseUrl` fails the run
  there.
- `ramping` leaves `ramp_seconds` after `ramp_started_at`; `holding` leaves
  `hold_seconds` after it was entered (the timeline says when) and drains with no
  reason.
- `draining` keeps the generator group's desired at 0 and ends once the generator
  group has zero in service: `succeeded`
  with no reason, `stopped` (operator), `expired` (hard_clock), `failed` (failed),
  `lost` (lost or leader_restart). A succeeded validation run sets the scenario's
  `last_validation_run_id` when the script version still matches.
- A step whose generator group or secret call throws is retried on the next tick. After
  `Load:ScaleRetryLimit` failed ticks in a row the leader sets the generator group to
  0 (a failure there is logged and does not hold the run), then drains the run with
  `failed`, or ends it `failed` when it was already draining; the guard keeps trying
  the group once the run is terminal.
- Every status change is one conditional update on the run's status and timeline,
  appends `{ at, status, by: "gateway", note }` and publishes `run` on `ops:load`.

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

Built as: every `Load:FleetSampleInterval` while the run is non-terminal, one row
with source `fleet`, index null and body
`{ at, instances: [{ instanceId, leader, heartbeatAt, gatewayVersion, runningServices, cpu, memory, disk, hubConnections }], channelMembers }`,
then `fleet` on `ops:load`. `instances` lists the rows with a heartbeat within the
stale threshold, by id. The heartbeat row carries no cpu, memory, disk or hub
connection figures yet, so those four are null. `channelMembers` is the presence
count of the channel named by the run's `params.channel` itself, null when the
params name none.

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

Evaluated by `GET /mgmt/load/preflight?scenario=&target=&generators=&vus=`
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
| `target_confirmed` | the page sent `confirm` equal to the target name | type the target to confirm; production is allowed | yes |

A load test is meant to surface resource limits, so pre-flight does not guard
them: no check reads the database, the backplane or the fleet's size or class.

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
| `POST /mgmt/load/runs` | OpsLoad | Start. Body `{ scenario, target, confirm, generators, vusPerGenerator, rampSeconds, holdSeconds, params?, validation }`. `202 { runId }`; `409 run_active` when a run is non-terminal; `409 preflight_failed { checks }`; `400 { error, field }`. |
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
| `GET /mgmt/load/audit?limit=50` | OpsDeploy | The `resource_audit` rows (section 2), newest first: `[{ id, at, actor, resource, action, before, after, detail }]` with `detail` as a JSON object. `limit` is 1..500, else `400 { error, field: "limit" }`. |

**OpsLoad policy**: `ops-admin`, or a client-credentials token carrying scope
`mgmt/load`. A scenario script runs on generators with the generator role,
which can read one secret and nothing else, so the bar is lower than deploy;
Start scales the generator group, so it stays with admins and a dedicated client. The
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
  and ignores the sent values. Unknown body fields are ignored. `hard_stop_at` is
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

## 9. Resources

Resources are managed by hand in the AWS console: the fleet Auto Scaling group
and its launch template, the ElastiCache backplane and the RDS instance. The
gateway touches only the generator group (section 5), through
`IGeneratorGroupScaler`.

## 10. Real-time

Two ops channels beside `ops:jobs`, same gate:

- `ops:load`: `scenario` `{ name, action: upserted | deleted, by }`; `run`
  `{ runId, scenario, status, actor, generators, vusPerGenerator, startedAt, finishedAt, stopReason }`
  on every status change; `stats` `{ runId, at, aggregate }` after every stored
  generator line, where `aggregate` is the fleet-wide merge of the newest line
  from every generator; `fleet` `{ runId, at, sample }` after every fleet
  sample.
- `ops:resources`: `change` `{ resource, action, by, before, after }` when the
  orphan guard writes its audit row (section 3).

The global banner (section 11) is driven by `ops:load` `run` events and by
`GET /mgmt/load/runs?limit=1` on load.

## 11. Dashboard

One drawer entry, `Load` at `/load` between Jobs and Instances. There is no
Resources page. The Jobs page is untouched.

**Load page**, three views:

- **Runs list** (default): status chip, scenario, target, generators times
  users, started, duration, peak of `event_lag_ms.p(95)` when present,
  verdict, with New run, Validate, Stop, Open and Delete. Below the runs, the
  orphan guard events from `GET /mgmt/load/audit`.
- **Run**: the form (scenario, target with a typed confirm field, generators,
  users each with the computed total, ramp and hold in minutes, params as key
  and value rows seeded from `paramsDefault`; no fleet fields), the pre-flight list refreshed as the form changes
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

**Global banner**: on every page, red, whenever a run is non-terminal: "Load
run {id} {status}, generators {n}, hard stop {time}" with a Stop button, driven
by `ops:load` and a poll every 30 s. A generator group left above zero with no
run is the orphan guard's job (section 3), and its audit rows show on the Load
page.

Tests in the repo's style: api module tests with the axios mock, page and
dialog tests with testing-library, the hub mock for live events.

## 12. Operations

- IAM, inline policy on the gateway instance role: the generator group
  (`autoscaling:DescribeAutoScalingGroups`, `autoscaling:SetDesiredCapacity`),
  the spec secret (`secretsmanager:PutSecretValue`), and the ECR and CloudWatch
  Logs rights the gateway already holds (the generator log route reads group
  `/gateway/load`). Nothing for the fleet group, the launch template, ElastiCache
  or RDS. Operator step.
- The fleet Auto Scaling group and its launch template, the ElastiCache
  backplane and the RDS instance are sized by hand in the AWS console. A
  `Resources:*` key left in the environment is ignored.
- The generator stack, the spec secret (created with a placeholder value), the
  `mgmt/load` scope on the ops pool's resource server, and the
  `gateway-ci-load` client: operator steps.
- Alarm outside the gateway: `GroupInServiceInstances` on the generator group
  above 0 for 5 hours notifies the ops address, for the case where the gateway
  itself is down.
- Metrics: `LoadRunCount` by scenario and status, `LoadRunDuration`,
  `LoadGeneratorStats` (lines received), in the gateway namespace through the
  same EMF path as jobs. Built as `ILoadMetrics` (`LoadMetrics`): dimensions
  `Scenario` and `Status` for the count, `Scenario` for the duration (seconds,
  `started_at` to `finished_at`) and for the stats lines (one per stored line,
  recorded by the stats endpoint), sent to `Jobs:MetricsEndpoint` in
  `Jobs:MetricsNamespace`.
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
| `Load:FleetSampleInterval` | `00:00:10` | How often the leader writes a fleet sample |
| `Load:TickInterval` | `00:00:10` | How often the load scheduler ticks |
| `Load:GatewayBaseUrl` | unset | Public origin the generators reach the gateway at (`https://<gateway-domain>`), written into the spec secret as `gatewayBaseUrl`; unset fails a run in `generators_up` |
| `Load:ScaleRetryLimit` | `6` | Failed ticks in a row of one run's scaling or secret step before the run fails |
| `Load:DefaultImage`, `Load:DefaultTag` | as jobs | Runner image a scenario gets when PUT omits them |

## 13. Phase 2 (not in v1)

The signed-in flow and test people; CloudWatch-backed fleet samples (load
balancer, Redis, database); a `spend` pre-flight check; bigger generator types and a mixed instances policy with
spot; `Load:MaxGenerators` raised; the capacity ladder to 100k.

## 14. Build plan

Flat tasks, posted to grunt one at a time, each verified before the next is
posted. Nothing touches the fleet until an operator merges main.

0. **wmsfo-api: scenario v1.** `tools/loadtest/`: a k6 script for an anonymous
   tracker visitor (raw websocket SignalR handshake and `JoinChannel`, one
   location event per second measured as `event_lag_ms` from `publishedAt`,
   `event_gap_ms` and `seq_regressions` (seq values are not consecutive on the hub: every stored fix of the event takes one, spare beacons included, so only a repeat or a step back is a fault), CDN poll of the live object at the poll cadence for the poll
   fraction (zero since scenario version 2: the CDN is not under test and polls cost money), keepalive pings, re-join on `channelEvicted`), offline tests with
   a fake socket, README. The signed-in pick is phase 2.
1. **gateway-job-runner: k6 and the load mode.** Section 7, smoke test with
   a two-metric scenario against a fake stats intake.
2. **gateway-api: load model, store, API, events, docs.** Sections 2, 4
   (intake side), 5 (check-in side), 6 (the checks that need no AWS: target,
   scenario, presence; the generator group check through
   `IGeneratorGroupScaler` with a fake), 8, 10. No scheduler yet: a run stays
   `queued`.
3. **gateway-api: generator group scaler.** `IGeneratorGroupScaler` with the
   AWS implementation, a null one and a fake; the `generator_group` pre-flight
   check becomes real. Section 9: no resource is scaled by the gateway.
4. **gateway-api: load scheduler.** Section 3, the fleet samples of section 4,
   the generator spec secret of section 5, metrics, and the README and
   tech-spec rows for everything. Depends on 2 and 3.
5. **gateway-api-admin: Load page, banner.** Section 11.
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
