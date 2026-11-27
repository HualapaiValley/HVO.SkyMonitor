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
| **AD-1** | Raw camera frames vs. RTSP/H.264/H.265 vs. proprietary streams | **Support two first-class built-in ingestion tiers plus a certified third-party seam:**<br>1. `RawLinearStream`: continuous uncompressed Mono16/Mono8/BayerRggb16/BayerRggb8 frames from direct camera adapters (starting with an opt-in `IStreamCameraModule` extension on `ZwoAsiCameraModule` using `ASIStartVideoCapture`/`ASIGetVideoData` and deterministic `VirtualSky` stream mode).<br>2. `EncodedNetworkStream`: RTSP/H.264/H.265 ingested via a hardened FFmpeg demux/decode subprocess (`HVO.SkyMonitor.Video.FFmpeg`) supplying both untouched compressed GOP packets and decoded 8-bit luma frames for screening.<br>3. `ProprietaryStream`: deferred behind `#140`/`#141`. | Preserves scientific linear fidelity on astronomy cameras while supporting low-cost IMX291/RTSP all-sky deployments (GMN/AllSky7 pattern). `EncodedNetworkStream` artifacts are explicitly typed as lossy native bitstreams (`FrameArtifactRole.Raw` is never overloaded with lossy video; V2 introduces explicit native-stream role/media semantics). |
| **AD-2** | Stream session identity, time base, exposure timing, PTS/DTS, keyframes, reconnects, gaps, clock quality, and synchronization | **Explicit `stream-session-v1` + monotonic-anchored UTC clock contract.** Every start or reconnect allocates a new `SessionEpoch` under one stable `StreamSessionId`, records stream time base (`TimeBaseNumerator/Denominator`), codec/format, keyframe cadence, per-frame PTS/DTS (nullable for raw USB), host monotonic capture timestamp (`Stopwatch.GetTimestamp()`), UTC exposure/arrival bounds, and latest `ClockSyncSnapshot` / `AstrometricClockFacts` (`kernel-adjtimex` max error, GPS/PPS lock when present). Discontinuities, PTS wraps, and dropped frames append immutable gap records rather than interpolating silent timestamps. | Prevents GMN/CAMS-style nominal-FPS drift from silently corrupting timing when USB or RTSP drops frames. Honors the existing `TimeSync` contract (`AstrometricClockFactsMapper.cs`): SNTP offset is never presented as an error bound, and leap-second-crossing intervals are explicitly flagged/withheld rather than miscalculated in `DateTimeOffset`. |
| **AD-3** | Volatile circular memory/disk buffering and exact promotion boundary | **Two-tier bounded volatile ring strictly outside Silo; atomic event-only promotion into Silo.**<br>- **Tier 1 (RAM ring):** fixed-capacity pre-allocated slab ring (compressed packet ring for RTSP; raw frame ring for low-rate raw streams) in `CameraAgent.Common`.<br>- **Tier 2 (optional NVMe spill ring):** bounded rotating segment files under `state/scratch/stream-ring/` (outside `spool/` and Silo `p/`) for high-rate raw streams whose 20–30 s pre/post window exceeds RAM budget.<br>- **Promotion boundary:** Non-event samples are discarded silently in the volatile ring without touching SQLite `raw_captures` or Silo. Only triggered events cross the **durable promotion boundary**: copy ring slice -> fsync to `spool/` -> commit durable promotion record in SQLite -> asynchronous Silo publication (`SiloPublisher`) under P3/P4 verification. | Resolves the tension between `#65`'s no-silent-loss guarantee for accepted captures and stream-native storage economics (~96× reduction on ASI174 5 fps). Explicitly configured volatile stream mode may discard unpromoted ring samples, while **every accepted V1 still capture and every promoted V2 event** retains full durable Silo guarantees. |
| **AD-4** | Configurable pre-trigger, event, post-trigger, maximum-duration, and coalescing behavior | **Deterministic state machine with bounded windows and cooldown:**<br>- Configurable `PreTriggerSeconds` (default `5 s`, max `30 s`), `PostTriggerSeconds` (default `5 s`, max `30 s`), `MaximumEventDurationSeconds` (default `30 s`, max `120 s`), and `MergeGapSeconds` (default `2 s`) to coalesce fragmented/intermittent fireballs into one logical event.<br>- Events exceeding `MaximumEventDurationSeconds` close cleanly with completeness `MaxDurationCapped` and emit a new linked continuation or cooldown suppression (`FalseTriggerBurst` guard) so cloud/lightning storms cannot exhaust storage. | Matches FreeTure/MeteorDL/AllSky7 operational experience while adding explicit, machine-readable truncation/coalescing states (`Complete`, `TruncatedPreRoll`, `TruncatedPostRoll`, `GapAffected`, `MaxDurationCapped`). |
| **AD-5** | Screening representations (individual-frame, frame-range, 1 s chunk, max-pixel, temporal-difference) | **Decoupled two-tier representation:**<br>- **Continuous Stage 1 screening:** Rolling **1-second temporal summary chunk** (`maxpixel`, `maxframeIndex`, `runningMean`, `runningMad` or temporal-difference exceedance map) computed in a single zero-copy pass per incoming frame (1 s chunk = 5 frames @ 5 fps or 25–30 frames @ 25–30 fps).<br>- **Triggered Stage 1b refinement:** Full-rate frame-by-frame centroiding, light-curve extraction, and saturation tracking over the promoted ring slice (using full linear Mono16/RGGB16 frames when raw, or decoded luma frames when encoded). | Combines the low CPU cost of CAMS/GMN FTP and MeteorDL 1 s maxpixel screening with FreeTure's full-frame temporal fidelity on triggered events. Avoids running heavy per-frame connected-components + tracking on every quiet frame while preserving exact per-frame kinematics for every promoted event. |
| **AD-6** | Preservation of native packets/frames, processed clips, scientific stills, calibration, source-span lineage, and `#436` layered products | **Immutable multi-member Silo Event Group (`groupKind: "transient-event-v2"`):**<br>1. `NativeClip` (`p/full/`): untouched bitstream-copied MP4/MKV starting on the preceding IDR keyframe (for RTSP) or lossless packed raw frame sequence + per-frame timing sidecar (for `RawLinearStream`).<br>2. `ProcessedClip` (`p/display/`): browser-playable H.264/VP9/AV1 display MP4 rendered by `HVO.SkyMonitor.Video.FFmpeg` with explicit encoder/options identity.<br>3. `ScientificStills` (`p/full/` + on-demand `fits`/`fits.fz`/`xisf` via `#2129`): peak-residual frame, clean pre/post background frame, and event-only positive-residual composite (linear Mono16/RGGB16 when raw, explicitly flagged `DerivedComposite`).<br>4. `ScreeningSummary` (`p/meta/`): 1 s chunk statistics & per-frame timing/gap ledger.<br>5. `CalibrationRefs`: immutable `compute`/`reopen` dependency references to active calibration masters, masks, and periodic long-exposure astrometry frames.<br>6. **Overlays (`#436`):** Transient trajectories, centroids, and labels remain structured JSON evidence + `OverlayManifestV1` views, never burned into `NativeClip` or `ScientificStills`. | Preserves `#436` layered-product invariants and `#141` native-evidence custody. Display clips and overlays remain independently expirable (`SiloRetentionClass.Display`) without losing primary scientific evidence (`SiloRetentionClass.Full` / `Metadata`), matching `SiloDependencyKind.Reopen` vs `Compute` (`SiloContracts.cs`). |
| **AD-7** | Two-stage classical + optional AI vs. end-to-end neural detection; CPU fallback & GPU/NPU/external execution | **Mandatory deterministic Stage 1 classical candidate generator + optional Stage 2 AI assessment.**<br>- Stage 1 runs deterministically on CPU in `HVO.SkyMonitor.Imaging`/`Processing` (no ML dependency required).<br>- Stage 2 AI assessment (`transient-assessment-producer-v2`) is opt-in, runs asynchronously after durable promotion (or as a non-blocking provisional scorer), supports built-in ONNX Runtime CPU EP fallback plus optional GPU/NPU EPs or `#140` certified external services.<br>- Stage 2 outputs **append** independent `TransientAssessment` records and **never** alter Stage 1 geometry, suppress durable promotion of high-signal events, or overwrite prior assessments. | Directly supported by Peña-Asensio et al. (2023) and FMDT benchmarks. Guarantees identical event identity and deterministic baseline behavior across CPU-only Raspberry Pi 5 and GPU-equipped x64 hosts; accelerator loss degrades gracefully to deterministic/CPU-only assessment without losing events. |
| **AD-8** | CameraAgent trigger authority, local validation, archived replay, geometry, and operator review | **CameraAgent is sole trigger and local validation authority.**<br>- Promoted events flow into the existing standalone CameraAgent event store, `TransientPage` / `TransientDetail` operator review workflow, and `cameraagent-capture-trails-v1` / V2 trail projection.<br>- **Replay boundary (`REPLAY-LOCAL-001`):** Live stream capture and real-time Stage 1/2 triggers execute in CameraAgent's live workers (or `#140` live-qualified bridge), **never** through `HVO.SkyMonitor.CameraAgent.ReplayRunner`, which remains strictly reserved for explicitly requested archived replay of durable promoted events. | Preserves standalone CameraAgent independence (`RM-017`) and `#140`'s recorded architecture boundary (`REPLAY-LOCAL-001`). Reprocessing promoted events replays exact immutable native clips/frames with pinned detector/model versions. |
| **AD-9** | Failure, backpressure, and recovery behavior | **Explicit bounded degradation per failure mode (detailed matrix in Section 2.1).** Acquisition never stalls waiting on Stage 2 AI, FFmpeg display encoding, or Silo network publication. Volatile ring exhaustion drops oldest unpromoted ring entries first and records explicit `RingOverflow` telemetry; disk/spool pressure engages the existing Silo pressure fence before exhausting host headroom. | Prevents optional stream processing from causing OOM, NVMe exhaustion, or silent corruption; guarantees crash recovery leaves zero partial/unverified promoted events visible. |

