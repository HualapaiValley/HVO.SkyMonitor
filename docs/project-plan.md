# HVO SkyMonitor Virtual-First Completion Plan

Status date: 2026-07-25

This is the authoritative architecture, virtual-first requirements, phase-order,
and completion source for HVO SkyMonitor. The repository-visible
[product roadmap](roadmap.md) owns stable portfolio initiative IDs, planning
horizons, and top-level epic mappings. Live coordination is tracked by the
[Virtual-First Platform Completion milestone](https://github.com/RoySalisbury/HVO.SkyMonitor/milestone/1)
and [epic #89](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/89).
Detailed execution rules and reusable agent prompts are maintained in:

- [Product roadmap](roadmap.md)
- [Agent execution protocol](planning/agent-execution.md)
- [Agent prompts](planning/agent-prompts.md)
- [Performance validation](planning/performance-validation.md)
- [Requirements crosswalk](planning/requirements-crosswalk.md)
- [Standalone CameraAgent course correction](planning/standalone-cameraagent-course-correction.md)
- [Document migration](planning/document-migration.md)

Aggregate phase status belongs here; live issue/PR execution state and evidence
belong in linked GitHub issues. Normative subsystem behavior belongs in the
owning specification. Dated measurements belong in validation or calibration
evidence.

## 1. Objective

Complete a production-like standalone CameraAgent and then the optional
CameraAgent-to-LogicHost software path using VirtualSky before implementing
production physical camera modules.

The target system must:

1. Acquire an immutable virtual raw frame through the ordinary camera contract.
2. Durably accept the raw payload before optional processing.
3. Fan out durable references to independent standard, upload, and secondary
   processing lanes.
4. Execute versioned local recipes for calibration, combination, preview, and
   annotation.
5. Evaluate local schedules, acquire virtual environmental observations, and
   expose the complete local workflow through authenticated CameraAgent UI
   without requiring LogicHost.
6. Prove a full-catalog, calibrated, full-resolution standalone CameraAgent with
   central services absent.
7. Optionally upload a reconstruction-complete raw contract to LogicHost.
8. Reconstruct the raw frame centrally without consulting current device state.
9. Execute the same shared recipes centrally, with optional enhanced central
   configuration and contextual inputs.
10. Execute deterministic weather, cloud, and transient workflows through the
   same durable processing substrate.
11. Recover correctly across CameraAgent, network, LogicHost, SQL, object-store,
   and worker failures.
12. Expose durable operational state through authenticated APIs and secondary
    operator UI.

The final automated flow is:

```text
VirtualSky acquisition
  -> SQLite/file durable raw ingress
  -> standard, upload, and secondary lanes
  -> local schedule, environment, calibration, derivatives, and durable history
  -> authenticated standalone CameraAgent operations and recovery
  -> optional central integration
  -> manifest-v2 upload
  -> LogicHost reconstructable ingest
  -> shared central recipe execution
  -> multi-source and window processing
  -> weather/cloud and virtual transient processing
  -> authenticated retrieval, operations, and review
```

## 2. Explicit Exclusions

The milestone does not require:

- A production physical camera module.
- Camera SDK or native interop in CameraAgent.
- USB/readout characterization.
- Physical lens or sensor calibration.
- Raspberry Pi performance acceptance.
- Hardware-specific metering thresholds.
- A production external weather-provider selection.
- Real-event detector sensitivity or false-positive acceptance.

Hardware-neutral timing, metering, ownership, and adapter-facing contracts are
included. Deterministic pseudo calibration, provisional virtual sensor/lens
profiles, and virtual environmental acquisition are included; physical
implementation and acceptance remain later work.

## 3. Architecture Decisions

### 3.1 Deployment and ownership

| ID | Requirement |
| --- | --- |
| `SYS-001` | One CameraAgent process owns exactly one configured camera; a host may run multiple isolated CameraAgent processes or containers. |
| `SYS-002` | Each CameraAgent owns its configuration, local identity/device state, pipeline, bounded history, and telemetry. |
| `SYS-003` | Camera modules normally run in process behind `ICameraModule`; a separate vendor service requires an explicit adapter decision. |
| `SYS-004` | CameraAgent continues acquisition and bounded retention during LogicHost or network outage. |
| `SYS-005` | One LogicHost accepts many CameraAgents across many observatories without becoming part of acquisition correctness. |
| `SYS-006` | CameraAgent is not the permanent archive; LogicHost is the durable central system of record after verified ingest. |
| `SYS-007` | Explicit standalone mode provides local schedule, environmental history, calibration, processing, storage, recovery, and authenticated operations with zero central network attempts. |
| `SYS-008` | Optional central integration consumes durable edge facts and artifacts; it never determines whether local acquisition, processing, history, or operator control exists. |

CameraAgent owns camera discovery/acquisition, setpoint control, authoritative
local schedule/rig/readout/calibration/pipeline revisions, raw normalization,
local recipes and derivatives, bounded local history, offline authenticated
operation, outbox, telemetry, and a local read-only catalog. LogicHost owns
agent registration and received capture-time profile snapshots, streamed
idempotent ingest, central lifecycle, contextual and cross-agent processing,
durable history/UI, and reprocessing. LogicHost displays but does not edit
CameraAgent-owned revisions. These ownership statements are tracked as
`OWN-EDGE-001` through `OWN-EDGE-010` and `OWN-CENTRAL-001` through
`OWN-CENTRAL-006` in the
[requirements crosswalk](planning/requirements-crosswalk.md).

### 3.2 Project boundaries

| Project | Owns | Must not own |
| --- | --- | --- |
| `HVO.SkyMonitor.AgentCore` | Stable transport-neutral camera, rig, capture timing, frame-layout, artifact, identity, lineage, and version-descriptor contracts | Executable recipes, event assessments, ASP.NET Core, EF Core, MinIO, SQLite, SkiaSharp, camera SDKs, host orchestration |
| `HVO.SkyMonitor.Astronomy` | Catalog contracts, time, coordinates, ephemerides, camera geometry, projection, and visible-scene behavior | Storage, jobs, image filters, host APIs |
| `HVO.SkyMonitor.Imaging` | Pure raw/image layout validation, renderers, calibration primitives, combination, demosaic, encoding, annotation, masks, and image-analysis algorithms | Host orchestration, SQL, MinIO, UI |
| `HVO.SkyMonitor.Fleet.Contracts` | Stable transport-neutral fleet status, health, timing, and acknowledgement contracts | HTTP, host orchestration, persistence, retention, authentication, UI |
| `HVO.SkyMonitor.Processing` | Host-neutral recipe definitions/execution contracts, selectors, transforms, analyzers, gates, windows, environmental/event products, assessments, outcomes, and canonical recipe identity | Host persistence, HTTP, UI, background-service policy |
| `HVO.SkyMonitor.Catalog.Sqlite` | Shared read-only SQLite catalog adapter | Shared SQL schema, mutable catalog state |
| `HVO.SkyMonitor.Common` | Reusable ASP.NET security, identity, API, middleware, and observability infrastructure used by either host | Camera acquisition, recipes/image algorithms, central/edge workflow ownership, or shared domain persistence |
| `HVO.SkyMonitor.CameraAgent.Common` | Edge acquisition orchestration, SQLite WAL journal, durable lanes, local storage, outbox, retention, telemetry, and configuration | LogicHost references or central persistence |
| `HVO.SkyMonitor.CameraAgent.Modules.Zwo` | Linux ZWO ASI SDK interop and full-frame bin-1 color RAW16 acquisition for the ASI676MC and ASI178MC | Host orchestration, processing, persistence, vendor artifacts, unsupported ZWO modes, or non-ZWO cameras |
| `HVO.SkyMonitor.CameraAgent` | Local ASP.NET/Blazor host, local Identity, authenticated local APIs and composition | Central persistence or private processing algorithms |
| `HVO.SkyMonitor.LogicHost` | Central SQL/Redis/MinIO services, durable jobs, workers, fleet state, history, retrieval, and central UI | CameraAgent references or host-private projection/image algorithms |

CameraAgent and LogicHost must never reference each other. Shared behavior moves
through AgentCore, Astronomy, Imaging, Processing, or another explicitly
approved host-neutral project.

The intended production reference graph is below. `A --> B` means project `B`
may reference or consume project `A`; arrows point from dependency to consumer.

```text
AgentCore
  +--> Astronomy
  +--> Imaging (also references Astronomy)
  +--> Processing (may reference Astronomy and Imaging)

Astronomy --> Catalog.Sqlite

AgentCore + Astronomy + Imaging + Processing
  +--> CameraAgent.Common --> CameraAgent
  +--> LogicHost

AgentCore --> CameraAgent.Modules.Zwo --> CameraAgent

Common --------------------> CameraAgent and LogicHost only
Catalog.Sqlite ------------> CameraAgent and LogicHost composition roots
```

`TestSupport` may reference production projects and may be referenced only by
test projects. Phase 0 architecture and publish checks must enforce the complete
graph, including transitive production output.

### 3.3 Acquisition and raw evidence

- Camera modules only acquire frames and report capabilities.
- A capture receives stable identity before optional processing.
- Raw bytes are immutable primary evidence.
- Raw acceptance means durable payload, sidecar, and discoverable journal state,
  not merely an in-memory channel write.
- Optional processing cannot silently drop raw evidence.
- Finite storage cannot guarantee unlimited nonblocking capture. If ingress
  cannot accept another frame, CameraAgent enters an explicit unhealthy state
  and pauses or stops according to policy.

### 3.4 CameraAgent persistence

- Raw and derivative payloads remain files.
- SQLite WAL stores transactional ingress, lane, retry, acknowledgement, and
  quarantine state.
- Payload and sidecar temporary files are published atomically.
- Discoverable SQLite work is committed only after required files exist.
- In-memory channels are bounded wake-up accelerators, never the source of
  durable truth.
- Raw deletion waits for every required consumer acknowledgement and retention
  eligibility.

### 3.5 Processing model

Shared processing supports four operation kinds:

- Transforms produce image or encoded artifacts.
- Analyzers produce structured assessments, geometry, masks, or measurements.
- Gates produce explicit run/skip decisions and reason codes.
- Window processors consume compatible ordered captures.

Every operation returns one of:

- `Produced`
- `Skipped` with a reason
- `RetryableFailure`
- `TerminalFailure`

Recipe identity includes name, semantic version, implementation version,
canonical options, and parameter SHA-256. Changing options without changing
identity is prohibited.

### 3.6 LogicHost processing

- SQL is authoritative for jobs, leases, state transitions, lineage, and
  historical metadata.
- MinIO is authoritative for immutable payload bytes.
- Redis may accelerate notifications or caches but is never authoritative job
  state.
- HTTP ingest records durable work but never performs derivative rendering.
- Workers load, verify, reconstruct, execute, persist, and complete jobs.
- Job output and completion are idempotent and recoverable after crashes.
- Central processing may execute the same local recipe or a separately versioned
  enhanced recipe.

### 3.7 Virtual-first simulation

- VirtualSky remains an ordinary `ICameraModule`.
- Astronomy determines visible sources and geometry.
- Simulation applies optics, environment, sensor response, noise, CFA sampling,
  and quantization before normal processing begins.
- Cloud and transient scenarios pass through ordinary raw capture paths.
- Expected scenario labels remain test-oracle data and are not available to the
  detector under test.

### 3.8 Configuration and artifact contracts

- Module implementation options remain separate from physical/virtual rig
  profiles.
- Rig and pipeline configuration used by a capture are versioned and validated
  before initial camera startup or later revision activation.
- Operator configuration uses stable aliases. Unknown operations, unsupported
  formats, incompatible dependencies, and cycles fail with actionable errors.
- Assembly-qualified operation type names remain an explicit advanced extension
  mechanism; ordinary operator samples and UI use stable aliases.
- Secrets never live in camera rig or processing-profile files.
- Reduced deterministic CI and full-resolution performance profiles are both
  maintained.

### 3.9 Standalone CameraAgent operation

- Local schedule, profile revisions, calibration references, environmental
  observations, processing state, artifacts, and operator actions remain
  durable and queryable while central services are absent.
- Astronomy scene, optics, native sensor, readout mode, response, and
  quantization are separate configuration-selected stages.
- Meaningful sample depth is distinct from container depth. Native ROI, binning,
  output geometry, CFA phase, packing, and transformed optical calibration are
  reconstruction facts rather than preset-name behavior.
- Exposure duration and capture cadence are independent. Fixed and solar
  schedule boundaries use the local deployment location and Observatory-owned
  timezone without requiring a live central lookup.
- Virtual environmental sources publish durable local facts first; optional
  central delivery is an independent consumer.
- Versioned profile changes validate before activation and bind every capture to
  the exact schedule, rig/readout, calibration, and processing revision.
- Detailed requirements and the representative sensor/readout matrix are owned
  by `docs/planning/standalone-cameraagent-course-correction.md`.

Raw, Calibrated, Combined, Preview, AnnotatedPreview, and Metadata are distinct
artifact roles. Raw bytes are immutable. Every artifact records stable artifact
and capture identity, role and variant, media/layout, byte length/checksum,
creation time, ordered source identities, canonical recipe identity, and the
capture-time profile identity. A failed optional operation cannot erase raw or
silently substitute a different role.

### 3.10 Maintainability and structural evolution

Cross-cutting maintainability hypotheses and structural guardrails are tracked by
[#242 Evaluate maintainability and structural complexity before further platform growth](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/242).
That review is non-blocking and does not approve a broad refactor. Static review,
file size, constructor size, and interface count identify candidates only; a
change must show concrete duplication, ownership ambiguity, test friction, or
approved extension pressure before becoming focused implementation work.

Sealed composition remains the default. Interfaces describe real plugins,
infrastructure boundaries, or independently consumed capabilities; abstract
classes and virtual methods require multiple implementations sharing a stable
invariant algorithm. New work keeps transaction, lock, lease, retry, and
compensation ownership explicit, does not grow interfaces through silent no-op
or throwing defaults, and avoids adding unrelated responsibilities to already
broad coordinators when a focused collaborator can own the behavior. Generic
repositories, universal workflow bases, database splits, and additional projects
require a concrete architectural need rather than organizational preference.

### 3.11 Database engineering and shared-server operations

SQL Server and SQLite critical-section, access-plan, schema-ownership, and
shared-server readiness work is coordinated by
[#243 Audit and harden SQL Server and SQLite critical sections and access plans](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/243).
The existing technology split remains authoritative: LogicHost uses EF Core and
named parameterized SQL Server-specific operations, CameraAgent workflow state
uses direct SQLite, local CameraAgent Identity uses isolated EF Core SQLite, and
catalog snapshots remain immutable read-only SQLite.

Before production rollout, current-head evidence must disposition SQL
transactions that span external object I/O, SQLite immediate transactions that
span full-file validation, and unbounded reconciliation inside long database
transactions. Accepted rollout-blocking corrections use durable intermediate
state, short fenced transactions, bounded batches, and explicit crash recovery;
they merge before #151. Database indexes, views, stored procedures, TVPs,
compiled queries, connection-pool limits, RCSI, and retention changes require
the exact production query or transaction plus comparable plan, lock, I/O, and
correctness evidence. Dapper, generic repositories, blanket retries, `NOLOCK`,
database splits, and provider-wide procedure conversion are not default
solutions.

## 4. Performance Is a Design Requirement

Performance-sensitive work is identified by the `performance` GitHub label or a
named operational milestone and must follow these requirements. Ordinary
changes do not acquire a comparative benchmark requirement merely because they
touch an image or host project.

| ID | Requirement |
| --- | --- |
| `PERF-001` | Record reproducible baseline/after measurements using equivalent workloads and environment. For a genuinely new path with no valid before implementation, record the nearest-path or first-simple-correct comparison; otherwise mark before `N/A` with reason and establish an absolute candidate baseline. |
| `PERF-002` | Measure relevant I/O bytes and operations, CPU time, allocations/working set, throughput, latency, queue depth, and oldest-work age. |
| `PERF-003` | Prefer streaming, pooled or ownership-safe buffers, durable references, batched I/O, and indexed queries over full-frame copies and repeated scans. |
| `PERF-004` | Full-resolution payloads must not be duplicated merely to fan out work. |
| `PERF-005` | Optional lane backlog must remain memory-bounded and observable. |
| `PERF-006` | SQLite transactions and indexes must be designed around measured ingest, claim, acknowledgement, and recovery access patterns. |
| `PERF-007` | Central reads and writes stream payloads and verify checksums without buffering entire objects unless the algorithm requires contiguous memory. |
| `PERF-008` | Algorithm complexity, temporary memory, and compatibility-window size are part of recipe documentation. |
| `PERF-009` | Logs, metrics, and traces must use bounded cardinality and avoid payloads, credentials, or unbounded IDs as metric dimensions. |
| `PERF-010` | No universal threshold is invented without evidence, but unexplained regression blocks merge. A more complex implementation is acceptable when evidence shows meaningful bounded benefit. |

Performance results are descriptive until an issue establishes an approved
budget. x64 virtual measurements are required now. ARM64 and physical-camera
measurements remain later acceptance evidence.

Canonical workloads, measurement fields, phase-specific metrics, and the rule
for accepting additional complexity are defined in
[`docs/planning/performance-validation.md`](planning/performance-validation.md).
Every performance-sensitive issue and PR must link its selected workload and
evidence; a generic statement that performance was considered is insufficient.
Tier A/B/C/M selection, evidence invalidation, and milestone benchmark ownership
are defined in the execution protocol and standalone course correction.

Cross-cutting performance hypotheses and their required current-head evaluation
are tracked by
[#241 Evaluate cross-cutting CPU, memory, and I/O performance opportunities](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/241).
That review is a non-blocking suggestion and evidence backlog, not an accepted
architecture or final performance conclusion. Historical measurements and static
code review motivate investigation only; each candidate must be profiled and
remeasured under a named workload before it becomes separate implementation work
or an approved milestone requirement.

Low-overhead production metrics, Linux/.NET profiling escalation, telemetry
cardinality and sampling policy, and diagnostic security are deferred under
[#244 Define low-overhead production profiling and observability operations](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/244).
That follow-up does not block the virtual-first milestone merely by existing.
Routine metrics should identify the pressured stage or resource with bounded
cost; method-level CPU, allocation, lock, heap-root, syscall, and kernel evidence
remains short-lived on-demand collection unless soak evidence justifies a
separately budgeted continuous profiler.

## 5. Requirement Ownership

Detailed behavior remains normative in the owning subsystem specification. The
[requirements crosswalk](planning/requirements-crosswalk.md) maps every retained
requirement group from superseded plans to a phase, issue, and retained source.
In particular:

- [`docs/virtual-camera.md`](virtual-camera.md) owns VirtualSky sensor, optics,
  scene, rendering, annotation, fixture, and conformance behavior.
- [`docs/projects/fireball-transient-detection.md`](projects/fireball-transient-detection.md)
  owns transient lane, detector, event, execution-mode, and reconstruction
  behavior.
- Catalog documents own source, licensing, checksum, and derivation provenance.
- Validation and calibration documents own dated evidence and external or
  deferred hardware procedures, not implementation order.
- Runbooks own only commands and procedures that are executable against the
  current repository topology.

## 6. Implemented Baseline

The plan starts from the following completed foundation:

- Shared Astronomy and Imaging projects with extensive numerical tests.
- Read-only local HYG catalog adapter and versioned catalog evidence.
- VirtualSky Mono16, RGB24, and ASI178 RGGB16 acquisition.
- Fisheye, rectilinear, and telescope projection implementations.
- Shared solar-system, catalog, and constellation behavior.
- Local ordered processing, raw artifact preservation, rolling combination,
  preview, annotation, storage, browse indexes, retention, and outbox v1.
- Multipart checksum-verified LogicHost ingest into MinIO and SQL.
- Normalized central frame and artifact records.
- Bounded central history APIs.
- Durable derivative scheduling, leases, retries, expiry recovery, terminal
  failure, and idempotent completion.
- CameraAgent local bootstrap and LogicHost device/observatory administration.
- Accelerated CameraAgent soak, projection conformance, Testcontainers
  integration, and scheduled/manual Stellarium validation.
- Standalone ASI profile characterization, which is evidence and not a physical
  CameraAgent module.

This initial baseline was not yet a reconstruction-complete or executable
central pipeline.

### Aggregate status at 2026-07-25

- Phases 0-11 and the backend children of phase 12 are merged. Transient epic
  #65 remains open for final UI/two-host closure.
- The standalone foundation through #191, #194-#197, #171, and central location
  reconciliation #170 is merged.
- Phase 12A is the active course correction. Epic #205 and children #206-#211
  are open; #206 is the first implementation dependency.
- Phase 13 is partial: CameraAgent UI #106 is merged, while LogicHost UI #107
  follows standalone gate #211.
- Phase 14 remains open: #166 and #211 precede deployment automation #151;
  #243 database disposition and accepted rollout-blocking corrections also
  precede #151; #151, #107, and #211 precede final two-host gate #108.

## 7. Phase 0 - Planning, Boundaries, and Quality

Issues:

- [#90 Consolidate the virtual-first master plan and retire obsolete plans](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/90)
- [#91 Remove production test-support coupling and enforce architecture boundaries](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/91)
- [#110 Enforce CI categories and protected-main quality gates](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/110)

Requirements:

| ID | Requirement |
| --- | --- |
| `ARCH-001` | Keep this file authoritative for architecture, virtual-first requirements, phase order, and completion; keep portfolio initiative IDs and horizons in `docs/roadmap.md`. |
| `ARCH-002` | Enforce all project-reference directions with executable architecture tests. |
| `ARCH-003` | Remove production references to TestSupport. |
| `ARCH-004` | Remove or explicitly deprecate legacy upload scaffolding. |
| `ARCH-005` | Rename misleading production `NoOp` components or make disabled behavior explicit. |
| `ARCH-006` | Define `HVO.SkyMonitor.Common` as reusable host infrastructure without domain workflow ownership. |
| `ARCH-007` | Enforce the complete direct/transitive production graph, TestSupport prohibition, and publish-output boundary. |
| `QA-001` | Add real Unit, Integration, Manual, Soak, External, and future Hardware test categories. |
| `QA-002` | Build Debug and Release and treat warnings as errors. |
| `QA-003` | Enforce formatting, package audit, migration validation, architecture checks, and coverage non-regression. |
| `QA-004` | Use deterministic focused failing tests for new domain behavior and regressions where feasible; prohibit arbitrary sleeps, live-network unit dependencies, mutable global state, and test-order dependence. |
| `QA-005` | Never lower the current executable coverage gate. #110 captures a checked-in aggregate baseline and exact path-based risk mapping, then enforces aggregate non-regression to 0.01 percentage point plus 95% line/90% branch for high-risk projection/layout/control/durability logic and 90%/85% for renderer/catalog logic. |
| `QA-006` | Keep Debug and Release warning-clean without broad suppressions, disabled analyzers, weakened tests, or hidden package advisories. |
| `QA-007` | Document public shared APIs with units, ranges, ownership/lifetime, failure behavior, and thread safety; cite astronomy constants and algorithms. |
| `QA-008` | Give defects regression coverage and validate data-producing behavior through bytes, geometry, statistics, identity, lineage, and durable convergence. |
| `QA-009` | Categorize Unit, Integration, Manual, Soak, External, and future Hardware tests by actual behavior, not project name. |
| `QA-010` | Keep external, soak, and hardware checks separately selectable and never report an unrun category as passed. |
| `OPS-001` | Distinguish fixture and full catalog deployment. |
| `OPS-002` | Mount persistent CameraAgent payload and journal state in container deployment. |
| `OPS-003` | Keep product, component, UUID instance, and named catalog roots distinct, ownership-bound, and multi-instance safe. |
| `OPS-004` | Install one local VirtualSky CameraAgent from immutable local/offline inputs through a self-contained, resumable, redaction-safe CLI. |

Exit gate `GATE-P00`:

- Documentation has no competing live roadmap.
- Architecture tests encode the complete intended dependency graph.
- Production projects do not reference test support.
- CI filters correspond to real categories.
- Coverage, warning, package, migration, publish, and architecture gates are
  executable and report actionable failure.

## 8. Phase 1 - Reconstructable Contracts

Issue:

- [#92 Add reconstructable capture contracts and manifest v2](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/92)

Umbrella relationship:

- [#60 Complete reprocessable raw artifact identity and central job scheduling](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/60)

Requirements:

| ID | Requirement |
| --- | --- |
| `CTR-001` | Assign a stable capture ID distinct from artifact IDs. |
| `CTR-002` | Assign a restart-safe per-agent capture sequence. |
| `CTR-003` | Record requested start, exposure start/end, readout completion, and durable ingress time. |
| `CTR-004` | Describe dimensions, stride, pixel format, byte order, sample depth, CFA, packing, and known levels. |
| `CTR-005` | Identify artifacts by role, variant, and canonical recipe identity. |
| `CTR-006` | Persist ordered source-artifact lineage. |
| `CTR-007` | Persist capture-time rig, calibration, mask, sensor, and processing-profile identities. |
| `CTR-008` | Define canonical recipe name, semantic version, implementation version, options, and hash. |
| `CTR-009` | Add manifest v2 while preserving v1 parsing as `LegacyIncomplete`. |
| `CTR-010` | Version local sidecars with the same reconstruction descriptor. |

Exit gate `GATE-P01`:

- Mono16, RGB24, and RGGB16 golden fixtures round-trip without semantic loss.
- Central tests reconstruct byte-equivalent frames from descriptor plus bytes.
- Invalid versions, layout, identity, and checksums fail with reason codes.

## 9. Phase 2 - Shared Canonical Processing

Issue:

- [#93 Add shared canonical processing recipes](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/93)

Requirements:

| ID | Requirement |
| --- | --- |
| `PROC-001` | Add host-neutral `HVO.SkyMonitor.Processing`. |
| `PROC-002` | Define transforms, analyzers, gates, windows, selectors, inputs, products, and outcomes. |
| `PROC-003` | Extract preview and annotation orchestration from CameraAgent.Common. |
| `PROC-004` | Move shared encoded-image generation into Imaging. |
| `PROC-005` | Support explicit raw, calibrated, combined, or recipe-result inputs. |
| `PROC-006` | Support multiple same-role variants. |
| `PROC-007` | Bind output provenance to recipe options and implementation identity. |
| `PROC-008` | Keep public shared APIs free of disposable host-specific image objects. |

Initial recipes:

- Raw to calibrated representation.
- Raw or calibrated to encoded preview.
- Raw, calibrated, combined, or preview input to annotation.
- Rolling combination.
- Image-quality assessment.
- No-op analyzer for orchestration tests.

Rolling combination preserves these requirements:

| ID | Requirement |
| --- | --- |
| `COMB-001` | Emit a combined artifact after every capture, including warm-up. |
| `COMB-002` | Use the newest configured compatible frames. |
| `COMB-003` | Compute a linear arithmetic mean. |
| `COMB-004` | Use sufficient accumulator precision for the declared window and sample depth. |
| `COMB-005` | Record ordered source IDs, source count, and total integration time. |
| `COMB-006` | Reset on incompatible dimensions, layout, rig/orientation, calibration, mask, setpoint regime, or processing profile. |
| `COMB-007` | Support configured frame-count, integration-time, and temporal retention requirements without treating them as batch flush triggers. |
| `COMB-008` | Never silently substitute raw when a combined input is requested but unavailable. |
| `COMB-009` | Retain immutable compatible linear source snapshots or ownership-safe references for the active window. |
| `COMB-010` | Keep registration, sigma clipping, dark subtraction, and motion compensation as separate versioned algorithms rather than implicit baseline-mean behavior. |

Exit gate `GATE-P02`:

- CameraAgent and a LogicHost test adapter produce byte-identical output from
  equivalent inputs and recipes.
- Different recipe options create different identities.
- Invalid source formats fail deterministically.

## 10. Phase 3 - Mandatory Durable Raw Ingress

Issues:

- [#94 Add SQLite-backed durable raw ingress](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/94)
- [#59 Add durable raw ingress and independent processing lanes](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/59)

Requirements:

| ID | Requirement |
| --- | --- |
| `EDGE-001` | Add mandatory `IRawCaptureIngress`. |
| `EDGE-002` | Persist immutable payload and reconstructable sidecar before discoverable journal work. |
| `EDGE-003` | Use SQLite WAL for transactional ingress and recovery state. |
| `EDGE-004` | Reconcile temporary files, missing journal work, and conflicting records. |
| `EDGE-005` | Quarantine malformed or ambiguous records. |
| `EDGE-006` | Make raw durability independent of configured processing steps. |
| `EDGE-007` | Enter explicit unhealthy pause/stop behavior when ingress cannot accept another frame. |

Exit gate `GATE-P03`:

- Fault tests at every commit boundary lose no acknowledged capture.
- Restart discovers committed unfinished work.
- Ingress failure cannot be reported as stored success.
- Sustained full-resolution virtual capture remains memory-bounded.

## 11. Phase 4 - Durable Capture Distribution

Issues:

- [#95 Add durable capture distribution and independent lanes](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/95)
- [#59 umbrella](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/59)

Required lanes:

| Lane | Default | Purpose |
| --- | --- | --- |
| Standard | Required | Local calibration, combination, preview, and annotation |
| Upload | Required when central is enabled | Raw and configured artifact export |
| Secondary | Optional | Cloud, quality, transient, or experimental processing |

Requirements:

| ID | Requirement |
| --- | --- |
| `LANE-001` | Fan out durable references, not duplicate full-resolution buffers. |
| `LANE-002` | Track pending work and acknowledgements per lane in SQLite. |
| `LANE-003` | Support required/optional policy, retry, quarantine, and bounded backlog. |
| `LANE-004` | Use in-memory channels only as wake-up accelerators. |
| `LANE-005` | Pin raw until every required lane acknowledges. |
| `LANE-006` | Expose count, bytes, oldest age, lag, failure, and throughput. |
| `LANE-007` | Separate acquisition shutdown from worker drain and recovery. |
| `LANE-008` | Prohibit nested queues inside arbitrary processing operations. |
| `LANE-009` | Preserve one ordered stateful standard lane while independent lanes progress separately. |
| `LANE-010` | Make full-mode, overload, and required/optional pressure policy explicit and observable. |
| `LANE-011` | Keep payload and pooled-buffer ownership explicit across every lease and reload boundary. |

Exit gate `GATE-P04`:

- A blocked optional lane does not block standard work or acquisition while
  durable ingress remains healthy.
- Restart restores all pending lane work.
- Disabled secondary processing allocates no secondary window.

## 12. Phase 5 - Complete Local Standard Processing

Issue:

- [#96 Finish dependency-based CameraAgent processing and persistence](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/96)

Requirements:

| ID | Requirement |
| --- | --- |
| `LOCAL-001` | Replace order-only semantics with declared inputs and dependencies. |
| `LOCAL-002` | Validate the full graph before camera initialization. |
| `LOCAL-003` | Add real shared calibration primitives or mark calibration disabled. |
| `LOCAL-004` | Include layout, rig, orientation, calibration, mask, setpoint, and processing profile in rolling compatibility. |
| `LOCAL-005` | Support frame-count, integration-time, and temporal windows. |
| `LOCAL-006` | Support multiple same-role recipe variants. |
| `LOCAL-007` | Add role- and recipe-specific upload and retention policy. |
| `LOCAL-008` | Treat upload acknowledgement as hold release, not unconditional deletion. |
| `LOCAL-009` | Persist processing outcomes and restart-resumable local history. |

Exit gate `GATE-P05`:

- A configuration-selected raw to calibrated to combined to preview to
  annotation flow works.
- Invalid dependency graphs fail before capture.
- Restart resumes work without duplicate outputs.

## 13. Phase 6 - Cadence, Metering, Heartbeat, and Telemetry

Issues:

- [#58 Add capture cadence modes and host-metered exposure control](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/58)
- [#102 Add durable CameraAgent heartbeat and fleet telemetry](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/102)

Requirements:

| ID | Requirement |
| --- | --- |
| `CTRL-001` | Add explicit `Continuous` and `MinimumStartInterval` modes. |
| `CTRL-002` | Keep VirtualSky fixed and deterministic by default. |
| `CTRL-003` | Base feedback on the active setpoint. |
| `CTRL-004` | Add day, twilight, and night policy. |
| `CTRL-005` | Add exposure/gain preference, hysteresis, and saturation rejection. |
| `CTRL-006` | Add stride-aware sparse ROI/mask and CFA-aware metering. |
| `CTRL-007` | Skip metering when host automatic control is disabled. |
| `CTRL-008` | Persist timing and reason-coded decisions. |
| `FLEET-001` | Send durable heartbeat state with software, capture, CPU, temperature, pipeline, outbox, storage, and profile identity. |
| `FLEET-002` | Persist current and bounded historical fleet state centrally. |
| `FLEET-003` | Distinguish module render, readout, ingress, processing, and upload latency. |

Exit gate `GATE-P06`:

- Deterministic brightness sequences converge without oscillation.
- Optional processing does not affect continuous-mode next-start timing.
- Heartbeat recovers after central outage.
- Telemetry never claims durable state from in-memory inference.

Physical timing measurements are not required to close #58.

## 14. Phase 7 - Operational Manifest-v2 Outbox

Issue:

- [#97 Add operational manifest-v2 outbox and quarantine](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/97)

Requirements:

| ID | Requirement |
| --- | --- |
| `OUTBOX-001` | Emit validated manifest v2 from reconstructable local records. |
| `OUTBOX-002` | Verify equality when an idempotency filename already exists. |
| `OUTBOX-003` | Persist attempt count, next attempt, status, and last error. |
| `OUTBOX-004` | Classify retryable and permanent HTTP outcomes. |
| `OUTBOX-005` | Add quarantine and audited abandonment. |
| `OUTBOX-006` | Validate structured LogicHost acknowledgement before releasing holds. |
| `OUTBOX-007` | Make raw-only upload the default and allow role/recipe export policy. |
| `OUTBOX-008` | Align supported authentication modes with working local defaults. |

Exit gate `GATE-P07`:

- Permanent failures do not retry forever.
- Retry timing survives restart.
- Conflicting idempotency records fail closed.
- Malformed records cannot terminate the drain service.

## 15. Phase 8 - Reconstructable LogicHost Ingest and Retrieval

Issues:

- [#98 Add reconstructable LogicHost ingest and exact rig binding](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/98)
- [#99 Add authorized central artifact retrieval](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/99)
- [#60 umbrella](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/60)

Requirements:

| ID | Requirement |
| --- | --- |
| `CENTRAL-001` | Accept manifest v1 and v2 concurrently. |
| `CENTRAL-002` | Add capture sequence, timing, layout, metadata, profile identity, and reconstruction status through additive migrations. |
| `CENTRAL-003` | Add variant, canonical recipe, and normalized source lineage. |
| `CENTRAL-004` | Bind delayed upload to capture-time rig/profile identity. |
| `CENTRAL-005` | Add internal streamed MinIO reader and writer abstractions. |
| `CENTRAL-006` | Add authorized content and range retrieval with checksum verification. |
| `CENTRAL-007` | Preserve legacy history as explicitly incomplete. |
| `CENTRAL-008` | Never expose MinIO credentials or browser-direct storage references. |

Exit gate `GATE-P08`:

- LogicHost reconstructs every supported raw layout from central records alone.
- Delayed upload resolves the correct historical profile.
- V1/v2 concurrent ingest remains idempotent.
- Retrieval re-verifies length and checksum.

## 16. Phase 9 - Central Derivative Execution

Issue:

- [#100 Execute central derivative jobs with shared recipes](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/100)

Requirements:

| ID | Requirement |
| --- | --- |
| `WORKER-001` | Register a hosted derivative worker. |
| `WORKER-002` | Claim and renew durable leases. |
| `WORKER-003` | Load, verify, and reconstruct recipe inputs. |
| `WORKER-004` | Execute through shared Processing. |
| `WORKER-005` | Persist output, recipe identity, and source lineage idempotently. |
| `WORKER-006` | Recover output written before job completion. |
| `WORKER-007` | Add worker queue, latency, retry, lease, and quarantine telemetry and health. |
| `WORKER-008` | Add audited requeue or supersede operations for terminal work. |

Initial execution includes encoded preview, annotated preview, image quality, and
explicit reprocessing under a new recipe.

Exit gate `GATE-P09`:

- Raw ingest eventually creates central derivatives.
- Local and central outputs match for equivalent recipes.
- Worker crash and lease expiry do not duplicate results.
- Corrupt input quarantines work without altering source evidence.

## 17. Phase 10 - Multi-Source and Windowed Processing

Issue:

- [#101 Add multi-source and windowed central processing](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/101)

Requirements:

| ID | Requirement |
| --- | --- |
| `WINDOW-001` | Add ordered multi-source job rows and dependency rows. |
| `WINDOW-002` | Add waiting, quarantined, canceled, and completed lifecycle states. |
| `WINDOW-003` | Define sequence offsets, required/optional positions, timeout, and compatibility. |
| `WINDOW-004` | Resolve by agent and capture sequence, never ingest order. |
| `WINDOW-005` | Pin every source while active work references it. |
| `WINDOW-006` | Preserve previous output during historical reprocessing. |
| `WINDOW-007` | Prove the model with rolling combination or timelapse before transient detection. |

Exit gate `GATE-P10`:

- `N-2..N+2` resolves under delayed and out-of-order ingest.
- Waiting work becomes runnable when inputs arrive.
- Missing or incompatible windows finish with explicit reason codes.
- Restart preserves waiting and runnable work.

## 18. Phase 11 - Weather and Cloud Processing

Issues:

- [#103 Add environmental observation contracts and persistence](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/103)
- [#157 Add durable environmental observation delivery](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/157)
- [#104 Add deterministic VirtualSky cloud scenarios](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/104)
- [#105 Add shared cloud assessment and masks](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/105)

Requirements:

| ID | Requirement |
| --- | --- |
| `ENV-001` | Represent observation time, validity, provider, units, quality, and staleness. |
| `ENV-002` | Support temperature, pressure, humidity, wind, precipitation, and sky quality when available. |
| `ENV-003` | Persist observations centrally and associate them by time without silent defaults. |
| `ENV-004` | Deliver CameraAgent observations through restart-safe durable edge state without making central availability part of acquisition. |
| `CLOUD-001` | Add seeded clear, scattered, broken, and overcast virtual scenarios. |
| `CLOUD-002` | Apply deterministic opacity and motion before sensor response. |
| `CLOUD-003` | Add versioned global cloud score, optional mask, confidence, and quality output. |
| `CLOUD-004` | Produce identical assessment at edge and central hosts. |
| `CLOUD-005` | Add weather/cloud overlay as a derivative consuming explicit inputs. |

Exit gate `GATE-P11`:

- Fixed environmental scenarios produce stable raw checksums.
- Cloud motion is deterministic across exposure intervals.
- Clear, partial, and overcast fixtures satisfy documented numeric ranges.
- Missing and stale weather remain explicit.
- Cloud results can gate downstream processing through normal recipe outcomes.

## 19. Phase 12 - Software-Complete Virtual Transient Processing

Issues:

- [#61 Add deterministic VirtualSky transient scenarios](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/61)
- [#62 shared-detector umbrella](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/62)
- [#113 Define transient event contracts and linear detector inputs](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/113)
- [#115 Implement transient compatibility, masks, and temporal backgrounds](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/115)
- [#121 Extract and assess transient candidates with structured geometry](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/121)
- [#119 Establish deterministic transient detection and performance baselines](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/119)
- [#63 Add optional CameraAgent transient detection lane](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/63)
- [#64 central transient umbrella](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/64)
- [#116 Execute and persist central transient validation jobs](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/116)
- [#118 Add transient reconstruction, reprocessing, and review state](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/118)
- [#65 Epic: Fireball and transient detection](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/65)

Requirements:

| ID | Requirement |
| --- | --- |
| `EVENT-001` | Simulate meteors, fireballs, fragmentation, boundary crossing, satellites, aircraft, cosmic rays, hot pixels, and environmental artifacts. |
| `EVENT-002` | Integrate sky events before sensor response and sensor artifacts after optics. |
| `EVENT-003` | Keep multi-capture events separate from per-capture artifact sets. |
| `EVENT-004` | Define structured observations, geometry, features, assessments, reason codes, versions, and lineage. |
| `EVENT-005` | Detect against compatible linear raw/calibrated windows, never annotated display images. |
| `EVENT-006` | Treat fireball as meteor severity. |
| `EVENT-007` | Run optional causal edge detection in the durable secondary lane. |
| `EVENT-008` | Persist restart-safe pending candidates and wait for future context explicitly. |
| `EVENT-009` | Run authoritative centered central validation and preserve assessment versions. |
| `EVENT-010` | Persist one/two-frame reconstruction, review state, overrides, and notification state. |

Exit gate `GATE-P12`:

- Expected scenario labels are unavailable to detector code.
- Fixed seeds produce stable raw and event outputs.
- One-frame and multi-frame evidence is reasoned rather than classified by frame
  count alone.
- Edge and central submissions converge on one event identity.
- Restart, retry, and reprocessing preserve every assessment version.

Physical sensitivity and real-world false-positive claims remain unverified.

## 20. Phase 12A - Standalone CameraAgent Operational Completion

Issues:

- [#205 Epic: Complete standalone CameraAgent operational experience](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/205)
- [#206 Make VirtualSky sensor and readout simulation configuration-driven](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/206)
- [#207 Add local CameraAgent scheduling and versioned profile control](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/207)
- [#208 Add CameraAgent calibration library and virtual reference acquisition](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/208)
- [#209 Add virtual environmental acquisition and standalone local history](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/209)
- [#210 Complete CameraAgent pipeline composition and operator controls](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/210)
- [#211 Prove full-catalog ASI676MC standalone CameraAgent operation](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/211)

Requirements:

| ID | Requirement |
| --- | --- |
| `READOUT-001` | Configure native sensor geometry, mono/RGB/CFA response, meaningful sample depth, container/packing, stored-code alignment/transfer, stride, byte order, levels/units, and deterministic response without preset-name branches. |
| `READOUT-002` | Configure native-coordinate ROI, X/Y binning and algorithm, CFA origin/parity, and output geometry; transform calibrated optics and projected coordinates exactly. |
| `READOUT-003` | Support representative 8-, 10-, 12-, 14-, and 16-bit modes and reject impossible, misaligned, or unsupported readout/pipeline combinations before acquisition. |
| `READOUT-004` | Add stored-code transform/level-space to manifest v2 and central layout additively; use canonical defaults only when old facts prove them and classify ambiguous existing lower-depth history as byte-preserved `LegacyIncomplete`. |
| `SCHED-001` | Evaluate local weekly schedules, exceptions, blackouts, and overrides using fixed or solar-relative day/twilight/night boundaries. |
| `SCHED-002` | Keep exposure and cadence independent; persist deterministic precedence, DST/clock/restart behavior, active revision, next transition, and admission reason. |
| `SCHED-003` | Define no-catch-up clock/restart behavior, one-shot replay protection, boundary-crossing exposure completion, and explicit no-solar-event fallback or closed state. |
| `PROFILE-001` | Validate, preview, audit, activate, and roll back versioned local rig/readout/schedule/pipeline configuration at an explicit capture boundary. |
| `CALIB-001` | Persist immutable bias/dark/flat/defect source and master artifacts with checksums, recipes, validity, compatibility, activation, retention, and ordered lineage. |
| `CALIB-002` | Acquire deterministic virtual references compatible with configured ASI response models and prove correction improves declared synthetic residuals without changing raw bytes. |
| `ENV-LOCAL-001` | Acquire deterministic virtual weather/camera observations on source-appropriate triggers and retain bounded targetless local history in standalone mode. |
| `ENV-LOCAL-002` | Preserve measured/imported/derived/simulated provenance and explicit fresh/stale/missing/failed state; optional central delivery consumes local facts independently. |
| `PIPE-LOCAL-001` | Configure and validate the effective calibration, combination, preview, quality/cloud, annotation, overlay, storage, telemetry, and edge-transient graph with explicit enable/disable dependency behavior. |
| `PIPE-LOCAL-002` | Add reconstructable object/cardinal/image-circle and four-corner metadata overlays plus per-node artifact/status/lineage comparison in authenticated local UI. |
| `STANDALONE-001` | Run the full production-catalog ASI676MC 12-in-16 Bayer profile with provisional 2.5 mm fisheye, five-second calibrated lights, virtual environment, edge transient processing, storage, retention, and telemetry. |
| `STANDALONE-002` | Exercise the real CameraAgent UI, durable restart/fault/pressure boundaries, known-sky geometry, calibration residuals, checksums, lineage, runtime signals, and bounded resource/backlog behavior. |
| `STANDALONE-003` | Complete with LogicHost, SQL Server, Redis, and MinIO absent and prove zero central attempts; also run an ASI174 Mono8 ROI/bin profile through the same configuration-driven path. |

Exit gate `GATE-P12A`:

- The production CameraAgent host completes the configured full-resolution
  acquisition-to-local-history flow from a clean standalone environment.
- Operators can schedule, configure, calibrate, inspect, compare, pause/resume,
  and recover the real VirtualSky pipeline through authenticated local UI.
- Known-sky geometry, pixel/layout ranges, calibration improvement, checksums,
  recipe/profile identity, ordered lineage, and durable state pass.
- Restart, optional-step failure, disk pressure, retention, and drain converge
  without acknowledged loss or duplicate logical output.
- The milestone composition records CPU, memory, I/O, latency, throughput,
  backlog, storage growth, rendered outputs, and zero central network attempts.
- Physical sensor, lens, calibration, timing, and detector-sensitivity claims
  remain explicitly provisional or deferred.

## 21. Phase 13 - Secondary Operator UI

Issues:

- [#106 Add CameraAgent operations and gallery UI](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/106)
- [#107 Add LogicHost imaging, jobs, weather, and event UI](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/107)

Requirements:

| ID | Requirement |
| --- | --- |
| `UI-001` | UI reads durable backend state and never orchestrates processing directly. |
| `UI-002` | Authenticate operational pages and artifact content APIs. |
| `UI-003` | Add CameraAgent ingress, lane, pipeline, outbox, storage, retention, health, weather, and gallery views. |
| `UI-004` | CameraAgent owns authenticated versioned configuration preview, validation, apply, rollback, and audit; LogicHost exposes only received read-only capture-time snapshots. |
| `UI-005` | Replace the LogicHost placeholder with fleet, ingest, queue, and dependency health. |
| `UI-006` | Add central frames, artifacts, provenance, jobs, retrieval, and reprocessing views. |
| `UI-007` | Add weather/cloud timeline and event review/reconstruction views. |
| `UI-008` | Audit every mutating operation and never expose worker lease tokens. |

Exit gate `GATE-P13`:

- bUnit covers loading, empty, stale, failed, and unauthorized states.
- Browser automation covers local gallery through central gallery.
- UI remains secondary to automated backend completion.

## 22. Phase 14 - End-to-End and Production Readiness

Issues:

- [#108 Add full VirtualSky two-host E2E and fault matrix](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/108)
- [#109 production-readiness umbrella](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/109)
- [#110 Enforce CI categories and protected-main quality gates](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/110)
- [#111 Package and verify production astronomy catalog snapshots](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/111)
- [#114 Persist and recover CameraAgent and shared-service state](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/114)
- [#120 Replace stale identity and security operations guidance](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/120)
- [#243 Audit and harden SQL Server and SQLite critical sections and access plans](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/243)

Requirements:

| ID | Requirement |
| --- | --- |
| `E2E-001` | Exercise the real CameraAgent outbox drain against an in-process/test LogicHost and SQL/Redis/MinIO dependencies. |
| `E2E-002` | Cover raw ingress, local derivatives, outage, restart, upload, central reconstruction, central execution, retrieval, windows, clouds, and transients. |
| `E2E-003` | Inject crashes at every durable boundary. |
| `E2E-004` | Verify outputs through checksums, numeric invariants, provenance, and expected durable state. |
| `E2E-005` | Review logs, metrics, traces, queue age, failures, and bounded cardinality. |
| `E2E-006` | Keep Stellarium, soak, external, and future hardware gates separate from normal CI. |
| `E2E-007` | Capture current-head SQL Server and SQLite plans, lock/transaction duration, I/O, WAL, shared-instance attribution, and accepted critical-section corrections before production rollout evidence. |

Production-readiness children may begin after their own prerequisites; #109
closes only after those children and #108 are complete. It is not a Phase 0
prerequisite.

Required fault cases:

- Crash during raw payload write.
- Crash after payload publication but before journal commit.
- Crash after journal commit but before wake-up.
- LogicHost/network outage.
- MinIO failure.
- SQL failure.
- Worker crash before output persistence.
- Worker crash after output persistence but before completion.
- Out-of-order upload.
- Permanent upload rejection.
- Disk pressure.
- Optional-lane backlog.

Final automated acceptance:

```text
VirtualSky capture
  -> mandatory durable raw ingress
  -> standard/upload/secondary lanes
  -> local derivatives and persistent history
  -> CameraAgent restart and outbox recovery
  -> manifest-v2 LogicHost ingest
  -> exact raw reconstruction
  -> central preview and annotation
  -> authenticated retrieval
  -> out-of-order multi-frame window
  -> deterministic weather/cloud
  -> deterministic virtual transient
  -> durable API/UI read models
```

Every durable boundary must be exercised by a restart or fault test.

Exit gate `GATE-P14`:

- The complete automated flow and fault matrix pass from a clean environment.
- Finite outage backlogs drain faster than the configured arrival rate.
- Catalog and persisted application/service state pass documented install,
  upgrade, backup, restore, and reconciliation drills.
- Logs, metrics, traces, health, coverage, output evidence, and performance have
  no unexplained failure or regression.

The virtual-first milestone closure is historical. Post-milestone issue #305 and
its campaign-definition child #318 organize deferred exhaustive evidence and
measured optimization; they do not reopen or claim `GATE-P14`. The bounded
definition and current disposition are recorded in
`docs/planning/phase14-evidence-campaign.md`.

## 23. Dependency Order

```text
#90 --> #91 --> #110 CI quality gates
             +--> #92 reconstructable contracts
             +--> #111 production catalog

#92 --> #93 shared recipes
     +--> #94 durable ingress --> #95 lanes --> #58 cadence/control
                                      +-------> #96 local pipeline (also #93)
                                      +-------> #97 outbox v2

#97 --> #98 central ingest --> #99 retrieval --> #100 central worker (also #93)
                                                   +--> #101 windows

#95 + #98 --> #102 heartbeat/fleet
#98 --> #103 environment --> #104 cloud (also #93) --> #105 cloud assessment
#92 --> #61 transient scenarios
#93 --> #113 transient contracts/inputs
#101 + #105 + #113 --> #115 compatibility/backgrounds
#61 + #115 --> #121 extraction/assessment --> #119 baselines (under #62)
#62 + #58 + #95 --> #63 edge transient runtime
#62 + #99 + #100 + #101 --> #116 --> #118 (under #64)

#58 + #96 + #97 + #102 + #105 --> #106 CameraAgent UI
#103 + #157 + #206 --> #209
#206 --> #207 + #208
#207 + #208 + #209 --> #210 --> #211 standalone CameraAgent gate
#211 + #98-#105 + completed #64 children --> #107 LogicHost UI

#94 + #97 + #98 --> #114 persistent state
#91 --> #120 operations guidance
#166 + #211 + #243 disposition/accepted rollout blockers --> #151 split-host deployment
#211 --> close #205 standalone CameraAgent epic
#151 + #107 + all required backend/UI/readiness children --> #108 E2E
#108 --> close #65, #109, and #89
```

Parallel work is allowed only when contracts and migration order are stable.
Independent issues use separate branches and isolated worktrees, with a default
limit of two active implementation issues plus non-editing research/review
agents. One roadmap coordinator records claims in epic #89 and owns global slot
accounting. Agents must not implement a downstream issue against an unmerged
speculative contract unless the issues explicitly coordinate one PR series.

## 24. PR and Validation Gate

Every issue follows this sequence:

1. Read the master plan, issue, execution protocol, and relevant specifications.
2. Inspect the current branch, worktree, open PR state, and recent commits.
3. Record baseline behavior and performance where relevant to the selected
   validation tier.
4. Implement the smallest coherent issue slice.
5. Add focused, integration, migration, fault, and UI tests as applicable.
6. Validate outputs numerically or by checksum where behavior produces data.
7. Use the execution protocol's validation ladder: focused inner-loop tests,
   tier-appropriate stable-candidate local evidence, affected correction gates,
   and complete protected current-head replacement CI. Tier C/M work runs the
   complete local candidate gate. Rerun performance only when its measured code
   path, configuration, fixture, workload, environment, or measurement logic
   changes.
8. Inspect logs, metrics, traces, health, and durable state.
9. Commit only issue files and preserve unrelated worktree changes.
10. Push and open a PR linked to the issue and epic.
11. Wait for automatic CI and review.
12. Correct every actionable finding.
13. Push a new correction commit and wait for replacement CI.
14. Reply to and resolve review threads only after correction evidence exists.
15. Merge only when the current head has green required checks and no unresolved
    actionable review.
16. Synchronize local `main`, confirm issue closure, and update epic/handoff state.
17. Unless explicitly paused or blocked, select the highest-priority
    candidate-ready issue, post its plain-language synopsis and `READY` claim,
    and begin automatically.

Any red, canceled, timed-out, flaky, or missing required check blocks merge until
it is understood and corrected. A stale green run from before a correction does
not satisfy the gate.

## 25. Definition of Virtual-First Completion

The milestone is complete when:

- `DONE-001`: Every issue in epic #89 is closed or explicitly moved to a later named
  milestone with a recorded decision.
- `DONE-002`: The standalone CameraAgent operational gate and final automated
  two-host acceptance flow pass from clean environments.
- `DONE-003`: CameraAgent schedule, versioned profiles, configurable sensor/readout,
  raw evidence, calibration, environment, lanes, local processing, storage,
  recovery, telemetry, and authenticated operations are durable and bounded
  without LogicHost.
- `DONE-004`: LogicHost reconstructs raw evidence, executes shared recipes, handles windows,
  supports reprocessing, and exposes authenticated history and operations.
- `DONE-005`: Weather/cloud and transient scenarios run through ordinary raw
  paths and local environmental observations remain available in standalone mode.
- `DONE-006`: The software-complete virtual transient path persists and reviews versioned
  edge and central assessments.
- `DONE-007`: Output checksums, numerical invariants, migrations, logs, telemetry, health,
  coverage, and performance evidence are reviewed and green.
- `DONE-008`: No physical camera module or hardware acceptance is required.

Issues #305 and #318 retain deferred exhaustive evidence after milestone closure.
They do not retroactively reopen or claim `DONE-007`; any discovered correctness
defect returns through a separate functional issue.

At that point the architecture is ready for a separately planned physical-camera
adapter without redesigning acquisition ownership, local processing, central
reprocessing, secondary lanes, or operator experience.
