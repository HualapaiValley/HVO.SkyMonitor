# Requirements Crosswalk

This crosswalk maps normative sources to the authoritative virtual-first phases,
GitHub issues, and retained specifications. It prevents planning consolidation
from dropping detailed behavior and prevents subsystem documents from becoming
competing status authorities.

`docs/roadmap.md` owns portfolio initiative IDs, horizons, and top-level epic
mappings. `docs/project-plan.md` owns virtual-first scope, architecture, phase
order, aggregate phase status, and completion. The retained source in this table
owns detailed subsystem behavior. GitHub issues own live issue/PR execution
state and evidence.

## 1. Cross-Cutting Requirements

| IDs | Requirement group | Owning source | Phase/issues |
| --- | --- | --- | --- |
| `SYS-001`-`SYS-008` | One camera per CameraAgent process; isolated multi-agent hosts; per-agent configuration, identity, pipeline, history, and telemetry; in-process modules by default; complete standalone operation; optional LogicHost for many agents/sites | `docs/project-plan.md` sections 1-3 | All; epics #89 and #205 |
| `OWN-EDGE-001`-`OWN-EDGE-010` | Acquisition, authoritative local profile revisions, setpoint control, raw normalization, local recipes, derivatives, bounded history, offline authenticated operation, outbox, telemetry, and local catalog ownership | `docs/project-plan.md` section 3 | Phases 1-7, 12A, and 13 |
| `OWN-CENTRAL-001`-`OWN-CENTRAL-006` | Registration/received profile snapshots, idempotent ingest, central lifecycle, contextual processing, history/UI, and reprocessing without editing edge profiles | `docs/project-plan.md` section 3 | Phases 8-14 |
| `ARCH-001`-`ARCH-007` | Scoped roadmap/architecture authority, complete dependency graph, no production TestSupport, no host cross-reference, and explicit AgentCore/Astronomy/Imaging/Processing/Common/catalog/host ownership | `docs/roadmap.md`; `docs/project-plan.md` section 3 and phase 0 | #90, #91 |
| `DOC-001`-`DOC-007` | Roadmap/specification/evidence/runbook authority, safe retirement, provenance retention, and link validation | `docs/planning/document-migration.md` | #90 |
| `EXEC-001`-`EXEC-014` | Issue readiness and plain-language synopsis, dependencies, compatibility, parallel coordination, validation economy, performance, runtime review, local candidate gate, PR correction cycle, non-green recovery, continuous next-issue execution, and resumable handoff | `docs/planning/agent-execution.md` | Every milestone issue |
| `PERF-001`-`PERF-010` | Reproducible baseline/after evidence, bounded memory/backlog, streaming, references, indexed access, algorithm complexity, cardinality, and regression disposition | `docs/project-plan.md` section 4 and `docs/planning/performance-validation.md` | Every `performance` issue |
| `QA-001`-`QA-010` | Real test categories, warning-clean Debug/Release, formatting/package/migration/architecture/publish gates, deterministic tests, coverage, public API documentation, regression/output checks, and honest external/hardware selection | `docs/project-plan.md` phase 0 and `docs/planning/agent-execution.md` | #91, #110 |
| `CFG-001`-`CFG-006` | Separate module/rig options, version rig and pipeline configuration, validate before initialization, stable aliases, actionable incompatibility errors, and no secrets in rig/pipeline files | `docs/project-plan.md` section 3.8; `docs/virtual-camera.md` | #92, #93, #96 |
| `IDENT-001`-`IDENT-010` | Offline local Identity, separate central registration, verification/envelope flow, per-device scoped credentials, secure import, heartbeat/rotation/revocation, least privilege, and short expiry | `docs/identity/overview.md` | #102, #106, #107, #120 |
| `SEC-001`-`SEC-008` | Layered configuration, no committed secrets, scoped credentials, provider-neutral secure production storage, key overlap, persistent data protection, TLS, and leakage review | `docs/security/secrets.md` | Cross-cutting; #120 |
| `OPS-001`-`OPS-004` | Fixture/full catalog distinction, persistent CameraAgent state, product/component/UUID instance and named catalog roots, and self-contained installation from signed immutable online, mirrored, cached, or offline inputs with resumable redacted evidence | `docs/project-plan.md`; `docs/runbooks/*.md`; `docs/runbooks/deployment-installer.md`; `docs/runbooks/release-distribution.md` | #111, #114, #120, #243, #108, #414, #415, #417 |
| `OPS-005`-`OPS-010` | SQL/Redis/MinIO/Mailpit ownership, names/prefixes/buckets, safe migration/reset, backup/restore, and reconciliation | `docs/project-plan.md`; `docs/runbooks/*.md` | #111, #114, #120, #243, #108 |

