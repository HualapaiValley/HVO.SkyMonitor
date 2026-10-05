# #1043 retained-history cold start

Measured 2026-10-05 in the isolated `codex/queue-1043-20261005` worktree,
Ubuntu 24.04 x64, pinned SDK 10.0.401. Baseline production sources are
`499df22901b10c201f76408615753d7b95d00015`; candidate production sources are
`0898fe6bf9043ccb9fa3fd902565fa50e19a3641` plus unchanged-source build metadata. The same Manual test assembly
runs against both Common assemblies. No installed agent, hardware, shared
service, or `/mnt/hvo-1058` state was opened for write.

The generated fixture contains 1,812 accepted captures. Each has a complete
3096x2080 RGGB16 W2 payload and a manifest with 14,000 deterministic projected
objects. Fixture creation uses production ingress, stores real payload and
sidecar files, and initializes protected location history through its production
store. This is synthetic retained history with deliberately enlarged scene
metadata; it is not the deleted original instance or a production throughput
qualification.

## Boundary and preservation

The component trial invokes actual `RawCaptureIngress.InitializeAsync`, schema
inspection, full payload/sidecar reconciliation, and stage-ownership enumeration.
Its only stage-reconciliation adapter asserts exactly the expected 1,812 keys.
Every retained raw identity, manifest byte, payload SHA/length, retention hold,
state, and evidence origin contributes to a before/after SHA-256 fingerprint.
Successful trials require 1,812 inspected records, no missing/quarantined/recovered
records or index failures, accepting health, no unexpected warnings, and successful
initialization/reconciliation spans. Existing event 2048 logs a successful checkpoint
at Warning level; the harness allows only its exact checkpoint-success message
and retains it in the evidence. Every other warning remains a failure.

The real CameraAgent trial uses the exact candidate binary bundle and a disposable
copy of that history. Before measurement, the synthetic standard consumer is
acknowledged through production lane claim/completion APIs; the resulting
completed-history fingerprint is saved. Supported provisioning startup gating
pauses capture admission, central integration/delivery are disabled, and an empty
processing graph prevents fixture history from starting unrelated processing or
retention work. A disposable local identity/configuration and a read-only copy of
the production celestial catalog complete the host fixture. Both `/health` and
`/alive` must return HTTP 200 and Healthy for at least 15 seconds; the completed
history fingerprint must still match after graceful shutdown.

All measured containers have one CPU and no swap. Default-envelope trials use
1 GiB; the larger-memory baseline only measures the old cost, without changing
product defaults. Samples cover the complete container including test runner or
host startup overhead, at approximately 200 ms intervals. `memory.peak` is the
kernel high-water mark; anonymous/file/tmpfs components are sampled maxima and
need not coincide. Per-process allocations, CPU and `/proc/self/io` differences
cover ingress initialization only. Whole-container cgroup I/O includes assembly
loading, final fingerprint verification and runner overhead. Before each trial,
only disposable fixture files receive fsync and POSIX_FADV_DONTNEED advice; no
host-global cache reset is performed. Cold start means a new process and SQLite
connection; eviction advice is not a proof that all filesystem/device caches
were cold.

## Implementation

Inspection now reads main+committed WAL in one bounded, read-only SQLite
transaction with a private 2 MiB cache and mmap disabled. It preserves physical
file/link checks, schema/integrity validation, contention retry and cancellation;
it creates no full-journal temporary copy. Startup reconciliation, owned-stage
lookup, and recovered-live binding visit one primary-key row at a time. Each
reader closes before file I/O or state repair, avoiding a history-length WAL
reader. Compact path/key sets still scale with capture count; total retained
manifest bytes no longer determine the live manifest working set. Required
payload verification I/O remains proportional to retained bytes.

## Retained fixture and component results

Fixture SHA-256: `04EC65D4ACC7A3EB98959EAA81A4B48B9420EA30D6470A3F268FFA02705828AA`.
Payload bytes: 23,337,400,320; manifest bytes: 3,225,045,207; SQLite main file:
3,243,257,856 bytes. Every payload has SHA-256
`6CDFFE4A62E387E17FCF250C44C5C9F3839C702ED6A1E79D96F1647132393976`.

One successful trial per condition; no statistical noise/confidence claim:

| Production code | Memory limit | Initialization | CPU | Process peak | Total allocated | Outcome |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| Baseline | 1 GiB | OOM before initialization completed | — | — | — | Docker OOM event; container ended after 3.08 s |
| Baseline | 8 GiB | 135.647 s | 120.763 s | 6,567,030,784 B | 36,912,829,600 B | Healthy; all preservation assertions pass |
| Candidate | 1 GiB | 139.401 s | 116.035 s | 270,397,440 B | 36,916,708,376 B | Healthy; all preservation assertions pass |
| Candidate control | 8 GiB | 116.770 s | 107.205 s | 282,816,512 B | 36,915,878,168 B | Healthy; all preservation assertions pass |

| Production code / limit | Logical read bytes (`rchar`) | Logical write bytes (`wchar`) | Physical read bytes | Physical write bytes |
| --- | ---: | ---: | ---: | ---: |
| Baseline / 8 GiB | 52,498,766,635 | 6,486,600,214 | 34,145,832,960 | 204,800 |
| Candidate / 1 GiB | 46,012,776,884 | 90,662 | 45,088,509,952 | 229,376 |
| Candidate / 8 GiB | 46,012,472,478 | 90,614 | 29,814,398,976 | 229,376 |

The default-envelope candidate lowers process peak by 95.9%, to 258 MiB, and
eliminates approximately 6.49 GB of logical temporary-copy writes. Allocations
remain approximately 36.9 GB because all retained manifests are still processed;
the change bounds live memory rather than eliminating required deserialization.
Its initialization is 2.8% slower than the baseline given eight times as much
memory, with 3.9% less CPU but more physical reads. The controlled 8 GiB comparison
resolves that cache-budget confound: candidate initialization is 13.9% faster,
CPU 11.2% lower, and physical reads 12.7% lower than the baseline. Durable physical
writes remain small metadata/checkpoint traffic; tmpfs copy writes appear in
`wchar`, not physical disk writes.

The whole-container cgroup peak reaches its configured ceiling as filesystem
cache fills (approximately 1 GiB or 8 GiB, with page-accounting granularity).
That is distinct from process working set. At 1 GiB, candidate sampled anonymous
memory peaks at 143,593,472 bytes and tmpfs at 46,960,640 bytes, with no OOM event;
the old code OOMs while filling its temporary snapshot (sampled tmpfs
953,757,696 bytes). The successful baseline samples 6,426,955,776 anonymous bytes
and 3,246,952,448 tmpfs bytes; maxima occur at different moments.

The first 8 GiB baseline completed reconciliation but its harness rejected the
existing checkpoint-success warning before publishing final measurements. It is
superseded by the table's successful run. The assertion was corrected in the same
test assembly for both sides, with no production binary change, and the 1 GiB
baseline OOM was confirmed again with that exact assembly.

## Actual CameraAgent host

The same immutable production bundle reached `/health` and `/alive` with HTTP 200
and Healthy at 212.150 seconds under **1 CPU / 1 GiB / no swap**. All 21 health
checks were Healthy, including raw ingress, capture lanes, protected deployment
location, identity, production catalog, and intentionally paused capture admission.
Readiness remained healthy for more than 15 seconds; graceful shutdown completed
at 227.932 seconds with exit code 0 and no OOM event.

| Whole-host measurement | Result |
| --- | ---: |
| Process peak (`VmHWM`) | 395,714,560 B (377 MiB) |
| Whole-container cgroup peak | 1,073,745,920 B (1 GiB plus one page of accounting granularity) |
| Sampled anonymous-memory peak | 229,670,912 B |
| Sampled tmpfs-memory peak | 68,100,096 B |
| Whole-container CPU through readiness/shutdown sampling | 207.107 s |
| Process logical read / write bytes | 65,532,074,049 / 1,304,473 |
| Process physical read / write bytes | 60,184,047,616 / 1,863,680 |

The startup log records 1,812 inspected records, zero recovered, cleaned,
quarantined or missing records, and zero held captures after the synthetic consumer
acknowledgement. The completed-history journal fingerprint remained
`EA26F052ECB51D08F425826F606F312F55FFFB34A8467BEBA0674AD4CC512D4D`.
Independent post-shutdown Python SHA-256 verification also read every payload and
sidecar: all 1,812 payload lengths/checksums match and all sidecar bytes equal the
unchanged journal manifests. Its ordered capture-ID/payload-hash/sidecar-hash
inventory is `1287C952A7C0387913F0FACF04ED784F5055B31A05AFB8638A5D07F24A9D9905`.
Preparation and this 22.584-second independent verification are excluded from
startup timing.

