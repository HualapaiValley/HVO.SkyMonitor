# Issue #1643 retained astrometry evidence

Implementation base: `839e75d4c761f8e467bfdb1e22803f8e7b2f8c57`, target
`development/v1`. Protected profile: **Development v1 / Preflight** and
**Development v1 / Build and Unit**. The parent owns classifier selection,
the complete Tier C gate, independent deep review, protected CI and PR lifecycle.
This implementation ledger does not claim those pending gates or production
qualification. No defaults, installed rig, shared infrastructure or EF schema
were changed. [Adoption/API/#1644 handoff](../runbooks/retained-astrometry.md).

The generated #1642 software reference supplies actual 512 x 512 Mono16 pixels,
560 seeded catalog rows, a one-second exposure and a 0.04-degree rendered roll
offset. The durable fixture binds a fresh raw descriptor, runs the real scene,
measurement and astrometry recipes, then restores and reads their immutable
products after reopening the SQLite/file stores. Accepted, rejected,
unavailable and execution-budget results are retained. Five payload-tampering
cases (raw, scene, measured associations, context, solution) withhold the read.
Frozen replay reuses the original result despite different current catalog
completeness and a near-zero current budget. Structured manifests and payloads
independently reconstruct the identical accepted solution bytes and artifact ID.
Interrupted publication tests fail after files are published and before node
commit, then forbid further recipe execution during retry.

The central input-reader tests use the existing object-reader/lease boundary,
ingested immutable structured descriptors and no central-producer evidence row.
Runner tests serialize a real claim, preserve measured producer descriptors,
reconstruct its request and obtain identical accepted solution/output identity.
The actual ingest payload dispatcher accepts both native media types, validates
the canonical/native evidence and exact raw/capture/producer/ordered-source
anchors, and rejects altered descriptor, source and payload bytes in eight
focused cases. Remaining cross-artifact semantic validation occurs when the
frozen request is reconstructed. Producer descriptor, context, raw descriptor, scene and time tampering fail
closed. The shared #1642 integrity/prior selection remains the numerical owner.

## Resource comparison

[Machine-readable evidence](issue-1643-retained-resources.json) records SDK
10.0.402, .NET 10.0.13, Linux x64, 12 logical processors, Release, concurrency one.
The baseline executes the same admitted raw, scene and measured-association
pipeline without astrometry. The enabled arm adds the real analyzer and durable
context/solution publication. Both use fresh local SQLite/file roots. Fixture
generation and admission happen before timing; there is one reported cold
operation followed by five measured warm operations per arm. Sample p95 below
is the largest of five observations. CPU/allocation/I/O counters are process-wide
phase deltas; working set is sampled RSS, not managed live heap or isolated peak.

| Phase | Baseline median / sample p95 ms | Enabled median / sample p95 ms | Baseline / enabled CPU median ms | Baseline / enabled allocated bytes |
| --- | ---: | ---: | ---: | ---: |
| Producer plus persistence | 184.82 / 192.92 | 1510.77 / 1570.50 | 209.97 / 1585.10 | 27,961,448 / 1,305,640,048 |
| Authenticated read | 0.198 / 0.238 | 27.45 / 37.46 | 0.444 / 33.25 | 53,472 / 22,168,016 |

Median sampled producer RSS was 256,798,720 bytes baseline and 339,570,688 bytes
enabled; maximum enabled sampled producer RSS was 355,745,792 bytes.
The process peak counter reached 377,585,664 bytes across the complete run. Median
producer RSS deltas were 2,367,488 and 30,138,368 bytes respectively. Median
logical reads/writes were 1,691,907/811,578 bytes baseline and
2,007,841/1,050,517 bytes enabled; median storage writes were 831,488 and
1,085,440 bytes. Enabled read added 2,496,139 logical read bytes, no writes and
zero physical read bytes at the median (warm filesystem cache). These are real
process I/O counters, not estimated payload sizes. Durable root size was
2,067,940 bytes baseline and 2,306,879 bytes enabled.

The declared 5,000 ms producer and 500 ms read comparison ceilings are met.
The **200 MiB producer-allocation comparison ceiling is exceeded**; the read's
100 MiB allocation ceiling is met. This is explained by the inherited cold
solver's approximately 1.21 GB/op allocation measured in
[#1642](issue-1642-measured-astrometry.md#resource-comparison), plus actual
measurement, strict frozen-input validation and durable publication here. No
solver-allocation improvement is claimed and the original comparison ceiling is
preserved in the report. Admission bounds one numerical execution per host and
at least 60 capture sequences between live solves. At five-second acquisition,
the enabled median CPU corresponds to about 0.53% of one core averaged over
five minutes; this does not measure interference with concurrent acquisition.

Hard structural/cooperative bounds remain: 2,500 catalog rows, 2 MiB context,
128 KiB solution, 16M raw samples, a 20-second default/60-second maximum numerical
budget, and the native solver's finite work bounds. The read admits one request
without queuing raw buffers, at most five artifacts, 32 MiB raw and 44 MiB total.
Allocation is measured rather than hard-preempted by those bounds. These results
do not qualify W1/W2/W6, ARM/Pi, physical data, sustained acquisition interference,
or a production catalog. Those limitations and the high cold allocation remain
visible to adoption/review.

The compact accepted solution is 18,306 bytes with semantic SHA-256
`959067FF97697681F5118ED6543F39C40EEACEC4CA43999D083A75316B6B80A9`.
Catalog rows, scene and complete calibration stay in referenced inputs. Inliers,
native residuals and explicit covariance/clock withhold reasons remain the shared
recipe's evidence, with no fitted uncertainty invented from configured geometry.

## Reproduction and local evidence

Scratch roots created with `hvo scratch HVO.SkyMonitor 1643 implementation` and
`hvo scratch HVO.SkyMonitor 1643 resources` retain logs, TRX, the resource JSON and
the twelve durable resource trees. This run used
`/tmp/hvo-skymonitor-1643-implementation.zdv0vO` and
`/tmp/hvo-skymonitor-1643-resources.AZjLkx`. Every heavy build/test was serialized
with `flock -o /tmp/hvo-heavy-gate.lock timeout ...`; builds use
`-nodeReuse:false -p:UseSharedCompilation=false -warnaserror`. Tests unset
`HVO_PRODUCTION_CATALOG_BUNDLE` and use
`DOCKER_HOST=unix:///tmp/hvo-no-docker.sock`.

Build the CameraAgent, LogicHost and ProcessingRunner test project roots in
Release. Focused selectors are:

- CameraAgent: `FullyQualifiedName~RetainedAstrometry&TestCategory!=Manual`.
- LogicHost: `FullyQualifiedName~CentralDerivativeJobInputReaderTests` and
  `FullyQualifiedName~AstrometryIngestValidationTests`.
- ProcessingRunner: `TestCategory=Unit`.
- Processing: `FullyQualifiedName~MeasuredAstrometry&TestCategory=Unit`.
- Resource method: `FullyQualifiedName~RetainedAstrometry_ResourceEvidence`,
  with `HVO_ISSUE1643_EVIDENCE_ROOT` set to a fresh `hvo scratch` directory.

Per-project inventories add 6 CameraAgent Unit, 11 CameraAgent Integration,
1 CameraAgent Manual, 11 LogicHost Unit and 6 ProcessingRunner Unit cases.
Repository totals become Unit=14272, Integration=984, Manual=288; the parent
candidate gate performs complete discovery. Corrections made during the inner
loop and their failed preliminary logs remain in scratch; final gate outcomes
are reported with the implementation handoff.

Final affected evidence: 17 new capture/graph cases pass (including both
interrupted commits); the wider CameraAgent selection passed 66 cases and its
one stale alias-count assertion passed separately after correction. The real
Kestrel authorization check is included. Shared measured-astrometry Unit passed
55/55; central input reader passed 13/13 and ingest validation 8/8; runner Unit
passed 35/35, with the six affected runner cases rechecked on the final shared
implementation (6/6). The resource method passed 1/1. Affected Release project
builds passed with zero warnings/errors, formatting completed successfully, and
the operations documentation audit passed. The parent still owns exhaustive
category discovery and the complete candidate gate.