## 2. Astronomy, VirtualSky, and Catalog Requirements

| IDs | Requirement group | Owning source | Phase/issues |
| --- | --- | --- | --- |
| `ASTRO-001`-`ASTRO-008` | Deterministic, thread-safe, ambient-state-free projection with explicit units, normal visibility results, no duplicated host math, cited catalog-frame/precession model, and fixture records of every enabled/omitted effect | `docs/virtual-camera.md` sections Optical Profiles, Astronomy Model, and Validation Strategy | #91, #92, #93 |
| `PROV-001`-`PROV-006` | Projection/algorithm, rig/hash, catalog/checksum, ephemeris, refraction, and recipe identity on every derivative | `docs/virtual-camera.md`; catalog provenance documents | #92, #93, #96, #100 |
| `ART-001`-`ART-003` | Raw/calibrated/combined/preview/annotated/metadata roles, immutable raw, complete artifact identity/layout/checksum/time/ordered-sources/recipe/profile | `docs/project-plan.md` section 3.8; manifest compatibility | #92 |
| `VSKY-001`-`VSKY-008` | Ordinary `ICameraModule`, separated astronomy/optics/sensor stages, configuration selection, named profile semantics, and no preset-name branches | `docs/virtual-camera.md` sections Purpose through Virtual Camera Family | #92, #93, #104, #61 |
| `SENSOR-001`-`SENSOR-022` | Linear mono/RGB/CFA behavior, electron-domain response, explicit sourced/simulated parameters, raw preservation, demosaic derivative, and display transfer | `docs/virtual-camera.md` section Sensor Pipeline | #92, #93 |
| `OPTIC-001`-`OPTIC-018` | Four fisheye mappings, rectilinear/telescope projection, principal point/circle/masks/intrinsics/distortion/orientation, calibrated precedence, and no equidistant assumption | `docs/virtual-camera.md` section Optical Profiles | #92, #93 |
| `MATRIX-001`-`MATRIX-012` | Representative Mono8, Mono10-in-16, unpacked 12/14/16-bit, RGB24, and CFA sensor/readout/optics combinations plus reduced CI and milestone full-resolution evidence | `docs/virtual-camera.md` section Required Compatibility Matrix | #92, #93, #206, #211 |
| `FIXTURE-001`-`FIXTURE-016` | Complete deterministic fixture manifest fields and byte checksum | `docs/virtual-camera.md` section Deterministic Fixture Manifest | #92, #93, #108 |
| `FIXTURE-HVO-001`-`FIXTURE-HVO-010` | Hualapai observer, fixed UTC cases, ASI174 geometry, synthetic 180-degree equidistant profile, sourced coordinates, and orientation cases | `docs/virtual-camera.md` section Canonical Hualapai Fixture; `tests/fixtures/astronomy/hualapai-asi174-conformance-v1.json` | #92, #93, #108 |
| `SCENE-001`-`SCENE-003` | Immutable scene request, complete projected-object result, and one geometry authority for render and annotation | `docs/virtual-camera.md` section Visible Scene Contract | #93 |
| `SCENE-LAYER-001`-`SCENE-LAYER-008` | Versioned projected scenes, truthful scene kinds, explicit coordinate transforms, canonical ordering and identity, structured presentation layers, deterministic composition identity, payload limits, and legacy flattened-product compatibility | `docs/virtual-camera.md`; `docs/roadmap.md` `RM-004`; epic #436 and its child acceptance criteria | #431, #435, #433, #434, #437, #432 |
| `RENDER-001`-`RENDER-008` | Deterministic background, magnitude/flux, bounded PSF and edge energy, vignetting, exposure/gain, seeded noise/defects, and little-endian quantization | `docs/virtual-camera.md` section Rendering Invariants | #93 |
| `ANNO-001`-`ANNO-010` | Derivative-only bounded annotation, explicit transforms, centroid agreement, stable HIP topology, great-circle clipping, physical-detection disclaimer, endpoint option, deterministic style, and domain-safe clipping | `docs/virtual-camera.md` section Annotation Invariants; D3 provenance | #93, #96, #100 |
| `CAT-001`-`CAT-014` | Offline build/install, source/license/hashes, pinned serializer, atomic validation, fixture/full distinction, Sol exclusion, stable HIP IDs/order, conservative storage cap, exact Astronomy visibility, no runtime download, default magnitude 6.5/result limit 2000, and post-visibility limiting regression | `docs/catalog/hyg-v42.md`; `docs/catalog/d3-celestial-constellations.md` | #111, #108 |
| `VAL-STEL-001`-`VAL-STEL-012` | Pinned networkless Stellarium environment, coordinate normalization/tolerances, machine reports, retained evidence, and no image-wide golden equality | `docs/validation/stellarium.md` | #108 external gate |