### 2.1 Failure, Backpressure, and Recovery Matrix

| Failure Mode | Immediate Runtime Action | Durable State & Evidence Impact | Recovery & Operator Visibility |
| --- | --- | --- | --- |
| **Dropped frames** (USB timeout, RTSP packet loss, buffer overrun) | Increment session drop counter; append gap entry `(lastSeq, nextSeq, monotonicDelta, utcBounds)` to session ledger. | Promoted event overlapping gap is tagged `Completeness = GapAffected` with exact missing span; never fabricated or silently interpolated. | Continues acquisition; exposes drop rate and gap counts via telemetry & health check. |
| **Decoder failure / corrupt bitstream** (bad H.264/H.265 NALU, RTSP stall) | Subprocess exits or reports error; bounded exponential reconnect (`SessionEpoch + 1`); isolate fault inside subprocess. | Native packets already in ring up to last valid IDR/frame remain promotable if triggered (`TruncatedPostRoll`); corrupt packet span quarantined with reason code. | CameraAgent process stays healthy; degraded status reported until clean keyframe resumes. |
| **Detector backlog** (Stage 1 screening slower than frame rate) | Screening drops oldest unscreened summary slot only after bounded queue limit; raw/packet ring continues independently; emits `ScreeningOverload` counter. | Active triggered promotions retain priority over new low-confidence triggers; no silent loss of already-triggered events. | Backpressure telemetry + health degradation; recommends lower resolution/binning or reduced FPS. |
| **Accelerator loss / OOM** (CUDA/TensorRT/OpenVINO/NPU device reset or external bridge timeout) | Cancel in-flight Stage 2 inference; mark accelerator provider `Unavailable` with cooldown; fall back to CPU EP (if configured) or deterministic Stage 1 assessment only. | Promoted native evidence and Stage 1 deterministic assessment are already durable and completely unaffected. | Quarantine failed Stage 2 attempt with reason code; operator may re-run Stage 2 via archived replay once accelerator recovers. |
| **Host / process restart** | Volatile RAM ring is lost (expected and explicitly documented for unpromoted samples). | Startup reconciler inspects `spool/stream-promotions/` and SQLite promotion journal: completed spool bundles finish Silo P3/P4 publication; incomplete pre-commit temp files are cleaned up without surfacing partial events. | Rebuilds clean state bounded by staged promotions + Silo journal cursor; no full-history scan. |
| **Disk / spool pressure** (`CameraAgent:Silo:MaximumStagedBytes`, `MaximumStagedFiles`, or 10 GiB headroom reserve breached) | Pause new stream promotions (and pause optional NVMe ring spill first); shed non-essential `ProcessedClip` rendering before shedding `NativeClip` promotion; engage Silo pressure pause when spool hits ceiling. | Existing committed Silo events and V1 captures are untouched; refused triggers record lightweight SQLite/telemetry `PromotionSuppressedDiskPressure` audit rows. | Automatically resumes after Silo drain/reclamation restores headroom below low-water mark. |
| **Incomplete event promotion** (crash or Silo unavailability mid-copy) | Promotion is staged atomically into a versioned spool directory before SQLite commit; Silo publication runs asynchronously via `SiloPublisher` exact-byte replay. | Event becomes visible as `Validated`/`Provisional` only after spool commit (local) and P3 Silo manifest+journal commit (Silo layout); incomplete ring copies are tagged `IncompletePromotion` or discarded before commit. | Exact-byte idempotent retry via `SiloPublisher`; never leaves half-written Silo manifests visible. |

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
|  | - Tier 2: Optional NVMe     |    | - 1 s MaxPixel / MaxFrame / Mean/MAD|  |
|  |   Scratch Segments          |    | - Mask-aware Exceedance & Trigger   |  |
|  | - Unpromoted samples drop   |    +------------------+------------------+  |
|  +-------------+---------------+                       | Trigger + Coalesce  |
|                | Slice [pre .. event .. post]          v                     |
|  ==============|================ PROMOTION BOUNDARY =========================|
|                v                                                             |
|  +------------------------------------------------------------------------+  |
|  | EventPromotionCoordinator (Durable Spool -> SQLite -> SiloPublisher)   |  |
|  | - Writes NativeClip + ScientificStills + TimingLedger to state/spool/  |  |
|  | - Commits promotion row in SQLite; publishes Silo group                |  |
|  |   (p/full/, p/display/, p/meta/, m/, j/)                               |  |
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
- `mediaFormat`: explicit codec/pixel layout descriptor (`FrameLayoutDescriptor` for raw; `CodecName`, `Profile`, `Width`, `Height`, `PixelFormat`, `BitDepth`, `NominalFpsNumerator/Denominator`, `TimeBaseNumerator/Denominator` for encoded)
- `clockAnchor`: immutable snapshot binding `HostMonotonicTicks`, `AnchorUtc`, `ClockSyncStatus`, `KernelMaxErrorMicroseconds` (from `AstrometricClockFactsMapper`), `ClockSource` (`kernel-adjtimex` / `gps-pps` / `sntp-only` / `unverified`), and `LeapSecondPending`
- `profileSet`: `CaptureProfileSet` (rig, calibration, mask, sensor, processing profile hashes) + optional coordinate-free `CaptureLocationProvenance`
- `gapLedger[]`: ordered bounded list of `(StartMonotonicTicks, EndMonotonicTicks, StartUtc, EndUtc, EstimatedDroppedFrames, ReasonCode)`

