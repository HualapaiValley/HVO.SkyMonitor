# Stream-Native Fireball Detection V2: Standalone CameraAgent Architecture and Contract Design

Status: architecture and contract design deliverable for
[#143](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/143)
(`roadmap:RM-008`, `component:cameraagent`). Design baseline: `development/v1`.
Companion literature survey and license audit:
[fireball-detection-v2-research.md](fireball-detection-v2-research.md).

> **Research Gate Notice:** Every design choice below is a reviewed **proposal**
> for `#143`. No V2 runtime code is implemented here, no V1 contract or
> acceptance gate is altered, and no physical sensitivity, timing precision, or
> field false-positive claim is made before target-hardware qualification.

---

## 1. Scope, Delivered Baseline, and Issue Seams

### 1.1 Standalone CameraAgent Baseline

V2 builds strictly on the delivered standalone CameraAgent architecture on
`development/v1`:

- **V1 Still-Frame Transient Subsystem ([#65](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/65), closed):**
  - Host-neutral contracts and canonical JSON validation in
    `src/HVO.SkyMonitor.Processing/TransientEventContracts.cs`,
    `TransientDetectorInputs.cs`, `TransientTemporalBackgrounds.cs`,
    `TransientCandidateExtraction.cs`, `TransientAssessment.cs`, and
    `TransientContractJson.cs` (documented in
    [`transient-contracts-v1.md`](../contracts/transient-contracts-v1.md),
    [`transient-temporal-background-v1.md`](../contracts/transient-temporal-background-v1.md),
    and [`transient-extraction-assessment-v1.md`](../contracts/transient-extraction-assessment-v1.md)).
  - Pure linear detector/extraction/reconstruction algorithms in
    `src/HVO.SkyMonitor.Imaging/Linear16TransientExtraction.cs`,
    `Linear16TransientReconstruction.cs`, and
    `Linear16TransientDerivativeProducts.cs`, plus deterministic virtual
    scenarios in `VirtualTransientScenario.cs`.
  - CameraAgent edge runtime, journal, and operator UI in
    `src/HVO.SkyMonitor.CameraAgent.Common/Transients/TransientWorkerService.cs`,
    `TransientDetectorRuntime.cs`, `SqliteTransientCandidateJournal.cs`,
    `SqliteTransientRuntimeStore.cs`, `CameraAgentCaptureTrails.cs`, and
    `src/HVO.SkyMonitor.CameraAgent/Components/Pages/TransientPage.razor` /
    `TransientDetail.razor`.
- **Canonical Layered Products ([#436](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/436), delivered):**
  - `src/HVO.SkyMonitor.Processing/LayeredPresentationContracts.cs` and
    [`cameraagent-measured-transient-layer-v1.md`](../contracts/cameraagent-measured-transient-layer-v1.md).
- **CameraAgent Silo Storage & Durable Ingress ([#1964](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1964)–#1968):**
  - Immutable content-addressed exact-version object storage (`p/full/`,
    `p/display/`, `p/meta/`, `m/`, `j/`, `c/`) with bounded local spool and
    logical holds in `src/HVO.SkyMonitor.CameraAgent.Common/Silo/`
    (`SiloContracts.cs`, `SiloRecords.cs`, `SiloPublisher.cs`,
    `SiloFilePayloadSpool.cs`), documented in
    [`cameraagent-silo-cutover.md`](../planning/cameraagent-silo-cutover.md)
    and [`cameraagent-silo-runtime.md`](../runbooks/cameraagent-silo-runtime.md).
- **Video Subprocess & Scientific Exports ([#1158](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1158), [#2129](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/2129)):**
  - Hardened FFmpeg process boundary in `src/HVO.SkyMonitor.Video.FFmpeg/`
    (`FFmpegOptions.cs`, `FFmpegTimeLapseEncoder.cs`).
  - Authenticated scientific still exports (`fits`, `fits.fz`, `xisf`) in
    `src/HVO.SkyMonitor.CameraAgent/Endpoints/CameraAgentScientificExportEndpoints.cs`.
- **Clock Discipline & Local Archived Replay:**
  - Kernel `adjtimex`, SNTP, and GPS time monitoring in
    `src/HVO.SkyMonitor.CameraAgent.Common/TimeSync/` (`ClockSyncMonitor.cs`,
    `KernelClockReader.cs`, `GpsTimeReader.cs`, `AstrometricClockFactsMapper.cs`).
  - Local archived replay boundary (`REPLAY-LOCAL-001`) in
    `src/HVO.SkyMonitor.CameraAgent.Replay/` ([`processing-runner.md`](../runbooks/processing-runner.md)).
- **Direct Camera Adapter:**
  - `src/HVO.SkyMonitor.CameraAgent.Modules.Zwo/ZwoAsiCameraModule.cs` and
    `AsiNativeApi.cs` (currently still-exposure `ASIStartExposure` /
    `ASIGetDataAfterExp` on SDK `1.41`).

### 1.2 Conditional Seams: `#140` and `#141`

- **[#140](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/140) (`RM-006`, Certified Extension Platform & External Bridge):**
  Conditional dependency for **third-party installable detectors, proprietary
  GPU/NPU execution-provider plugins, and sandboxed external AI services**
  (local executable, Unix socket, container, HTTP/gRPC). Built-in classical V2
  acquisition, ring buffering, event promotion, and built-in CPU ONNX inference
  do **not** block on `#140`, provided they emit the exact producer provenance
  envelope defined in Section 4.5.
- **[#141](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/141) (`RM-007`, Native Camera Artifacts & Pluggable Formats):**
  Built-in FITS/compressed-FITS/XISF writers (#2129) and FFmpeg video encoding
  (#1158) are already delivered. V2 consumes `#141`'s two-artifact invariant
  (**never classify demosaiced, stretched, or lossy encoded video as raw linear
  evidence**) and depends on `#140`+`#141` only if operators install third-party
  proprietary stream decoders or scientific exporters.

---

## 2. Architecture Decision Ledger

Every architecture question in `#143` is resolved below into an explicit
proposed decision, rationale, and verification gate.

| ID | Question | Proposed Decision | Rationale & Guardrails |
| --- | --- | --- | --- |
| **AD-1** | Raw camera frames vs. RTSP/H.264/H.265 vs. proprietary streams | **Support two first-class built-in ingestion tiers plus a certified third-party seam:**<br>1. `RawLinearStream`: continuous uncompressed Mono16/Mono8/BayerRggb16/BayerRggb8 frames from direct camera adapters (starting with an opt-in `IStreamCameraModule` extension on `ZwoAsiCameraModule` using `ASIStartVideoCapture`/`ASIGetVideoData` and deterministic `VirtualSky` stream mode).<br>2. `EncodedNetworkStream`: RTSP/H.264/H.265 ingested via a hardened FFmpeg demux/decode subprocess (`HVO.SkyMonitor.Video.FFmpeg`) supplying both untouched compressed GOP packets and decoded 8-bit luma frames for screening.<br>3. `ProprietaryStream`: deferred behind `#140`/`#141`. | Preserves scientific linear fidelity on astronomy cameras while supporting low-cost IMX291/RTSP all-sky deployments (GMN/AllSky7 pattern). `EncodedNetworkStream` artifacts are explicitly typed as lossy native bitstreams (`FrameArtifactRole.Raw` and V1 linear-ADU `TransientFeaturesV1` are never overloaded with lossy video or decoded 8-bit luma; V2 introduces explicit native-stream role/media and encoded-domain feature semantics). |
| **AD-2** | Stream session identity, time base, exposure timing, PTS/DTS, keyframes, reconnects, gaps, clock quality, and synchronization | **Explicit `stream-session-v1` + explicit multi-domain clock contract.** Every start or reconnect allocates a new `SessionEpoch` under one stable `StreamSessionId`, records stream time base (`TimeBaseNumerator/Denominator`), codec/format, keyframe cadence, per-frame PTS/DTS (nullable for raw USB), host monotonic clock metadata (`ClockDomain`, `TicksPerSecond`, `BootOrProcessEpochId`), separated **host arrival UTC** vs. **sensor exposure UTC** (each classified `Measured` / `Estimated` / `Unknown` with explicit nullable uncertainty), and latest `ClockSyncSnapshot` / `AstrometricClockFacts` (`kernel-adjtimex` max error, GPS/PPS lock when present). Discontinuities, PTS wraps, and dropped frames append immutable gap records distinguishing observed monotonic bounds from `Measured`/`Estimated`/`Unknown` dropped counts and exposure spans. | Prevents GMN/CAMS-style nominal-FPS drift from silently corrupting timing when USB or RTSP drops frames. Honors the existing `TimeSync` contract (`AstrometricClockFactsMapper.cs`): SNTP offset is never presented as an error bound, leap-second-crossing intervals are explicitly flagged/withheld rather than miscalculated in `DateTimeOffset`, and arrival timestamps are never silently promoted to sensor exposure times without explicit calibration/hardware upgrade evidence. |
| **AD-3** | Volatile circular memory/disk buffering and exact promotion boundary | **Two-tier bounded volatile pre-trigger ring strictly outside Silo; trigger pinning + incremental draining into bounded Silo segment/event groups.**<br>- **Tier 1 (RAM ring):** fixed-capacity pre-allocated slab ring sized for the **pre-trigger lookback window plus screening/GOP margin** (compressed GOP-aware packet ring for RTSP that retains the preceding IDR keyframe across ring wrap; raw frame ring for low-rate raw streams) in `CameraAgent.Common`.<br>- **Tier 2 (optional NVMe spill ring):** bounded rotating segment files under `state/scratch/stream-ring/` (outside `spool/` and Silo `p/`) for high-rate raw streams whose pre-trigger/in-flight window exceeds RAM budget.<br>- **Trigger pinning & incremental staging:** Non-event samples are discarded silently in the volatile ring without touching SQLite `raw_captures` or Silo. Immediately on trigger, the coordinator reserves bytes under hard RAM/scratch ceilings (`MaximumPinnedRingBytes`, `MaximumScratchRingBytes`, `MaxConcurrentPromotions`), detaches/pins the pre-roll slice (walking back to the preceding IDR keyframe for encoded streams), and **incrementally drains** leading and live event frames into bounded immutable segments (`≤256 MiB`) across the durable promotion boundary (fsync to `spool/` -> SQLite promotion state -> `SiloPublisher` P1→P2→P3→P4) so events longer than the pre-trigger ring (e.g., 30 s `W9` or 120 s max duration) survive without overwrite. | Resolves the tension between `#65`'s no-silent-loss guarantee for accepted captures, bounded RAM/NVMe budgets, and stream-native storage economics (~96× reduction on ASI174 5 fps). Explicitly configured volatile stream mode may discard unpromoted ring samples, while **every accepted V1 still capture and every promoted V2 event** retains full durable Silo guarantees and becomes visible to operators and replay only at P4. |
| **AD-4** | Configurable pre-trigger, event, post-trigger, maximum-duration, and coalescing behavior | **Deterministic state machine with bounded windows, reservation ceilings, and cooldown:**<br>- Configurable `PreTriggerSeconds` (default `5 s`, max `30 s`), `PostTriggerSeconds` (default `5 s`, max `30 s`), `MaximumEventDurationSeconds` (default `30 s`, max `120 s`), and `MergeGapSeconds` (default `2 s`) to coalesce fragmented/intermittent fireballs into one logical event.<br>- Overlapping triggers within `MergeGapSeconds` coalesce onto the same active pinned reservation; distinct concurrent promotions are capped by `MaxConcurrentPromotions` (default `2` per stream) and shared RAM/scratch ceilings.<br>- Events exceeding `MaximumEventDurationSeconds` close cleanly with completeness `MaxDurationCapped` and emit a new linked continuation or cooldown suppression (`FalseTriggerBurst` guard) so cloud/lightning storms cannot exhaust storage. | Matches FreeTure/MeteorDL/AllSky7 operational experience while adding explicit, machine-readable truncation/coalescing states (`Complete`, `TruncatedPreRoll`, `TruncatedPostRoll`, `GapAffected`, `MaxDurationCapped`, `SpoolExhaustedTruncated`) and deterministic reservation-failure recording. |
| **AD-5** | Screening representations (individual-frame, frame-range, 1 s chunk, max-pixel, temporal-difference) | **Decoupled two-tier representation with strict linear vs. encoded feature separation:**<br>- **Continuous Stage 1 screening:** Rolling **1-second temporal summary chunk** (`maxpixel`, `maxframeIndex`, `runningMean`, `runningMad` or temporal-difference exceedance map) computed in a single zero-copy pass per incoming frame (1 s chunk = 5 frames @ 5 fps or 25–30 frames @ 25–30 fps).<br>- **Triggered Stage 1b refinement:** Full-rate frame-by-frame centroiding, track/profile extraction, and clipping tracking over the promoted slice: **`RawLinearStream`** extracts V1-compatible linear-ADU features (`LinearAdu` domain on Mono16/RGGB16); **`EncodedNetworkStream`** extracts spatial kinematics and non-photometric relative luma profiles (`EncodedLuma8` domain) with explicit `PhotometryStatus = UnavailableEncodedSource` and never maps 8-bit decoded luma into linear ADU fields. | Combines the low CPU cost of CAMS/GMN FTP and MeteorDL 1 s maxpixel screening with FreeTure's full-frame temporal fidelity on triggered events while preserving `docs/contracts/transient-contracts-v1.md`'s invariant that linear ADU photometry comes exclusively from `Raw`/`Calibrated` linear sources. |
| **AD-6** | Preservation of native packets/frames, processed clips, scientific stills, calibration, source-span lineage, and `#436` layered products | **Immutable Silo Event Publication (`groupKind: "transient-event-v2"` + bounded `transient-event-segment-v1` groups when needed):**<br>1. `NativeClip` (`p/full/`): untouched bitstream-copied MP4/MKV starting on the preceding IDR keyframe (for RTSP) or lossless packed raw frame sequence + per-frame timing sidecar (for `RawLinearStream`), published either as member(s) of a single event group (when total staged size fits the verified `MaximumStagedBytes` admission reservation) or as independently durable `transient-event-segment-v1` Silo groups (`≤256 MiB` each) referenced by exact verified locators from the final `transient-event-v2` manifest group (Section 6.2).<br>2. `ProcessedClip` (`p/display/`): browser-playable H.264/VP9/AV1 display MP4 rendered by `HVO.SkyMonitor.Video.FFmpeg` with explicit encoder/options identity.<br>3. **Event Stills (strict domain separation):**<br>   - `RawLinearStream`: `ScientificStill` (`p/full/` + on-demand `fits`/`fits.fz`/`xisf` via `#2129`): linear Mono16/RGGB16 peak-residual frame, clean pre/post background frame, and event-only positive-residual composite explicitly tagged `DerivedComposite`.<br>   - `EncodedNetworkStream`: `DerivedEncodedStill` (`p/display/` or `p/meta/`): decoded non-linear inspection stills explicitly tagged `SourceDomain = EncodedLuma8`, `LinearPhotometryAvailable = false`, and decoder provenance (never classified as `ScientificStill` or linear raw FITS evidence).<br>4. `ScreeningSummary` & `TimingLedger` (`p/meta/`): 1 s chunk statistics & per-frame timing/gap ledger.<br>5. `CalibrationRefs`: immutable `compute`/`reopen` dependency references to active calibration masters, masks, and periodic long-exposure astrometry frames.<br>6. **Overlays (`#436`):** Transient trajectories, centroids, and labels remain structured JSON evidence + `OverlayManifestV1` views, never burned into `NativeClip` or stills. | Preserves `#436` layered-product invariants, `#141` native-evidence custody, `transient-contracts-v1.md` linear-ADU honesty, and `SiloFilePayloadSpool` cumulative byte ceilings (`SiloPublisher.cs:83`, `SiloFilePayloadSpool.cs:72`). Display clips and overlays remain independently expirable (`SiloRetentionClass.Display`) without losing primary native evidence (`SiloRetentionClass.Full` / `Metadata`), matching `SiloDependencyKind.Reopen` vs `Compute` (`SiloContracts.cs`). |
| **AD-7** | Two-stage classical + optional AI vs. end-to-end neural detection; CPU fallback & GPU/NPU/external execution | **Mandatory deterministic Stage 1 classical candidate generator + optional Stage 2 AI assessment.**<br>- Stage 1 runs deterministically on CPU in `HVO.SkyMonitor.Imaging`/`Processing` (no ML dependency required).<br>- Stage 2 AI assessment (`transient-assessment-producer-v2`) is opt-in, runs asynchronously after durable promotion (or as a non-blocking provisional scorer), supports built-in ONNX Runtime CPU EP fallback plus optional GPU/NPU EPs or `#140` OS-isolated external services.<br>- Stage 2 outputs **append** independent `TransientAssessment` records and **never** alter Stage 1 geometry, suppress durable promotion of high-signal events, or overwrite prior assessments. | **HVO V2 architectural design choice** informed by candidate-filtering literature (Peña-Asensio et al. 2023 evaluated transfer-learned CNN candidate-image precision and Grad-CAM localization on SPMN data) and classical embedded feasibility (FMDT CPU tracking on Pi 4), **not** an empirical claim that prior papers proved superiority over end-to-end detectors or saturated-fireball recall. Preserves identical deterministic Stage 1 event/geometry hashes across CPU-only Raspberry Pi 5 and GPU-equipped x64 hosts; CPU fallback latency and recall remain open target-hardware qualification questions (`W7–W12`). |
| **AD-8** | CameraAgent trigger authority, local validation, archived replay, geometry, and operator review | **CameraAgent is sole trigger and local validation authority.**<br>- Promoted events become visible to `TransientPage` / `TransientDetail`, APIs, and `cameraagent-capture-trails-v1` / V2 trail projections only after reaching Silo P4 (`visible`) under the Silo layout.<br>- **Replay boundary (`REPLAY-LOCAL-001`):** Live stream capture and real-time Stage 1/2 triggers execute in CameraAgent's live workers (or `#140` live-qualified bridge), **never** through `HVO.SkyMonitor.CameraAgent.ReplayRunner`, which remains strictly reserved for explicitly requested archived replay of durable P4-visible promoted events. | Preserves standalone CameraAgent independence (`RM-017`), Silo P4 visibility consistency (`cameraagent-silo-cutover.md:349`), and `#140`'s recorded architecture boundary (`REPLAY-LOCAL-001`). Reprocessing promoted events replays exact immutable native clips/frames with pinned detector/model versions. |
| **AD-9** | Failure, backpressure, and recovery behavior | **Explicit bounded degradation per failure mode (detailed matrix in Section 2.1).** Acquisition never stalls waiting on Stage 2 AI, FFmpeg display encoding, or Silo network publication. Volatile ring exhaustion drops oldest unpromoted ring entries first (preserving GOP IDR anchors while their packets remain live) and records explicit `RingOverflow` / `RingReservationExhausted` telemetry; disk/spool pressure engages the existing Silo pressure fence before exhausting host headroom. | Prevents optional stream processing from causing OOM, NVMe exhaustion, or silent corruption; guarantees crash recovery rolls P3 forward to P4 and leaves zero partial/unverified promoted events visible. |

### 2.1 Failure, Backpressure, and Recovery Matrix

| Failure Mode | Immediate Runtime Action | Durable State & Evidence Impact | Recovery & Operator Visibility |
| --- | --- | --- | --- |
| **Dropped frames** (USB timeout, RTSP packet loss, buffer overrun) | Increment session drop counter; append gap entry `(beforeMonotonicTicks, afterMonotonicTicks, observedMonotonicGapUs, utcBounds, beforePts, afterPts, ptsDiscontinuity, missingFrameCountKind, missingFrameCount, reasonCode)` to session ledger. | Promoted event overlapping gap is tagged `Completeness = GapAffected` with observed monotonic/UTC bounding interval and explicitly classified `Measured` / `Estimated` / `Unknown` dropped frame count and exposure span; missing sensor exposure times are never fabricated or silently interpolated. | Continues acquisition; exposes drop rate, PTS discontinuities, and gap counts via telemetry & health check. |
| **Decoder failure / corrupt bitstream** (bad H.264/H.265 NALU, RTSP stall) | Subprocess exits or reports error; bounded exponential reconnect (`SessionEpoch + 1`); isolate fault inside subprocess. | Native packets already pinned/drained up to last valid IDR/frame remain promotable if triggered (`TruncatedPostRoll`); corrupt packet span quarantined with reason code; reconnect boundary recorded as `PtsDiscontinuity = true` with `MissingFrameCountKind = Unknown` if continuity is lost. | CameraAgent process stays healthy; degraded status reported until clean keyframe resumes. |
| **Detector backlog** (Stage 1 screening slower than frame rate) | Screening drops oldest unscreened summary slot only after bounded queue limit; raw/packet ring continues independently; emits `ScreeningOverload` counter. | Active triggered promotions retain priority over new low-confidence triggers; no silent loss of already-pinned events. | Backpressure telemetry + health degradation; recommends lower resolution/binning or reduced FPS. |
| **Ring reservation exhaustion / wrap during active event** (concurrent bursts or `pre + event + post` exceeding `MaximumPinnedRingBytes` / `MaximumScratchRingBytes`) | Refuse new low-priority trigger reservations first; if an active promotion exhausts its reserved RAM/scratch ceiling mid-event, close appending cleanly and flush already-captured segments. | If pre-roll was partially overwritten before trigger pin, tag `Completeness = TruncatedPreRoll`; if stopped mid-event, tag `Completeness = TruncatedPostRoll` with reason `stream-promotion.ring-reservation-exhausted`; refused triggers write lightweight SQLite/telemetry `PromotionSuppressedRingBudget` audit rows. | Never allocates unbounded RAM or disk; exposes `RingReservationExhausted` and `PromotionSuppressedRingBudget` counters in health/UI. |
| **Accelerator loss / OOM** (CUDA/TensorRT/OpenVINO/NPU device reset or external bridge timeout) | Cancel in-flight Stage 2 inference; mark accelerator provider `Unavailable` with cooldown; fall back to CPU EP (if configured) or deterministic Stage 1 assessment only. | Promoted native evidence and Stage 1 deterministic assessment are already durable and completely unaffected. | Quarantine failed Stage 2 attempt with reason code; operator may re-run Stage 2 via archived replay once accelerator recovers. |
| **Host / process restart** | Volatile RAM ring is lost (expected and explicitly documented for unpromoted samples). | Startup reconciler runs `SiloPublisher.ResumeAsync` and inspects SQLite promotion/segment state: uncommitted pre-P3 staged groups resume from P1 or are abandoned; durable P3 `publish` records roll forward to P4 only after authenticating the journal prefix and verifying manifest + required-member identity and exact bytes (`cameraagent-silo-cutover.md` Section 5.3); incomplete pre-stage temp files are deleted. | Rebuilds clean state bounded by unreleased staged groups + Silo journal cursor; no full-history scan and zero partial events surfaced. |
| **Disk / spool pressure or mid-event spool exhaustion** (`CameraAgent:Silo:MaximumStagedBytes`, `MaximumStagedFiles`, or 10 GiB headroom reserve breached, including `SiloSpoolFullException` between multi-segment groups) | Pause optional NVMe ring spill and new stream promotions first; shed non-essential `ProcessedClip` rendering before shedding `NativeClip` segments; hold in-flight un-staged segments in bounded scratch only until drain or wait-budget expiry; engage Silo pressure pause when spool hits ceiling. | Existing committed Silo events and V1 captures are untouched. If spool capacity runs out between segments of a multi-segment event and drain does not recover within scratch/wait budget: already-P4 segments `0..k-1` stay immutable; finalize `transient-event-v2` manifest referencing verified segments `0..k-1` with `Completeness = SpoolExhaustedTruncated`, explicit `missingSegmentRange`, and `PromotionTruncatedSpoolExhausted` audit row (or if 0 segments reached P4, mark `FailedSpoolExhausted` / `PromotionSuppressedDiskPressure`). | Automatically resumes new promotions after Silo drain/reclamation restores spool below low-water mark; exposes truncated/suppressed event badges and operator alert telemetry. |
| **Incomplete event promotion / crash between P3 and P4** (crash or Silo unavailability mid-copy or post-P3) | Stage group bytes into `SiloFilePayloadSpool` before SQLite claim; Silo publication advances asynchronously through P1 (`payloads-qualified`) -> P2 (`manifest-qualified`) -> P3 (`recorded`, commit point) -> P4 (`visible`). | **Single consistent visibility boundary (`cameraagent-silo-cutover.md:349`):** Under Silo layout, neither operator views (`TransientPage`/`TransientDetail`) nor `ReplayRunner` observe an event at local spool commit, P1, P2, or P3. An event revision becomes visible as `Validated`/`Provisional` **only after P4** indexes the P3 journal sequence in SQLite. If a crash occurs between P3 and P4, startup recovery authenticates the P3 record and rolls it forward idempotently to P4 after verifying exact bytes (or keeps readers closed with `temporarily-unavailable` / `missing` / `unverified` / `conflict` per Section 5.3). Pre-P3 incomplete copies are retried from spool or abandoned before visibility. | Exact-byte idempotent retry via `SiloPublisher`; never leaves half-written or unverified P3 manifests visible to UI or replay. |

---

## 3. Ownership and Execution Boundary Diagram

All project boundaries follow `AGENTS.md` and `docs/project-plan.md`:
`AgentCore` stays dependency-free, `Imaging` owns pure pixel/signal algorithms,
`Processing` owns host-neutral contracts/recipes, `Video.FFmpeg` owns bounded
FFmpeg subprocesses, `CameraAgent.Common` owns edge runtime/ring/Silo/SQLite
orchestration, and `CameraAgent.Replay` stays strictly isolated to archived
replay.

```text
+------------------------------------------------------------------------------+
|  ACQUISITION SOURCES (Opt-in V2 Stream Mode; V1 Still Mode Unchanged)        |
|  [ZWO ASI Video Mode]  [VirtualSky Stream]  [RTSP/H264/H265 FFmpeg Subproc]  |
+-------------------+---------------------------+------------------------------+
                    | Raw linear frames         | Native GOP packets + 8-bit luma
                    v                           v
+------------------------------------------------------------------------------+
|  HVO.SkyMonitor.CameraAgent.Common  (Live Stream Ingestion & Volatile Ring)  |
|                                                                              |
|  +---------------------------+      +-------------------------------------+  |
|  | StreamSessionManager      |<---->| TimeSync (ClockSyncMonitor /        |  |
|  | - SessionId / Epoch       |      | KernelClockReader / GpsTimeReader)  |  |
|  | - PTS/DTS & Drop Ledger   |      +-------------------------------------+  |
|  +------------+--------------+                                               |
|               |                                                              |
|        +------+----------------------------+                                 |
|        | (zero-copy ref / packet span)     | (per-frame streaming pass)      |
|        v                                   v                                 |
|  +-----------------------------+    +-------------------------------------+  |
|  | Bounded Volatile Ring       |    | Stage 1 Continuous Screener         |  |
|  | - Tier 1: Fixed RAM Slabs   |    | (calls HVO.SkyMonitor.Imaging)      |  |
|  |   (GOP/IDR-aware eviction)  |    | - 1 s MaxPixel / MaxFrame / Mean/MAD|  |
|  | - Tier 2: Optional NVMe     |    | - Mask-aware Exceedance & Trigger   |  |
|  |   Scratch Segments          |    +------------------+------------------+  |
|  | - Trigger pin + drain       |                       | Trigger + Coalesce  |
|  +-------------+---------------+                       v                     |
|                | Pinned pre-roll + incremental segments                      |
|  ==============|================ PROMOTION BOUNDARY =========================|
|                v                                                             |
|  +------------------------------------------------------------------------+  |
|  | EventPromotionCoordinator (Bounded Spool -> SQLite -> SiloPublisher)   |  |
|  | - Publishes <=256 MiB NativeClip segment groups (P1->P4, spool drain)  |  |
|  |   or admission-reserved single group, then final transient-event-v2    |  |
|  |   manifest group; visible to UI & Replay ONLY at Silo P4               |  |
|  +-----+----------------------------+-----------------------------+-------+  |
+--------|----------------------------|-----------------------------|----------+
         |                            |                             |
         v                            v                             v
+-----------------------+  +------------------------+  +-----------------------+
| HVO.SkyMonitor.       |  | HVO.SkyMonitor.        |  | Optional Stage 2 AI   |
| Imaging & Processing  |  | Video.FFmpeg           |  | Assessment Seam       |
| (Host-Neutral Core)   |  | (Hardened Subprocess)  |  |                       |
| - Stage 1b full-rate  |  | - Native bitstream copy|  | A) Built-in ONNX RT   |
|   extraction & track  |  | - Display MP4 encode   |  |    (CPU EP baseline)  |
| - Deterministic       |  | - Bounded rlimits &    |  | B) #140 Certified Ext |
|   assessment v1/v2    |  |   scratch dir          |  |    / GPU/NPU EP       |
| - Layered #436 views  |  +------------------------+  | C) #140 Sandboxed     |
+-----------+-----------+                              |    External Bridge    |
            |                                          +-----------+-----------+
            +---------------------------+--------------------------+
                                        | Append-only assessments (never overwrite)
                                        v
+------------------------------------------------------------------------------+
|  Durable Standalone CameraAgent State & Operator Surfaces                    |
|  - Silo Exact-Version Store (p/full, p/display, p/meta, m, j, c)             |
|  - SQLite Event/Candidate/Catalog Indexes                                    |
|  - Operator UI (TransientPage / TransientDetail) & Scientific Exports (#2129)|
|  - Archived Replay ONLY -> HVO.SkyMonitor.CameraAgent.Replay (REPLAY-LOCAL)  |
+------------------------------------------------------------------------------+
```

---

## 4. Versioned V2 Contract Specifications and V1 Compatibility Proof

### 4.1 Exact Reuse of Delivered V1 Contracts

V2 preserves every core invariant of `src/HVO.SkyMonitor.Processing/TransientEventContracts.cs`:

- **Event Identity (`TransientEventV1.EventId`):** Remains an opaque
  caller-assigned `Guid`, independent of capture sequences, chunk boundaries,
  ring offsets, or clip packaging.
- **Immutable Event Versioning:** Retains `EventVersionId`, `Version`,
  `PreviousEventVersionId`, `EventCreatedUtc`, `VersionCreatedUtc`,
  `FirstObservedUtc`, and `LastObservedUtc`.
- **Append-Only Chains:** Assessments, reviews (`TransientReviewV1`), and
  notifications (`TransientNotificationV1`) retain non-forking, chronologically
  ordered supersession chains; `Fireball` remains a `TransientMeteorSeverity`
  value on `TransientClassification.Meteor`, never a top-level class.
- **Canonical JSON Rules (`TransientContractJson.cs`):** UTF-8 camelCase,
  PascalCase string enums, recursively sorted object keys, semantic array
  ordering, uppercase SHA-256 hex, `-0.0` normalization, strict unknown-member
  rejection, and root `schemaVersion` precedence over nested schema checks.

### 4.2 Acquisition Session Contract (`stream-session-v1`)

Owned by `HVO.SkyMonitor.AgentCore` (transport-neutral descriptor):

- `schemaVersion`: `"stream-session-v1"`
- `streamSessionId`: `Guid` (stable across reconnects of one configured stream)
- `sessionEpoch`: positive `long` (monotonically incremented per start/reconnect)
- `agentId`, `rigId`, `sourceId`: bounded identity strings matching `CaptureIdentityDescriptor`
- `ingestionTier`: `RawLinearStream` | `EncodedNetworkStream` | `ProprietaryStream`
- `mediaFormat`: explicit codec/pixel layout descriptor (`FrameLayoutDescriptor` for raw; `CodecName`, `Profile`, `Width`, `Height`, `PixelFormat`, `BitDepth`, `NominalFpsNumerator/Denominator`, `TimeBaseNumerator/Denominator`, nullable `PtsRolloverBits` for encoded)
- `hostMonotonicClock`: explicit monotonic clock domain binding:
  - `clockDomain`: `Stopwatch` | `ClockMonotonic` | `ClockMonotonicRaw`
  - `ticksPerSecond`: positive `long` (e.g., `Stopwatch.Frequency`; monotonic tick counts are never persisted without their frequency)
  - `bootOrProcessEpochId`: opaque `Guid` allocated per host boot/session epoch so monotonic deltas are computed only within the same epoch
- `clockAnchor`: immutable snapshot binding `AnchorMonotonicTicks`, `AnchorUtc`, `ClockSyncStatus`, `KernelMaxErrorMicroseconds` and `KernelEstimatedErrorMicroseconds` (from `AstrometricClockFactsMapper`, `null` when unsynchronized/unknown/stale — never populated from SNTP offset), `ClockSource` (`kernel-adjtimex` / `gps-pps` / `sntp-only` / `unverified`), and `LeapSecondPending`
- `sampleTimingRecord` (per-frame/sample timing ledger rules):
  - **Host arrival domain (always measured at receipt):** `HostArrivalMonotonicTicks` (in `hostMonotonicClock` domain) and derived `HostArrivalUtc`, plus nullable `Pts` / `Dts` (in `mediaFormat` time base).
  - **Sensor exposure domain (explicitly separated from host arrival):** `ExposureStartUtc`, `ExposureEndUtc`, `ExposureDurationMicroseconds` — each paired with `ExposureTimingKind` (`Measured` | `Estimated` | `Unknown`) and nullable `TimingUncertaintyMicroseconds` (never fabricated as `0`). For `EncodedNetworkStream` without calibrated encoder/network delay or hardware trigger, `ExposureStartUtc`/`ExposureEndUtc` remain `null` (`Unknown`) and observation timestamps are explicitly marked `ArrivalOnly` rather than mislabeled as sensor exposure UTC.
  - **Append-only upgrade rule:** `ArrivalOnly`, `Estimated`, or `Unknown` exposure timestamps may be upgraded only via an append-only timing provenance record citing explicit calibration or hardware evidence (e.g., calibrated encoder-latency measurement, GPS/PPS optical flash calibration, or hardware shutter trigger) — never by mutating original arrival timestamps or assuming nominal FPS.
- `profileSet`: `CaptureProfileSet` (rig, calibration, mask, sensor, processing profile hashes) + optional coordinate-free `CaptureLocationProvenance`
- `gapLedger[]`: ordered bounded list of gap records carrying `(BeforeMonotonicTicks, AfterMonotonicTicks, ObservedMonotonicGapMicroseconds, BeforeUtcBound, AfterUtcBound, UtcBoundQuality [Measured|Estimated|Unknown], BeforePts?, AfterPts?, PtsDiscontinuity, MissingFrameCountKind [Measured|Estimated|Unknown], MissingFrameCount?, ExposureSpanKind [Measured|Estimated|Unknown], ReasonCode)`. Across a PTS discontinuity or stream reconnect, `MissingFrameCount` and sensor exposure span are explicitly recorded as `Unknown` (`null`) unless a hardware frame sequence counter proves an exact count.

### 4.3 Temporal Source Locator and Domain-Separated Feature Contracts (`transient-source-locator-v2`, `transient-features-v2`)

Extends the explicit seam in `TransientEventContracts.cs` (`TransientWholeArtifactLocatorV1` / `"transient-source-locator-v1"`) without breaking V1 parsers:

- `schemaVersion`: `"transient-source-locator-v2"`
- `kind`:
  - `WholeArtifact` (exact semantic equivalent of V1 for still frames)
  - `FrameIndex` (`frameIndex: long`, `pts: long?`) — addresses one frame in a promoted raw sequence or video clip
  - `FrameRange` (`startFrameIndex: long`, `endFrameIndex: long` inclusive) — addresses a multi-frame observation span
  - `SampleRange` (`startSampleOffset: long`, `sampleCount: long`, `timeBaseNum/Den`) — addresses raw binary packed frames or chunk samples
  - `TimeRange` (`startPts: long`, `endPts: long`, `timeBaseNum/Den`) — addresses a presentation-time interval inside a native clip
  - `NativePacketSpan` (`startKeyFrameByteOffset: long`, `endByteOffset: long`, `startPts: long`, `endPts: long`) — addresses an exact GOP-aligned compressed packet span
- `artifact`: `TransientArtifactReferenceV1` (`ArtifactId`, `Role`, `Variant`, `RecipeIdentitySha256`, `ChecksumSha256`) where V2 adds non-raw native video role distinction (e.g., `FrameArtifactRole.NativeStreamClip` or dedicated V2 role enum value so `FrameArtifactRole.Raw` remains strictly linear uncompressed evidence).
- `streamSessionRef`: optional `(StreamSessionId, SessionEpoch)` binding the locator to its timing/clock provenance.
- `timingProvenance`: extends `TransientTimingProvenanceV1` with `TimestampDomain` (`SensorExposure` | `HostArrival`), `TimingQuality` (`HardwareSynced`, `KernelDisciplined`, `Reported`, `Estimated`, `Unknown`), `TimingUncertaintyMicroseconds` (nullable, never fabricated zero), `GapAffected` boolean, and optional `UpgradeEvidenceRef`.

**Domain-Separated Features (`transient-features-v2`):** Per `docs/contracts/transient-contracts-v1.md` (lines 34–36, 58–59), V1 `TransientFeaturesV1` requires linear-ADU brightness/integrated signal and accepts only `Raw` or `Calibrated` linear sources. V2 preserves that invariant by discriminating features by `featureDomain`:

- **`featureDomain: "LinearAdu"` (`RawLinearStream` only):** Carries linear-ADU `IntegratedSignalAdu`, `PeakSignalAdu`, linear `BrightnessProfile`, `SaturatedSampleCount`, continuous pixel-edge geometry/widths, and fragment count — an exact semantic superset of `TransientFeaturesV1`.
- **`featureDomain: "EncodedLuma8"` (`EncodedNetworkStream` only):** Carries spatial/kinematic measurements (continuous pixel-edge polyline, bounds, per-frame centroids, length, mean/maximum width, fragment count, clipped-luma sample count at 255) plus explicitly non-photometric relative luma summaries (`PeakLuma8`, `IntegratedDeltaLuma8`, `RelativeLumaProfile`, `DecodedColorMatrix`/`Range`). Linear ADU fields are omitted (`null`), `PhotometryStatus` is fixed to `UnavailableEncodedSource`, and observations unconditionally carry limitation code `transient.linear-photometry-unavailable-encoded-source`. Decoded 8-bit luma is **never** mapped into `TransientFeaturesV1` or linear ADU photometry.

**Packaging-Independence Guarantee:** Two observations derived from the same
physical event — e.g., one from a 15-second native clip and another from a
repackaged 20-second clip or extracted scientific still — carry distinct
locators and `EvidenceId`s while sharing the caller-owned `EventId` and
overlapping UTC observation interval.

### 4.4 Event Promotion and Event Product Contracts (`transient-event-promotion-v1`, `transient-event-segment-v1`, `transient-event-product-v1`)

1. **`transient-event-segment-v1`** (optional bounded segment Silo group used whenever a raw or long-duration event exceeds safe single-group spool reservation; see Section 6.2):
   - Published with `groupKind: "transient-event-segment-v1"`, `eventId`, `promotionId`, `segmentOrdinal`, `frameRange` / `ptsRange`, and one bounded `NativeClipSegment` payload (`≤256 MiB`, `SiloRetentionClass.Full`) plus per-segment timing sidecar (`SiloRetentionClass.Metadata`).
   - Each segment group advances through P1→P2→P3→P4 independently and releases its `SiloFilePayloadSpool` bytes at P4 before subsequent segments require spool capacity.
2. **`transient-event-promotion-v1`** (recorded in SQLite promotion journal and
   Silo group manifest `m/` with `groupKind: "transient-event-v2"`):
   - `promotionId`, `eventId`, `streamSessionId`, `sessionEpoch`, `publicationMode` (`SingleGroup` | `SegmentedGroups`)
   - `triggerUtc`, `requestedPreRollSeconds`, `requestedPostRollSeconds`,
     `actualStartUtc`, `actualEndUtc`, `firstPts`, `lastPts`
   - `completeness`: `Complete` | `TruncatedPreRoll` | `TruncatedPostRoll` | `GapAffected` | `MaxDurationCapped` | `SpoolExhaustedTruncated`
   - `missingSegmentRange`: nullable `(firstMissingOrdinal, reasonCode)` populated when `SpoolExhaustedTruncated` occurs
   - `coalescedTriggerCount`: integer >= 1
   - `segmentGroupRefs[]`: ordered list of verified exact-version P4 locators for `transient-event-segment-v1` groups (empty in `SingleGroup` mode)
   - `members[]`: exact `SiloManifestMember` entries separating:
     - `NativeClip` (`SiloRetentionClass.Full` — present inline in `SingleGroup` mode; represented via `segmentGroupRefs[]` in `SegmentedGroups` mode)
     - `ScientificStill` (`SiloRetentionClass.Full` — **only** for `RawLinearStream`: linear pre-event, peak, residual composite tagged `DerivedComposite`)
     - `DerivedEncodedStill` (`SiloRetentionClass.Display` or `Metadata` — **only** for `EncodedNetworkStream`: non-linear decoded inspection stills explicitly carrying `SourceDomain = EncodedLuma8`, `LinearPhotometryAvailable = false`, and decoder identity; never labeled `ScientificStill`)
     - `ProcessedClip` (`SiloRetentionClass.Display` — browser-playable MP4)
     - `ScreeningSummary` & `TimingLedger` (`SiloRetentionClass.Metadata`)
   - `dependencies[]`: exact `SiloManifestDependency` entries (`Reopen` for
     segment groups, display clip/stills/overlays; `Compute` for calibration
     masters, masks, and astrometry references), enabling policy expiry of
     display derivatives or compute inputs per `cameraagent-silo-cutover.md`
     Section 6.1 without breaking auditability.
   - **Visibility invariant:** Under the Silo layout, a `transient-event-v2`
     promotion is never browsable or replayable until its final manifest group
     reaches Silo **P4** (`visible` in SQLite catalog, matching
     `cameraagent-silo-cutover.md:349`).
3. **`transient-event-product-v1` and `#436` Layered-Product Integration:**
   - Extends `TransientDerivativeKind` additively (`NativeClip`, `ProcessedClip`,
     `ScientificStill`, `DerivedEncodedStill`, `TemporalSummary`, `ActivationMap`)
     while preserving existing V1 values (`Crop`, `Preview`, `Mask`, `Overlay`,
     `Reconstruction`).
   - Every derivative record binds explicit `SourceDomain` (`LinearAdu` | `EncodedLuma8`),
     `OrderedSourceEvidenceIds`, producing `RecipeIdentitySha256`, and
     `TransientDerivativeLimitation` flags (adding `LinearPhotometryUnavailableEncodedSource`
     for encoded-source derivatives).
   - Reuses `OverlayManifestV1` (`src/HVO.SkyMonitor.Processing/LayeredPresentationContracts.cs`)
     for transient overlay presentation: event polylines, per-frame centroid
     markers, saturation flags, and optional Grad-CAM/AI heatmaps are typed
     analytical layers referencing immutable event/assessment identities.
   - **Composition identity & storage/transfer impact:** Because overlays are
     materialized on demand from small JSON payloads (typically 2–32 KiB per
     event) rather than burning annotations into duplicated video files,
     layered views add <0.2% storage overhead relative to `NativeClip` +
     `ProcessedClip`, and avoid re-uploading multi-megabyte rendered videos
     when a new AI assessment or reviewer override is appended.

### 4.5 Append-Only Producer Provenance Contracts (`transient-extraction-producer-v2`, `transient-assessment-producer-v2`, `transient-detector-provider-v1`)

Extends the explicit seam documented in `docs/contracts/transient-contracts-v1.md`
lines 45–64 (`TransientExtractionProducerV1` and `TransientAssessmentProducerV1`
in `src/HVO.SkyMonitor.Processing/TransientEventContracts.cs:202-228`). In V1,
both producer records carry `(SchemaVersion, Kind, Name, Version)` and **omit**
`optionsSha256` (binding `RecipeIdentitySha256` on the enclosing
`TransientObservationExtractionV1` and `TransientAssessmentV1` records).

- **`transient-extraction-producer-v2` & `transient-observation-extraction-v2` (geometry and feature extraction lineage):**
  - Preserves V1's strict separation between **extraction** and **assessment**:
    `transient-observation-extraction-v2` binds measured geometry,
    `transient-features-v2` (`LinearAdu` or `EncodedLuma8`), and per-frame
    centroids to `originatingCandidateId` (nullable for retrospective extraction),
    `producer` (`transient-extraction-producer-v2`), `recipeIdentitySha256`, and
    nullable `extractionReceiptIdentitySha256`.
  - `transient-extraction-producer-v2` (discriminated by `kind`):
    - `DeterministicAlgorithm`: `name`, `version`, `optionsSha256` (required
      uppercase SHA-256 hex for newly emitted V2 records; **nullable / `null`
      when up-projected from V1** to preserve unknown options digest), and
      `featureDomain` (`LinearAdu` | `EncodedLuma8`).
    - `LocalMlModel` / `CertifiedExternalService` (optional future `#140`
      extractors only; built-in Stage 1/1b extraction remains deterministic):
      carries the same model/runtime/EP/extension/transport fields defined
      below plus `featureDomain` and `deterministicExecution`.
  - Re-extraction creates a new immutable observation with its own extraction
    binding and never mutates prior observations or conflates extraction
    provenance with assessment provenance.
- **`transient-assessment-producer-v2` (classification and severity assessment lineage, discriminated by `kind`):**
  - `DeterministicAlgorithm`: `name`, `version`, `optionsSha256` (required
    uppercase SHA-256 hex for newly emitted V2 records; **nullable / `null`
    when up-projected from V1**).
  - `LocalMlModel`:
    - `modelName`, `modelVersion`, `modelFormat` (`onnx-v1`), `modelSha256`
    - `preprocessingRecipeSha256`, `inputLayout` (dimensions, channel order, normalization)
    - `runtimeName` (e.g., `Microsoft.ML.OnnxRuntime`), `runtimeVersion`
    - `executionProvider` (`Cpu`, `Cuda`, `TensorRt`, `OpenVino`, `CoreMl`, `Npu`)
    - `hardwareDeviceIdentity` (sanitized device class/driver version, no host secrets)
    - `extensionPackageId`, `extensionPackageVersion`, `extensionPackageSha256` (required when loaded via `#140`)
    - `deterministicExecution`: boolean (explicitly `false` when GPU/NPU floating-point nondeterminism applies)
  - `CertifiedExternalService`:
    - `extensionPackageId`, `extensionPackageVersion`, `extensionPackageSha256`
    - `transport` (`LocalExecutable`, `UnixSocket`, `HttpTls`, `GrpcTls`)
    - `serviceIdentity`, `serviceVersion`, `remoteModelDigestSha256` (when reported)
    - `egressPolicySha256` (no credentials, tokens, or raw URLs with secrets)
- **V1 Up-Projection Rule (Preserving Unknown `optionsSha256`):**
  - When a V2 reader loads a stored V1 `TransientExtractionProducerV1`
    (`TransientEventContracts.cs:202`) or `TransientAssessmentProducerV1`
    (`TransientEventContracts.cs:221`), it up-projects in memory to the
    corresponding V2 `DeterministicAlgorithm` record with `optionsSha256 = null`
    (explicitly representing "not recorded in V1" while retaining the enclosing
    record's `RecipeIdentitySha256`). V2 never fabricates a placeholder hash or
    rewrites stored V1 JSON bytes.
- **Append-Only Coexistence Rule:**
  - Every `TransientAssessment` from a new producer kind, model checksum, or
    execution provider starts an **independent root** (or supersedes only an
    earlier assessment from the **same** producer identity).
  - AI assessments **never** overwrite, delete, or mutate deterministic V1/V2
    assessments or prior model assessments.
  - Operator review (`TransientReviewV1`) binds to a specific `AssessmentId`,
    preserving exact auditability of which deterministic or AI verdict the
    operator confirmed or overrode.
- **`transient-detector-provider-v1`** (runtime contract in `HVO.SkyMonitor.Processing`):
  - Declares supported input representations (`Mono16`, `Rggb16CellAverage`,
    `Luma8`), maximum batch/frame dimensions, memory ceiling, timeout budget,
    and `SupportsCpuFallback`.
  - Returns bounded `Produced` / `Skipped` / `RetryableFailure` /
    `TerminalFailure` outcomes with stable reason codes
    (`transient-provider.*`); never throws unmapped provider exceptions across
    the host boundary.

### 4.6 V1 Migration and Backward-Compatibility Analysis

| Existing V1 Artifact / Component | V2 Impact | Compatibility Proof |
| --- | --- | --- |
| `TransientEventV1` (`transient-event-v1`), `TransientCandidateV1` (`transient-candidate-v1`), `TransientFeaturesV1`, `TransientDetectorInputDescriptorV1` (`transient-detector-input-v1`) | **Unchanged** | V1 schemas, linear-ADU `TransientFeaturesV1` semantics, canonical JSON hashes, and golden fixtures under `tests/fixtures/processing/` remain frozen. V2 events use `transient-event-v2` (with `transient-features-v2` separating `LinearAdu` from `EncodedLuma8`), and V1 parsers continue rejecting unknown schemas cleanly via `TransientContractReasonCodes.UnsupportedSchema`, `UnsupportedLocator`, and `UnsupportedProducer`. |
| `TransientWholeArtifactLocatorV1` (`transient-source-locator-v1`), `TransientExtractionProducerV1` (`transient-extraction-producer-v1`), & `TransientAssessmentProducerV1` (`transient-assessment-producer-v1`) | **Preserved verbatim** | Every V1 locator and V1 deterministic extraction/assessment producer up-projects in memory to V2 readers without rewriting stored V1 JSON bytes; because V1 producer records omit `optionsSha256` (`TransientEventContracts.cs:202-228`), up-projection sets `optionsSha256 = null` (unknown) while preserving enclosing `RecipeIdentitySha256`. |
| `Linear16TransientExtraction` & `TransientTemporalBackgroundFactory` | **Preserved verbatim** | Still-frame `N-2..N+2` causal/centered processing remains the V1 mode; V2 stream mode lives behind an explicit opt-in configuration profile (`TransientDetectionOptions` / stream profile) and does not alter V1 default behavior. |
| `SqliteTransientCandidateJournal` & `SqliteTransientRuntimeStore` | **Additive schema version only** | Existing V1 rows require zero backfill or rewriting. V2 stream session, promotion, and segment-publication tables are additive and remain empty when stream mode is `Off`. |
| `cameraagent-capture-trails-v1` & `OverlayManifestV1` | **Unchanged for V1 captures** | Existing still-capture measured trail projections continue binding exclusively to V1 raw capture manifests; V2 event clip overlays use additive V2 composition profiles. |
| Silo layout (`cameraagent-silo-cutover.md`) | **Additive `groupKind` only** | Uses existing `p/full/`, `p/display/`, `p/meta/`, `m/`, and `j/` prefixes and `SiloGroupManifest` (`hvo.cameraagent.silo.group-manifest/1`) with additive `groupKind: "transient-event-segment-v1"` and `groupKind: "transient-event-v2"`, preserving P4 visibility semantics. |

---

## 5. Threat Model for Certified In-Process and External Processing

### 5.1 Assets and Trust Boundaries

- **Protected assets:** CameraAgent local Identity & API keys, Silo writer
  credentials (`config/secrets/CameraAgent__Silo__*`), deployment-location
  coordinates, SQLite state & durable Silo buckets, host CPU/RAM/NVMe stability,
  and scientific integrity of immutable event evidence.
- **Explicit In-Process vs. Out-of-Process Trust Boundary:**
  - **In-process code shares CameraAgent's full OS privileges:** Any .NET
    assembly or native library loaded in-process into CameraAgent executes with
    CameraAgent's OS user identity, virtual memory space, file descriptors,
    environment/secret files, SQLite handles, and network namespace. `#140`
    certification and approved-publisher signing establish **publisher identity,
    package integrity, SDK compatibility, and revocation eligibility only** —
    they **cannot** enforce memory, filesystem, credential, or network
    isolation.
  - **Accepted in-process scope:** In-process execution is accepted **only** for
    first-party built-in providers (deterministic C# Stage 1/1b, built-in
    `Microsoft.ML.OnnxRuntime` CPU EP executing static `.onnx` graphs with no
    custom native operators) or explicitly operator-trusted `#140`-certified
    extensions for which the deployment intentionally accepts host-process
    trust.
  - **Mandatory OS-isolated worker process boundary:** Any third-party
    provider, proprietary GPU/NPU execution runtime, or detector subject to
    **filesystem, credential, or network isolation requirements** **MUST** run
    outside the CameraAgent process in an **OS-isolated worker process or
    container** (via `#140`'s certified external processing bridge) using a
    distinct unprivileged OS identity, no access to SQLite databases or
    `config/secrets/`, read-only/restricted filesystem mounts, CPU/memory
    rlimits, and a default-deny network namespace. As recorded in `#140`,
    process separation alone provides fault isolation; security confinement
    requires OS/container/namespace controls.
- **Untrusted / semi-trusted boundaries:**
  1. Networked RTSP cameras and video bitstreams (untrusted parser input).
  2. Third-party extensions, ONNX models, and GPU/NPU drivers (supply-chain,
     memory-safety, and privilege boundary).
  3. External AI services over local IPC or HTTPS/gRPC (egress, privacy, and
     response-integrity boundary).

### 5.2 Threats and Mandatory Controls

| Threat Category | Attack / Failure Vector | Required Mitigation |
| --- | --- | --- |
| **Codec / Demux Parser Exploitation** | Malformed RTSP headers, crafted H.264/H.265 NALUs, or container length overflows causing RCE or memory exhaustion inside CameraAgent. | Run all network stream demux/decode in a dedicated non-privileged FFmpeg subprocess (`HVO.SkyMonitor.Video.FFmpeg`) with closed argument profiles, `RLIMIT_AS`/RSS/CPU/wall-time bounds, read-only root, no secrets mounted, and bounded pipe frame sizes. Never link untrusted C/C++ codec parsers directly into the main CameraAgent process. |
| **Credential & Secret Leakage** | RTSP `rtsp://user:pass@host` credentials or external AI API keys leaking into `stream-session-v1` manifests, logs, hashes, or crash dumps. | Strip and separate credentials from stream endpoints before session identity creation (same rule as `orbital-data-decision-527.md` line 219); inject secrets only through owner-only secret files (`0600`) or environment/stdin handoff; verify with `scripts/ci:secret-scan` and redaction tests. |
| **Supply-Chain & Model Deserialization RCE** | Malicious Python pickle / TorchScript weights or tampered extension DLLs executing arbitrary code at load time. | **Accept only static tensor/graph formats (`ONNX`)**; reject pickle/TorchScript/custom Python ops. Require `#140` approved-publisher signatures (specific cryptographic signature scheme is owned by `#140`, not hardcoded by `#143`), SHA-256 package/model pinning, SDK compatibility checks, and certification revocation enforcement before loading any third-party extension or model. |
| **In-Process Privilege Escalation / Host State Tampering** | A loaded in-process DLL reading SQLite DBs, `config/secrets/CameraAgent__Silo__*`, or host files, or an external processor attempting state tampering. | Do **not** rely on DLL signing for isolation: any provider requiring file, credential, or network confinement **must** run in an OS-isolated worker process / container under `#140`'s external bridge (distinct OS UID, no DB handles, no `config/secrets/` mounts, no host path arguments, read-only root, rlimits). In-process loading is restricted to built-in or explicitly host-trusted `#140`-certified extensions. External bridges receive only immutable streamed input bytes + canonical JSON request over stdin/Unix socket/TLS and return bounded JSON/derivative bytes; malformed or checksum-mismatched outputs are quarantined without losing primary evidence. |
| **Unauthorized Network Egress & Privacy Exfiltration** | A local AI extension or external bridge silently uploading sky/surveillance frames, deployment coordinates, or local files to remote servers. | Enforce default-deny network egress via OS/container network namespace isolation for out-of-process local workers and FFmpeg subprocesses (recognizing in-process code cannot be network-isolated from CameraAgent). External AI services require explicit operator opt-in, `https://`/TLS with no proxies, redirects, or cookies and strict configured CA/certificate validation (matching `SiloArchiveProbe.cs:148-169`, which uses OS-managed modern protocols via `EnabledSslProtocols = SslProtocols.None` and pins exclusively to the configured PEM authority rather than system trust; operators may additionally enforce a TLS 1.3 minimum where policy requires it), coordinate-free payloads (no lat/lon/elevation unless explicitly authorized), and strict payload/egress byte caps. |
| **Resource Exhaustion / Denial of Acquisition** | False-trigger storm (clouds, headlights, lightning) or hung GPU/NPU driver exhausting RAM, VRAM, NVMe, or CPU. | Hard ceilings on RAM ring bytes, pinned/scratch reservation bytes, concurrent promotions, `MaximumEventDurationSeconds`, cooldown budgets, inference timeouts, and Silo spool headroom (`CameraAgent:Silo:MaximumStagedBytes` + 10 GiB reserve). Acquisition remains decoupled from Stage 2 and display encoding. |
| **Update & Revocation Drift** | Updating or revoking a detector extension/model silently invalidates historical replay or leaves revoked code active. | Pin every event assessment to exact `(extensionPackageId, version, packageSha256, modelSha256, configSha256)`. Revocation blocks new live/replay invocations immediately with a dedicated reason code while preserving historical audit metadata; non-revoked versions stay retained while referenced by active jobs or configured replay retention. |

---

## 6. Data-Volume, Retention, Bandwidth, and Hardware-Capacity Model

> **Analytical Estimates Only:** Every figure below is an exact arithmetic
> sizing estimate derived from sensor geometry and bitrate parameters, **not**
> a measured benchmark on physical hardware (consistent with
> `cameraagent-silo-cutover.md` Decision D12).

### 6.1 Stream Ingestion and Volatile Pre-Trigger Ring Sizing

The volatile ring is sized for the **pre-trigger lookback window plus screening
and GOP/IDR margin** (e.g., `20 s` covers up to `15 s` pre-roll + margin). Once
a trigger fires, leading pre-roll and live frames are pinned and **incrementally
drained** into bounded promotion segments (`≤256 MiB`) rather than waiting
inside the overwriting circular ring for a full 30 s (`W9`) or 120 s event to
finish (AD-3).

| Profile | Resolution & Format | Frame Size | Rate | Raw Ingest Rate | 20 s Pre-Trigger Ring (100% full) | 30 s Event Raw/Native Payload (Before Stills/Meta) | 10 Events / Night (30 s avg) | 8 h Continuous (No Ring) | Reduction Factor |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| **P1: ASI174 Raw 5 fps** (`W1` geometry) | 1936×1216 Mono16 | 4.708 MB (`4,708,352 B`) | 5 fps | 23.54 MB/s (188.3 Mb/s) | **470.8 MB** (fits RAM) | `706,252,800 B` (706.3 MB / 673.5 MiB) | ~7.06 GB/night | ~678.0 GB/night | **~96×** |
| **P2: ASI174 Raw 25 fps Mono8** | 1936×1216 Mono8 | 2.354 MB (`2,354,176 B`) | 25 fps | 58.85 MB/s (470.8 Mb/s) | **1.18 GB** (RAM on ≥8 GB host, else NVMe ring) | `1,765,632,000 B` (1.77 GB / 1.64 GiB) | ~17.66 GB/night | ~1,695 GB/night | **~96×** |
| **P3: ASI676MC Raw 5 fps** (`W6` geometry) | 3552×3552 Bayer12-in-16 | 25.233 MB (`25,233,408 B`) | 5 fps | 126.17 MB/s (1,009 Mb/s) | **2.52 GB** (requires NVMe ring or ≥8 GB RAM; 2×2 binned screening recommended) | `3,785,011,200 B` (3.79 GB / **3.53 GiB** — **exceeds default 2 GiB Silo spool**; requires segmented groups or enlarged spool reservation) | ~37.85 GB/night | ~3,634 GB/night | **~96×** |
| **P4: 1080p RTSP H.264/H.265 25 fps** | 1920×1080 8-bit @ 6 Mbps | ~30 KB avg compressed; 2.07 MB decoded Y8 | 25 fps | 0.75 MB/s compressed; 51.84 MB/s decoded Y8 | **15.0 MB** compressed GOP packet ring (+ ~2–4 MB 1 s screening chunk) | ~22.5 MB native clip + ~15–25 MB display/stills | ~0.4–0.5 GB/night | ~21.6 GB/night compressed | **~45–55×** |

### 6.2 Silo Storage, Cumulative Spool Ceiling, and Retention Alignment

- **Verified Silo staging constraint (`SiloPublisher.cs:83`, `SiloFilePayloadSpool.cs:72`, `SiloInstalledSettings.cs:41`):**
  `SiloPublisher.StageAsync` stages **every member** of a single
  `SiloGroupDraft` into `SiloFilePayloadSpool` before committing the group
  state, and spool copies are released only after P4 verifies every required
  member (`SiloPublisher.cs:11-12`). Meanwhile `SiloFilePayloadSpool.StageAsync`
  enforces a **cumulative** byte ceiling across all currently staged payloads
  (`member.Length > _maximumBytes - _bytes` throws `SiloSpoolFullException`),
  defaulting `CameraAgent:Silo:MaximumStagedBytes` to **`2 GiB`**
  (`2,147,483,648 B`). Consequently, a 30-second `P3` (ASI676MC) event
  (`3,785,011,200 B` / `3.53 GiB` raw bytes before stills and metadata)
  **cannot enter the default `2 GiB` Silo spool as a single publication group**,
  even if split into `≤256 MiB` member objects inside one `SiloGroupDraft`!
- **Required V2 publication modes to satisfy both the 5 GiB single-PUT limit and `MaximumStagedBytes`:**
  1. **Default for high-rate / multi-second raw bursts — Independently durable segment publication groups (`SegmentedGroups`):**
     - Partition raw `NativeClip` payloads into bounded `≤256 MiB` immutable
       segment groups (`groupKind: "transient-event-segment-v1"`, e.g., 10 frames
       ≈ `240.6 MiB` per segment for `P3`, or 50 frames ≈ `224.5 MiB` for `P1`).
     - Stage and publish each segment group independently through P1→P2→P3→P4,
       releasing its `SiloFilePayloadSpool` bytes at P4 before staging later
       segments (bounded to `1–2` concurrent in-flight segment groups =
       `256–512 MiB` peak spool occupancy, safely inside the default `2 GiB`
       spool).
     - After all segment groups reach P4, publish one compact final event
       manifest group (`groupKind: "transient-event-v2"`) holding
       `ScientificStills`, `ProcessedClip`, `ScreeningSummary`, `TimingLedger`,
       and ordered exact-version P4 `segmentGroupRefs[]` / `Reopen`
       dependencies.
  2. **Explicit admission reservation for `SingleGroup` mode:**
     - Single-group publication (`NativeClip` + stills + metadata in one
       `SiloGroupDraft`) is allowed **only** when startup validation and
       pre-promotion admission verify that `CameraAgent:Silo:MaximumStagedBytes`
       covers `peakEventGroupBytes + concurrentSafetyMargin` (e.g., `P4` RTSP
       events at ~50 MB fit trivially in `2 GiB`; `P1` 30 s at ~706 MB fits
       with bounded concurrency; `P3` 30 s in `SingleGroup` mode is refused
       unless the operator explicitly raises `MaximumStagedBytes` to `≥8 GiB`
       on a Tier 3 host alongside the 10 GiB filesystem headroom reserve).
- **Deterministic recovery and operator visibility on mid-event spool exhaustion:**
  - Every multi-segment promotion persists its segment state machine in SQLite
    (`Pending` / `Staged` / `PublishedP4` / `SpoolExhausted` / `Abandoned`).
  - If `SiloSpoolFullException` or the disk-pressure fence occurs between
    segment `k` and `k+1`:
    1. Already-P4 segments `0..k-1` remain immutable in Silo.
    2. Remaining un-staged frames wait in the bounded NVMe/RAM scratch
       reservation only until Silo drain frees spool space below low-water mark
       or the scratch/wait budget expires.
    3. If the budget expires before spool drains, the coordinator closes the
       event cleanly with `Completeness = SpoolExhaustedTruncated`, records
       `missingSegmentRange`, publishes the final `transient-event-v2` manifest
       referencing verified segments `0..k-1` (plus available stills/timing),
       writes a `PromotionTruncatedSpoolExhausted` audit row, and surfaces an
       operator alert/badge. If zero segments reached P4, it records
       `FailedSpoolExhausted` (`PromotionSuppressedDiskPressure`) and releases
       scratch bytes.
    4. On crash/restart mid-promotion, startup reconciliation runs
       `SiloPublisher.ResumeAsync`, recovers any P1–P3 segment groups, resumes
       surviving durable scratch segments or finalizes truncated manifests, and
       marks orphaned segment groups that never joined a P4 event manifest for
       retention cleanup.
- **Commit-rate & `MINIO_FS_OSYNC` sizing:** Under `#1968` loopback
  qualification (`75.82 Mb/s` / `9.48 MB/s` serial committed ingest on a
  reference Linux x64 loopback qualification host with `MINIO_FS_OSYNC=on`, vs.
  `1,239–1,482 Mb/s` direct PUT), publishing one Silo group per individual
  video frame would trigger 150 separate `sync(2)` commits for one 30 s @ 5 fps
  event. Grouping frames into bounded `≤256 MiB` segments (~3 segment groups for
  30 s `P1`, ~15 for 30 s `P3`, or 1 group for `P4`) keeps `MINIO_FS_OSYNC=on`
  commit overhead proportional to bounded segment count rather than frame count.
- **Retention separation:** Using Silo's `Full`, `Display`, and `Metadata`
  classes allows operators to expire large raw `NativeClip` segments (e.g., 14–30
  days unless pinned/confirmed meteor) while keeping `ProcessedClip` (months)
  and compact `Metadata` + `ScientificStills` + structured `OverlayManifestV1`
  evidence (years) at <1% of raw volume.

### 6.3 Target Hardware Capacity Profiles (Initial Analytical Envelope)

| Host Tier | Typical Hardware | Recommended Ingestion Tier | Ring & Spool Placement | Detector Configuration |
| --- | --- | --- | --- | --- |
| **Tier 1: Edge SBC** | Raspberry Pi 5 (ARM64, 4–8 GB RAM, local ext4 NVMe) | `P4` (1080p RTSP 25 fps) or `P1` (ASI174 5 fps, 1 s screening) | RAM pre-trigger ring for `P4` (15–30 MB) or `P1` on 8 GB (≤512 MB); NVMe spill for >512 MB; default `2 GiB` Silo spool (`SegmentedGroups` for >512 MB raw events) | Stage 1 classical CPU + optional async Stage 2 ONNX CPU EP (rate-limited, post-promotion) |
| **Tier 2: Mini-PC** | Linux x64 (4–8 cores, 16 GB RAM, NVMe, optional Intel iGPU/VAAPI) | `P1`, `P2`, or `P4` (up to 1–2 streams) | RAM pre-trigger ring up to 2 GB; NVMe spill optional; `2–4 GiB` Silo spool with `SegmentedGroups` for `P2` bursts | Stage 1 classical CPU + FFmpeg VAAPI/software display encode + ONNX CPU or OS-isolated OpenVINO EP |
| **Tier 3: Accelerated Edge / Workstation** | Linux x64 (8+ cores, 32+ GB RAM, NVMe, NVIDIA GPU or dedicated NPU) | `P1`, `P2`, `P3` (ASI676MC 5 fps), or `P4` | RAM pre-trigger ring up to 4 GB + NVMe spill; `SegmentedGroups` (default `2 GiB` spool) or explicit `≥8 GiB` `MaximumStagedBytes` reservation for single-group `P3` | Stage 1 classical CPU + OS-isolated ONNX CUDA/TensorRT/NPU EP or `#140` external AI service + NVENC/software display encode |

---

## 7. Reproducible Prototype, Fixture, and Benchmark Qualification Plan

Aligned with [`docs/planning/performance-validation.md`](../planning/performance-validation.md),
every V2 implementation stage must produce reproducible evidence under named
workloads before moving out of prototype/qualification.

### 7.1 Required Workloads

| Workload ID | Input Definition | Duration / Scale | Purpose |
| --- | --- | --- | --- |
| **`W7-Raw5fps`** | Synthetic `VirtualSky` ASI174 1936×1216 Mono16 stream at 5 fps (`4,708,352 B/frame`, seeded, hidden oracle outside detector) | 5 min warm-up + 30 min measured; injected meteors, fireballs, satellites, aircraft, cosmic rays | Baseline raw linear ingestion, 1 s chunk screening, and 200 ms frame-budget headroom |
| **`W8-Rtsp25fps`** | Reproducible local loopback RTSP/H.264 stream (1920×1080 @ 25 fps, 6 Mbps, 1 s GOP / 25 frames, known PTS/DTS) with scripted packet drops, 3 s reconnects, and PTS discontinuities | 30 min measured | Encoded stream demux/decode isolation, IDR-aligned native clip copy, gap ledger accuracy, reconnect recovery |
| **`W9-EventLengths`** | Scripted 5 s, 10 s, and 20 s synthetic fireball/fragmentation events with 5 s pre-roll and 5 s post-roll (15 s, 20 s, and 30 s total promoted spans over a 20 s pre-trigger ring via trigger pinning + incremental segment draining) | 5 independent trials per duration (median/min/max) + 30-event latency run for p95 | Pre-trigger completeness, ring-pin/drain survival beyond ring length, IDR retention across ring wrap, coalescing, `MaxDurationCapped`, `SpoolExhaustedTruncated`, Silo segmented-group promotion & P3/P4 finalization latency |
| **`W10-NoEventSoak`** | Continuous quiet-sky stream with slow star drift, twilight/cloud luminance changes, and zero injected meteors | 4-hour sustained run (accelerated + wall-clock trials) | Verify zero unbounded memory/RSS/LOH/disk growth, zero Silo writes during quiet sky, and steady CPU/thermal profile |
| **`W11-FalseTriggerBurst`** | Synthetic lightning/headlight/broken-cloud burst injecting 60 triggers in 5 minutes while Silo spool approaches `MaximumStagedBytes` | 15 min burst + recovery drain | Cooldown/coalescing behavior, pressure-fence activation, zero OOM, zero acquisition halt, clean drain recovery |
| **`W12-AcceleratorParity`** | Frozen promoted event fixture set evaluated across Stage 1 deterministic CPU, Stage 2 ONNX CPU EP, and (when available) GPU/NPU EP and `#140` local executable bridge | 30 measured inferences per provider after warm-up | Verify append-only producer provenance, identical Stage 1 event/geometry hashes, accelerator-loss fallback, and provider latency/memory/power delta |

### 7.2 Mandatory Measurement Record

Every qualification run records all fields from
[`performance-validation.md`](../planning/performance-validation.md#3-evidence-record)
plus the V2-specific metrics required by `#143`:

1. **Latency & Timing:** capture/decode latency, Stage 1 screening latency per
   frame/1 s chunk, trigger delay (event onset to trigger fire), pre-trigger
   completeness %, event-finalization time (post-roll end to durable Silo P3
   commit and to P4 catalog visibility), validation latency, and timing error
   vs. injected ground truth (noting the 200 ms frame budget at 5 fps without
   inventing a portable pass/fail millisecond threshold before target hardware
   is measured).
2. **Throughput & Drops:** sustained frame/chunk throughput, dropped frames,
   skipped screening frames, and local queue/spool backlog depth & drain rate.
3. **Resources:** volatile ring bytes (RAM & NVMe spill), Silo storage & write
   rate (PUTs/GETs/MB/s), CPU/GPU/NPU utilization, managed allocations/LOH,
   process RSS & native subprocess memory, and host temperature/power (Watts)
   on physical ARM64/x64 targets when available.
4. **Scientific & Determinism Evidence (Virtual Fixtures Only):** synthetic
   recall and false-positive rate (with hidden oracle strictly excluded from
   detector inputs), polyline/centroid geometry error, saturation-bridge
   fidelity, and byte-identical determinism checks across repeated runs of
   Stage 1 and CPU Stage 2.

---

## 8. Proposed Implementation Issues and Dependency Order

Following `#143`'s exit criteria, V2 implementation starts only after this
research and architecture deliverable is reviewed, linked implementation issues
are created, and `RM-008` is explicitly promoted by the owner. Every runtime
issue below is **HIGH** review tier (camera/durable-state/lifecycle/concurrency/
security).

```text
#143 (Research & Design — this deliverable)
  |
  +-> [V2-1] AgentCore & Processing V2 Contracts (stream-session-v1,
  |          transient-source-locator-v2, transient-assessment-producer-v2,
  |          transient-event-promotion-v1, golden fixtures & V1 compat tests)
  |
  +-> [V2-2] VirtualSky Stream Fixture Generator & Harness (W7–W11 synthetic
  |          raw + loopback RTSP streams with injected gaps & hidden oracles)
  |     |
  +-----+-> [V2-3] Imaging Stream Screening & Multi-Frame Track Core (1 s
  |                maxpixel/maxframe/mean/MAD + full-rate Stage 1b extraction)
  |
  +-> [V2-4] CameraAgent Volatile Ring Buffer & Event Promotion Coordinator
  |          (RAM + optional NVMe spill, atomic spool -> Silo chunked group
  |           publication, crash recovery, disk-pressure & cooldown guards)
  |     |
  +-----+-> [V2-5] Raw Stream Acquisition Adapter (IStreamCameraModule +
  |                ZwoAsiCameraModule video mode + TimeSync clock anchoring)
  |     |
  +-----+-> [V2-6] Hardened RTSP/H.264/H.265 Stream Ingestion & Event Clip
  |                Encoder (HVO.SkyMonitor.Video.FFmpeg subprocess demux,
  |                IDR bitstream copy, display MP4 export, gap/reconnect ledger)
  |
  +-> [V2-7] Standalone CameraAgent V2 UI, Layered Overlays (#436), Scientific
  |          Still Exports (#2129), & Archived Replay Integration
  |
  +-> [V2-8] Optional Stage 2 Local ONNX Runtime Assessment Provider (CPU EP
  |          baseline, safe ONNX-only loader, append-only provenance, W12)
  |
  +-> [V2-9] Optional GPU/NPU Execution Providers & External AI Bridge
  |          (conditional on #140 certified extension platform)
  |
  +-> [V2-10] Full Virtual & Target-Hardware Qualification Campaign (W7–W12
              on x64 + ARM64 Raspberry Pi 5; separate physical sky campaign)
```

### Explicit Non-Goals and Closure Boundary

- This deliverable is **Part 1 (research and architecture design, `Refs #143`)**.
  Creating or merging this design document does **not** activate V2 runtime
  coding, alter V1 `#65` completion, claim physical meteor sensitivity or field
  false-positive rates, or close `#143`.
- **Follow-up tracking plan:** After maintainer review and approval of this
  Part 1 design deliverable, the owner will open numbered GitHub tracking
  issues corresponding to implementation slices `V2-1` through `V2-10` above
  (linked from `#143` with explicit dependencies and `HIGH` review-tier gates)
  and record explicit `RM-008` promotion before any V2 runtime work begins or
  `#143` is closed.
- Existing `#143` sub-issues `#527`–`#530` (bounded satellite tracking slice)
  remain governed by their separate activation record in
  [`docs/roadmap.md`](../roadmap.md#bounded-satellite-and-sky-context-activation--2026-10-19).
