# CameraAgent Silo Storage and Cutover Contract

Status: contract for [#1965](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1965), the first deliverable of
epic [#1964](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1964). It defines what the downstream issues build
and how they are accepted. It changes no code, deployment or LogicHost contract.

Every source anchor below refers to `development/v1` commit
[`87b238c766e83f2e12050542f2713b49e25550fa`](https://github.com/HualapaiValley/HVO.SkyMonitor/tree/87b238c766e83f2e12050542f2713b49e25550fa)
and uses these prefixes:

| Prefix | Directory |
| --- | --- |
| `CAC/` | `src/HVO.SkyMonitor.CameraAgent.Common/` |
| `CA/` | `src/HVO.SkyMonitor.CameraAgent/` |
| `CAR/` | `src/HVO.SkyMonitor.CameraAgent.Replay/` |
| `DCLI/` | `src/HVO.SkyMonitor.Deployment.Cli/` |

Terms: *logical identity* is an application-issued ID such as a capture, artifact, output, product, view base or
revision ID. An *exact version* is one Silo object version, named by store, bucket, key and provider version ID. A
*group* is the set of artifacts that becomes visible together, for example a settled capture or one centered view
revision. *Spool* is the local durable staging area for admitted and produced bytes that are not yet published.

## 1. Decisions

| ID | Decision | Section |
| --- | --- | --- |
| D1 | The Silo layout ships only as a clean install with a new state contract, a new state directory and an empty Silo bucket. Existing filesystem installs keep their roots and release. Nothing converts in place. | [2](#2-clean-start-and-release-boundary) |
| D2 | Logical IDs stay application-issued and never derive from Silo version IDs. Exact versions are verified locators bound to a logical revision and its SHA-256. A verified restore or import may remap them; it never re-identifies. | [4.2](#42-logical-identities-and-exact-versions) |
| D3 | Exposure admission keeps its local durability boundary. Silo reachability is not on the admission path. Products and views become visible only at a separate group publication boundary. | [5](#5-admission-staging-and-publication-boundaries) |
| D4 | Committed publication, live issue, copy, remap, pin, policy and expiry records live in a generation-ordered journal inside the instance's own bucket. That journal is authoritative for rebuilding the SQLite catalog. | [4.5](#45-durable-metadata-journal) |
| D5 | Each published revision has a reopen set, which is needed to display it at its original revision, and a compute set, which is needed to recompute it. Policy may expire compute inputs without breaking reopen. | [6](#6-dependency-closure-availability-and-reopen) |
| D6 | Holds name logical identities. They resolve to exact versions only at deletion time, carry across writer generations and recovery, and are serialized with expiry. | [7](#7-holds-writer-generations-retention-and-recovery) |
| D7 | Expiry is complete only when space is physically recovered. Exact-version delete success is not reclamation. | [7.4](#74-physical-reclamation) |
| D8 | First boot has no central dependency. Capture admission is persisted as paused before the first start, and the camera device reaches the candidate only after the former owner releases it. | [8.1](#81-clean-target-and-first-boot) |
| D9 | Legacy `cameraagent reinstall` resumes capture automatically. An early abort therefore invokes it only after proving the candidate stopped, fenced and holding zero accepted admissions, holding positive authority to restore active capture, and proving that no newer operator pause, unknown pause state or conflicting device owner would be overridden. Otherwise admission and the device stay fenced. | [8.3](#83-early-abort-ordering) |
| D10 | Newer operator pauses beat installer resumes. A pressure pause is a separate fence. Non-rewindable provider floors and halts are never reset by a clean install. | [8.4](#84-pause-precedence), [8.5](#85-request-floors-and-identity-floors) |
| D11 | Settled by Roy: the first Silo release uses the centered profile. Its launch is held until accepted #1922 and #1923 contracts and the relevant #1928 qualification are reachable at the exact integration source #1968 consumes and #1974 verifies. #1965, #1966 and #1967 proceed now. | [9](#9-branch-and-release-strategy) |
| D12 | Budgets in this contract are proposed targets. They are kept separate from measured values and attributed to their sources. | [10](#10-budgets) |

## 2. Clean start and release boundary

- **Clean install.** The Silo release installs a new instance with a distinct state contract, a new state directory, a
  new instance and application identity, and an empty versioned Silo bucket. Site configuration is set explicitly
  (#1964 cutover decision). Retention history starts at the first new admission.
- **Former instance.** Existing sites keep their installed filesystem release and data in their existing roots until
  the site adopts the Silo layout by clean install. Its history is not read by the new instance. Its disposition
  belongs to [#1976](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1976).
- **Preflight refusal.** A release or operation that would change an existing instance's storage layout in place is
  refused before mutation. The installer already evaluates the state contract and refuses an incompatible one before
  startup (`DCLI/CameraAgentStatePreflight.cs:197`, `:278`). The Silo state contract is a new contract, not a
  migration target of raw schema 17.
- **Supported operations in the first Silo release.** These are clean install, start and stop, pause and resume,
  uninstall and purge of the new instance, and new-layout backup and recovery
  ([#1972](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1972)). Stock Silo image replacement on a retained
  volume is supported after [#1966](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1966) qualifies it. In-place
  upgrade from a filesystem release, downgrade, legacy readers and live migration are excluded. A later release must
  qualify its own upgrade path on the new format before offering that operation.
- **Optional import.** A site may import selected history once through
  [#1977](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1977) ([8.6](#86-optional-historical-import-1977)).
  Fresh install, qualification and rollout complete with an empty history.
- **Prior layout context.** #1060's artifact-history layout decision and #1059's fault evidence describe the
  filesystem layout. This contract replaces neither for installs that stay on the filesystem release.

## 3. Current behavior at `87b238c`

### 3.1 Storage roots today

| Root | Contents | Primary writer and anchor |
| --- | --- | --- |
| `<RawIngressRoot>/{frames,index,journal,quarantine}` | Raw payload `.bin` and sidecar `.json`, the daily JSONL index, `raw-ingress.db`, quarantined pairs | `CAC/RawIngress/RawIngressFileStore.cs:16`, `:25-38` |
| `<StorageRoot>/frames/YYYY/MM/DD/<Role>/<ts>-<artifactId>.bin` and `.json` | Derived payloads and `ArtifactManifestV2` sidecars | `CAC/Storage/FileSystemFrameStorageService.cs:1146-1156` |
| `<StorageRoot>/index/frames_YYYY-MM-DD.jsonl` | Non-authoritative browse projection | `CAC/Storage/FileSystemFrameStorageService.cs:170-173` |
| `<StorageRoot>/…/*.archive-stage`, `*.archive` | Centered archive copy stage and receipt (`centered-archive-copy-v1`, `centered-archive-receipt-v2`) | `CAC/Storage/FileSystemFrameStorageService.cs:749-750`, `:988-990` |
| `<StorageRoot>/outbox/artifact-outbox.db` | Upload outbox for each storage root | `CAC/Upload/SqliteArtifactOutbox.cs:1718` |
| `raw/.time-lapses`, `raw/.nightly-products`, `raw/.automation`, `raw/.orbital-elements`, `raw/.space-weather`, `raw/.calibration-families`, `raw/.environment`, `raw/.fleet`, `raw/evidence` | Instance stores listed in the [install contract state table](../hvo-install-contract.md) | Per store; see 3.2 |
| `state/{identity,data-protection,provisioning,raw,archive,replay-runner}` | Installer-managed state directories | `DCLI/CameraAgentStateLayout.cs:46-51` |

### 3.2 Flow map

Each row gives today's writer, the durable record and hold, the readers, and the new-layout disposition. "Silo"
means immutable exact versions in the instance bucket ([4.3](#43-object-namespace)). "State" means local instance
state covered by the instance backup. The centered rows describe behavior observed at `87b238c`. Where accepted #1922
and #1923 contracts differ at the integration source, they supersede these rows ([9](#9-branch-and-release-strategy)).

| Flow | Writer today | Durable record and hold today | Readers | New layout |
| --- | --- | --- | --- | --- |
| Raw admission | `RawCaptureIngress.AcceptAsync` (`CAC/RawIngress/RawCaptureIngress.cs:428`). Order: stable IDs, lane capacity, graph binding, lifecycle lock, `ReserveIdentityAsync` as the point of no cancellation (`:496-499`), payload fsync (`:616`), sidecar fsync (`:643`), journal `CommitAsync` (`:705`), JSONL append whose failure only degrades (`:743-768`) | `raw_captures`, `raw_capture_sequences`, `capture_lane_work` (`CAC/RawIngress/SqliteRawCaptureJournal.cs:2478`, `:2489`, `:2553`); raw-ingress holds (`:827`) | Lanes, gallery, reconciler | Same order and boundary. Payload and sidecar go to the **spool** (State). The JSONL append is removed. Publication to Silo follows asynchronously ([5](#5-admission-staging-and-publication-boundaries)). |
| Spool and journals | `RawIngressFileStore`, `SqliteRawCaptureJournal`, quarantine planning | Journal tables, `raw_ingress_reconciliation`, quarantine directory | `RawIngressReconciler.RunAsync` (`CAC/RawIngress/RawIngressReconciler.cs:43`) scans every frame and index file and journal row (`:89`, `:318`) | Spool and admission journal stay State. Quarantine becomes an availability record on a retained exact version rather than a file move. Startup reconciliation is bounded to staged work and journal records after the catalog cursor ([5.3](#53-group-publication)). |
| Storage graph steps | `FileStorageCaptureProcessingStep` through `IFrameStorageService.SaveAsync` (`CAC/Capture/Processing/FileStorageCaptureProcessingStep.cs:333`), plus `CaptureProcessingPersistence` (`CAC/Capture/Processing/CaptureProcessingPersistence.cs:217`, `:753`, direct write `:1085`) | `processing_outputs` with relative paths and availability `Available`, `Missing`, `Quarantined` (`CAC/Capture/Processing/SqliteCaptureProcessingStore.cs:4251`) | Gallery, archive reader, replay, products | The path-returning `IFrameStorageService` seam (`CAC/Storage/IFrameStorageService.cs:5-43`) is replaced by a logical-reference seam. Products go spool, then Silo, then group publication. In-place producer sidecar upgrade (`CAC/Storage/FileSystemFrameStorageService.cs:159-167`) becomes a new revision. |
| Centered buffer inputs | Centered raw admission (`CAC/RawIngress/RawCaptureIngress.Centered.cs`), recent index with 16 entries per camera (`CAC/RawIngress/CenteredRecentExposureIndex.cs:7`), input lease (`CAC/Capture/Processing/CenteredExposureInputLease.cs:8`) | `centered_exposure_products`, `centered_exposure_holds` with expiry (`CAC/RawIngress/SqliteRawCaptureJournal.Centered.cs:91`, `:111`) | Window coordinator, registration | Recent-buffer holds become logical compute holds with expiry ([7.1](#71-hold-model)). Bytes resolve through the catalog, local first. |
| Centered windows and view revisions | `SqliteCenteredViews` writes (`CAC/Gallery/SqliteCenteredViews.Writes.cs`). `RegisterBaseAsync` adds a revision not selected live (`:9`, `:47`). `PublishLayerAsync` issues live only on the current base (`:62`, `:94-101`). `SelectLiveAsync` issues live (`:134`, `:164`). Revision IDs are content-derived (`:355`). Archive copy is staged then receipted (`CAC/Capture/Processing/FileStorageCaptureProcessingStep.cs:343`, `:373`) | `processing_centered_view_{bases,changes,revisions,intervals,selections}` (`CAC/Capture/Processing/SqliteCaptureProcessingStore.CenteredViews.cs:120-185`); live and registration holds | Current Sky, archive reopen, saved selections | Each revision is a group with a view manifest ([4.4](#44-manifests)). Live issue and interval end are journal records. Archive-only revisions stay reopenable. Artifact references are already path-free: `CenteredViewArtifact(ArtifactId, IdentitySha256, PayloadSha256)` (`CAC/Gallery/CenteredViewPublication.cs:25`). |
| Gallery and archive | `SqliteCameraAgentGallery` reads processing tables (`CAC/Gallery/SqliteCameraAgentGallery.cs:81`); `LocalArchiveReader` reads the JSONL indexes (`CAC/Gallery/LocalArchiveReader.cs:13-15`, `:93`); content opened by `CameraAgentArtifactService.OpenContentAsync` (`CAC/Gallery/CameraAgentArtifactService.cs:141`) | Index projection, relative paths | UI, API (`CA/Endpoints/CameraAgentArtifactEndpoints.cs:51`) | The catalog becomes the browse source. Content streams from exact versions or the bounded cache. Availability states are shown ([6.2](#62-availability-states)). |
| Calibration | `CalibrationArtifactPublisher` writes immutable pairs directly (`CAC/Capture/Calibration/CalibrationArtifactPublisher.cs:74`, `:196`); `CalibrationLibraryReconciler` (`CAC/Capture/Calibration/CalibrationLibraryReconciler.cs:290`); synthetic references under `<raw>/calibration/synthetic` (`CAC/Capture/Processing/SyntheticCalibrationReferenceStore.cs:92`) | `calibration_library_*` tables (`CAC/RawIngress/SqliteRawCaptureJournal.cs:2870-2997`), `calibration_family_*` (`CAC/Capture/Calibration/SqliteCalibrationFamilyStore.cs`) | Normalization plans, processing | Masters and bundles are Silo groups in the full class. Library and family state stays in State. Active family masters and frozen plan masters are compute holds. |
| Replay | `ProcessingReplayWorker` (`CAC/Capture/Processing/ProcessingReplayWorker.cs:89`); inputs prepared by `CAC/Capture/Processing/SqliteCaptureProcessingReplayStore.cs:406`; runner payloads declared by ordinal, length and SHA-256 (`CAR/ReplayProjection.cs:15`) | `processing_replay_work` | Local replay runner | Inputs resolve to exact versions under a restore/replay lease and stream to the runner ([#1971](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1971)). Outputs publish as groups. |
| Exports | Artifact download (`CA/Endpoints/CameraAgentArtifactEndpoints.cs:51`), time-lapse stream (`CA/Endpoints/CameraAgentTimeLapseEndpoints.cs:33`), focus-session and pipeline-run JSON (`CA/Endpoints/CameraAgentFocusSessionEndpoints.cs:61`, `CA/Endpoints/CameraAgentProcessingGraphOperationsEndpoints.cs:273`), execution evidence export lane (`CAC/Evidence/ExecutionEvidenceExportService.cs:13-20`) | None; reads only | Operator | Exports read exact versions through the same resolver. The evidence outbox stays State. |
| Time-lapses | `SqliteTimeLapseStore` writes `products/<id>.mp4` (`CAC/TimeLapses/SqliteTimeLapseStore.cs:265`); sources read by path (`CAC/TimeLapses/TimeLapseSourceReader.cs:73`); FFmpeg workspace (`src/HVO.SkyMonitor.Video.FFmpeg/FFmpegTimeLapseEncoder.cs:162`) | `jobs`, `products`, `publications`, `retries`; queued-input holds | UI, stream endpoint | The video is a display-class group. Sources stream from exact versions under a job hold. The workspace stays local scratch. |
| Nightly products | `SqliteNightlyProductStore` writes `nightly-products/YYYY/MM/DD/<target>/<id>.{bin,jpg,provenance.json}` (`CAC/NightlyProducts/SqliteNightlyProductStore.cs:58`, `:237-239`) | `nightly_products`, `nightly_product_sources` | UI | `.bin` is full class and `.jpg` is display class in one group. Provenance goes into the manifest. |
| Satellite work | `CaptureSatelliteWorkStore` writes `capture-satellites/.../{input,result,publication-input}.json` and an orphan cursor (`CAC/Capture/Processing/CaptureSatelliteWorkStore.cs:80`, `:131`, `:165`) | Work files; satellite holds | Satellite lane | The work queue stays State. Results publish as groups. Pending work holds inputs. |
| Projected-scene staging | `ProjectedSceneStagingStore` under `projected-scenes` with a stage cursor (`CAC/Modules/VirtualSky/ProjectedSceneStagingStore.cs:148-149`); `ProjectedSceneRetentionGuard` (`CAC/Capture/Processing/ProjectedSceneRetentionGuard.cs:191`) | Stage files and cursor | Virtual-sky module | Staging stays State (spool class). Only published scene products enter Silo. |
| Transient candidates | `SqliteTransientCandidateJournal` in `raw-ingress.db` (`CAC/Transients/SqliteTransientCandidateJournal.cs:171`); runtime store (`CAC/Transients/SqliteTransientRuntimeStore.cs`) | `transient_*` tables | Transient lane | State. Evidence products publish as groups. The installer turns detection off (`DCLI/ComposeDeployment.cs:309`). |
| Pruning | `RetentionBackgroundService` unions outbox, raw-ingress and processing holds as an absolute-path set (`CAC/Background/RetentionBackgroundService.cs:343-376`); age expiry shortened under pressure (`:160-162`); paused while a capture pause holds maintenance (`:98-104`); `ExpireOutputsAsync` plans and completes deletion of rows and files (`CAC/Capture/Processing/CaptureProcessingPersistence.cs:369`) | Path holds; `processing_lifecycle_operations` phases in SQLite; deleted rows leave no policy-expiry record outside centered outputs | Retention | Replaced by the logical-hold expiry protocol with journaled intent and disposition and physical accounting ([7](#7-holds-writer-generations-retention-and-recovery)). |
| Retained evidence checks | `RawIngressReconciler` (above); `RetainedIntegrityLedger` with a protected checkpoint (`CAC/DeploymentLocation/RetainedIntegrityLedger.cs:16`, `:135`); `DeploymentContinuityReader` reads raw, outbox and fleet databases (`CAC/Deployment/DeploymentContinuityReader.cs:51-53`) | File hashes, sequences | Installer, startup | Payload evidence becomes manifest digests plus exact-version verification and the journal hash chain. The deployment-location ledger stays State. Continuity reads catalog and journal cursors. |
| Deployment | `CameraAgentLifecycleManager` uninstall (`DCLI/CameraAgentLifecycleManager.cs:1403`, pause `:1440`) and reinstall (`:1469`, auto-resume `:1548`); compose settings (`DCLI/ComposeDeployment.cs:302-327`); counter floors (`DCLI/CameraAgentStateCounters.cs:8-19`) | Instance manifest, lifecycle record, snapshots | Operator | A new state contract adds the Silo binding, bucket marker and spool ([8](#8-installation-and-cutover)). The early-abort ordering wraps the legacy reinstall. |
| Upload and outbox (central) | `ArtifactOutboxDrainService` (`CAC/Upload/ArtifactOutboxDrainService.cs:326`); upload lane (`CAC/Capture/Distribution/CaptureLanePolicy.cs:17-21`) | `artifact_outbox_*` | LogicHost | Rejected for the standalone layout. Central integration and upload stay disabled (`DCLI/ComposeDeployment.cs:306-307`). |

### 3.3 Findings that constrain the design

| ID | Finding at `87b238c` | Consequence |
| --- | --- | --- |
| F1 | The storage seam returns `StoredFrameReference(RelativePath, AbsolutePath, …)` (`CAC/Storage/IFrameStorageService.cs:37-41`), and many writers bypass it (calibration, nightly, time-lapse, satellite, projected scenes, product persistence). | #1968 replaces each writer in 3.2, not only the interface. No new-layout table stores a payload path as authority. |
| F2 | Holds are `(ArtifactId, PayloadRelativePath, SidecarRelativePath)` (`CAC/Capture/Processing/ProcessingRetentionContracts.cs:5-8`; `CAC/RawIngress/RawCaptureContracts.cs:75-78`). Expiry compares absolute paths (`CAC/Capture/Processing/CaptureProcessingPersistence.cs:372`). | Holds become logical ([7.1](#71-hold-model)). |
| F3 | Availability allows only `Available`, `Missing` and `Quarantined` (`CAC/Capture/Processing/SqliteCaptureProcessingStore.cs:4251`). Expiry is a planned SQLite lifecycle operation (`:4267`) whose completion deletes the output row (`:1817`). Only centered outputs keep an `Expired` disposition (`:1790-1791`), also in SQLite only (`CAC/Capture/Processing/SqliteCaptureProcessingStore.CenteredAvailability.cs:20`). | Policy expiry needs a retained, journaled disposition so a rebuild can tell it from unexpected loss ([6.2](#62-availability-states)). |
| F4 | A producer sidecar upgrade rewrites a durable sidecar in place (`CAC/Storage/FileSystemFrameStorageService.cs:159-167`). | Any changed fact becomes a new revision. Exact versions are never rewritten. |
| F5 | Startup reconciliation traverses the full history (`CAC/RawIngress/RawIngressReconciler.cs:89`, `:318`). #1059 pinned this as unqualified ([fault evidence](../validation/artifact-history-1059-fault-evidence.md#explicitly-unqualified-acceptance)). | New-layout startup is bounded by staged work plus the journal after the catalog cursor. |
| F6 | A fresh installer-managed instance starts admitting. The startup gate that pauses a version-0 state (`CAC/Capture/CaptureAdmissionCoordinator.cs:764`) is enabled only by `ProvisioningStartupGate`, which the installer does not set (`DCLI/ComposeDeployment.cs:302-327`). | Persisted paused first boot is new #1966 work ([8.1](#81-clean-target-and-first-boot)). |
| F7 | The camera module initializes regardless of the admission state (`CAC/Capture/CameraCaptureService.cs:131-162`). | A persisted pause does not keep the candidate off the camera. Device allocation enforces the handoff. |
| F8 | Reinstall recreates, verifies, pauses, commits and then resumes with no expected version (`DCLI/CameraAgentLifecycleManager.cs:1522-1548`; `DCLI/CameraAgentLifecycleClient.cs:101-111`). With no active installer receipt the server allows the resume with no owned pause (`CAC/Deployment/CameraAgentControlStore.cs:117-118`; `CAC/Capture/CaptureAdmissionCoordinator.cs:426-433`). The read-only plan does not list the resume (`DCLI/CameraAgentLifecycleManager.Plan.cs:110-116`). | Reinstall lifts any persisted pause. The early-abort proof must precede it ([8.3](#83-early-abort-ordering)). |
| F9 | Retention deletions wait while a capture pause holds maintenance (`CAC/Background/RetentionBackgroundService.cs:98-104`). Capacity exhaustion makes raw ingress unhealthy rather than paused (`CAC/RawIngress/RawCaptureIngress.cs:1182-1203`). | A distinct pressure pause is needed, and reclamation must be allowed under it ([8.4](#84-pause-precedence)). |
| F10 | Orbital and space-weather stores hold non-rewindable floors and halts (`CAC/Orbital/OrbitalElementsRefreshJob.cs:17`; `CAC/SpaceWeather/Fetch/SwpcRetryAfter.cs`; install contract). Provider acquisition defaults to disabled (`CA/appsettings.json:94-98`). | The clean candidate must not reset them ([8.5](#85-request-floors-and-identity-floors)). |

## 4. New-layout data model

### 4.1 Placement

| Data | Home | Notes |
| --- | --- | --- |
| Raw and full-fidelity payloads: raw, linear, calibrated, combined, masters, nightly `.bin` | Silo `p/full/` | Content-addressed; [4.3](#43-object-namespace) |
| Display renditions: previews, annotated previews, nightly `.jpg`, time-lapse video | Silo `p/display/` | Separate display retention |
| Metadata products: typed metadata and astrometry solutions | Silo `p/meta/` | Kept with display history |
| Group manifests: capture, product, view revision, calibration, time-lapse, nightly, import | Silo `m/` | Immutable, one object per revision |
| Durable metadata journal and checkpoints | Silo `j/`, `c/` | [4.5](#45-durable-metadata-journal) |
| Bucket ownership and writer fence | Silo `meta/` | Create-only marker; conditional fence |
| Spool: admitted and produced bytes before verified, persistence-qualified publication | State `spool/` on the same NVMe | Bounded; [5.5](#55-staging-bound-and-pressure-pause) |
| SQLite catalog, admission journal, processing, centered, schedule, rig, calibration library and transient state | State | Catalog content is rebuildable to the verified cutoff ([4.6](#46-sqlite-catalog)) |
| Identity, Data Protection keys, provisioning, settings, automations, site profile, deployment location, orbital, space weather, time data | State | Instance backup; floors per [8.5](#85-request-floors-and-identity-floors) |
| Restore and replay cache, FFmpeg workspace | State scratch | Bounded by age and bytes (#1971) |
| Artifact, fleet and environmental outboxes | Not used by the standalone profile | Central integration rejected |

### 4.2 Logical identities and exact versions

- **Logical identities stay as issued today.** These are `captureId` and the raw `artifactId` from
  `CreateStableIds` and `CreateRawArtifactId` (`CAC/RawIngress/RawCaptureDescriptorFactory.cs:18-36`), processing
  `outputIdentitySha256` and `contentIdentitySha256`, centered `baseId` and content-derived `revisionId`
  (`CAC/Gallery/SqliteCenteredViews.Writes.cs:355`), and product, bundle and job IDs. None derives from a Silo version
  ID, ETag, key or path, and none is reused for different content.
- **Revision.** An artifact revision is `(artifactId, revisionId)` bound to exactly one `(length, sha256)`. Any change
  to bytes, sidecar facts or producer attribution creates a new revision. A conflicting digest for an existing
  revision is a conflict and never replaces a verified payload.
- **Exact version.** A locator is `(storeId, bucket, key, versionId)` plus the verified `(length, sha256)`. The
  `storeId` names a qualified store identity (local Silo, archive, future cold tier), not an endpoint URL, so
  endpoint relocation preserves it.
- **Remap.** A verified restore or import that assigns new version IDs records `remap(old → new)` only after the new
  bytes verify by length and SHA-256. Logical IDs, revisions and digests never change. Manifests stay immutable; the
  journal's remap and copy records supersede the locator a manifest recorded at publication.
- **Instance binding.** `instanceId` comes from the installer manifest. `storageBindingId` hashes `instanceId`, the
  qualified local store identity and the bucket. It is written once to `meta/instance.v1.json` and checked by
  preflight and startup. A mismatch refuses before mutation.
- **Writer generation.** `g ≥ 1` starts at 1 on clean install and advances on every recovery onto new storage, new
  primary or replacement host ([7.2](#72-writer-generations-and-fences)). Every journal record carries `g`.

### 4.3 Object namespace

One versioned bucket per instance. The default name derives from the instance ID; a configured name is allowed only
with the ownership marker. Prefixes:

| Prefix | Content | Write rule |
| --- | --- | --- |
| `meta/instance.v1.json` | Instance binding, format identities, creation time | Create-only (`If-None-Match: *`) |
| `meta/fence.json` | Current writer generation | Conditional replace (`If-Match` on the prior ETag) |
| `p/full/<aa>/<sha256>` | Full-fidelity payload bytes | Create-only with a supplied SHA-256 checksum; `<aa>` is the first two hex digits |
| `p/display/<aa>/<sha256>` | Display payload bytes | Same |
| `p/meta/<aa>/<sha256>` | Metadata product bytes | Same |
| `m/<yyyy>/<mm>/<dd>/<groupId>/<revisionId>.json` | Group manifest per revision, partitioned by the group's UTC anchor | Create-only, read back and digest-checked |
| `j/g<g:D6>/<seq:D12>.json` | Journal segment | Create-only, gapless per generation |
| `c/g<g:D6>/<seq:D12>.json` | Journal checkpoint covering `≤ seq` | Create-only |

Content addressing makes retries idempotent and deduplicates shared bytes. It also means one exact version can back
several retained revisions, so expiry must check every retained reference first ([7.3](#73-expiry-protocol)). Prefixes
let Silo replication, lifecycle and future tiering target `p/full`, `p/display`, `p/meta`, `m`, `j` and `c`
separately.
Payload deletes always name a version ID; deletes that only add a delete marker never count as expiry.

### 4.4 Manifests

Each published group revision has one immutable JSON manifest with an explicit format identity, starting at
`hvo.cameraagent.silo.group-manifest/1`. #1967 fixes the schema. It must carry:

- `format`, `instanceId`, `storageBindingId`, `writerGeneration`, `groupKind`, `groupId`, `revisionId`,
  `previousRevisionId`, `publishedUtc`, and UTC bounds `[from, to)`.
- `members[]`: `artifactId`, `artifactRevision`, role, retention class (`full`, `display`, `metadata`), media type,
  length, SHA-256, content and output identity where applicable, producer and recipe identity, and the locator at
  publication.
- `dependencies[]`: `artifactId`, `artifactRevision`, SHA-256, `kind` (`reopen` or `compute`), and `required`. This
  is the exact closure in [6.1](#61-reopen-and-compute-sets).
- Provenance: capture identity descriptor, graph and recipe input identities, rig and site revision, and the import
  source for #1977.

Readers accept additive fields within a format version and refuse an unsupported format without mutating it, as the
POC driver demonstrated. A centered view manifest also carries the base definition and layer list, identified today
as `cameraagent-centered-view-base-v1` and `cameraagent-centered-view-revision-v1`
(`CAC/Gallery/CenteredViewPublication.cs:100`, `:124`).

### 4.5 Durable metadata journal

The journal is the instance-owned record that survives SQLite loss (#1964 design item 1).

| Record | Meaning |
| --- | --- |
| `generation-open` | Starts generation `g`. It names the prior verified cutoff it was rebuilt from and the reason (install, restore, replacement). |
| `publish` | Group revision visible: manifest locator and digest, member digests |
| `live-issue` | Centered stream and camera live interval start, ending the prior interval (`change_order` and UTC) |
| `copy` | Verified copy of a manifest or payload version in a store (local, archive), with verification time and method |
| `remap` | Old locator to new locator after byte verification |
| `pin` and `policy` | Operator pins and retention policy changes, so they survive SQLite loss |
| `expiry-intent` | Locators to delete, policy reason, last-copy flag, and the hold-evaluation cutoff |
| `expiry-disposition` | Observed outcome per locator (`deleted`, `already-absent`, `refused`) |
| `availability` | `policy-expired`, `missing`, `quarantined` or `restored`, with evidence |
| `checkpoint` | Compacted state covering `≤ (g, seq)`, written to `c/` |

- **Ordering.** Records are totally ordered by `(g, seq)`. Sequences are gapless within a generation, and each segment
  names the SHA-256 of its predecessor. Retries reuse their sequence and are idempotent by record identity.
- **Verified complete cutoff.** This is the highest `(g, seq)` whose segments are all present, chain-valid, and whose
  `publish` manifests verify. A rebuild restores the index to that cutoff and reports later or unknown intervals as
  gaps.
- **Retention.** A segment is removed only after a verified checkpoint covering it is itself in backup and, when
  configured, archive coverage. `availability` and `expiry` records are kept as long as the group's display revision
  is retained.
- **Coverage.** The sealed backup ([#1972](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1972)) includes
  `meta/`, `m/`, `j/` and `c/` in full. Archive replication
  ([#1969](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1969)) includes them and the selected `p/` classes.

### 4.6 SQLite catalog

The application-owned SQLite catalog holds mutable admission and processing state and every searchable index. Indexed
SQLite measured 0.009 ms median per lookup on the Pi, against 0.89 ms per S3 JSON read. Its new-layout schema has its
own version identity. It carries no migration from raw schema 17. Rebuildable content must be separable from state
that needs the backup, so a rebuild never discards or rewinds non-rebuildable state. #1967 chooses the physical
database split.

| Catalog content | Rebuildable from Silo | Needs the instance backup |
| --- | --- | --- |
| Groups, members, dependencies, manifests, copies, remaps, availability, expiry dispositions, live-issue history, pins, policy | Yes, to the verified cutoff | — |
| Journal cursor and generation | Recomputed | — |
| Spool and staged work, pending lanes, processing executions and pins, schedules, rig and calibration library state, restore and replay leases | No | Yes |
| Admission journal, sequences, control versions | No | Yes (identity floors, [8.5](#85-request-floors-and-identity-floors)) |

### 4.7 Format identities

Every new format carries an explicit identity: the manifest, journal segment, checkpoint, instance marker, catalog
schema and spool record. An unsupported identity is refused read-only with a clear outcome (#1967 acceptance 5).

## 5. Admission, staging and publication boundaries

### 5.1 Durable admission boundary

The boundary is unchanged: an exposure is admitted when its `raw_captures` row commits after the fsynced payload and
sidecar (`CAC/RawIngress/RawCaptureIngress.cs:496-705`). In the new layout those files land in the spool. Admission
depends on local NVMe, the spool bound and the pause fences. It does not depend on Silo, archive or LogicHost
reachability.

### 5.2 Staged bytes and exact versions

A spool entry moves to Silo by create-only PUT with a supplied checksum, followed by verification of the returned
version ID, length and SHA-256. The staged bytes are released only when all three of these hold:

1. The exact version is verified.
2. The **qualified persistence condition** holds. Either the Silo profile #1966 qualifies (with the measured cost of
   its disk-sync setting, including `MINIO_FS_OSYNC`) guarantees the version through power loss, or a verified second
   copy exists.
3. The group that references it is published, or reconciliation has declared the staged work abandoned and handed it
   to retention.

Until then the bytes count against the spool bound.

### 5.3 Group publication

The steps are: P1 verify payload versions; P2 create the manifest, then read it back and check its digest; P3 append
the `publish` journal record; P4 mark the group visible in SQLite with its journal sequence. Readers only ever see
groups at P4, and P4 requires P3. A stack or view revision publishes all evidence and image planes in one group, so
readers never observe a partial stack (#1968 acceptance 4).

| Interruption | Restart outcome |
| --- | --- |
| Before P2 | Staged work resumes. Uploaded but unreferenced versions stay protected while staged work names them; abandoned ones go to retention as orphan candidates. |
| After P2, before P3 | The manifest is not visible. If staged work still intends that revision, P3 is appended; otherwise the manifest is an orphan candidate. |
| After P3, before P4 | Journal replay after the cursor completes P4. |
| Same revision, different digest | Conflict; refused; nothing replaced. |

Startup work is bounded by staged-work rows plus journal records after the catalog cursor, independent of history
size (F5).

### 5.4 Loss guarantees

| Event | Acknowledged admissions | Published revisions |
| --- | --- | --- |
| Process crash or kill | None lost | None lost; in-flight groups roll forward or stay invisible |
| Host power loss | None lost if NVMe and ext4 honor flush, which #1968 and #1974 must prove by abrupt power loss. Spool copies cover unqualified Silo persistence. | Survive when persistence-qualified; otherwise republished from the spool |
| Silo service down | None lost; admission continues to the spool bound, then a pressure pause | Local reads report temporarily unavailable |
| Archive outage | None lost | Local unaffected; archive receipts pending |
| SQLite catalog loss, Silo intact | None lost when non-rebuildable state survives; otherwise it comes from the instance backup. Unpublished spool sidecars carry identity for recovery (#1972). | Rebuilt to the verified cutoff |
| **Total primary loss**, archive enabled | Admissions after the archive's verified receipt cutoff are lost | Recoverable to that cutoff |
| **Total primary loss**, local only | Admissions after the last sealed backup are lost | Recoverable to the backup cutoff |

The process and power-loss rows are guarantees this contract requires the implementation to prove. The total-loss rows
are bounded losses, measured as recovery point objective (RPO) in [10](#10-budgets) and reported as a verified cutoff
plus gap.

### 5.5 Staging bound and pressure pause

The spool has a configured byte limit, sized from the cadence ([10.3](#103-proposed-budget-targets)). Reaching it, or
the Silo headroom limit, raises a **pressure-owned pause** that blocks only new admission. Clearing it requires
measured headroom recovery and clears only that pause ([8.4](#84-pause-precedence)).

## 6. Dependency closure, availability and reopen

### 6.1 Reopen and compute sets

- **Reopen set R.** This is what is needed to display a published revision exactly as published: its manifest and its
  display and evidence members. For a centered view revision that is the base's `Preview`, `Science` where displayed,
  `ClosedWindow`, each layer's `Evidence`, and the drawables (`CAC/Gallery/CenteredViewPublication.cs:70-81`, `:107`).
- **Compute set C.** This is what is needed to recompute or reprocess it: the raw and linear inputs, `Contributors`,
  registration artifacts, calibration and normalization-plan masters, and recipe inputs (`processing_output_sources`).
- **Closure.** Closures are computed over manifest `dependencies` with the existing safety bounds: depth below 512 and
  cycles refused (`CAC/Capture/Processing/SqliteCaptureProcessingStore.cs:2580` onward). They are exact by
  `(artifactId, revision, sha256)` and deduplicated by exact version.
- **Rules.** A revision is reopenable when every member of R is available. Policy may expire members of C without
  breaking reopen. A reprocess request then names the exact missing inputs (#1971 acceptance 4).

### 6.2 Availability states

Availability is tracked per artifact revision and per copy:

| State | Meaning | Evidence |
| --- | --- | --- |
| `local` | Verified version in the local Silo | `copy` record plus exact-version read |
| `archived` | Verified version in an archive store | `copy` record with archive receipt |
| `pending-archive` | Published locally; archive receipt not yet verified | Copy backlog |
| `temporarily-unavailable` | Store or tier unreachable | Probe failure; never inferred to be missing |
| `policy-expired` | Deleted under policy | `expiry-disposition` plus `availability` record |
| `missing` | Expected version absent or wrong digest with no expiry record | Physical verification failure |
| `quarantined` | Retained but refused for use | `availability` record |

Physical verification reads the exact version, by HEAD with version ID and checksum or by GET with SHA-256. A
metadata-only check is insufficient: the #1059
[negative control](../validation/artifact-history-1059-fault-evidence.md#explicitly-unqualified-acceptance) passed with
a referenced payload deleted.

### 6.3 Issued-live and archive-only revisions

This section maps the revision behavior observed at `87b238c`. The accepted #1922 and #1923 contracts reachable at
the integration source govern where they differ ([9](#9-branch-and-release-strategy)).

- Every revision is published, and therefore reopenable, by `(baseId, revisionId)`. `RegisterBaseAsync` revisions are
  published but not live. `PublishLayerAsync` issues live only for the current base. Late layer revisions on an older
  base are **archive-only**.
- Live issue is a `live-issue` journal record mirroring `processing_centered_view_intervals`. Current Sky is the open
  interval. An archive-only revision never becomes Current Sky retroactively.
- The open live interval and every saved selection (`processing_centered_view_selections`) hold their revision's
  reopen set.
- Expiry of a revision is journaled ([7.3](#73-expiry-protocol)), so its disposition is independent of SQLite.

### 6.4 Local-only and archive-enabled paths

- **Local-only.** Acquisition, publication, display and restore use the local Silo. RPO is the backup interval. Last
  copies expire only under the explicit last-copy policy.
- **Archive-enabled.** Optional one-way versioned replication, with delete-marker, permanent-delete and
  replica-modification replication disabled (#1969). Archive confirmation requires the committed manifest and the
  referenced exact versions and digests, verified at the archive independently of upload order. Local capture and
  display never wait on the archive. Confirmed archive locators stay readable when replication of new data is
  disabled.

## 7. Holds, writer generations, retention and recovery

### 7.1 Hold model

A hold is `(holdId, holderKind, holderRef, target artifactId [revision | any], scope reopen|compute|bytes, expiresUtc?,
createdGeneration)`. It resolves to exact versions only when a deletion is evaluated.

| Source today | New hold |
| --- | --- |
| Raw-ingress holds for committed captures with pending lane work (`CAC/RawIngress/SqliteRawCaptureJournal.cs:827`) | Compute hold on the raw artifact until its lanes complete |
| Processing store holds (`CAC/Capture/Processing/SqliteCaptureProcessingStore.cs:2580`): recent available outputs window, pending standard-lane captures, latest 100 raw, unexpired `centered_exposure_holds` and products, live view revisions and layers, terminal window registration artifacts, unreleased execution input pins, transitive sources | Display hold for the recent window; compute holds for pending lanes, the recent buffer (with expiry), registration and execution pins; reopen hold for live view revisions |
| Clear references (`CAC/Capture/Processing/CameraAgentClearReferenceLoader.cs:10`) | Configuration pin |
| Calibration library, active family masters, frozen normalization-plan masters (`CAC/Capture/Processing/ProcessingRetentionContracts.cs:37-80`) | Compute holds |
| Time-lapse queued inputs (`CAC/TimeLapses/SqliteTimeLapseStore.cs:22`) | Compute hold until the job settles |
| Satellite work (`CAC/Capture/Processing/CaptureSatelliteWorkStore.cs`) | Compute hold |
| Saved view selections | Reopen pin |
| Upload outbox holds (`CAC/Background/RetentionBackgroundService.cs:353`) | None in the standalone profile |
| New: restore and replay leases (#1971), operator pins (#1970) | Lease hold with expiry, revalidated per batch; journaled pin |

Holds stored in SQLite are State and covered by the backup. Pins and policy are also journaled.

### 7.2 Writer generations and fences

- Before any admission, publication or expiry, a primary writes `generation-open` and advances `meta/fence.json` to
  its generation by conditional replace.
- Before each expiry batch and each publish append, the writer re-reads the fence and stops if a newer generation
  exists. The batch size bounds the remaining window.
- Installer fencing stops a superseded container first. The fence is defense in depth, so that only one primary acts
  on restored storage (#1972 acceptance 2).
- Recovery onto empty storage, or onto a replacement host or daemon, advances the generation. Sequences and versions
  issued by an earlier generation are never reissued.

### 7.3 Expiry protocol

1. Dry run: deterministic candidates and reasons from per-role policy and pressure (#1970).
2. Resolve holds and every retained reference to each candidate exact version. Shared content-addressed versions are
   deleted only when no retained revision references them.
3. Append `expiry-intent`. A last copy requires the explicit last-copy policy. Eviction of a local copy requires a
   verified colder copy that survives the deletion.
4. Delete exact versions by version ID.
5. Verify absence by HEAD with the version ID, then append `expiry-disposition` and `availability policy-expired`.
6. Add the bytes to reclaim-pending accounting until physical recovery ([7.4](#74-physical-reclamation)).

A crash between steps re-checks version presence and completes idempotently. A repeated delete is `already-absent`.
New expiry decisions wait under operator and installer pauses, as today. Under a pressure pause, expiry and reclamation
continue, because that is how the pause clears.

### 7.4 Physical reclamation

Measured facts:

- **POC prune** (Pi, 1,017,004,032 logical bytes): 288 exact-version deletes returned in 0.541 s. Allocated bytes
  fell by 1,167,360 (0.11 %), while Silo trash grew from 53,248 to 1,019,895,808 bytes. It was unchanged after the
  replica restore that followed.
- **#1965 probe, default** (amd64, pinned 20261112, 64 MiB): 8 exact-version deletes returned in 390 ms. The bucket
  directory dropped to 4 KiB, but `.minio.sys/tmp/.trash` held 65,644 KiB and allocation stayed at about 65,724 KiB.
  Silo freed it 298.4–303.5 s after the deletes returned, consistent with a trash cleanup period of about five
  minutes. One observation cannot separate the period from its phase.
- **#1965 probe, kill and restart:** SIGKILL immediately after the same deletes, then restart on the retained volume.
  Startup purged the trash; allocation was back to 84 KiB 899 ms after the deletes returned.

Contract:

- Reclamation is measured as allocated or available bytes on the Silo data filesystem (statfs) plus Silo trash size.
  It is never inferred from delete success.
- Reclaim-pending bytes count as used. They are bounded ([10.3](#103-proposed-budget-targets)).
- If sweeps make no physical progress, further expiry stops and the pressure pause holds admission.
- Restart is a valid reclamation trigger. It is never the routine mechanism.

### 7.5 Recovery

Recovery restores the State backup (with its own identity floors), rebuilds the catalog from `c/` and `j/` to the
verified cutoff, verifies or remaps exact versions, advances the generation, keeps admission paused and holds
replication and expiry. It reports the verified cutoff and gaps. Expiry stays held after recovery until the operator
confirms, because holds created after the backup cutoff are unknown. The canary keeps expiry held from the start (#1970,
#1975).

## 8. Installation and cutover

### 8.1 Clean target and first boot

- **Clean roots.** The installer verifies empty, instance-owned State and Silo roots, a versioned bucket, and the
  `meta/instance.v1.json` binding before initialization (#1966).
- **No central dependency.** Central integration, upload and environmental delivery stay disabled
  (`DCLI/ComposeDeployment.cs:306-308`). Readiness never requires LogicHost. The canary verifies the absence of central
  traffic (#1975).
- **Persisted paused admission.** The first boot must start paused. Before the container first starts, the installer
  must seed an installer-owned pause with a known command identity and version, so a version-0 `Running` state never
  exists. It must not rely on the provisioning gate, which the installer does not set today (F6). The pause must survive
  startup, owner bootstrap and restart until an authorized resume names its expected version.
- **Device allocation.** Because the module initializes while paused (F7), the real camera is mapped to the candidate
  only after the former owner's container is removed and no process holds the device. #1966 documents that boundary.

### 8.2 Capture handoff

1. Persist the former instance's pause and record it as the **handoff pause**: command identity, actor, version and
   UTC time, read back from `capture_control_commands` (`CAC/RawIngress/SqliteRawCaptureJournal.cs:2596`). Then run
   the data-preserving `hvo-sky-install cameraagent uninstall` (`DCLI/CameraAgentLifecycleManager.cs:1403`, which
   pauses and drains at `:1440`), or the site equivalent, and record its lifecycle operation ID.
2. Confirm the camera is released and the former container is inactive after a restart.
3. Start the candidate paused. Verify authenticated readiness, camera and rig, catalog, endpoint, storage binding and
   absence of central traffic.
4. Resume once with the expected version. Exactly one capture owner remains.

### 8.3 Early-abort ordering

| Step | Action | Pass condition |
| --- | --- | --- |
| E1 | Stop the candidate container with no restart policy | Container exited; no candidate process holds the camera; admission persisted `Paused`, or the container is absent |
| E2 | Read the stopped candidate's durable admission ledger read-only, including its WAL: copy the database with its `-wal` and `-shm` files and open the copy | `raw_captures` has 0 rows; no reserved `raw_capture_sequences`; spool empty; no `publish` record in its journal. Record the query results, the ledger file SHA-256 and the UTC time. |
| E3 | Prove that restoring active capture on the former instance overrides nothing. Read its control ledger (`capture_control_state`, `capture_control_commands`) read-only the same way, check the camera device, and record the authorization. | All of these hold: (a) the accountable operator or site has recorded positive authority to restore the former instance as the active capture owner, with who, when and why; (b) the former instance is `Paused` at the handoff pause's version, and the only accepted command after the handoff pause is the uninstall operation's own lifecycle pause, so there is no newer operator pause and no unknown or unreadable state; (c) no process or other instance holds or maps the camera, and the candidate's device mapping is removed. Record each result and the UTC time. |
| E4 | Only after E1–E3 pass, run `hvo-sky-install cameraagent reinstall` for the former instance | This call **resumes capture automatically** (F8). Its plan output does not show the resume. |
| E5 | Verify the outcome | Former instance `Running` with one owner; candidate stopped, its data preserved for closeout |

- **E2 failure.** If E2 finds an admission, or cannot prove zero, do not run E4. The candidate owns new data. Recover
  forward with new-layout recovery (#1972) and escalate.
- **E3 failure.** Without authority, with a newer operator pause, with an unknown or unreadable control state, or with
  another device owner, do not run E4. Keep admission and the device fenced: the candidate stays stopped and paused,
  the former instance stays uninstalled, and the camera stays unmapped from both. Record the refusal and escalate the
  decision to the operator ([14](#14-remaining-decisions)).
- **Acceptance test (#1975 rehearsal and any automation).** Assert:
  1. The E2 and E3 records precede the reinstall operation's begin record.
  2. Reinstall is never invoked when E2 fails or counts at least one admission.
  3. Reinstall is never invoked when E3 lacks authority, finds a command newer than the handoff pause other than the
     uninstall's own pause, cannot read the former control state, or finds another device owner. Both instances stay
     fenced. The #1975 rehearsal exercises at least the newer-operator-pause case.
  4. After an authorized reinstall, the former instance's admission is `Running` with one owner.
  5. Faulting the candidate right after its first durable admission makes E2 fail and routes to forward recovery.

### 8.4 Pause precedence

- There are three pause owners: installer (a lifecycle operation), operator (a UI or API actor) and pressure (storage).
  Clearing one never clears another.
- An installer resume names its own pause's expected version and fails when a later accepted command, such as a newer
  operator pause, superseded it. Today that protection exists only through the control-plane receipt path
  (`CAC/Capture/CaptureAdmissionCoordinator.cs:426-433`, `:815-818`). The lifecycle resume used by reinstall and
  upgrade sends no expected version (F8). The Silo first-boot resume (#1966) must use the expected-version form.
- Legacy reinstall lifts any persisted pause (F8). In an early abort, E3 therefore admits it only with positive
  authority and only when the former instance's pause is exactly the recorded handoff pause. Otherwise both instances
  stay fenced. Restoring the former instance *paused* is not supported by reinstall at `87b238c`; see
  [14](#14-remaining-decisions).
- The pressure pause follows [5.5](#55-staging-bound-and-pressure-pause) and [7.3](#73-expiry-protocol).

### 8.5 Request floors and identity floors

- **Provider floors and halts.** The orbital two-hour floor and halt and the SWPC Retry-After, backoff and halts live
  in the former instance's `orbitalElements` and `spaceWeather` stores, which are never rewound
  ([install contract](../hvo-install-contract.md)). The candidate starts with empty stores and providers disabled by
  default. It must not make its first request to a provider the former instance used until one of these holds:
  1. A read-only floor capsule from the former stores is imported, carrying the attempt floor, Retry-After deadline
     and halt flags.
  2. The operator records that the provider was disabled on the former instance, or that its floor has elapsed and no
     halt is active.

  On early abort, the former stores are intact and reinstall does not rewind them.
- **Identity floors.** The installer gives the candidate a new application identity, which is its `AgentId`
  (`DCLI/CameraConfiguration.cs:88`). Capture IDs and sequences therefore start a new namespace that cannot collide
  with the former instance's. Within the new instance, counters and control versions follow
  `DCLI/CameraAgentStateCounters.cs:8-19` and are carried through new-layout backup and recovery (#1972).

### 8.6 Optional historical import (#1977)

Import runs only after #1968 and publishes through [5.3](#53-group-publication) with `groupKind=import`. Its stable
import identity is a hash of the source binding, source artifact ID and SHA-256. Source IDs, timestamps and digests are
kept as provenance only, never as new sequences. Settings, schemas, credentials, journals and in-flight work are not
imported. Imported bytes count against headroom, and the dry run reports which inputs the target policy would expire
immediately. Imported groups then follow ordinary retention, backup and restore.

## 9. Branch and release strategy

- **Branch.** Implementation targets `development/v1` with no long-lived branch. New-layout code is selected only by
  the new state contract at clean install. Filesystem installs keep today's behavior, and preflight refuses in-place
  change.
- **First release profile: centered.** This was settled by Roy on 2026-11-22 ("Hold the first release for centered").
  No non-centered Silo profile is offered for the first release. Because the installer defaults set no
  `CenteredExposure` at `87b238c`, the clean-install path sets the centered configuration explicitly as site
  configuration (#1966, #1975).
- **Launch hold.** The first Silo release launches only when the exact integration source that #1968 consumes and
  #1974 verifies contains:
  1. The accepted #1922 contract for single and stack view publication and independently published layer revisions.
  2. The accepted #1923 contract for exact archive reopen and Current Sky advance.
  3. The relevant #1928 qualification cases passing at that source.

  The release record names the merge SHAs that accepted them, the integration SHA, and the image and artifact
  digests #1974 qualified.

| Source state | Contents relevant to this contract | Use |
| --- | --- | --- |
| Observed at `87b238c` | Centered admission (#1919) and five-slot windows (#1921) delivered; the view base, revision, interval and selection store and the archive copy stage and receipt exist (3.2); centered mode is opt-in, and the installer defaults set no `CenteredExposure` (`DCLI/CameraConfigurationDefaults.cs`) | Basis for this contract's centered seams and for the #1966 and #1967 designs |
| Parked: draft PR [#1963](https://github.com/HualapaiValley/HVO.SkyMonitor/pull/1963) (head `5d87bdee`, #1922) and #1923; #1928 open | Not accepted | Not relied on; not restarted, adopted or implemented by #1964 work |
| Accepted at the integration source | Merged #1922 and #1923 contracts and relevant #1928 qualification | Required before launch: #1968 consumes it and records any delta from 3.2 and 6.3; #1974 verifies it with merge and artifact evidence |

- **Work that proceeds now.** #1965 (this contract), #1966 runtime and #1967 catalog do not depend on centered
  acceptance. #1968 may develop against the seams observed at `87b238c`. Only an integration source that meets the
  launch hold can qualify the release.
- **Seam for #1918.** The centered pipeline epic keeps admission, window, registration and revision-order semantics.
  This contract replaces only bytes-at-path with exact versions at these points: `SaveArchiveAsync` and
  `PublishCommittedArchiveAsync`, `centered_exposure_holds`, view revision tables, and `CenteredViewArtifact`
  references.
- **Seam for #1300.** Sky-condition policy supplies each artifact's retention class and selection at publication
  (daytime presentation-only, detection-gated raw, compressed previews). This contract supplies physical retention,
  pressure fallback, holds and the `policy-expired` disposition.

## 10. Budgets

### 10.1 Workload basis

| Basis | Value | Source |
| --- | --- | --- |
| Cadence | 20 s: 4,320 captures per day, 30,240 per week per camera | [#1974](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1974) fixture |
| POC capture size (raw plus preview) | 7,062,528 B; 2.83 Mb/s at cadence; 30.51 GB per day | 144 captures, 1,017,004,032 B ([#1964 POC](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1964#proof-of-concept-evidence)) |
| W6 raw size | 25,233,408 B; 10.09 Mb/s at cadence; 109.0 GB per day | [W6](performance-validation.md) |

### 10.2 Measured values

Unless a row names another platform, POC values were measured on the POC's ARM64 Raspberry Pi (ext4 NVMe) with Silo
20261112. All are single short batches, not percentiles under sustained load. *Published* marks values that appear in
the [#1964 POC](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1964#proof-of-concept-evidence) section. *POC run* marks values from the same bounded runs that the epic does not publish.

| Measurement | Value | Source |
| --- | ---: | --- |
| TCP, Pi to development host | 936.38 Mb/s | Published |
| Direct Silo GET over that route | 904.8 Mb/s (96.6 % of TCP) | Published |
| Local PUT, 1 worker / 8 workers | 1,038 / 1,580 Mb/s | Published |
| Local GET | 1,972 Mb/s | Published |
| Serial import with readback | 312 Mb/s committed; 0.18 s per capture; 720 PUT, 1,440 GET | Published; per-capture time derived |
| Replication to archive / replica restore | 839.8 / 820.9 Mb/s | POC run / published as about 821 |
| Exact-version prune | 0.541 s for 288 versions; immediate physical reclaim 0.11 % | POC run; published as about 1.02 GB left in trash |
| Trash reclamation, default / after restart | 298.4–303.5 s / 0.9 s (amd64, 64 MiB) | #1965 probe ([13](#13-evidence-and-reproduction)) |
| Silo replacement on a retained volume | 2.35 s amd64 / 2.61 s Pi (stop through verified old-version read) | POC run |
| Replica catch-up after an outage | 2.14 s, one object (amd64, two hosts) | POC run |
| Metadata read: SQLite indexed / S3 JSON GET | 0.009 / 0.89 ms p50 | Published / POC run |
| Lifecycle, small state, fake clients | 0.06–0.38 s | [#1615](../validation/upgrade-recovery-cost-1615.md) |
| Installer budgets | Drain 2 min, read 15 s, owner read 120 s | `DCLI/CameraAgentLifecycleClient.cs:43-44`; `DCLI/OwnerBootstrapClient.cs:53` |

### 10.3 Proposed budget targets

These are targets for #1968 and #1974 to measure on the production configuration. None is a measured result. For
the centered profile, #1974 measures them with the accepted #1928 workload at the integration source. Centered
timing semantics come from those accepted contracts, not from this document.

| Budget | Proposed target | Derivation |
| --- | --- | --- |
| Cadence | 20 s sustained for the #1974 week; zero missed admissions attributable to storage | Local PUT exceeds W6 ingest about 103 times |
| Publication latency (admission commit to group visible) | p95 ≤ 5 s, p99 ≤ 10 s at W6 size on the Pi | W6 at the serial committed rate is 0.65 s; the targets are at most 25 % and 50 % of the interval |
| Spool | Steady backlog ≤ 2 captures (p99). Bound ≥ 2 h at cadence (2.54 GB POC, 9.08 GB W6 raw). After an outage of D, drain within max(60 s, 0.1 D). | Cadence × size; ingest headroom at least 100× |
| Switchover pause (former owner's last admission to candidate's first) | Automated steps ≤ 5 min: drain ≤ 2 min, candidate ready (paused) ≤ 2 min, resume ≤ 15 s. Attended total recorded, ≤ 30 min for the canary. | Installer budgets; Silo replacement about 2.6 s |
| RPO, archive enabled | Verified receipt lag p99 ≤ 5 min on a nominal link | Replication exceeds W6 ingest about 83 times; catch-up 2.14 s |
| RPO, local only | Sealed backup interval ≤ 24 h, as configured by the operator | Bounded loss accepted by local-only sites |
| RTO, empty storage to capture resumed | ≤ 30 min for one week of history, independent of payload volume | 30,240 manifests × 0.89 ms ≈ 27 s; payloads restore lazily |
| Full payload restore | ≥ 70 % of same-route TCP (≥ 655 Mb/s): one POC day in about 297 s, one W6 raw day in about 1,062 s | Measured 820.9 Mb/s |
| Concurrent restore and LAN | Hour, day and week restores at ≥ 655 Mb/s while capturing, with publication latency in budget and zero missed admissions | Measured GET 96.6 % and restore 87.7 % of TCP, without concurrent capture |
| Physical reclamation | ≥ 95 % of expired logical bytes reclaimed within 10 min | Twice the observed ~5 min trash period |
| Reclaim-pending bound | ≤ min(5 % of the Silo filesystem, 1 h of capture) | Bounds unreclaimed trash at cadence |
| Startup readiness | ≤ 60 s at any history size | Bounded by staged work plus the journal after the cursor (F5) |
| Peak disk | Headroom reserve covering the spool bound, the reclaim-pending bound, catalog and journal growth for 24 h, the restore cache limit, and a capture reserve | Explicit sum checked by #1974 |

## 11. Verification design

### 11.1 Clean-install matrix

Run on amd64 and ARM64, each in local-only and archive-enabled profiles, with the centered profile at an integration
source that meets the launch hold ([9](#9-branch-and-release-strategy)):

- Fresh-root and binding refusal.
- Paused first boot through restart and owner bootstrap.
- Authorized resume.
- First exposure settled and published as single and stack view revisions, including a late layer revision.
- Exact archive reopen by `(baseId, revisionId)`, and Current Sky advance, under the accepted #1923 contract.
- No central traffic.
- Restart with staged work.
- Stock Silo replacement on the retained volume.
- Cold boot with an unreachable archive, showing degraded archive status while local capture and playback continue.

### 11.2 Fault matrix

| Fault | Expected | Owner |
| --- | --- | --- |
| Kill before identity reservation; after payload, before journal commit | No identity consumed; one capture after reconciliation | Existing; #1968 |
| Kill after admission, before PUT; after PUT, before verify | Spool retained; one exact version; no duplicate | #1968 |
| Kill at each publication step P1–P4 | Visibility only after P3; table in [5.3](#53-group-publication) | #1967, #1968 |
| Fault between staging stack planes and group commit | Readers see only settled stacks | #1968 |
| Abrupt host power loss after acknowledged admission | Admission survives | #1968, #1974 |
| Silo down during capture | Spool grows, then pressure pause; drains on recovery | #1968, #1970 |
| Wrong digest or missing version with a valid catalog (#1059 negative control on the new layout) | Detected as `missing` by physical verification | #1967, #1968 |
| Expiry interrupted after intent or after delete | Idempotent completion; `policy-expired` | #1970 |
| Stale generation attempts expiry or publication after recovery | Refused by the fence | #1972 |
| SQLite catalog deleted | Rebuild to cutoff with stable IDs, previews, original view revisions, and expired versus missing | #1967 |
| Early abort with zero admissions, authority and only the handoff pause | Reinstall after E1–E3; former instance `Running` with one owner | #1975 |
| Early abort after a durable admission | Reinstall refused; forward recovery | #1975 |
| Early abort without authority, with a newer operator pause, unknown control state or another device owner | Reinstall refused; admission and device stay fenced | #1975 |
| Wrong endpoint or bucket | Refused before mutation | #1966 |
| Newer operator pause than the installer pause | Installer resume refused | #1966 |
| Trash cleanup stalled | Reclaim-pending bound reached; pressure pause; no assumed space | #1970 |

### 11.3 Focused acceptance

Acceptance uses disposable volumes, bounded sizes and dry runs. The only long campaign is #1974's defined week. No
test operates on live storage or alters published evidence.

## 12. Downstream contract slices

| Issue | Implements | Can start |
| --- | --- | --- |
| [#1966](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1966) runtime | Pinned stock Silo, bucket, marker, fence prefix, versioning, sync setting and cost for 5.2, private listeners, credentials, binding preflight, paused first boot with expected-version resume, device allocation (2, 4.3, 5.2, 8.1, 8.4) | Now; independent of #1967 |
| [#1967](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1967) catalog | Identities, manifests, journal, catalog, format identities, crash table, availability, rebuild (4.2–4.7, 5.3, 6.2) | Now; any S3 fixture; independent of #1966 |
| [#1968](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1968) store | Every 3.2 disposition, spool, bounded startup, persistence-qualified release, power-loss proof (3, 5). Records the delta between the accepted centered contracts at its integration source and 3.2 and 6.3. | After #1966 and #1967; release integration needs the launch hold (9) |
| [#1969](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1969) replica | Copy records, archive confirmation, coverage of `m/`, `j/`, `c/` (4.5, 6.4) | After #1968 |
| [#1970](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1970) retention | Holds, expiry protocol, reclamation, pressure pause (5.5, 7.1–7.4) | After #1968 |
| [#1971](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1971) restore | Reopen and compute closure, leases, cache (6) | After #1968 |
| [#1972](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1972) recovery | Generations, fence, cutoff, remap, floors, held expiry (4.5, 7.2, 7.5, 8.5) | After #1968 |
| [#1973](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1973) operations | States, pauses, alarms against budgets (5.5, 6.2, 10) | After #1968 |
| [#1974](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1974) qualification | Budgets and matrices (10, 11) with the centered profile at the exact integration source. Records the #1922 and #1923 merge SHAs, the relevant #1928 cases, the integration SHA and artifact digests. | After the storage path and the launch hold (9) |
| [#1975](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1975) canary | Handoff, early abort, pauses, floors (8.2–8.5) | After #1974 |
| [#1976](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1976) closeout | Former-data disposition (2) | After #1975 |
| [#1977](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1977) import (optional) | 8.6 | After #1968, only if selected |
| [#1922](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1922), [#1923](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1923), [#1928](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1928) (outside this epic) | Launch-hold inputs only (9) | Owned by epic #1918, with draft #1963 and #1923 parked; not restarted or adopted by #1964 work |

## 13. Evidence and reproduction

- **Source anchors.** Every `path:line` anchor in this document was resolved at `87b238c`. The candidate handoff
  records the check.
- **POC.** The [#1964 POC section](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1964#proof-of-concept-evidence)
  publishes the setup and headline results. Values marked *POC run* in [10.2](#102-measured-values) come from the same
  bounded runs. The #1965 handoff lists their retained records and digests.
- **#1965 reclamation probe.** One run per variant on amd64 with the pinned Silo 20261112 image, an isolated container
  with no network, a disposable volume and 8 × 8 MiB random objects. Results are in
  [7.4](#74-physical-reclamation). To reproduce with Docker, `jq` and OpenSSL:

```bash
img=pgsty/silo@sha256:c20d112390b9a9ce8c11b8af95ad35bcaa62f4759663c09c936cd8d2b511f329
run="reclaim-$RANDOM"; user=probe; pass="$(openssl rand -hex 16)"
docker volume create "$run"
docker run -d --name "$run" --network none -v "$run:/data" -e MINIO_ROOT_USER="$user" \
  -e MINIO_ROOT_PASSWORD="$pass" -e MINIO_BROWSER=off "$img" server /data --address 127.0.0.1:9000
box() { docker exec "$run" sh -c "$1"; }
until box "mcli alias set p http://127.0.0.1:9000 $user $pass" >/dev/null 2>&1; do sleep 1; done
box 'mcli mb --with-versioning p/reclaim'
box 'for i in 1 2 3 4 5 6 7 8; do head -c 8388608 /dev/urandom > /tmp/o$i; mcli cp -q /tmp/o$i p/reclaim/o$i; done'
box 'mcli ls --versions --json p/reclaim' | jq -r 'select(.type == "file") | "\(.key) \(.versionId)"' |
  while read -r key version; do box "mcli rm --version-id $version p/reclaim/$key"; done
# Kill variant only: docker kill --signal KILL "$run" && docker start "$run"
for _ in $(seq 1 120); do box 'du -sk /data /data/.minio.sys/tmp/.trash'; sleep 5; done
docker rm -f "$run" && docker volume rm "$run"
```

## 14. Remaining decisions

Settled: the first Silo release uses the centered profile, and its launch is held as described in
[9](#9-branch-and-release-strategy). Roy decided this on 2026-11-22.

| Decision | Recommendation | Decides |
| --- | --- | --- |
| Restoring the former instance paused on early abort, which reinstall cannot do today (8.3, 8.4) | Accept that an authorized abort restores the former owner as the active writer, and that a failed E3 keeps both instances fenced. Otherwise file an installer change to honor a persisted pause. | Roy, before #1975 |
| Provider floors: capsule import or hold until elapsed (8.5) | Keep providers disabled on the candidate until the operator records that floors elapsed and no halt is active. Import a capsule only if a site needs provider data immediately. | Roy, #1966 or #1975 |
| Spool release versus the Silo sync setting (5.2) | Keep the spool copy until #1966 measures the `MINIO_FS_OSYNC` cost; then choose per profile | #1966 evidence |
| Proposed budget targets (10.3) | Accept as the qualification bar or revise | Roy, before #1974 |
| Roadmap entry for #1964 | Add an index row when the epic is scheduled | Coordinator |