### 4.3 Temporal Source Locator Contract (`transient-source-locator-v2`)

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
- `timingProvenance`: extends `TransientTimingProvenanceV1` with `TimingQuality` (`HardwareSynced`, `KernelDisciplined`, `Reported`, `Estimated`, `Unknown`), `TimingUncertaintyMicroseconds` (nullable, never fabricated zero), and `GapAffected` boolean.

**Packaging-Independence Guarantee:** Two observations derived from the same
physical event — e.g., one from a 15-second native clip and another from a
repackaged 20-second clip or extracted scientific still — carry distinct
locators and `EvidenceId`s while sharing the caller-owned `EventId` and
overlapping UTC observation interval.

### 4.4 Event Promotion and Event Product Contracts (`transient-event-promotion-v1`, `transient-event-product-v1`)

1. **`transient-event-promotion-v1`** (recorded in SQLite promotion journal and
   Silo group manifest `m/` with `groupKind: "transient-event-v2"`):
   - `promotionId`, `eventId`, `streamSessionId`, `sessionEpoch`
   - `triggerUtc`, `requestedPreRollSeconds`, `requestedPostRollSeconds`,
     `actualStartUtc`, `actualEndUtc`, `firstPts`, `lastPts`
   - `completeness`: `Complete` | `TruncatedPreRoll` | `TruncatedPostRoll` | `GapAffected` | `MaxDurationCapped`
   - `coalescedTriggerCount`: integer >= 1
   - `members[]`: exact `SiloManifestMember` entries separating:
     - `NativeClip` (`SiloRetentionClass.Full`)
     - `ScientificStill` (`SiloRetentionClass.Full` — linear pre-event, peak, residual composite)
     - `ProcessedClip` (`SiloRetentionClass.Display` — browser-playable MP4)
     - `ScreeningSummary` & `TimingLedger` (`SiloRetentionClass.Metadata`)
   - `dependencies[]`: exact `SiloManifestDependency` entries (`Reopen` for
     display clip/stills/overlays; `Compute` for calibration masters, masks,
     and astrometry references), enabling policy expiry of display derivatives
     or compute inputs per `cameraagent-silo-cutover.md` Section 6.1 without
     breaking auditability.
