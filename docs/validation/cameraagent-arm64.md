# CameraAgent ARM64 Validation

## Intended Host Preflight

On 2026-07-13, `allskycamera01` (`192.168.1.5`) was confirmed reachable by
SSH and Docker context. The host reported:

- Linux `aarch64`, four CPUs, and 3,980,189,696 bytes of memory;
- Docker Engine `29.6.1`;
- approximately 51 GB free on the root filesystem;
- no existing containers; and
- the ZWO ASI178MC attached on USB 3.

No CameraAgent image was built or deployed during this preflight. The idle host
temperature was approximately 84.7 C, `vcgencmd get_throttled` returned
`0xe0008`, and the ARM clock was approximately 600 MHz despite a configured
1.5 GHz frequency. This indicates an active soft temperature limit and prior
frequency-capping/throttling, so deployment and performance observations would
have added thermal load and produced misleading results.

Repeat the intended-host validation only after cooling and power are stable.
Record image build/start time, health, restart recovery, graceful shutdown,
capture cadence, CPU, memory, storage growth, temperature, and throttle flags.
Do not derive acceptance thresholds from the thermally constrained preflight.

## Development Fallback

The `devpi5` Docker context may be used for ARM64 VirtualSky container startup
and functional testing. It is not a substitute for final performance and
physical-camera characterization on `allskycamera01`.

### 2026-07-13 VirtualSky Run

`devpi5` reported Linux `aarch64`, four CPUs, 17,006,903,296 bytes of memory,
Docker Engine `29.5.0`, no throttle flags, and approximately 63 C before the
validation run. Its root filesystem was initially full; pruning only inactive
Docker build cache recovered approximately 56 GB without stopping its existing
containers.

An on-device SDK build reached `dotnet publish` but then lost SSH connectivity.
The host recorded an orderly shutdown and reboot at that time, retained no
persistent prior-boot journal, and reported no throttle flags after restart. Do
not attribute that reboot to temperature, memory, or power without additional
host evidence. To avoid repeating the heavy build, the application was then
published locally for `linux-arm64` as framework-dependent output and copied
into the ARM64 ASP.NET runtime image on `devpi5`.

The isolated validation container used `cameraagent.sample.json`, dedicated
named volumes, and host port `15130`. LogicHost was intentionally unavailable
to exercise durable outbox retention. Observations were:

- the image reported `arm64` and 287,643,745 bytes;
- `/health` returned healthy for self, identity database, camera configuration,
  and disk pressure;
- ten captures followed the configured 25-second cadence without drift in the
  recorded timestamps;
- each storage root indexed 40 artifacts: raw, combined, preview, and annotated
  preview for every capture;
- the unavailable central endpoint left all 40 upload manifests durable in the
  local outbox;
- after 3 minutes 52 seconds, `docker top` reported 5.7 percent process CPU and
  250,912 KiB RSS;
- temperature was 63.1 C, the ARM clock was approximately 2.4 GHz, and
  `get_throttled` remained `0x0`;
- graceful stop logged `Capture processing channel drained` and exited with
  code 0; and
- restart preserved the 40 prior index/outbox entries, returned healthy, and
  increased both to 48 after two more captures.

The validation container, image, and dedicated volumes were removed afterward.
These are descriptive development-host observations, not acceptance thresholds
and not final full-resolution or physical-camera performance evidence.

### 2026-07-13 Full-Resolution and Central Ingest Run

Issue #81 extended the `devpi5` fallback validation to the full-resolution
VirtualSky profiles and an isolated LogicHost deployment. Both profiles used the
pinned 119,625-object HYG 4.2 catalog with SHA-256
`b51d18b722199e89aa8fe4622ebe507346c75effb375e546881452a263f0b9e2`. The
LogicHost dependencies, device bootstrap, OAuth client credentials, checksum-
verified MinIO ingest, SQL upload records, Loki logs, and Prometheus/OTLP
telemetry were healthy.

The full ASI174MM run produced `1936x1216` Mono16 frames with 2,004 visible
objects and 163 constellation strokes. Capture intervals remained 24.992-25.000
seconds. The profile required `horizontalFlip: true` to match the intended
east/west orientation. At the captured timestamp, Capella and Procyon scene
coordinates differed from the pinned Stellarium 24.4 oracle by 0.021 and 0.067
pixels. Raw pixel neighborhoods at both positions contained signal above the
frame background. Runtime-counter observations over approximately one minute
were:

- 408-467 MB working set, 448 MB mean;
- 140-167 MB large-object heap, 160 MB mean;
- 44-67 MB LOH fragmentation, 62 MB mean;
- 4.7 MB/s mean and 73 MB/s peak managed allocation rate;
- approximately 167 ms processing and 209 ms capture-loop time; and
- less than 0.005 seconds of GC pause time in any sampled second.

The full ASI178MC run produced `3096x2080` Bayer RGGB16 frames and RGB24 preview
and annotated artifacts. The configured 300-object query plus constellation
endpoints yielded 309 visible objects and 155 strokes. Capture cadence remained
24.992 seconds and processing required approximately 1.33 seconds. Capella, the
Sun, Venus, and Jupiter differed from Stellarium by at most 0.054 pixels, and
their raw Bayer neighborhoods contained strong signal. Temperature remained
57-60 C with `get_throttled=0x0` throughout both profiles.