No Error or Fatal log was emitted. The retained warnings are the existing
checkpoint-success event, fresh disposable Data Protection keys without an XML
encryptor, and `/work/wwwroot` missing from this health-only test-output bundle.
This is the actual CameraAgent process and its infrastructure startup, not a
published static-UI/container-image qualification. The binary bundle, protected
location keys, identity and provisioning state are all disposable copies.

## Immutable measured binaries

SHA-256 values:

- baseline Common: `b047a91b784ef4236b3a16e421cf1c65c1f97ec577c563a414d6b7c64e6963ca`
- candidate Common: `9757a8cbe0ee2c75ce65b6aee0420465ee34211729b32482bdcaa13143f7ac92`
- identical test assembly: `f45846f6ef9dbf813d7398667b6089f1cc654f2a1101abd93f4303deb1605a06`
- real host assembly: `e4ed67d9d67bf68cc17ac9be75f6e8f48ae51a5817918b2a64f592b20016321f`

## Reproduction

Build the CameraAgent test project with the pinned SDK, then choose a new disposable
root. The four Manual methods are intentionally opt-in and require
`HVO_COLD_START_ROOT`; fixture preparation refuses an existing root.

```bash
export HVO_COLD_START_ROOT="$PWD/TestResults/issue-1043/retained-history"
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release \
  --filter 'FullyQualifiedName~RetainedHistory_PrepareColdStartFixture'
```

Keep a copy of the Release test output for each candidate and record its SHA-256
manifest. For this baseline, the five changed Common production files were saved,
replaced by their exact `git show 499df229:<path>` bytes, built with the pinned SDK,
and restored byte-for-byte in a `finally` block. Only the resulting baseline
Common DLL/PDB replaced those files in the baseline test bundle. The two bundles
otherwise contain identical assemblies, including the final test DLL.

Invoke the test DLL directly in a container to avoid compiling inside the
measurement. Bind the SDK directory at `/sdk`, the chosen test bundle at `/tests`
read-only, and the fixture at `/history`. Use a compatible Linux image with the
same native dependencies as the CameraAgent; the measured Ubuntu 24.04 image was
`hvo-skymonitor-devcontainer:issue-494-validation`. Override its entrypoint and
user explicitly. Set `DOTNET_ROOT=/sdk`, `DOTNET_CLI_HOME=/tmp/dotnet-home`,
`HVO_COLD_START_ROOT=/history`, and a unique `HVO_EVIDENCE_REVISION`.

```text
docker run --cpus=1 --memory=1g --memory-swap=1g --network=none
  --tmpfs=/tmp:rw,size=4g --user=<uid>:<gid>
  <the mounts and environment above> --entrypoint=/sdk/dotnet <image>
  vstest /tests/HVO.SkyMonitor.CameraAgent.Tests.dll
  /TestCaseFilter:FullyQualifiedName~RetainedHistory_ColdStartPreservesEvidenceAndBecomesHealthy
  /ResultsDirectory:/history/results-<revision>
  /Logger:trx;LogFileName=cold-start.trx
```

Use `--memory=8g --memory-swap=8g` for the controlled before/after comparison.
The test emits `cold-start-<revision>.json` only after its preservation assertions
pass. Record the container state and Docker OOM event independently, because an
OOM prevents the test from publishing successful-run evidence. Resolve its host
PID through `docker inspect`, then its cgroup through `/proc/<pid>/cgroup`; sample
`memory.current`, `memory.peak`, `memory.stat`, `memory.events`, `cpu.stat` and
`io.stat` approximately every 200 ms. Retain the test log and TRX. These measurements
used one exclusive host resource slot; no compiler, test lane or other performance
fixture ran concurrently with a measured startup.

For real-host readiness, copy the complete fixture while no process has it open,
point `HVO_COLD_START_ROOT` at that disposable copy, and run
`RetainedHistory_PrepareHostReadinessClone` using the same candidate test bundle.
This acknowledges the synthetic consumer through real claim/completion APIs,
then writes `host-fixture-fingerprint.txt`. It is preparation, not measured startup.
Use a fresh host configuration/state directory and a read-only copy of the
production catalog snapshot, preserving its `current` symbolic link. No installed
state needs to be changed.