2. **`transient-event-product-v1` and `#436` Layered-Product Integration:**
   - Extends `TransientDerivativeKind` additively (`NativeClip`, `ProcessedClip`,
     `ScientificStill`, `TemporalSummary`, `ActivationMap`) while preserving
     existing V1 values (`Crop`, `Preview`, `Mask`, `Overlay`, `Reconstruction`).
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

### 4.5 Append-Only Producer Provenance Contracts (`transient-assessment-producer-v2`, `transient-extraction-producer-v2`, `transient-detector-provider-v1`)

Extends the explicit seam documented in `docs/contracts/transient-contracts-v1.md`
lines 61–64 (`"Model checksums, preprocessing, execution providers, hardware
providers, and certified services require a later producer schema"`):

- **`transient-assessment-producer-v2`** (discriminated by `kind`):
  - `DeterministicAlgorithm`: `name`, `version`, `optionsSha256` (exact superset of V1).
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
| `TransientEventV1` (`transient-event-v1`), `TransientCandidateV1` (`transient-candidate-v1`), `TransientDetectorInputDescriptorV1` (`transient-detector-input-v1`) | **Unchanged** | V1 schemas, canonical JSON hashes, and golden fixtures under `tests/fixtures/processing/` remain frozen. V2 events use `transient-event-v2` (or additive V2 envelope) and V1 parsers continue rejecting unknown schemas cleanly via `TransientContractReasonCodes.UnsupportedSchema`, `UnsupportedLocator`, and `UnsupportedProducer`. |
| `TransientWholeArtifactLocatorV1` (`transient-source-locator-v1`) & `TransientAssessmentProducerV1` (`transient-assessment-producer-v1`) | **Preserved verbatim** | Every V1 locator and V1 deterministic producer is a valid subset of V2 readers (up-projection in memory without rewriting stored V1 JSON bytes). |
| `Linear16TransientExtraction` & `TransientTemporalBackgroundFactory` | **Preserved verbatim** | Still-frame `N-2..N+2` causal/centered processing remains the V1 mode; V2 stream mode lives behind an explicit opt-in configuration profile (`TransientDetectionOptions` / stream profile) and does not alter V1 default behavior. |
| `SqliteTransientCandidateJournal` & `SqliteTransientRuntimeStore` | **Additive schema version only** | Existing V1 rows require zero backfill or rewriting. V2 stream session and promotion tables are additive and remain empty when stream mode is `Off`. |
| `cameraagent-capture-trails-v1` & `OverlayManifestV1` | **Unchanged for V1 captures** | Existing still-capture measured trail projections continue binding exclusively to V1 raw capture manifests; V2 event clip overlays use additive V2 composition profiles. |
| Silo layout (`cameraagent-silo-cutover.md`) | **Additive `groupKind` only** | Uses existing `p/full/`, `p/display/`, `p/meta/`, `m/`, and `j/` prefixes and `SiloGroupManifest` (`hvo.cameraagent.silo.group-manifest/1`) with additive `groupKind: "transient-event-v2"`. |

