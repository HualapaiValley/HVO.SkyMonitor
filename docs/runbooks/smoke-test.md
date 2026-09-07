# Smoke-Test Environment Runbook

This runbook defines the legacy environment-file boundary for local reproducible
platform smoke tests. Issue #151 provides the inventory-driven split-host
[deployment, bootstrap, W0 smoke, and teardown workflow](split-host-preflight.md).
The commands in this document only generate and verify legacy smoke inputs and do
not start, stop, reset, or mutate resources.

## Environment Contract

`.env.smoketest.template` is the checked-in, secret-free schema. The generated
`.env.smoketest` is ignored, must remain owner-only, and contains one smoke run's
complete operator inputs:

- control/Docker host names, addresses, public URLs, ports, and host-path mapping;
- SQL Server, Redis, MinIO, SMTP, and OpenTelemetry endpoints and credentials;
- LogicHost and CameraAgent owner/client identity inputs;
- desired Observatory name, east-positive longitude, latitude, elevation, and
  timezone;
- CameraAgent friendly name, configuration file, expected dimensions, exposure,
  and cadence;
- approved production catalog identity and control-host installation;
- run ID, state/evidence paths, bootstrap mode, and data policy.

Device ID, verification code, registration/public IDs, envelope, and device
secret are application-generated state. They must never be added to
`.env.smoketest`; split-host automation obtains them through supported
application APIs and retains private transient material under the owner-protected
`SMOKETEST_RUN_STATE_PATH`.

## Initialize

Create or refresh the ignored file from `.env` and the template:

```bash
./scripts/smoke:env init
```

Existing `.env.smoketest` operator values win, so rerunning initialization
preserves an established run. The canonical Compose/SQL/Redis/MinIO namespaces
are re-derived in isolated mode. The development `.env` runtime root is ignored
so a smoke cannot silently reuse ordinary application state. Missing smoke owner
credentials are generated without being printed. Shared-service and CameraAgent
credentials are never invented because they must match provisioned services.

Non-secret host-specific values can be supplied without opening the secret file:

```bash
./scripts/smoke:env init \
  SMOKETEST_DOCKER_HOST_NAME=hvo-dev-01 \
  SMOKETEST_DOCKER_HOST_ADDRESS=192.168.1.9 \
  SMOKETEST_LOGIC_HOST_PUBLIC_URL=http://192.168.1.9:5174 \
  SMOKETEST_CAMERA_AGENT_PUBLIC_URL=http://192.168.1.9:5130
```

Only `SMOKETEST_*` and `HVO_RUNTIME_DATA_ROOT` overrides are accepted on the
command line. Credentials must come from the owner-only baseline or generated
target, never process arguments.

Review non-secret values locally, then set host-specific public URLs and the
host-side repository/runtime paths. Never paste the file or its secret values
into an issue, PR, log, or evidence bundle.

## Validate And Preflight

Static validation is safe before services are reachable:

```bash
./scripts/smoke:env validate
```

Preflight adds read-only Docker, DNS/TCP endpoint, production catalog manifest,
database-file, and checksum checks:

```bash
./scripts/smoke:env preflight
```

Both commands clear ambient contract variables, fail closed, and print variable
names or resource classes, never secret values. Validation also checks that the
selected CameraAgent JSON has the declared dimensions, cadence mode/interval,
and night exposure. Preflight does not contact application login/bootstrap
endpoints because the applications may not have started yet.

## Data Policy

`SMOKETEST_DATA_POLICY` is one of:

- `preserve`: retain existing application and shared-service history;
- `isolated`: use run-scoped resources created by the #151 orchestrator;
- `reset-shared`: permit a future explicit shared-data reset.

`reset-shared` is rejected unless `SMOKETEST_CONFIRM_SHARED_DATA_RESET` exactly
matches `SMOKETEST_RUN_ID`. The current environment command only validates this
intent; it does not delete data. A reset implementation must enumerate the exact
SQL database, Redis namespace, MinIO buckets, application state, and Mailpit data
before deletion and remain scoped to HVO.SkyMonitor resources.

`preserve` and `reset-shared` accept only database `SkyMonitor`, Redis prefix
`skymonitor:`, and buckets `skymonitor-artifacts` and
`skymonitor-diagnostics`. This prevents a future reset consumer from accepting
an unrelated resource merely because the confirmation token matched.