## 3. Edge and Local Processing Requirements

| IDs | Requirement group | Owning source | Phase/issues |
| --- | --- | --- | --- |
| `CTR-001`-`CTR-010` | Capture identity/sequence/timing, reconstructable layout, artifact variant/recipe, ordered lineage, capture-time profiles, manifest v2, and sidecars | `docs/project-plan.md` phase 1 | #92 |
| `PROC-001`-`PROC-008` | Host-neutral recipes, operation kinds/outcomes, canonical identity, shared encoding, explicit inputs, same-role variants, provenance, and infrastructure-free APIs | `docs/project-plan.md` phase 2 | #93 |
| `COMB-001`-`COMB-010` | Warm-up output, newest immutable compatible linear sources, arithmetic mean/precision, ordered source/count/integration lineage, reset, no fallback, and separate advanced algorithms | `docs/project-plan.md` phase 2; rolling-combination tests | #93, #96 |
| `EDGE-001`-`EDGE-007` | Mandatory SQLite/file durable ingress, publication order, recovery/reconciliation, quarantine, processing independence, and explicit refusal state | `docs/project-plan.md` phase 3 | #94 |
| `LANE-001`-`LANE-011` | Reference fan-out, durable per-lane state, policy/retry/quarantine, wake-up-only channels, retention pins, telemetry, shutdown, no nested queues, ordered stateful lane, observable pressure, and buffer ownership | `docs/project-plan.md` phase 4; `docs/projects/fireball-transient-detection.md` | #95; umbrella #59 |
| `LOCAL-001`-`LOCAL-009` | Dependency graph, startup validation, calibration semantics, rolling compatibility/windows, variants, upload/retention policy, hold release, and restart history | `docs/project-plan.md` phase 5 | #96 |
| `CTRL-001`-`CTRL-008` | Cadence modes, deterministic default, active setpoint, day/twilight/night policy, bounded feedback, sparse CFA-aware metering, no disabled scan, and reason-coded timing | `docs/project-plan.md` phase 6; transient specification | #58 |
| `READOUT-001`-`READOUT-004` | Configuration-driven meaningful/container depth and stored-code transfer, mono/RGB/CFA response, native ROI/binning, output layout, transformed optics, additive durable compatibility, capability validation, and representative conformance | `docs/virtual-camera.md`; `docs/planning/standalone-cameraagent-course-correction.md` | #206 |
| `SCHED-001`-`SCHED-003` | Local fixed/solar weekly schedules, exceptions/overrides, independent exposure/cadence, precedence, DST/clock/restart/no-event behavior, and reason-coded admission | `docs/planning/standalone-cameraagent-course-correction.md` | #207 |
| `PROFILE-001` | Local validate/preview/audit/apply/rollback lifecycle and exact capture-time revision binding | `docs/planning/standalone-cameraagent-course-correction.md` | #207 |
| `CALIB-001`-`CALIB-002` | Additive immutable compatible local reference library, virtual acquisition/master generation, fail-closed selection, correction residuals, migration/reconciliation, and ordered lineage | `docs/contracts/reference-calibration-v1.md`; standalone course correction | #208 |
| `ENV-LOCAL-001`-`ENV-LOCAL-002` | Source-scheduled virtual environmental acquisition, durable targetless local history, provenance/freshness/failure, temporal association, and optional delivery | `docs/contracts/environmental-observation-v1.md`; standalone course correction | #103, #157, #209 |
| `PIPE-LOCAL-001`-`PIPE-LOCAL-002` | Explicit effective graph/toggle behavior, complete local adapters, reconstructable annotations, edge transient visibility, artifact comparison, and operator controls | `docs/planning/standalone-cameraagent-course-correction.md` | #210 |
| `FLEET-001`-`FLEET-003` | Durable heartbeat, bounded central history, and truthful segmented timing | `docs/project-plan.md` phase 6 | #102 |
| `OUTBOX-001`-`OUTBOX-008` | Manifest v2, equality on idempotency collision, durable attempts, HTTP classification, quarantine/abandonment, structured acknowledgement, raw-only default, and working auth | `docs/project-plan.md` phase 7 | #97 |
| `COMPAT-EDGE-V1-001`-`COMPAT-EDGE-V1-006` | Preserve current JSONL/outbox history, pending-upload holds, fail-closed retention, torn-line handling, ordering/restart discovery, and idempotent upload during migration | `docs/runbooks/cameraagent-retention.md` | #94-#97 |