Start with the repository's CameraAgent `appsettings.json`, retaining its default
observatory. Configure these supported fixture values:

| Configuration | Fixture value |
| --- | --- |
| `CameraAgent:RawIngressRoot` | `/history` (completed-history clone) |
| `CameraAgent:ConfigFilePath` | `/work/camera.json` |
| `CameraAgent:CentralIntegration:Mode` | `Disabled` |
| `CameraAgent:CaptureDistribution:UploadEnabled` | `false` |
| `CameraAgent:EnvironmentalDelivery:Enabled` | `false` |
| `CameraAgent:ExecutionEvidenceExport:Enabled` | `false` |
| `CameraAgent:Automation:Enabled` | `false` |
| `CameraAgent:ProvisioningStartupGate:Enabled` | `true` |
| `CameraAgent:TimeSync:Enabled` | `false` (networkless fixture) |
| `CameraAgent:DeploymentLocation:LocationId` | `local-deployment` |
| `CameraAgent:DeploymentLocation:Source` | `local-configuration` |
| `CameraAgent:DeploymentLocation:EffectiveFromUtc` | `1970-01-01T00:00:00+00:00` |
| `LocalIdentity:DatabasePath` | `/work/identity.db` |
| `LocalIdentity:AdminPassword` | fresh generated fixture-only password |
| `DeviceProvisioning:StateDirectory` | `/work/provisioning` |
| `Catalog:Root` | `/catalog` |

Copy `virtual-asi178mc.full.json` to `/work/camera.json`, use its provisioning
placeholder `agentId`, and replace `pipeline` with the supported raw-only graph:

```json
{
  "schemaVersion": "cameraagent-capture-pipeline-v2",
  "dependencyPolicy": "reject-enabled-dependent-v1",
  "steps": []
}
```

Mount the same candidate binary bundle at `/app` read-only, fresh configuration
at `/work`, completed history at `/history`, and the catalog copy at `/catalog`
read-only. Set both .NET environment variables to `Production` and
`ASPNETCORE_URLS=http://0.0.0.0:8080`. With the same 1 CPU/1 GiB/no-swap/tmpfs limits,
run `/sdk/dotnet /app/HVO.SkyMonitor.CameraAgent.dll --contentRoot /work`.
Use `docker exec <container> curl -fsS http://127.0.0.1:8080/health` and `/alive`
to avoid publishing a host port. Require Healthy/HTTP 200 for at least 15 seconds,
then stop gracefully and run `RetainedHistory_VerifyHostReadinessClone` against the
same history copy. Also retain `/proc/<pid>/status` (`VmHWM`) and process I/O for the
actual host, alongside the cgroup measurements.

This proves retained-history startup and ownership enumeration with acquisition
paused. It does not qualify live capture throughput, processing backlog drain,
remote delivery, hardware, production upgrades, or a full deployed soak. The
scene-stage adapter verifies every expected owned key; the fixture does not
manufacture external projected-scene stage bundles. Existing stage cleanup and
recovery regression tests cover their file-preservation behavior.

After the host stops, also verify every retained file independently of ingress.
Completed captures no longer require a retention hold, so journal identity alone
would not prove payload availability. This bounded read-only check compares all
sidecar bytes with the immutable journal manifests and hashes every payload:

```python
import hashlib, os, pathlib, sqlite3
root = pathlib.Path(os.environ["HVO_COLD_START_ROOT"]).resolve()
count = 0
with sqlite3.connect((root / "journal/raw-ingress.db").as_uri() + "?mode=ro", uri=True) as db:
    db.execute("PRAGMA cache_size=-2048;")
    for payload, sidecar, manifest, sha, length in db.execute(
        "SELECT payload_relative_path,sidecar_relative_path,manifest_json,"
        "payload_sha256,payload_length FROM raw_captures ORDER BY raw_capture_row_id"
    ):
        assert (root / sidecar).read_bytes() == manifest
        assert (root / payload).stat().st_size == length
        with (root / payload).open("rb") as source:
            assert hashlib.file_digest(source, "sha256").hexdigest().upper() == sha
        count += 1
assert count == 1812
```
