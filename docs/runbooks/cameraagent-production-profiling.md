# Standalone CameraAgent production profiling

Start with the protected scrape and local capture readiness. Use a short
diagnostic only when those signals leave a specific question unanswered.
This runbook covers standalone CameraAgent on Linux. Storage capacity,
retention, and restore workflows belong to the
[Silo runtime runbook](cameraagent-silo-runtime.md) and
[#1973](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1973).

## Configure and read the signals

Set `Telemetry__ProductionProfiling` before starting the instance. `Baseline`
(the default) keeps the existing runtime, scrape, and configured OTLP behavior.
`Enabled` adds the bounded profile below to the existing telemetry pipeline.
Use it on a disposable instance or authorized soak until the
[qualification campaign](../validation/cameraagent-profiling-qualification.md)
establishes the overhead budget on the intended host. `Disabled` is the
comparison control. It leaves local console logs and health available while
skipping metrics and trace collection and all OTLP export. Keep console
verbosity identical in all three cases.

Load [prometheus.yml](../../deploy/observability/cameraagent/prometheus.yml) and
the [rules](../../deploy/observability/cameraagent/cameraagent.rules.yml) into
a private Prometheus instance. Replace the sample target with the instance's
trusted HTTPS address and provision an owner-created **Read** key in a
collector-owned credentials file with mode `0600`. Mount it read-only when
containerized. Configure trust for the instance CA and keep redirects disabled.
Import the [dashboard](../../deploy/observability/cameraagent/dashboard.json)
into Grafana and select that Prometheus data source. Use a private endpoint for
optional cAdvisor monitoring. Scope the example to the actual container name
and confirm the enabled cAdvisor metrics on the deployed version and kernel.
Its privileged observation, if needed, belongs on the host side. Match the
application and host panels by configured instance, not by joining container IDs.
An unauthorized scrape returns 401 or 403. A healthy `/alive` only confirms that
the process responds. Check `/health` and actual capture and publication outcomes
before declaring recovery.

| Operational question | Signal and interpretation | Attribution gap |
| --- | --- | --- |
| Is CameraAgent CPU-bound? | `dotnet.process.cpu.time` rate gives CPU cores used. Compare with quota and cgroup throttled periods. | A high value identifies pressure. Use on-CPU stacks to locate work. |
| Is memory managed, native, or cache pressure? | Process working set, allocations, GC collections and pause time, heap generation sizes including LOH/POH, and cgroup memory/current limit and OOM events. | RSS includes native memory and mapped pages. Compare trends before asking for a managed heap or native profile. |
| Are workers blocked or starving? | ThreadPool queue/workers, monitor contentions and exceptions, plus ingress/lane SQLite lock waits, processing dependency waits, and storage lifecycle waits. | These are selected waits and aggregate signals. Compare repeated stacks or an approved contention capture. |
| Is work piling up? | Ingress, lane, graph and outbox pending counts and oldest age. Use pending bytes where exposed, retry/quarantine counters and commit or completion rates. `camera_agent.ingress.projected_scene.backlog` covers staged scenes. `camera_agent.workload.materialization.pending` counts on-demand saves waiting for storage. | Graph pending is aggregate. Attribute sustained growth with accepted/processed rates and the configured dependency graph. |
| Is SQLite slow or contended? | `camera_agent.workload.duration` for `sqlite_write` and `sqlite_checkpoint`, plus existing ingress transaction, checkpoint and lane lock metrics. `busy` and `locked` are terminal SQLite return codes. `incomplete` on a passive checkpoint means some WAL frames stayed uncheckpointed. | Provider-internal retries that ultimately succeed are only reflected in elapsed time. No SQL text or statements are collected. |
| Is the filesystem, Silo, HTTP or network delaying work? | Compare ingress atomic publication and configured Silo readiness/publication duration and outcome against local HTTP status/duration, process I/O, block-device stalls, cgroup I/O PSI and network counters. Silo timings include readiness checks or an explicit publication attempt. | Host signals can aggregate unrelated work. Match the device and interval, then use host attribution. |
| Is UI or layered-product work material? | Active and connected Blazor circuits, Current Sky refresh latency, staged scene serialization duration/bytes, SVG cache hit/miss/eviction, SVG render wall latency and current-thread allocations, and compositor/encoder latency. Refresh `observed` describes duration and `incomplete` records a deadline. | Circuit errors use operational logs and process error signals. SVG CPU stacks require a short profile. Current-thread allocation excludes native allocations and other threads. |

### Collection policy

- Scrape every 30 seconds, with a 5-second timeout. Use 15 seconds only after
  qualifying that cadence. The example accepts at most 10,000 samples and 4 MB
  per CameraAgent scrape and 256 samples per optional matched host target.
  A limit rejection marks the scrape failed. Review `scrape_samples_post_metric_relabeling`,
  missing series and overflow before adjusting the deployment.
- The enabled provider limits each reader to 512 streams and 64 labeled
  points per stream, plus the SDK’s unlabelled and overflow points. The
  added application meter uses nine operation values and seven possible
  outcomes. Even the full cross-product fits under 64 and yields at most
  819 duration series (63 × 13). The two byte histograms, the cache counter and the pending gauge add
  fewer than 60 series, including SDK unlabelled and overflow points. Budget at most 4,000 total live series per agent in the qualified
  collector profile. Count the live series and reject unexpected overflow.
- Explicit duration buckets in seconds are `0.001, 0.005, 0.025, 0.1, 0.25, 1,
  5, 15, 60, 300` (scaled by 1,000 for millisecond instruments). The top
  bucket groups longer operations. Compare counts and rates before interpreting
  p95, and use median and range in controlled reports with fewer than 30 samples.
- Workload labels are fixed operation, outcome and cache-event names. The
  enabled HTTP duration view keeps only the numeric response status. Routing
  metrics keep only match status and fallback status. Circuit views remove
  all dimensions. Keep existing configuration and enum labels bounded.
  Never add job, artifact, user or correlation IDs, paths, object keys, SQL,
  timestamps, query strings or payloads as metric labels. Exemplars are off.
- If OTLP is configured, use TLS and private credentials. Enabled mode uses
  parent-based sampling with a 1% root ratio, honors local parents and drops
  remote-parent spans to prevent external requests from choosing the sampling
  decision. Keep the periodic OTLP metrics interval at 60 seconds and use
  bounded exporter queues and timeouts. During rollout check the collector's
  accepted/refused telemetry, queue size/capacity and failed sends. Keep the
  collector's resource and queue telemetry separate from application measurements.
- Retain aggregate metrics for 15 days initially and restrict dashboard and
  collector access to operations. Explicit alerts cover missing scrape,
  nonaccepting ingress and OOM. These use availability or invariant thresholds.
  Select resource, throttling, backlog and duration thresholds from an approved
  workload baseline before enabling paging for them.

## Escalation and private diagnostic capture

Record the incident, approving operator, UTC interval, workload, instance
build identity, and specific question in the private incident record. Keep one
diagnostic active at a time. Stop it if capture cadence, backlog, free space,
pressure or OOM indicators worsen. Match the target by its exact process and
instance before attaching. On an installed Linux container, use the host PID
from `docker inspect --format '{{.State.Pid}}' "$container"` only if the app is
the container's initial process. Otherwise select the actual runtime process
from the container's process list. Check `/proc/$pid/stat` start time and cgroup
identity again before each attach. For a native host service use its verified
service PID. Run tools from a separate, preapproved diagnostic host context or
ephemeral workload with matching architecture/libc and a verified .NET 10
tool version. Do not install them into the application image.

Use a dedicated private disk volume outside capture, with a quota and headroom.
In a restricted administrative shell (no history or shell tracing), create a
fresh directory with `umask 077` and
`bundle=$(mktemp -d "$diagnostic_root/cameraagent.XXXXXXXX")`. Check mode 0700 and
use mode 0600 for output. For PID-based EventPipe tools, run as the app UID
with access to the target PID and the runtime's Unix diagnostic socket in its
mount namespace. Alternatively use the approved same-UID shared Unix socket
with `--diagnostic-port "$socket"` (for a tool listening on the socket, configure
the runtime port as `connect,nosuspend` on the diagnostic instance). If these
conditions cannot be met, stay with host/Tier 1 signals until access is approved.
A diagnostics socket conveys process access. Never forward it over the network
or share it with routine collectors.

### Tier 2. Counters and Linux pressure

Run up to 60 seconds. Compare CPU use and wait, allocation/LOH/GC movement,
ThreadPool growth and process or device I/O with the actual symptom.

```bash
timeout --signal=INT --kill-after=5s 60s dotnet-counters collect --process-id "$pid" \
  --refresh-interval 2 --counters System.Runtime --format csv --output "$bundle/runtime.csv"
pidstat -u -r -d -w -t -p "$pid" 2 30 > "$bundle/pidstat.txt"
iostat -xz 2 30 > "$bundle/iostat.txt"
vmstat 2 30 > "$bundle/vmstat.txt"
sar -n DEV,TCP,ETCP 2 30 > "$bundle/network.txt"
```

Check `/proc/$pid/io` before and after the interval. On cgroup v2, inspect
`/proc/$pid/cgroup` and map its `0::` entry to the matching host cgroup-v2
mount. Read `cpu.max`, `cpu.stat`, `memory.current`, `memory.max`,
`memory.events`, `io.stat`, and `cpu.pressure`, `memory.pressure` and
`io.pressure` from the confirmed group. Compare with host
`/proc/pressure/{cpu,memory,io}`. Throttle and OOM counters are cumulative.
Rising wait/stall time without high process CPU points toward scheduling or
I/O pressure. Capture only the relevant numeric counters. Exclude cmdline,
environment, mount listings and full container inspection from the bundle.

### Tier 3. Short attribution

Collect one 30–60 second trace per question. An EventPipe sample represents
managed thread wall time, which includes waits. Compare it with true on-CPU
samples before attributing CPU use. Select one EventPipe command:

```bash
timeout --signal=INT --kill-after=10s 60s dotnet-trace collect --process-id "$pid" \
  --profile dotnet-sampled-thread-time,dotnet-common --duration 00:00:40 \
  --buffersize 32 --output "$bundle/threads.nettrace"
timeout --signal=INT --kill-after=10s 60s dotnet-trace collect --process-id "$pid" \
  --profile gc-verbose --duration 00:00:40 --buffersize 32 --output "$bundle/alloc.nettrace"
timeout --signal=INT --kill-after=10s 60s dotnet-trace collect --process-id "$pid" \
  --providers Microsoft-Windows-DotNETRuntime:0x14000:4 --duration 00:00:40 \
  --buffersize 32 --output "$bundle/locks.nettrace"
```

Use the fixed threading/contention provider for lock and starvation questions.
Add dependency providers only after reviewing event schemas for paths, URL
queries, SQL and credentials. Leave the database profile off. For hangs, take
three reports several seconds apart with
`timeout --signal=INT --kill-after=2s 5s dotnet-stack report --process-id "$pid"`
and write each to a separate private file. Repeated identical blocked stacks
and a growing queue justify looking at the waiting boundary.

For native/kernel CPU attribution, on a Linux host where the operator already
allows performance events, use
`timeout --signal=INT --kill-after=5s 60s perf record -F 49 -g -p "$pid" -o "$bundle/cpu.data" -- sleep 40`.
Alternative on supported .NET 10/Linux x64 or ARM64 glibc hosts with
tracefs, kernel 6.4+, `CONFIG_USER_EVENTS`, and approved root access:
`timeout --signal=INT --kill-after=5s 60s dotnet-trace collect-linux --process-id "$pid" --duration 00:00:40 --output "$bundle/linux.nettrace"`.
Check managed symbolization in the qualification environment before trusting
missing frames. When host policy allows BCC, use a PID-scoped aggregate such as
`offcputime -p "$pid" 40`, `runqlat -p "$pid" 10 4`, or
`tcprtt -i 10 -d 40` on a disposable host where that workload owns the observed
traffic. Use `biolatency 10 4` for device-level latency, then correlate it with
PID I/O. BCC tool names/flags can vary by distro. Confirm scoping via the
installed tool's help. Keep histograms and stack counts private and avoid
filename, command-line, or per-packet tracing.

### Tier 4. Intrusive escalation

Use a GC dump only after heap trends support retained managed memory and a
pause is tolerable. Obtain a separate decision for a process dump, LLDB/SOS,
native allocation profiling or `strace` and for any required ptrace/perf/BPF
permissions. Record the expected pause, heap/RSS and headroom, disk quota,
permission scope, OOM impact and sensitive-data risk. Prefer an isolated
reproduction. Run only one approved command with an operator-selected
deadline and an output quota larger than the expected artifact.

On Linux, CameraAgent resolves the `dotnet-dump --output` path in its own
mount namespace. Before attaching, verify that the exact `$bundle` path in
the target container or process resolves to the same directory on the private,
quota-controlled artifact volume seen by the diagnostic shell. Verify write
access there with a create-and-remove probe in the target mount namespace as
the CameraAgent process UID. Skip the dump if either check fails. After
collection, verify that `$bundle/process.dmp` resolves to a file on that same
private volume and is accessible to the authorized retention and cleanup process.

Attaching `strace` only to the leader PID misses `fsync` and `fdatasync` calls
made by background capture and storage workers. Use `-p "$worker_tid"` without
`-f` only when you have verified the worker TID under `/proc/$pid/task` and
can attribute the sync work to it. Otherwise obtain approval for a strictly
time-bounded all-thread attach with `-f -p "$pid"`. It can also follow child
processes and add substantial overhead even with a syscall filter. Stop
the trace immediately if capture cadence or liveness degrades, backlog grows,
or disk headroom drops below the incident limit.

Examples:

```bash
timeout --signal=INT --kill-after=5s 60s dotnet-gcdump collect --process-id "$pid" --output "$bundle/heap.gcdump"
timeout --signal=INT --kill-after=5s 60s dotnet-dump collect --process-id "$pid" --type Heap --output "$bundle/process.dmp"
timeout --signal=INT --kill-after=5s 40s strace -f -c -e trace=fsync,fdatasync -p "$pid" \
  2> "$bundle/sync-summary.txt"
```

A dump can contain credentials, image buffers and user data even with narrow
tool settings. Analyze it offline with a restricted debugger account. Keep
raw artifacts in the private volume. Share derived counts and redacted
attribution only. Retain raw incident files for at most seven days unless a
documented incident hold requires an expiry extension. Record collection end
and artifact hashes in the private incident record. Remove every raw copy and
confirm deletion at expiry. On closeout, stop the diagnostic workload and
remove temporary mounts, sockets and grants. Revoke any time-limited keys and
verify application liveness, current capture and local publication.

### Product disposition

Use the existing same-UID Unix socket or a controlled ephemeral diagnostic
workload for this slice. `dotnet-monitor` could automate authenticated capture,
but would create a diagnostic API, separate identity, socket access, and an
artifact-retention control plane. It is deferred until an operator requires
automation and the exact image, TLS/authentication, volume and deny-by-default
collection route can be verified. No diagnostic workload is installed by
CameraAgent. For a later continuous-profiler trial, start with sampled CPU/wall
profiles in an isolated soak. Verify architecture, libc, symbols, retention,
privacy and backend failure behavior. Qualify allocation, contention,
exceptions and live-heap observers independently. The historical
[#1151 P5 evidence](../validation/issue-1151-p5-rss-diagnostics.md) established
neither a leak nor observer neutrality.