---

## 5. Threat Model for Certified In-Process and External Processing

### 5.1 Assets and Trust Boundaries

- **Protected assets:** CameraAgent local Identity & API keys, Silo writer
  credentials (`config/secrets/CameraAgent__Silo__*`), deployment-location
  coordinates, SQLite state & durable Silo buckets, host CPU/RAM/NVMe stability,
  and scientific integrity of immutable event evidence.
- **Untrusted / semi-trusted boundaries:**
  1. Networked RTSP cameras and video bitstreams (untrusted parser input).
  2. Third-party in-process extensions / ONNX models / GPU/NPU drivers
     (supply-chain & memory-safety boundary).
  3. External AI services over local IPC or HTTPS/gRPC (egress, privacy, and
     response-integrity boundary).

### 5.2 Threats and Mandatory Controls

| Threat Category | Attack / Failure Vector | Required Mitigation |
| --- | --- | --- |
| **Codec / Demux Parser Exploitation** | Malformed RTSP headers, crafted H.264/H.265 NALUs, or container length overflows causing RCE or memory exhaustion inside CameraAgent. | Run all network stream demux/decode in a dedicated non-privileged FFmpeg subprocess (`HVO.SkyMonitor.Video.FFmpeg`) with closed argument profiles, `RLIMIT_AS`/RSS/CPU/wall-time bounds, read-only root, no secrets mounted, and bounded pipe frame sizes. Never link untrusted C/C++ codec parsers directly into the main CameraAgent process. |
| **Credential & Secret Leakage** | RTSP `rtsp://user:pass@host` credentials or external AI API keys leaking into `stream-session-v1` manifests, logs, hashes, or crash dumps. | Strip and separate credentials from stream endpoints before session identity creation (same rule as `orbital-data-decision-527.md` line 219); inject secrets only through owner-only secret files (`0600`) or environment/stdin handoff; verify with `scripts/ci:secret-scan` and redaction tests. |
| **Supply-Chain & Model Deserialization RCE** | Malicious Python pickle / TorchScript weights or tampered extension DLLs executing arbitrary code at load time. | **Accept only static tensor/graph formats (`ONNX`)**; reject pickle/TorchScript/custom Python ops. Require `#140` Ed25519/Sigstore publisher signatures, SHA-256 package/model pinning, SDK compatibility checks, and certificate revocation list enforcement before loading any third-party extension or model. |
| **Unauthorized Network Egress & Privacy Exfiltration** | A local AI extension or external bridge silently uploading sky/surveillance frames, deployment coordinates, or local files to remote servers. | Default-deny network egress for local extensions and subprocesses (private/no-network namespace). External AI services require explicit operator opt-in, TLS 1.3 with pinned CA/cert validation (matching `SiloArchiveProbe` policy), coordinate-free payloads (no lat/lon/elevation unless explicitly authorized), and strict payload/egress byte caps. |
| **Host State Tampering by Extensions** | In-process or external processor attempting to access SQLite DBs, Silo credentials, or arbitrary host paths. | External bridges receive only immutable streamed input bytes + canonical JSON request over stdin/Unix socket/TLS and return bounded JSON/derivative bytes; zero DB handles, zero Silo credentials, and zero host path arguments are exposed (matching `#140` bridge contract). Malformed or checksum-mismatched outputs are quarantined without losing primary evidence. |
| **Resource Exhaustion / Denial of Acquisition** | False-trigger storm (clouds, headlights, lightning) or hung GPU/NPU driver exhausting RAM, VRAM, NVMe, or CPU. | Hard ceilings on RAM ring bytes, NVMe scratch bytes, concurrent promotions, `MaximumEventDurationSeconds`, cooldown budgets, inference timeouts, and Silo spool headroom (`CameraAgent:Silo:MaximumStagedBytes` + 10 GiB reserve). Acquisition remains decoupled from Stage 2 and display encoding. |
| **Update & Revocation Drift** | Updating or revoking a detector extension/model silently invalidates historical replay or leaves revoked code active. | Pin every event assessment to exact `(extensionPackageId, version, packageSha256, modelSha256, configSha256)`. Revocation blocks new live/replay invocations immediately with a dedicated reason code while preserving historical audit metadata; non-revoked versions stay retained while referenced by active jobs or configured replay retention. |