## 4. Central, Environmental, UI, and E2E Requirements

| IDs | Requirement group | Owning source | Phase/issues |
| --- | --- | --- | --- |
| `CENTRAL-001`-`CENTRAL-008` | Concurrent v1/v2 ingest, reconstructable normalized records, capture-time profile binding, streamed object access, authorized range retrieval, explicit legacy state, and no storage credential exposure | `docs/project-plan.md` phase 8 | #98, #99; umbrella #60 |
| `WORKER-001`-`WORKER-008` | Hosted worker, leases, verified inputs, shared execution, idempotent output/lineage, crash recovery, telemetry/health, and audited requeue/supersede | `docs/project-plan.md` phase 9 | #100 |
| `WINDOW-001`-`WINDOW-007` | Durable ordered inputs/dependencies, lifecycle, offsets/deadlines/compatibility, sequence-based resolution, pins, historical outputs, and non-transient proof | `docs/project-plan.md` phase 10 | #101 |
| `ENV-001`-`ENV-004` | Versioned time/source/unit/quality/staleness observations, central temporal association without defaults, and durable edge delivery | `docs/project-plan.md` phase 11 | #103, #157 |
| `CLOUD-001`-`CLOUD-005` | Seeded scenarios, pre-sensor opacity/motion, versioned score/mask/confidence, shared output, and explicit overlay inputs | `docs/project-plan.md` phase 11 | #104, #105 |
| `TRANS-ARCH-001`-`TRANS-ARCH-007` | Observable top-level lanes, no hidden queues or frame clones, central references, and ordered stages unless justified | `docs/projects/fireball-transient-detection.md` sections 2-5 | #95, #101, transient epic #65 |
| `TRANS-DET-001`-`TRANS-DET-010` | Centered context, causal/final behavior, timeout, compatible linear inputs, masks, normalization/motion, structured outputs, and non-authoritative overlays | Transient specification sections 7-8 | Shared detector epic #62 and children |
| `TRANS-EVENT-001`-`TRANS-EVENT-008` | Multi-capture identity/state, observations, source references, assessments/review/notification, derivatives/reconstruction, and limitations | Transient specification section 8 | #62 and central transient epic #64 |
| `TRANS-MODE-001`-`TRANS-MODE-006` | Off/Edge/Central/Hybrid, zero disabled allocation, restart-safe edge journal, out-of-order central work, and assessment history | Transient specification section 9 | #63, #64 |
| `EVENT-001`-`EVENT-010` | Scenario matrix, simulation stage, event separation, structured contracts, linear detection, severity, edge/central execution, persistence, and review/reconstruction | `docs/project-plan.md` phase 12 | #61-#65 and transient child issues |
| `UI-001`-`UI-008` | Durable read models, authorization, local operations/gallery/config validation, central fleet/jobs/artifacts/weather/events, and audited mutation | `docs/project-plan.md` phase 13 | #106, #107 |
| `UI-LOCAL-001`-`UI-LOCAL-008` | Authenticated image-led current view, independent image/system freshness, truthful stage availability, accessible large-image viewing, presentation-first capture detail, approachable bounded archive, responsive layouts, and separate technical operations | `docs/design/cameraagent-presentation/README.md`; `docs/roadmap.md` `RM-014`; epic #438 and its child acceptance criteria | #440, #443, #439, #442, #441 |
| `E2E-001`-`E2E-007` | Real outbox two-host path, complete feature flow, fault injection, output/state validation, observability review, separate long/external/hardware gates, and current-head database critical-section/access-plan disposition | `docs/project-plan.md` phase 14 | #243, #108 |
| `EVIDENCE-P14-001`-`EVIDENCE-P14-007` | Post-milestone scenario disposition, executable workload manifests, sanitized revision-bound imports, immutable artifact admissibility, explicit deferment/exclusion, aggregation, and measured optimization follow-ups | `docs/planning/phase14-evidence-campaign.md` | #305, #318 |
| `STANDALONE-001`-`STANDALONE-003` | Full-catalog ASI676MC five-second calibrated standalone operation, real local UI/fault evidence, zero central attempts, and ASI174 Mono8 ROI/bin conformance | `docs/project-plan.md` phase 12A; course-correction plan | #211; epic #205 |
| `GATE-P00`-`GATE-P14`, `GATE-P12A` | Every phase exit gate | `docs/project-plan.md` phase sections | Owning phase issues |
| `DONE-001`-`DONE-008` | Virtual-first completion definition | `docs/project-plan.md` final section | Epic #89 |