For `isolated`, initialization derives run-scoped SQL database, Redis prefix,
MinIO artifact/diagnostic bucket, and Compose project names from
`SMOKETEST_RUN_ID`; validation rejects an isolated contract if any namespace no
longer exactly matches its canonical run-owned name.

## Contract Tests

```bash
./scripts/test:smoke-env
```

The tests use temporary files and mocked connectivity. They verify owner-only
permissions, generated run-scoped credentials, required fields, coordinate and
reset guards, exclusion of generated bootstrap state, production catalog
identity/checksum checks, and output redaction.

## Full-Resolution Standalone CameraAgent

Issue #171 has a separate opt-in smoke that does not start LogicHost or shared
services. It loads `cameraagent.standalone-production-smoke.json`, rejects all
central HTTP traffic, and requires an installed Production HYG 4.2 catalog. The
checked-in `appsettings.StandaloneProductionSmoke.json` supplies the standalone
host boundary when running the CameraAgent directly with
`DOTNET_ENVIRONMENT=StandaloneProductionSmoke`. The checked-in deployment pair
uses the instance `state/` root for both raw ingress and derivative storage and
the selected `/var/lib/hvo/skymonitor/catalogs/hyg-v42-production` installation.
If an operator changes the storage
location, both `CameraAgent__RawIngressRoot` and the deployment copy's
`LocalStorage.options.storageRoot` must resolve to the same directory.

Direct-host startup also requires an owner password and writable identity,
provisioning, and Data Protection state. Keep the password out of source and
provide it through environment configuration, user secrets, or the host's
secret manager. `LocalIdentity__AdminPasswordFile` accepts an absolute path to
a newline-terminated secret file when the password must not be placed in the
process environment. Identity and provisioning paths are configurable as shown
below; Data Protection keys are written to `DataProtection-Keys` below the
CameraAgent content root, which must therefore be writable and persistent. A
representative direct launch is:

```bash
DOTNET_ENVIRONMENT=StandaloneProductionSmoke \
LocalIdentity__AdminPassword='OperatorSecret!171' \
LocalIdentity__DatabasePath=/var/lib/hvo/skymonitor/cameraagents/<instance-uuid>/state/identity/cameraagent_identity.db \
DeviceProvisioning__StateDirectory=/var/lib/hvo/skymonitor/cameraagents/<instance-uuid>/state/provisioning \
  dotnet run --project src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj
```

Build and install the pinned catalog only when the approved package is not
already present:

```bash
./scripts/catalog/build-hyg-v42.sh --fetch \
  --install-root /var/lib/hvo/skymonitor/catalogs/hyg-v42-production \
  /var/lib/hvo/catalog-build
```

Run the smoke against the installation root, not a bundle or SQLite file:

```bash
HVO_CATALOG_PERF_ROOT=/var/lib/hvo/skymonitor/catalogs/hyg-v42-production \
  ./scripts/test:cameraagent-standalone-production-smoke
```

The five-second arrival budget (each module start 4.9-5.5 s after the previous
one under the minimum-start-interval cadence) is a cadence contract measured as
wall-clock module-start intervals, so it is only meaningful on a quiescent host.
The runner's post-deadline work (schedule confirmation, admission, timer wake-up)
competes with every other process on a shared build host, and one contended
interval fails the trial without any runtime change. Before each trial the
runner therefore reads the one-minute load average and refuses to start (exit
`3`, naming the load) when it exceeds `HVO_SMOKE_MAX_LOAD1`, which defaults to
half the processor count; set `HVO_SMOKE_QUIESCENCE_WAIT_SECONDS` to let it
poll every ten seconds for the host to settle first, and set
`HVO_SMOKE_QUIESCENCE_CHECK_ONLY=1` to run only that check. A trial that still
trips the budget fails once with every contended interval named (capture index
and sequence, observed interval, monotonic start jitter, start reason) together
with the host load at the start and end of the measured captures and the GC pause
total, and it writes `issue-171-cadence-diagnostic.json` beside the other
evidence before asserting. Read that file to separate host contention (high load
average, `DeadlineReached` with jitter, long GC pauses) from a runtime regression;
the budget itself is not relaxed for busy hosts.

