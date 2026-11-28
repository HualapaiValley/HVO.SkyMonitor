# CameraAgent Smoke Tests

## Full-Resolution Standalone CameraAgent

The opt-in full-resolution smoke from #171 starts a standalone CameraAgent. It loads `cameraagent.standalone-production-smoke.json`, rejects all
central HTTP traffic, and requires an installed Production HYG 4.4 catalog. The
checked-in `appsettings.StandaloneProductionSmoke.json` supplies the standalone
host boundary when running the CameraAgent directly with
`DOTNET_ENVIRONMENT=StandaloneProductionSmoke`. The checked-in deployment pair
uses the instance `state/` root for CameraAgent state and
the selected `/var/lib/hvo-sky/catalogs/hyg-v44-production` installation.
Use an installed local Silo binding. Set `CameraAgent__RawIngressRoot` to
the writable application state directory for that installation. See the
[Silo runtime runbook](cameraagent-silo-runtime.md).

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
LocalIdentity__DatabasePath=/var/lib/hvo-sky/cameraagents/<instance-uuid>/state/identity/cameraagent_identity.db \
DeviceProvisioning__StateDirectory=/var/lib/hvo-sky/cameraagents/<instance-uuid>/state/provisioning \
  dotnet run --project src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj
```

Build and install the pinned catalog using the
[production catalog procedure](../catalog/production-install.md).

Run the smoke against the installation root, not a bundle or SQLite file.

`HVO_CATALOG_PERF_ROOT` and the Deployment CLI installer bundle variable take
different levels of the same tree, and this is the part no reader guesses.
`HVO_CATALOG_PERF_ROOT` is the catalog **root**, the directory holding the
`current` pointer beside `versions/`, because that pointer is what the container
resolves. The installer bundle variable is a **version** directory below
`versions/`, and it refuses the `current` entry outright. Both paths exist and
both are readable directories, so passing the version directory here used to
satisfy the scripts' checks and then fail inside the container minutes later. The
three campaign scripts now validate the shape and refuse the version directory by
name at the point of the mistake.

```bash
HVO_CATALOG_PERF_ROOT=/var/lib/hvo-sky/catalogs/hyg-v44-production \
  ./scripts/test:cameraagent-standalone-production-smoke
```

The five-second arrival budget (each module start 4.9-5.5 s after the previous
one under the minimum-start-interval cadence) is a cadence contract measured as
wall-clock module-start intervals, so it is only meaningful on a quiescent host.
The runner's post-deadline work (schedule confirmation, admission, timer wake-up)
competes with every other process on a shared build host, and a single
out-of-budget interval fails the trial. Before each trial the runner therefore
reads the one-minute load average and refuses to start (exit `3`, naming the
load, the limit, and the wait) when it exceeds `HVO_SMOKE_MAX_LOAD1`, which
defaults to half the processor count. `HVO_SMOKE_QUIESCENCE_WAIT_SECONDS` is a
true monotonic elapsed upper bound. The initial immediate observation may admit;
after any sleep, the load comparison and final monotonic observation must finish
at or before the inclusive deadline or the runner refuses, even when that late
observation is below the limit. The runner polls at most every ten seconds.
`HVO_SMOKE_QUIESCENCE_CHECK_ONLY=1` runs only that check. The check fails closed:
`awk` and `sleep` are required commands, an unreadable or non-numeric
`/proc/loadavg` or `/proc/uptime` refuses, and any comparator or clock-arithmetic
error refuses. Evidence is produced in a staging directory beside
`TestResults/issue-171/production-smoke` (the build runs before the first
quiescence check, so build load never counts against a trial) and is published
in place of the previous run only after every gate passes. On Linux, a probed
`renameat2` operation installs the first result without replacement or atomically
exchanges a staged result with the prior directory. The canonical path therefore
always names the old or new complete tree if publication fails or the runner is
interrupted; an exchanged old tree is cleaned up only after the new name is
directory-synced. A refused or failed run leaves its staged directory for
inspection. Each trial's admission and monotonic elapsed time are recorded in
`quiescence-log.tsv`.

A trial observes the persisted cadence without asserting, writes
`issue-171-cadence-diagnostic.json` beside the other evidence (per-capture
index and sequence, observed interval, monotonic start jitter, start reason,
within-budget flag; structural violations; host load at the start and end of the
measured captures, or `null` with `available: false` when the host does not
expose it; GC pause total and mode), and only then asserts the structural
invariants and the budget. A trip fails once naming every out-of-budget interval.
The load average, GC pauses, and start reasons in that message are triage
signals, not proof of cause: corroborate with the diagnostic and a quiet-host
rerun before classifying a failure as host interference or as a runtime
regression. The budget itself is not relaxed for busy hosts.

The gate fails closed on package kind, version, manifest/schema/preprocessing
versions, database SHA-256/length, and 119,613-row identity. It retains a
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
OTLP file collector. The shared SQL Server, Redis, and
Mailpit services must be absent.

Install the approved Production HYG package and run.
`HVO_CATALOG_PERF_ROOT` is the catalog **root** here as well, not a version
directory below `versions/`:

```bash
HVO_CATALOG_PERF_ROOT=/var/lib/hvo-sky/catalogs/hyg-v44-production \
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