---

## 6. Data-Volume, Retention, Bandwidth, and Hardware-Capacity Model

> **Analytical Estimates Only:** Every figure below is an exact arithmetic
> sizing estimate derived from sensor geometry and bitrate parameters, **not**
> a measured benchmark on physical hardware (consistent with
> `cameraagent-silo-cutover.md` Decision D12).

### 6.1 Stream Ingestion and Volatile Ring Sizing

| Profile | Resolution & Format | Frame Size | Rate | Raw Ingest Rate | 20 s Volatile Ring (100% full) | 30 s Event Payload (Raw / Native) | 10 Events / Night (30 s avg) | 8 h Continuous (No Ring) | Reduction Factor |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| **P1: ASI174 Raw 5 fps** (`W1` geometry) | 1936×1216 Mono16 | 4.708 MB (`4,708,352 B`) | 5 fps | 23.54 MB/s (188.3 Mb/s) | **470.8 MB** (fits RAM) | 706.3 MB uncompressed | ~7.06 GB/night | ~678.0 GB/night | **~96×** |
| **P2: ASI174 Raw 25 fps Mono8** | 1936×1216 Mono8 | 2.354 MB (`2,354,176 B`) | 25 fps | 58.85 MB/s (470.8 Mb/s) | **1.18 GB** (RAM on ≥8 GB host, else NVMe ring) | 1.77 GB uncompressed | ~17.66 GB/night | ~1,695 GB/night | **~96×** |
| **P3: ASI676MC Raw 5 fps** (`W6` geometry) | 3552×3552 Bayer12-in-16 | 25.233 MB (`25,233,408 B`) | 5 fps | 126.17 MB/s (1,009 Mb/s) | **2.52 GB** (requires NVMe ring or ≥8 GB RAM; 2×2 binned screening recommended) | 3.79 GB uncompressed (approaches 5 GiB Silo single-PUT ceiling!) | ~37.85 GB/night | ~3,634 GB/night | **~96×** |
| **P4: 1080p RTSP H.264/H.265 25 fps** | 1920×1080 8-bit @ 6 Mbps | ~30 KB avg compressed; 2.07 MB decoded Y8 | 25 fps | 0.75 MB/s compressed; 51.84 MB/s decoded Y8 | **15.0 MB** compressed packet ring (+ ~2–4 MB 1 s screening chunk) | ~22.5 MB native clip + ~15–25 MB display/stills | ~0.4–0.5 GB/night | ~21.6 GB/night compressed | **~45–55×** |

