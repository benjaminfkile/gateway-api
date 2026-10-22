# Scheduled jobs

Design for the gateway's job feature: a script that runs on a schedule, or on
demand, as a short-lived container on the fleet. The first job is the WMSFO
prod to dev mirror; wisper and file-manager get the same treatment. This
document is the contract for the build tasks at the end.

Status: built. The gateway side is complete: the job model, store, management
API and `ops:jobs` events (sections 2, 4, 5) and the executor with its metrics and
failure handling (sections 3, 8, 9). The dashboard (section 6) and the runner image
(section 7) live in their own repositories.

## 1. What a job is

A job is a named script with a cron schedule. When it is due, or when an
operator presses run, the fleet leader starts one container from a runner
image, hands it the script and the job's secret as environment, waits for it to
exit, and records the result. The gateway knows nothing about what the script
does. The script belongs to the application it serves and is written against
the runner image's tools (shell, psql, aws cli, curl, jq).

Compared with a service: no port, no route, no health probe, no blue-green, no
restart policy. One run at a time per job. A run has a timeout.

Not in scope: fan-out across instances, run dependencies, retries. A job that
needs more than one process at a time is a service.

## 2. Data model

Two tables plus a script history, EF Core migration, snake_case like the rest.

```sql
job (
  name              text primary key,       -- ^[a-z0-9][a-z0-9-]{1,62}$
  schedule          text,                   -- 5-field cron, null = manual only
  time_zone         text not null,          -- IANA id, e.g. America/Chicago
  script            text not null,          -- the current script body, <= 256 KiB
  script_version    int  not null,          -- bumps when the script body changes
  image             text not null,          -- runner repository, default from config
  tag               text not null,          -- default 'latest'
  env_secret_ref    text,                   -- Secrets Manager name or ARN, flat JSON of strings
  timeout_seconds   int  not null,          -- 30..21600, default 3600
  enabled           boolean not null,
  next_run_at       timestamptz,            -- null when disabled or schedule null
  updated_by        text not null,
  updated_at        timestamptz not null
)
job_script_version (job, version, script, sha256, updated_by, updated_at,
                    primary key (job, version))
job_run (
  id                bigint generated always as identity primary key,
  job               text not null,          -- not a foreign key: history outlives a deleted job
  trigger           text not null,          -- 'schedule' | 'manual'
  actor             text not null,          -- Cognito username, client id, or 'schedule'
  status            text not null,          -- queued | running | succeeded | failed | timed_out | cancelled | lost
  instance_id       text,                   -- the instance that ran it
  image_digest      text,
  script_version    int not null,
  exit_code         int,
  queued_at         timestamptz not null,
  started_at        timestamptz,
  finished_at       timestamptz,
  cancel_requested_at timestamptz,
  detail            text                    -- last error or the last lines of output, <= 2 KiB
)
```

`next_run_at` is computed by the gateway with Cronos from `schedule` and
`time_zone` whenever the job is upserted, enabled, or a scheduled run is
claimed. Cron is the standard five fields; seconds are rejected. Daylight
saving follows Cronos rules for the zone.

## 3. Execution

A new hosted service, `JobSchedulerService`, ticks every 10 seconds on every
instance and does two things.

**Leader duties** (only when this instance is the leader as the reconciler last
evaluated it, the same in-process answer `GET /internal/leader` reads):

1. Claim due scheduled runs: for each enabled job with `next_run_at <= now`
   and no run in `queued` or `running`, insert a `queued` run with trigger
   `schedule` and advance `next_run_at`. If a job was due more than one
   schedule period ago (the fleet was down) it runs once, not once per missed
   slot.
2. Mark lost runs: a `running` run whose `instance_id` has a stale heartbeat
   becomes `lost` with a detail line.
3. Start queued runs: each `queued` run, oldest first, is claimed with a
   single conditional update (`status = 'queued'` to `running`,
   `instance_id` = this instance). Overlap between two leaders during a
   transition is harmless: the claim is atomic, so a run starts once.

**Own duties** (every instance): drive the runs it has claimed. For each:

- Resolve the job's env from `env_secret_ref` exactly as services do
  (`SecretsManagerEnvProvider`, refactored to take a ref rather than a
  manifest row), add `GATEWAY_JOB_NAME`, `GATEWAY_JOB_RUN_ID`,
  `GATEWAY_JOB_TRIGGER`, `GATEWAY_JOB_SCRIPT` (the script, base64). The secret
  is read at run time, so a rotated value is used on the next run.
- Pull `image:tag` through the existing runtime (`PullImageAsync`, which also
  returns the digest recorded on the run).
- Create and start the container: name `job-{name}-{runId}`, the internal
  network, no published port, no restart policy, memory limit 1 GiB, the
  awslogs driver with group `/gateway/jobs/{name}` and stream `run-{id}`
  (`json-file` on a dev box, same escape hatch as services). Labels
  `gateway.job={name}` and `gateway.job-run={id}`, and not the managed-service
  label, so the reconciler's inventory and orphan handling never touch it.
- Wait for exit with the job's timeout. On timeout: kill, status `timed_out`.
  On a cancel request: kill, status `cancelled`. Otherwise `succeeded` when
  the exit code is 0, `failed` when not. Record `exit_code`, `finished_at`,
  and the last 20 log lines in `detail` for the dashboard's run row.
- Remove the container. Logs stay in CloudWatch under 30 day retention, set
  the same way service groups get theirs.
- On gateway shutdown mid-run the container keeps running under Docker; the
  next tick on the same instance re-attaches by label and finishes the
  bookkeeping. If the instance is gone, the leader marks the run `lost`.

The container runtime interface grows
`RunJobContainerAsync(JobContainerSpec, timeout, cancel) -> JobRunResult`, plus
`FindJobContainerAsync(runId)` and `WaitJobContainerAsync(container, timeout,
cancel)` for the re-attach, and the fake runtime in the tests grows a matching
scripted implementation.

Runner images are pulled through the same registry login the services use.
The image reference is per job so a job may bring its own image; the default
is the shared runner (section 7).

## 4. Management API

Under `/mgmt/jobs`, public listener, Cognito like everything else.

| Endpoint | Policy | Action |
|---|---|---|
| `GET /mgmt/jobs` | OpsDeploy | List: every job with schedule, next run, last run summary, enabled, script version. Never the script body. |
| `GET /mgmt/jobs/{name}` | OpsDeploy | One job with the script body. |
| `PUT /mgmt/jobs/{name}` | OpsJobs | Create or update. Body `{ schedule?, timeZone?, script?, image?, tag?, envSecretRef?, timeoutSeconds?, enabled? }`. On update an absent field preserves, an empty string clears where the column is nullable. A changed script bumps `script_version` and writes a history row. Validation: name pattern, cron parses, zone exists, timeout range, script size, image and tag shape. |
| `DELETE /mgmt/jobs/{name}` | OpsJobs | Remove the job. `409` while a run is queued or running unless `?force=true`, which cancels it first. History rows stay. |
| `POST /mgmt/jobs/{name}/run` | OpsJobs | Enqueue a manual run: `202 { runId }`. `409 run_active` when one is queued or running. Works on a disabled job (disabled means the schedule is off, not the job). |
| `POST /mgmt/jobs/{name}/enable` and `/disable` | OpsJobs | Flip `enabled`, recompute `next_run_at`. |
| `GET /mgmt/jobs/{name}/runs?limit=50&before={id}` | OpsDeploy | Run history, newest first. |
| `GET /mgmt/jobs/{name}/runs/{id}` | OpsDeploy | One run. |
| `POST /mgmt/jobs/{name}/runs/{id}/cancel` | OpsJobs | Stamp `cancel_requested_at`; a queued run becomes `cancelled` at once, a running one when its instance kills the container (next tick). |
| `GET /mgmt/jobs/{name}/runs/{id}/log?tail=500` | OpsAdmin | Lines from CloudWatch group `/gateway/jobs/{name}`, stream `run-{id}`, same `LogLine` shape and same store as service logs. |
| `GET /mgmt/jobs/{name}/script/versions` | OpsDeploy | `[{ version, sha256, updatedBy, updatedAt }]`. |
| `GET /mgmt/jobs/{name}/script/versions/{v}` | OpsDeploy | That version's body. |