Profiling identified storage and large-buffer allocation as the major costs,
not scene rendering. Sampled stacks were dominated by file `unlink`, `open`,
write, and wait operations; scene rendering and Bayer demosaic were each below
one percent of sampled wall-clock stacks. The original outbox path rewrote a
large JSONL index after every acknowledged artifact. With four artifacts per
capture this prevented the ASI174 backlog from catching up even though each
central upload returned HTTP 202 in approximately 0.27-0.52 seconds. Cleanup
was changed to delete one acknowledged upload batch and rewrite each affected
daily index once.

Storage also copied every large pixel buffer with `ToArray()` before writing and
placed full scene provenance in both each sidecar and each browse-index entry.
The optimized path writes `ReadOnlyMemory<byte>` directly and keeps full
provenance in the sidecar while writing compact browse entries. In comparable
fresh ASI178 samples, mean allocation fell from 14.1 to 10.0 MB/s, mean working
set from 733 to 699 MB, mean LOH size from 353 to 289 MB, and mean LOH
fragmentation from 98 to 23 MB. Processing time and cadence were unchanged.
Longer baseline sampling reached 1.26 GB working set and 540 MB LOH, confirming
that full-resolution ASI178 remains the memory-bound profile and should receive
continued allocation and outbox-provenance optimization.

These measurements are descriptive development-host data. Future acceptance of
a production ASI acquisition module, its USB throughput, and clean thermal
behavior still requires a cooled and unthrottled `allskycamera01` with the
physical ASI178MC. That runtime acceptance is separate from the standalone SDK
and profile characterization and does not block use of an explicitly
provisional virtual profile.

### 2026-07-16 Split-Host Post-Merge Run

The durable fleet telemetry work from issue #102 was validated after PR #145
merged as `d85600b`. LogicHost ran natively on the amd64 `hvo-docker` context,
and CameraAgent ran natively on the ARM64 `devpi5` context. The run used an
isolated `SkyMonitorSmoke145` database, run-specific Docker volumes, a Redis
namespace, MinIO bucket and credentials, device identity, and the nine-row
fixture catalog. Existing SQL Server, Redis, MinIO, Mailpit, and observability
containers remained shared dependencies and were not reset.

The from-scratch path found two deployment defects before the successful run:

- Linux `O_DIRECTORY` differs by ABI: it is `0x10000` on x86_64 and `0x4000`
  on ARM64. The fixed x86-only value prevented raw-ingress directory durability
  on ARM64. Issue #146 and PR #147 added architecture-specific mapping and
  merged as `44d5573`.
- An unprovisioned CameraAgent attempted OAuth token acquisition before its
  bootstrap envelope had supplied credentials. Issue #148 and PR #149 bypassed
  central authorization only for the bootstrap request, retained normal auth
  for every other central call, and merged as `f627711`.

The corrected run exercised the real local-owner and central registration
workflow rather than editing identity stores. It verified registration and
activation, envelope import, encrypted local secret persistence, rig-profile
v1 seed, restart with the generated device identity as the configured agent
identity, and upload enablement only after provisioning. The authenticated
CameraAgent latest-frame route returned HTTP 200. OpenTelemetry trace and metric
exports returned HTTP 200 throughout the observed interval.

At bounded evidence checkpoints the run recorded:

- 24 locally committed captures with all standard and upload lanes completed,
  and 24 artifact-outbox batches acknowledged;
- 26 central frames and 126 central artifacts totaling 14,577,817 bytes;
- 26 accepted uploads with matching upload, object-store, and retrieved object
  checksums, with no pending or quarantined artifact-consistency work;
- 100 completed derivative jobs, two reason-coded skipped derivative jobs, and
  two waiting derivative jobs;
  subsequent derivative health reported zero pending jobs and a recent success;
- one current fleet agent at sequence 11, with 11 retained history rows and all
  reports advancing the central current state; and
- healthy database, artifact consistency, derivative worker, fleet persistence,
  Redis, MinIO, and SMTP checks. Overall LogicHost health was degraded only
  because the deliberately installed fixture catalog is not production data.

One central reconciliation attempt logged a recoverable EF Core concurrency
exception while another durable path scheduled the same derivative work. The
system converged without duplicate or stranded work, but the error-level event
is noisy and could hide actionable failures. Issue #150 tracks idempotent
concurrency handling and focused integration coverage.

The final CameraAgent health state was intentionally not presented as a clean
product result. During harness iteration, the fleet SQLite state was deleted
after the same device identity had already submitted sequences 1 and 2. The
reset edge generated two expected HTTP 409 ordering conflicts before sequence 3
advanced and later reports reached sequence 11. Those conflicts were correctly
quarantined and therefore latched fleet-heartbeat health as unhealthy. This was
a test-harness violation of installation-global sequence durability, not lost
central telemetry. Future automation must either preserve edge sequence state
or allocate a new isolated identity; it must never reset an edge sequence while
retaining central history.

The manual split-host setup also confirmed that the current application Compose
and `scripts/infra:start` assumptions do not support a from-scratch deployment
across remote Docker contexts. Issue #151 owns resumable multi-architecture
image distribution, remote path and catalog staging, optional shared-service
deployment, automated bootstrap, isolated and persistent modes, sanitized
evidence, performance workloads, and run-scoped teardown.

This was a VirtualSky functional validation with a fixture catalog. It is not
production-catalog acceptance, sustained performance evidence, or physical
camera and thermal acceptance on `allskycamera01`.