### 6.2 Silo Storage, Spool, and Retention Alignment

- **Silo 5 GiB single-PUT ceiling (`cameraagent-silo-runtime.md`):** CameraAgent
  writes each Silo object in one create-only PUT without multipart upload
  (ceiling `5 GiB`). For high-resolution raw events (e.g., `P3` 3552×3552 at
  5 fps for >40 s, or `P1` at 120 s max duration), native raw frame archives
  **must be chunked into bounded immutable segment objects** (e.g., 5-second or
  ≤256 MiB segments) referenced in order by `transient-event-promotion-v1`,
  keeping every Silo PUT well below the 5 GiB ceiling and keeping spool copies
  inside `CameraAgent:Silo:MaximumStagedBytes` (default `2 GiB`, peak ~`4 GiB`
  during spool-to-Silo overlap).
- **Spool & headroom interaction:** Under `P1` (706 MB per 30 s raw event),
  back-to-back events can rapidly approach the default `2 GiB` spool ceiling if
  Silo publication falls behind (recall `#1968` measured `75.82 Mb/s` /
  `9.48 MB/s` serial committed ingest on `hvo-dev-04` loopback with
  `MINIO_FS_OSYNC=on`, vs. `1,239–1,482 Mb/s` direct PUT). Therefore:
  1. V2 must publish promoted event segments as bounded multi-frame chunks (not
     one SQLite/Silo group commit per individual video frame, which would
     trigger 150 separate `sync(2)` commits for one 30 s @ 5 fps event!).
  2. Bundling a promoted event into 1–6 segment objects + 1 group manifest keeps
     `MINIO_FS_OSYNC=on` commit latency O(1) per event rather than O(frames).