**OpsJobs policy**: `ops-admin`, or a client-credentials token carrying scope
`mgmt/jobs`. A job script runs with the instance role, which is the same
power a deployed image already has, so the bar is the same as deploy: a human
admin or a dedicated machine client. The CI deploy client does not get this
scope by accident; it is a separate client an operator creates if a repo's CI
should push its script on change. Run and cancel sit under the same policy so
an operator can also trigger from a script with that client. The same scope may
also call `POST /mgmt/services/{name}/restart` (policy `OpsRestart`: ops-admin or
`mgmt/jobs`), because a job such as a database mirror needs the dependent service
to boot again and migrate forward; stop, start and manifest edits stay admin only.

Every mutation records the caller in `updated_by` or `actor`. Responses never
include secret values; `envSecretRef` is the only secret-related field and it
is a name.

## 5. Real-time

A new ops channel `ops:jobs` beside `ops:deploys`, gated by the same policy.
Events:

- `job` `{ name, action: upserted | deleted | enabled | disabled, by }`
- `run` `{ runId, job, status, trigger, actor, instanceId, startedAt, finishedAt, exitCode }` on every status change

The dashboard subscribes to it the way the Deploys page subscribes to
`ops:deploys`; liveness still comes from `ops:fleet`.

## 6. Dashboard

A `Jobs` page at `/jobs` in the drawer, between Deploys and Instances.

- **List**: name, schedule with time zone, next run (relative and absolute),
  last run (status chip, when, duration), enabled switch, actions: Run now,
  Edit, Runs, Delete. Run now and Delete confirm. A job with an active run
  shows a spinner on its row and Run now is disabled.
- **Edit dialog**: name (fixed after create), cron with a human reading of the
  next three fire times from the API's `nextRunAt` plus a client-side
  preview, time zone (text with the browser's `Intl.supportedValuesOf`
  list), image and tag, secret ref, timeout, enabled, and the script in a
  monospace multi-line field sized to the dialog. Save calls `PUT`. The
  dialog shows the current script version and links to the version list.
- **Runs**: a drawer or sub-route with the run table (status, trigger, actor,
  started, duration, exit code, instance) and per run a Log button that opens
  the existing terminal panel against the run's log endpoint, following while
  the run is `running`, and a Cancel button while queued or running.
- Live updates from `ops:jobs`; the same polling fallback the other pages use.
- Tests in the repo's style: api module tests with the axios mock, page and
  dialog tests with testing-library.

## 7. Runner image

Repository `gateway-job-runner` (public, like everything), image
`benkile/gateway-job-runner`, tags `latest` and the commit sha. Alpine with
`bash`, `coreutils`, `curl`, `jq`, `postgresql17-client` (`pg_dump`, `psql`),
`aws-cli`, `ca-certificates`, `tzdata`, `gettext` (`envsubst`). Entrypoint:
decode `GATEWAY_JOB_SCRIPT` to `/job/script.sh`, `exec bash -e /job/script.sh`.
No credentials in the image; AWS calls use the instance role through IMDS like
service containers do. CI builds and pushes on `main` through an OIDC role
scoped to that one ECR repository.

A job may name any other image; the contract is only the entrypoint behaviour
above, or an image whose own entrypoint ignores the script and does its own
thing.

## 8. Operations

- IAM: the instance role already reads every secret and already writes and
  reads `/gateway/*` log groups, so `/gateway/jobs/*` needs no change. The
  runner image needs an ECR repository and a CI push role (operator step).
- A job's secret lives under `gateway/jobs/{name}` by convention, a flat JSON
  object of strings, created by an operator. For the WMSFO mirror it holds the
  secret names the script reads, the dev API base URL, and the gateway CI
  client id and secret so the script can ask for a same-tag dev deploy when
  dev is ahead in migrations.
