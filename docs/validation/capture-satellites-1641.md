# Retained capture satellite evidence — #1641

This is bounded software/virtual integration evidence for `development/v1`. Setup,
compatibility, retention and restart behavior are described in the
[runbook](../runbooks/capture-satellites.md). The complete observed resource rows,
checksums and unavailable outcomes are in the [resource artifact](capture-satellites-1641-resources.json).

## Reproduction and measured scope

Measured 2026-10-24T09:24:35.8892563+00:00 with SDK 10.0.402, .NET 10.0.13 Release, native Linux X64,
12 available processors, workstation GC and Interactive latency mode. One process,
one capture at a time, no central services and no provider requests; the shared
heavy-gate lock excludes other cooperating heavy gates. It does not reserve the
whole host or establish production server-GC/container performance.

Each arm executes one complete warmup and five measured operations with real
durable raw admission, the standard lane, SQLite/filesystem publication, retained
SVG composition/read, and frozen packed-image export. Inputs are 640×640 Bayer16
(819,200 raw bytes), a five-second reported exposure, one selected object, six
normalized records (7,962 bytes), 1,080 EOP bytes and 5,065 leap-table bytes.
The fixture admits already acquired evidence; measured wall time excludes a real
five-second acquisition wait. The baseline uses the same preview/manifest/export
path with both satellite nodes absent. The enabled arm adds the actual two nodes.

Numerical limits are 200ms, 100,000 evaluations and 8,192 samples. The `deadline`
arm deliberately substitutes 0.000001ms to exercise the real deadline outcome;
the top-level resource JSON records the ordinary 200ms default. Per-operation
envelopes are producer/persistence ≤3,000ms, render/read ≤1,000ms and total managed
allocation ≤128MiB. These are this harness’s declared bounds, not #1395 acceptance.

Reproduce after a Release solution build with the runbook’s `flock`/`timeout`
command and `FullyQualifiedName~CaptureSatelliteResourceTests`. Focused correctness:

```bash
flock -o /tmp/hvo-heavy-gate.lock timeout 900 \
  env -u HVO_PRODUCTION_CATALOG_BUNDLE DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
  dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --no-build --configuration Release --filter FullyQualifiedName~CaptureSatellite \
  -nodeReuse:false -p:UseSharedCompilation=false
```

## Observed resource results

Producer/persistence includes the complete standard lane, including preview and
manifest publication. CPU, allocations and I/O cover admission through export.
Throughput is the inverse mean of those sequential complete software operations.

| Arm | Producer median / worst ms | Render/read median / worst ms | Mean CPU ms | Mean allocated MiB | Operations/s | Envelope met |
|---|---:|---:|---:|---:|---:|---:|
| disabled-baseline | 40.79 / 42.33 | 6.37 / 7.32 | 79.68 | 8.48 | 11.40 | 5/5 |
| enabled | 113.67 / 130.06 | 8.17 / 9.00 | 138.12 | 21.87 | 5.73 | 5/5 |
| time-unavailable | 66.19 / 68.30 | 7.28 / 7.67 | 97.22 | 11.84 | 8.80 | 5/5 |
| deadline | 95.19 / 98.95 | 6.69 / 6.71 | 165.72 | 18.77 | 7.05 | 5/5 |

| Arm | Mean logical read / write MiB | Mean storage write MiB | Working-set delta range MiB | Largest retained root MiB |
|---|---:|---:|---:|---:|
| disabled-baseline | 8.38 / 3.95 | 4.08 | -5.20…2.46 | 23.99 |
| enabled | 9.27 / 4.48 | 4.69 | 0.02…4.28 | 25.01 |
| time-unavailable | 8.80 / 4.29 | 4.44 | 0.00…1.63 | 24.20 |
| deadline | 9.17 / 4.43 | 4.64 | -4.46…4.26 | 24.90 |

Process peak working set reached 358.82MiB across the test run.
Linux `/proc/self/io` supplies logical characters, operation counts and attributed
storage bytes; storage reads were zero with warmed/page-cached inputs. Root bytes
include SQLite/WAL, raw, private inputs/results, products and saved exports, and
grow across six retained captures per arm. Working-set deltas may be negative due
to collection/reclamation; the rows preserve those observations.

The enabled increment is expected work: propagation, five retained typed products,
private crash-recovery inputs/result, an orbital reference, and overlay composition.
It adds about 73ms median lane time and 13.4MiB mean managed allocation over the
disabled baseline here. This fits the declared envelopes; it is not a general
regression allowance for larger sources, images, populations or concurrency.

All five enabled operations retained `Complete/None`, one renderable track each.
The time-unavailable arm retained `Unavailable/TimeInputsUnavailable:Disabled`
for all five operations. The forced deadline arm retained `Unavailable/Deadline`
for all five, with zero tracks; successful persistence does not turn these into
successful predictions. Pending, leased and retry backlog were all zero after
every measured completed operation. No additional scheduler was introduced.

## Acceptance evidence

| Boundary | Evidence |
|---|---|
| Post-admission, opt-in, exact immutable inputs | Actual standalone ingress/standard-lane integration; shared compact/exact and portable reconstruction tests; production registration has `AutoInclude: false` |
| Pin before publication; crash/restart/idempotency | Two interruption boundaries: private input before intent and outputs before node commit; restart refuses current/time lookups and compares frozen bytes; archived replay preserves original result despite changed current site/profile |
| Source retention and archive safety | Actual expiration of ordinary consumers then support then raw/reference; existing immutable metadata archive copy protects the reference after canonical loss; corrupt intent cannot redirect the raw hold |
| Canonical tracks and typed source/payload | Exact S1 output bytes; exact `CreatePayloadAndSourceFacts` envelope; existing gap/segment, sample/label/style and unknown/stale caption tests; query-partial qualification also covers complete child prefixes |
| Authenticated retained UI and export | Restart reads products; gallery SVG returns 401/403/200 for anonymous/reader/owner; existing independent satellite/measured controls freeze selection; with/without-layer exports have distinct checksums and actual JPEG read succeeds |
| Missing/expired/stale/withheld/no-result states | Real capture integration for absent snapshot, disabled/expired time; shared stale withholding/diagnostic and partial cases; Current Sky disabled state includes source age/coverage/configured geometry |
| Host-neutral reconstruction | Processing-only tests reconstruct identical canonical products with embedded evidence or explicit exact snapshot; the shared composition has no host/persistence reference; standalone integration independently reproduces the canonical prediction |

The complete Tier C gate, committed-range classifier and exact candidate SHA are
recorded in the issue handoff and registered scratch ledger. The target protected
profile is Development v1; no production/full-qualification workflow is dispatched.

## Limits

No shipped profile activation, live deployment, hardware timing, held-out physical
accuracy, accepted calibration or measured satellite association is claimed.
#1395 retains full integrated five-second/accuracy acceptance and #530 retains
association. This small warmed fixture does not qualify a full catalog, cold
startup, saturated backlog, full-resolution renderer or production host memory.
Canonical unavailable/partial results and rendering refusals remain evidence.
Existing snapshot retention owns source bytes; compact carriers need that exact
reference resolved explicitly. The slice adds no default upload/secondary archive
policy and no CameraAgent/LogicHost project reference.