- **Retention separation:** Using Silo's `Full`, `Display`, and `Metadata`
  classes allows operators to expire large raw `NativeClip` segments (e.g., 14–30
  days unless pinned/confirmed meteor) while keeping `ProcessedClip` (months)
  and compact `Metadata` + `ScientificStills` + structured `OverlayManifestV1`
  evidence (years) at <1% of raw volume.

### 6.3 Target Hardware Capacity Profiles (Initial Analytical Envelope)

| Host Tier | Typical Hardware | Recommended Ingestion Tier | Ring Placement | Detector Configuration |
| --- | --- | --- | --- | --- |
| **Tier 1: Edge SBC** | Raspberry Pi 5 (ARM64, 4–8 GB RAM, local ext4 NVMe) | `P4` (1080p RTSP 25 fps) or `P1` (ASI174 5 fps, 1 s screening) | RAM ring for `P4` (15–30 MB) or `P1` on 8 GB (≤512 MB); NVMe spill for >512 MB | Stage 1 classical CPU + optional async Stage 2 ONNX CPU EP (rate-limited, post-promotion) |
| **Tier 2: Mini-PC** | Linux x64 (4–8 cores, 16 GB RAM, NVMe, optional Intel iGPU/VAAPI) | `P1`, `P2`, or `P4` (up to 1–2 streams) | RAM ring up to 2 GB; NVMe spill optional | Stage 1 classical CPU + FFmpeg VAAPI/software display encode + ONNX CPU or OpenVINO EP |
| **Tier 3: Accelerated Edge / Workstation** | Linux x64 (8+ cores, 32+ GB RAM, NVMe, NVIDIA GPU or dedicated NPU) | `P1`, `P2`, `P3` (ASI676MC 5 fps), or `P4` | RAM ring up to 4 GB + NVMe spill | Stage 1 classical CPU + ONNX CUDA/TensorRT/NPU EP or `#140` external AI service + NVENC/software display encode |

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
| **`W9-EventLengths`** | Scripted 5 s, 10 s, and 20 s synthetic fireball/fragmentation events with 5 s pre-roll and 5 s post-roll (15 s, 20 s, and 30 s total promoted spans) | 5 independent trials per duration (median/min/max) + 30-event latency run for p95 | Pre-trigger completeness, coalescing, `MaxDurationCapped` handling, Silo chunked promotion & finalization latency |
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
   commit), validation latency, and timing error vs. injected ground truth
   (noting the 200 ms frame budget at 5 fps without inventing a portable
   pass/fail millisecond threshold before target hardware is measured).
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

- Creating this design document does **not** activate V2 runtime coding, alter
  V1 `#65` completion, claim physical meteor sensitivity or field false-positive
  rates, or close `#143` automatically until the deliverables are reviewed and
  linked implementation issues exist.
- Existing `#143` sub-issues `#527`–`#530` (bounded satellite tracking slice)
  remain governed by their separate activation record in
  [`docs/roadmap.md`](../roadmap.md#bounded-satellite-and-sky-context-activation--2026-10-19).