- Metrics: run count by status and run duration per job, in the gateway's
  metric namespace; an alarm on `failed + timed_out + lost > 0` is an
  operator choice per job.
- Deploying this feature is a gateway release: ASG refresh, the usual rule.
  The dashboard deploys separately.

## 9. Failure modes

| Case | Behaviour |
|---|---|
| Secret missing or not flat JSON | Run `failed` before start, detail names the ref, nothing executes |
| Image pull fails | Run `failed`, detail carries the registry error |
| Script exits non-zero | `failed` with exit code and tail |
| Timeout | Container killed, `timed_out` |
| Leader dies mid-run | The run's instance finishes it; if that instance is gone, the new leader marks it `lost` |
| Gateway restarts mid-run | Container continues; the same instance re-attaches by label on its first tick |
| Two leaders for one tick | The claim is a conditional update; one wins, the other sees nothing to do |
| Operator deletes a job mid-run | `409` unless forced; forced cancels first |

## 10. Build plan

Flat tasks, posted to grunt in this order. Each verifies with the repo's own
test suite; nothing here touches the fleet until an operator merges main.

1. **gateway-api: job model, store, management API, ops events, docs.**
   Entities and migration from section 2; `IJobStore` with EF and fake
   implementations; Cronos for schedules (`next_run_at`, validation);
   `OpsJobs` policy with the `mgmt/jobs` scope; every endpoint of section 4
   with validation and `409` semantics; `ops:jobs` events of section 5; the
   log endpoint through the existing log store with a jobs group template;
   tests through `ManagementApiFactory` for auth matrix, validation, upsert
   tri-state, script version bump, run enqueue and `409`, cancel of a queued
   run, delete with and without force, list shape never carrying the script
   body. README gains a Jobs section, tech-spec 4.4 and 4.5 gain the tables
   and the endpoint rows, REALTIME.md gains `ops:jobs`. No executor yet: a
   queued run stays queued.
2. **gateway-api: job executor.** `IContainerRuntime.RunJobContainerAsync`
   and the Docker implementation (create without port, memory limit, labels,
   awslogs config from a `LogConfigFactory.ForJob`, wait with timeout, kill,
   last-lines capture, remove); the fake runtime's scripted version;
   `SecretsManagerEnvProvider` refactor to a ref-based method; a leader
   status seam the scheduler reads without touching the database;
   `JobSchedulerService` with the duties of section 3 (claim, lost marking,
   start, drive, cancel, re-attach after restart, catch-up rule); metrics;
   tests against the fakes for every row of section 9 plus the reconciler
   ignoring job containers. Depends on task 1.
3. **gateway-job-runner: the image.** Repository scaffold, Dockerfile and
   entrypoint of section 7, a smoke test that runs the image with a two-line
   script and checks the exit code and output, CI workflow that tests on
   every push and pushes to ECR on `main` through the OIDC role named in the
   repository secrets. Placeholders only in the tree.
4. **gateway-api-admin: Jobs page.** `jobsApi` and types, the page, edit
   dialog, runs view, log panel reuse, `useJobsChannel` on `ops:jobs`, drawer
   entry, tests. Depends on task 1 for the contract; built against the
   documented shapes.

After the four verify locally: operator steps (ECR repository, CI role,
optional `gateway-ci-jobs` client), gateway release, dashboard release, then
the first job: the WMSFO mirror script ported from the workstation tool into
`wmsfo-v2/jobs/mirror.sh`, its secret, and its registration with schedule
`0 4 * * 1` in `America/Chicago`.

## 11. Rules for every task

Every repository is public: no account ids, hosts, ARNs, pool ids, secret
values or LAN addresses anywhere, tests included (invent placeholders). No em
dash or en dash in any file. Comments describe the code as it is. Fakes over
real services in tests: no Docker, Postgres or AWS on the build box. Do not
change deploy, rollback, reconcile or realtime behaviour for services. Keep
the existing style of each repo (records, snake_case columns, MUI, vitest).