## 5. Deferred Hardware Evidence

These requirements are retained but do not gate the virtual-first milestone.

| IDs | Requirement group | Owning source | Future disposition |
| --- | --- | --- | --- |
| `VAL-ARM-001`-`VAL-ARM-004` | Intended-host startup, cadence, resources, storage, temperature/throttling, restart, and shutdown under stable power/cooling | `docs/validation/cameraagent-arm64.md` | Future physical deployment milestone |
| `CAL-178-001`-`CAL-178-008` | Provisional status, immutable raw, derivative recipes, layout/bias/photon-transfer/dark/optics datasets, no advertised-mode inference, and no unsupported virtual mono-bin model | `docs/calibration/asi178mc-characterization.md` and JSON evidence | Future ASI178 adapter/calibration milestone |
| `CAL-174-001`-`CAL-174-006` | Native RAW16, frame/session manifests, capture matrix, versioned analysis, pair-difference variance, and no pre-analysis averaging | `docs/calibration/asi174mm-characterization.md` | Future ASI174 adapter/calibration milestone |

## 6. Retirement Verification

The retired virtual-planetarium implementation plan is represented by
`ARCH-*`, `ASTRO-*`, `PROV-*`, `VSKY-*`, `SENSOR-*`, `OPTIC-*`, `MATRIX-*`,
`FIXTURE-*`, `FIXTURE-HVO-*`, `SCENE-*`, `RENDER-*`, `ANNO-*`, `CAT-*`,
`QA-*`, and `VAL-STEL-*`. The retained VirtualSky, catalog, and validation
specifications own the values and behavior.

The retired ASI178 smoke narrative is represented by `CAL-178-*`; its distinct
2026-07-12 matrix, paths, controls, medians, and findings are retained in
`docs/calibration/asi178mc-characterization.md`, while the separate 2026-07-13
binning session remains machine-readable JSON evidence.