The gate fails closed on package kind, version, manifest/schema/preprocessing
versions, database SHA-256/length, and 119,625-row identity. It retains a
sanitized full-frame annotated JPEG and JSON manifest for each of five
independent, approximately one-minute trials below
`TestResults/issue-171/production-smoke`, including the
fixed `2026-01-15T08:00:00Z` astronomy scene epoch and run clock anchor,
selected catalog row IDs, deployment location, recipes/lineage, actual
module-start five-second cadence, CPU, RSS/LOH, allocations, retained/download I/O, throughput, latency,
queue/backlog/drain, restart, and central-traffic evidence. The UTC anchor is
fixed once at the start of each run and then advances with real elapsed time so
all five trials render the same sky while durable lease and retention clocks
remain on operational wall time. The runner also executes the 5-warmup/30-sample
W1 and W2 reference-calibration gates and the representative durable local-graph
gate into the same result root.

## Two Isolated Standalone CameraAgents

Issue #197 extends the standalone proof to concurrent Hualapai and explicitly
synthetic Siding Spring CameraAgents. The opt-in runner builds CameraAgent once,
uses that content-addressed image ID for both containers, and invokes
`deploy/acceptance/cameraagent-dual-standalone-smoke/compose.yml` under two
project names. Each project
owns a separate bridge, runtime root, catalog copy, owner secret, local Identity
database, Data Protection directory, provisioning state, cookie, AgentId, and
OTLP file collector. LogicHost and the shared SQL Server, Redis, MinIO, and
Mailpit services must be absent.

Stop LogicHost, install the approved Production HYG package, and run:

```bash
HVO_CATALOG_PERF_ROOT=/var/lib/hvo/skymonitor/catalogs/hyg-v42-production \
HVO_OTEL_COLLECTOR_IMAGE=otel/opentelemetry-collector-contrib@sha256:f2f01157055a9b2aab9df7118e1f1c9abf345e99b23bc7a2bc791db374a7d0f6 \
  ./scripts/test:cameraagent-dual-standalone-smoke
```

On a direct Docker host, bind roots and published endpoints use the repository
path and loopback. In a devcontainer, the runner discovers the repository bind
as seen by both the control container and Docker daemon, stages runtime state
below the primary checkout's ignored `data/agent/issue-197`, and publishes only
on the Docker bridge gateway used by the control container. Explicit
`HVO_CAMERAAGENT_DUAL_STANDALONE_BIND_LOCAL_ROOT`,
`HVO_CAMERAAGENT_DUAL_STANDALONE_BIND_HOST_ROOT`, and
`HVO_CAMERAAGENT_DUAL_STANDALONE_DOCKER_HOST_ADDRESS` overrides are available
when automatic bind discovery is not possible. Bind-root overrides are
constrained to the dedicated repository `data/agent/issue-197` subtree; the
runner requires its ownership marker and serializes executions with a PID lock
directory before deleting any state.

The default workload runs five independent trials with five warm-up and 30
measured W1 captures per agent at five-second cadence. Each trial verifies
authenticated operations/gallery access, exact configuration/recipe/retention
identity, artifact hashes and lineage, distinct catalog and mutable-state
inodes, both independent SIGKILL recoveries during known pending durable work,
bidirectional 20-second pause isolation, deny-sink central-attempt counts,
health, Prometheus runtime allocation/LOH metrics, OTLP logs/metrics/traces,
fixed-period CameraAgent process and collector container resource evidence, and
final durable drain.
Transient evidence is written to
`TestResults/issue-197/dual-agent`; only sanitized annotated images and reviewed
summary evidence belong in `docs/validation`.

Only the default 5-trial, 5-warm-up, 30-measured workload emits
`five-trial-summary.json`; overrides emit a non-citable
`diagnostic-summary.json` and suppress p95 below 30 samples. CameraAgent process
CPU, RSS, and storage I/O use two-second Linux `/proc/1` counters. Cumulative
CPU and I/O include each PID 1 lifetime through a final pre-stop sample and use
process start ticks to separate deliberate restarts. Docker runtime samples
separately report collector CPU estimates, container memory, block I/O, and
network I/O. Managed allocation is a measured-window counter delta; LOH/POH size
and fragmentation are explicitly last-GC before/after observations, not
continuous heap peaks.
Final mode also runs the unchanged #171 standalone gate and blocks citation if
the equivalent normalized retained bytes/capture or cadence has an unexplained
positive regression above 20 percent. CPU/capture and peak RSS are retained as
non-blocking observations with an explicit boundary warning because #171
combines VSTest and its in-process host while #197 measures each container PID 1
without the harness or collectors. Completion latency is also non-blocking
because #171 waits for retention-visible gallery completion while #197 records
processing-node completion. Full-lifecycle throughput remains non-blocking
because #197 includes two crashes and two 20-second isolation pauses that are
not part of #171's single-agent workload.
