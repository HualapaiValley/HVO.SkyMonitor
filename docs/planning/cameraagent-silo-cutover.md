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
| D3 | Exposure admission keeps its local durability boundary, and Silo reachability is not on the admission path. A group becomes visible only after every exact locator its committed records name (required payloads, manifest and `publish` record) is itself persistence-qualified in its own store and verified by exact-byte reads. A copy elsewhere is custody of the bytes, never qualification of the named locator. After P3, reads use the named locator or a committed eligible copy or remap of the same member. | [5](#5-admission-staging-and-publication-boundaries) |
| D4 | Committed publication, live issue, copy, custody, remap, pin, policy and expiry records live in a generation-ordered journal inside the instance's own bucket. It is authoritative for rebuilding the SQLite catalog, and compaction removes segments only behind a verified, backed-up anchor. | [4.5](#45-durable-metadata-journal) |
| D5 | Each published revision has a reopen set, which is needed to display it at its original revision, and a compute set, which is needed to recompute it. Policy may expire compute inputs without breaking reopen. | [6](#6-dependency-closure-availability-and-reopen) |
| D6 | Holds name logical identities and point only from consumers to inputs, so they cannot cycle. They resolve to exact versions at deletion time and carry across writer generations and recovery. Evicting a local copy because a colder copy survives requires a non-expiring hold on that exact survivor until each local delete is conclusively resolved; otherwise only role-scoped last-copy authorization permits the delete. | [7](#7-holds-writer-generations-retention-and-recovery) |
| D7 | Physical proof of a version reads its exact bytes and computes length and SHA-256 independently. Expiry is complete only when space is physically recovered. Delete success and metadata HEAD prove neither. Expiry receipts are scoped to their exact copy, and a role is `policy-expired` only under role-scoped authorization. | [6.2](#62-availability-states), [7.4](#74-physical-reclamation) |
| D8 | First boot has no central dependency. Capture admission is persisted as paused before the first start, and the camera device reaches the candidate only after the former owner releases it. | [8.1](#81-clean-target-and-first-boot) |
| D9 | Legacy `cameraagent reinstall` cannot hold control authority or camera exclusivity across its own restart and automatic resume. An early abort therefore stops and fences the candidate, proves zero admissions, and leaves the former instance and the camera fenced until an authority-preserving restore route exists. | [8.3](#83-early-abort-ordering) |
| D10 | A resume is accepted only when its own pause is still the latest accepted command, so newer operator pauses win, including no-op pauses. A pressure pause is a separate fence. Provider acquisition stays off on the candidate through the abort window, and non-rewindable provider floors and halts are never reset. | [8.4](#84-pause-precedence), [8.5](#85-request-floors-and-identity-floors) |
| D11 | Settled by Roy: the first Silo release uses the centered profile. Its launch is held until accepted #1922 and #1923 contracts and the relevant #1928 qualification are reachable at the exact integration source #1968 consumes and #1974 verifies. #1965, #1966 and #1967 proceed now. | [9](#9-branch-and-release-strategy) |
| D12 | Budgets in this contract are proposed targets. They are kept separate from measured values and attributed to their sources. | [10](#10-budgets) |
| D13 | Recovery evaluates every expected role against every surviving source and claims only what it has verified. It proves the highest issued writer generation from a complete generation witness set, carries every issued-identity floor, and keeps admission fenced while that proof is missing. | [5.4](#54-loss-guarantees-and-recovery-cutoffs), [7.5](#75-recovery) |

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
- **Supported topology.** Each instance runs one CameraAgent container and one stock Silo container on the same Linux
  host and Docker daemon, on amd64 or ARM64 (the POC's Raspberry Pi 5 class). Each has its own instance-owned root on
  local ext4 NVMe: the CameraAgent state root holds SQLite, the spool and the cache, and the Silo data root holds the
  bucket. The Silo S3 listener is reachable only on the instance's private container network and publishes no host port
  except an authorized maintenance route; its console is off. The optional archive is a second stock Silo on another
  host, reached over authenticated TLS on a private link. One CameraAgent writer owns each bucket. Remote, shared and
  non-ext4 filesystems are unsupported. #1966 implements and verifies this topology.
- **Release record and artifact supplier.** The clean-install release's `release.json` and `SHA256SUMS` name the
  qualified artifacts ([install contract §4](../hvo-install-contract.md#4-release-assets)). They pin the stock Silo
  image per platform by digest beside the CameraAgent image. Online installs pull by digest. Offline installs use the
  release's `image-archive` assets for both images and both architectures. The accountable supplier is the HVO release
  owner who cuts the release ([install contract §3](../hvo-install-contract.md#3-cutting-a-release)). #1966 verifies
  the digests and the Silo license and redistribution obligations, and #1974 qualifies the exact record. If #1966 finds
  that the Silo image cannot be redistributed, the record still pins the upstream digest and names the upstream
  registry as the online supplier; offline sites then stage that exact archive and verify it by digest.
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
| F11 | The capture-sequence allocator increments a per-agent counter in its own transaction before payload publication (`CAC/RawIngress/SqliteRawCaptureJournal.cs:426-481`). A retry of the same capture reuses its assignment (`:437`). The counter lies outside any journal generation. | Recovery must carry identity floors and fence admission while they are unproven ([5.4](#54-loss-guarantees-and-recovery-cutoffs)). |
| F12 | A pause that finds admission already paused commits a completed command with `changed = 0` and an unchanged version (`CAC/Capture/CaptureAdmissionCoordinator.cs:821-830`). Only the owned-pause check reads the latest command, inside the resume transaction (`:815-818`, `:896-911`). | Resume authority needs that latest-command check, not the version alone ([8.4](#84-pause-precedence)). |
| F13 | Silo's HEAD returns checksums from stored object metadata without reading the bytes ([Silo 20261112 `object-handlers.go`](https://github.com/pgsty/silo/blob/RELEASE.2026-11-12T00-00-00Z/cmd/object-handlers.go#L1111-L1115)). | Physical proof needs an exact-version GET ([6.2](#62-availability-states)). |
| F14 | A versioned Silo PUT without a supplied version ID always allocates a new version ID ([Silo `erasure-object.go`](https://github.com/pgsty/silo/blob/RELEASE.2026-11-12T00-00-00Z/cmd/erasure-object.go#L1515-L1518)). | Replaying exact bytes after a lost version yields a different locator ([5.3](#53-group-publication)). |
| F15 | Silo authorizes a delete when the request arrives ([`object-handlers.go`](https://github.com/pgsty/silo/blob/RELEASE.2026-11-12T00-00-00Z/cmd/object-handlers.go#L3296-L3313)) and takes the object lock later ([`erasure-server-pool.go`](https://github.com/pgsty/silo/blob/RELEASE.2026-11-12T00-00-00Z/cmd/erasure-server-pool.go#L1248-L1259)). | A client timeout or a credential change cannot prove that an accepted delete has finished ([7.2](#72-writer-generations-and-fences)). |
| F16 | SWPC prunes completed attempt rows after 35 days (`CAC/SpaceWeather/Storage/SqliteSwpcStore.Retention.cs:38-46`), while provider and product halts, checkpoints and last attempts live in `swpc_meta` and `swpc_products` (`CAC/SpaceWeather/Storage/SqliteSwpcStore.Schema.cs:11`, `:15`). Orbital keeps its floor and halt in `orbital_provider_state` (`CAC/Orbital/SqliteOrbitalElementsStore.Schema.cs:77-90`). | An empty attempt table does not prove that no provider fact exists ([8.5](#85-request-floors-and-identity-floors)). |
| F17 | The layer validator requires `Ready`, `Partial` and `AssessedEmpty` layers to bind a committed evidence product, and accepts an evidence identity on any layer (`CAC/Gallery/SqliteCenteredViews.Validation.cs:136-140`). | Evidence bindings are kept even where reopen needs no bytes ([6.1](#61-reopen-and-compute-sets)). |

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
  bytes verify by length and SHA-256. Logical IDs, revisions and digests never change. Manifests stay immutable. A
  committed remap names the new exact locator, which must itself be persistence-qualified before readers use it; until
  then readers of that member stay closed. Publication never remaps ([5.3](#53-group-publication)).
- **Eligible locator.** After P3, a required member may be read from its named locator while that copy is not
  evicted. It may also be read from a previously committed `copy` or `remap` entry on the same validated journal chain
  within the verified cutoff, bound to the same immutable manifest or member identity, revision, length and SHA-256.
  Matching keys or hashes alone never make a locator eligible. Reading through an eligible copy never rewrites the
  immutable identity or the original P3 facts.
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
| `meta/fence.json` | Current writer generation and its credential identity | Created once at install with `If-None-Match: *` as generation 1; later advanced only by conditional replace (`If-Match` on the prior ETag) during takeover ([7.2](#72-writer-generations-and-fences)) |
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
| `generation-open` | Starts generation `g` after takeover ([7.2](#72-writer-generations-and-fences)). It names the trusted anchor and cutoff it was rebuilt from, the highest prior generation and its evidence, the identity floors and the reason (install, restore, replacement). |
| `publish` | Commit point (P3): immutable logical revision, manifest locator and digest, required member identities with lengths and digests, the original persistence qualification of each exact named locator, and for a capture its agent ID and capture sequence. P4 indexes this same committed sequence. |
| `live-issue` | Centered stream and camera live interval start, ending the prior interval (`change_order` and UTC) |
| `copy` | Verified copy of a manifest or payload version in a store (local, archive), with custody, verification time and method. A copy is custody of bytes; it never qualifies another locator. Bound to the same member identity, revision, length and SHA-256, it makes that exact locator eligible for reads after P3 ([4.2](#42-logical-identities-and-exact-versions)). |
| `reserve-survivor`, `release-survivor` | Non-expiring legal hold on an exact colder version for a copy-scoped local eviction, held until each local delete is conclusively resolved ([7.3](#73-expiry-protocol)) |
| `remap` | Old locator to new locator after byte verification; persistence-qualified before readers use it, and then eligible ([4.2](#42-logical-identities-and-exact-versions)) |
| `pin` and `policy` | Operator pins and retention policy changes, so they survive SQLite loss |
| `expiry-intent` | Exact copy locators to delete, scope (`copy` eviction, or `role` expiry with its authorization), per-copy custody, policy reason, last-copy flag and the hold-evaluation cutoff |
| `expiry-disposition` | Terminal outcome of one delete attempt on one exact copy: `deleted` or `already-absent` (absence proven), `not-deleted` (present and quiesced) or `refused`. It never states a role outcome. |
| `availability` | Copy-scoped (`evicted`, `missing`, `quarantined`, `restored`) or role-scoped (`missing`, `conflict`, or `policy-expired` only with a role-scoped `expiry-intent`), with evidence. `temporarily-unavailable` is a retried state, never a terminal record. |
| `checkpoint`, `anchor` | Compacted state covering `≤ (g, seq)` in `c/`, and the record that makes it a trusted anchor |

- **Durable commit.** A segment counts as committed only when its exact version is persistence-qualified
  ([5.2](#52-staged-bytes-and-the-persistence-condition)) and read back by exact-version GET with matching length and
  SHA-256. Until then its bytes stay in the spool, and no visible group depends on it.
- **Exact-byte replay.** A segment's bytes are fixed and written to the spool before its first PUT. A retry after an
  uncertain write re-sends those bytes. A create-only conflict is resolved by reading the existing version and
  comparing SHA-256: equal means the earlier write succeeded; different is a conflict that stops the writer. A
  sequence is never reused for different bytes. Manifests follow the same rule.
- **Ordering.** Records are totally ordered by `(g, seq)`. Sequences are gapless within a generation, and each segment
  names the SHA-256 of its predecessor.
- **Trusted anchors.** A checkpoint names the SHA-256 of the last segment it covers and carries forward every retained
  identity floor, pointer, copy, custody, remap, pin, policy, availability and expiry fact. It becomes a trusted
  anchor only after three things hold: its bytes are durable and verified by exact-version GET; an `anchor` record
  naming its exact version and SHA-256 is committed; and the checkpoint and that record are inside a sealed backup
  and, when the archive is enabled, inside a verified archive receipt. Replay starts at the newest trusted anchor
  whose bytes verify, and falls back to the previous one otherwise.
- **Retention.** Segments behind the newest trusted anchor may be removed, except those since the previous trusted
  anchor, which stay so that a damaged newest checkpoint can fall back.
- **Replay without an anchor.** Until the first trusted anchor exists, rebuild replays every segment in order from the
  first segment of generation 1. It checks each predecessor hash and follows each `generation-open` record into the
  next generation. A gap or hash mismatch ends the verified cutoff there.
- **Verified complete cutoff.** This is the highest `(g, seq)` reachable from a trusted anchor whose segments are all
  present and chain-valid and whose `publish` manifests verify. A rebuild restores the index to that cutoff and
  reports later or unknown intervals as gaps.
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
| Admission journal, sequences, control versions | No; floors are also evidenced by journal records ([5.4](#54-loss-guarantees-and-recovery-cutoffs)) | Yes |

### 4.7 Format identities

Every new format carries an explicit identity: the manifest, journal segment, checkpoint, instance marker, catalog
schema and spool record. An unsupported identity is refused read-only with a clear outcome (#1967 acceptance 5).

## 5. Admission, staging and publication boundaries

### 5.1 Durable admission boundary

The boundary is unchanged: an exposure is admitted when its `raw_captures` row commits after the fsynced payload and
sidecar (`CAC/RawIngress/RawCaptureIngress.cs:496-705`). In the new layout those files land in the spool. Admission
depends on local NVMe, the spool bound and the pause fences. It does not depend on Silo, archive or LogicHost
reachability.

### 5.2 Staged bytes and the persistence condition

Payloads, manifests and journal segments follow one rule. Each object's exact bytes are fsynced into the spool before
its first PUT. It is then created once with a supplied checksum and read back by exact-version GET with independently
computed length and SHA-256. #1968 may replace the readback only with an independently qualified equivalent.

A locator is **persistence-qualified** when both of these hold for that exact store, bucket, key and version:

1. The exact version is verified by GET and SHA-256.
2. The store runs the Silo profile that #1966 qualifies, including the measured cost of its disk-sync setting such as
   `MINIO_FS_OSYNC`. That profile guarantees an acknowledged, verified version through power loss.

Qualification belongs to the named locator. A verified copy in another store is custody evidence for the bytes, in
`copy` records and recovery sources, but it never qualifies a different locator. A record that names another copy names
that copy's own qualified locator, committed before the group is published.

The spool copy is released only when its named locator is persistence-qualified and its group is visible, or when
reconciliation has declared the staged work abandoned and handed it to retention. Until then the bytes count against
the spool bound.

### 5.3 Group publication

Silo assigns a new version ID to every versioned PUT (F14). Replaying exact bytes after a lost version therefore
creates a different locator. A committed record must never name a version that a power cut can lose, so every locator a
committed record names is persistence-qualified ([5.2](#52-staged-bytes-and-the-persistence-condition)) before that
record is written. P3 is the commit point.

| State | Entered when | Browsable | After a crash |
| --- | --- | --- | --- |
| `staged` | Members produced; every payload's exact bytes fsynced in the spool | No | Resume at P1 |
| `payloads-qualified` (P1) | Every required payload's named locator created, verified and qualified | No | Re-verify by GET; re-PUT any lost version from the spool, and name only the new qualified locator |
| `manifest-qualified` (P2) | Manifest, naming only qualified payload locators, created from its spooled bytes; its own locator qualified | No | Same rule for the manifest; a changed payload locator means a new manifest |
| `recorded` (P3, commit point) | `publish` segment, naming the qualified manifest locator, created from its spooled bytes; its own locator qualified | Not until P4 | Roll forward to P4 after the checks below |
| `visible` (P4) | SQLite marks the group visible with its journal sequence | Yes | Nothing to replay |
| `abandoned` | Reconciliation abandons staged work before P3 | No | Unreferenced versions go to retention as orphan candidates |

- **Before P3.** Everything before an authenticated, durable P3 is staged work. It is never browsable and stays out
  of the rebuilt publication index. Its spool copy is kept for exact-byte replay or the existing staged abandonment. A
  manifest object that exists without a durable `publish` record leaves the group hidden.
- **P3 recovery.** The durable `publish` record is the commit. It fixes the logical revision, the manifest digest, the
  required member identities and the original qualification of each named locator, and P4 indexes that same sequence.
  Before serving, startup checks every `publish` record after the catalog cursor:
  1. **Authority.** The record's generation is the current generation or a witnessed earlier one, and the predecessor
     chain hash matches.
  2. **Identity.** The manifest's instance, storage binding, group and revision match the record.
  3. **Exact bytes.** For the manifest and for each required member, at least one eligible locator
     ([4.2](#42-logical-identities-and-exact-versions)) verifies by exact-version GET with independently computed
     length and SHA-256. Not every replica needs verifying.

  When all three pass, P4 indexes the original P3 sequence, idempotently, so the revision reaches P4 once. Otherwise
  readers of that group stay closed while other groups are served, the P3 record and its retry state stay
  authoritative, and the verified cutoff is kept. Each unsatisfied member then takes one outcome:

| Outcome | When | Effect |
| --- | --- | --- |
| `temporarily-unavailable` | No eligible locator has verified, and an eligible source could not be checked, for example because a GET timed out | Retried. No loss or conflict is recorded, and a later successful verification brings the same revision to P4. |
| `missing` | Every eligible source has been checked and holds no verifiable bytes: absent, or damaged with its own integrity finding | Reader closed; P3 kept for diagnosis |
| `conflict` | Authenticated authority, identity or revision evidence contradicts itself, and no verified consistent candidate exists | Reader closed; P3 kept for diagnosis |
| `policy-expired` | A deliberate, durably authorized and settled role-scoped expiry ([7.3](#73-expiry-protocol)) | Closed by policy |

- **Rebuild.** After catalog loss, rebuild applies checks 1 and 2 to every committed `publish` record, verifies each
  manifest through an eligible locator, and marks member roles `unverified` ([7.5](#75-recovery)). Reopening a
  revision first establishes each required member through an eligible locator
  ([6.1](#61-reopen-and-compute-sets)), and stays closed with the outcome above for any it cannot establish.
- **Ordering.** A record is written only after every locator it names is qualified. A locator replaced before then is
  never referenced, and publication never remaps a committed manifest or `publish` record.
- **Uncertain writes.** An uncertain PUT is retried with the same spooled bytes. A create-only conflict compares
  SHA-256: equal bytes mean the earlier write succeeded, different bytes stop the writer, and nothing is replaced.
- **Stacks.** A stack or view revision publishes all evidence and image planes in one group, so readers never observe a
  partial stack (#1968 acceptance 4).
- **Startup bound.** Startup work is bounded by unreleased spool objects plus journal records after the catalog
  cursor, independent of history size (F5).
- **Qualification.** #1966 must qualify a Silo profile whose acknowledged, verified versions survive power loss. A
  profile that cannot meet the condition for the named locator would leave local-only groups unpublishable. Using a
  closed-state remap alternative instead needs a contract revision ([14](#14-remaining-decisions)).

### 5.4 Loss guarantees and recovery cutoffs

| Event | Acknowledged admissions | Published revisions |
| --- | --- | --- |
| Process crash or kill | None lost | None lost; staged groups resume, and a durable P3 rolls forward ([5.3](#53-group-publication)) |
| Host power loss | None lost if NVMe and ext4 honor flush, which #1968 and #1974 must prove by abrupt power loss. | None lost: every locator a committed record names is persistence-qualified |
| Silo service down | None lost; admission continues to the spool bound, then a pressure pause | Local reads report temporarily unavailable |
| Archive outage | None lost | Local unaffected; archive receipts pending |
| SQLite catalog loss, Silo intact | None lost when non-rebuildable state survives; otherwise it comes from the instance backup. Unpublished spool sidecars carry identity for recovery (#1972). | Rebuilt to the verified cutoff |
| **Total primary loss** | Reported by the cutoffs and dispositions below | Reported by the cutoffs and dispositions below |

The process and power-loss rows are guarantees this contract requires the implementation to prove. After total
primary loss, recovery reports two cutoffs and a disposition for every expected role:

- **Admission cutoff:** the newest admission proven by the sealed State backup's admission ledger or by a verified
  archived capture manifest.
- **Publication cutoff:** the verified complete journal cutoff ([4.5](#45-durable-metadata-journal)) across the
  surviving backup and archive.

**Per-role dispositions.** The expected roles are the members of every group up to the publication cutoff and every
admitted capture up to the admission cutoff. Each is evaluated against every surviving source: a retained local Silo,
durable spool entries in the restored State, exact payload versions in the sealed backup, and verified archive copies.
Expiry receipts are scoped to the exact copy they name ([4.5](#45-durable-metadata-journal)). A role's disposition comes
from its independently checked copies and from any role-scoped authorization, never from one copy's eviction.

| Disposition | Meaning |
| --- | --- |
| `recoverable` | Exact bytes verified by GET and SHA-256 at an eligible locator in a named source; that source and the verification time are its evidence cutoff |
| `unverified` | Not checked yet; the lazy phase has not reached it ([7.5](#75-recovery)) |
| `temporarily-unavailable` | No eligible locator has verified, and a source expected to hold it, such as a protected archive survivor or a store that timed out, has not been checkable |
| `policy-expired` | A deliberate role-scoped expiry, durably authorized and settled: every copy has a terminal deletion disposition |
| `missing` | Every eligible source is reachable and checked, none holds verifiable bytes, and no role-scoped authorization exists. Copy-scoped evictions do not count. |
| `conflict` | Authenticated authority, identity or revision evidence contradicts itself, and no verified consistent candidate exists |

A damaged copy keeps its own integrity finding whatever the role's outcome. Suppose a deliberate local eviction
relied on a protected archive survivor. The role stays available through the archive. If that archive copy is later
verified absent without role-scoped authorization, the role is `missing`; while the archive cannot be checked, the role
is `temporarily-unavailable`.

The cutoffs can differ in either direction:

- **Archive newer than the backup.** The archive proves publications whose admission rows, pending work, pins and
  settings the backup lacks. Those groups recover from the archive as published history, and their capture sequences
  raise the identity floors.
- **Backup newer than the archive.** The backup proves admissions and state that the archive lacks. Their roles recover
  from the backup's exact payloads or spool entries when present, so a capture that exists only in the newer backup
  reopens. An older archive never implies loss.

Pending work, holds and settings come only from the backup and carry its time. Admissions newer than every source are
lost, and the gap is reported. After the lazy phase transfers or locates them, `recoverable` roles take their ordinary
availability state ([6.2](#62-availability-states)). RPO budgets are in [10](#10-budgets).

**Identity floors.** The per-agent capture sequence is allocated outside any journal generation (F11), so the new
layout carries identity floors explicitly. That covers the capture sequence, the capture-control version, each
high-water counter that `DCLI/CameraAgentStateCounters.cs:8-19` lists for the filesystem layout, and the writer
generation.

- Every issued-identity counter allocates only inside a range reserved for the current writer generation `g`:
  `[g × R, (g + 1) × R)`. R is far above any feasible per-generation count (for example 2^40, which is about 700,000
  years of 20 s captures).
- Floors and the generation are recorded in `generation-open` records and checkpoints, and are covered by the backup
  and archive.
- Recovery opens `g' = h + 1`, where `h` is the highest **issued** generation proven by the generation witnesses
  below. Each counter starts at the larger of `g' × R` and any higher value that evidence shows.

Every identity the lost primary issued lies in a range at or below `h`. New identities are at least `g' × R` and cannot
repeat one, even when the primary issued identities after the last surviving evidence. Capture and artifact IDs cannot
repeat either, because they derive from the agent identity and exposure times
(`CAC/RawIngress/RawCaptureDescriptorFactory.cs:18-36`).

**Generation witnesses.** The highest issued generation is proven only by a complete witness set; the largest value
among readable survivors is not enough.

- **Witness set.** The witness set is the configured sealed-backup target (#1972) and, when enabled, the archive
  (#1969). Each witness keeps a live witness area with one create-only record per generation, naming the instance, the
  generation, its predecessor, the witness set and the operation. A sealed backup snapshot is evidence of state at its
  time; it is never the witness.
- **Durable before issue.** Opening generation `g` follows these steps in order:
  1. Read every witness in the newest known set. `h` is the highest generation recorded in any of them.
  2. Write record `g = h + 1` to every witness create-only from fixed bytes, and read each one back.
  3. Only when every witness has acknowledged, commit `generation-open` locally and start issuing identities in `g`.
- **Ambiguous writes.** An uncertain or partial write leaves `g` possibly created. No identity is issued in `g`, and
  retries send the same bytes. If a witness stays unreachable, the opening stops fenced. A later attempt reads `g`
  wherever it landed and opens `g + 1`. A `g` that landed nowhere was never used for identities, so reusing it is
  harmless.
- **Set changes.** Changing the witness set is part of a generation opening and is written to every old and new
  witness.
- **Completeness on restore.** Recovery proves `h` only when every witness in the newest known set is readable. If any
  witness is unavailable or unreadable, `h` is unknown, and admission, publication and expiry stay fenced; reads of
  verified history may proceed. For example, if a readable backup snapshot shows generation 1 while generation 2 is
  recorded only at an archive witness that is unavailable, recovery stays fenced until the archive is readable and
  then opens generation 3.
- **Normal operation.** Openings happen only at install, recovery and replacement, so normal admission never waits on
  a witness. A local-only site without an off-primary witness can still capture, but any later recovery stays fenced
  until Roy approves an alternative such as a distinct identity namespace (#1972).

Consumers of per-agent sequences treat a generation boundary as a reported gap (#1972).

### 5.5 Staging bound and pressure pause

The spool has a configured byte limit, sized from the cadence ([10.3](#103-proposed-budget-targets)). Reaching it, or
the Silo headroom limit, raises a **pressure-owned pause** that blocks only new admission. Clearing it requires
measured headroom recovery and clears only that pause ([8.4](#84-pause-precedence)).

## 6. Dependency closure, availability and reopen

### 6.1 Reopen and compute sets

- **Reopen set R.** This is what exact reopen needs: the manifest, the base's `Preview`, `Science` where displayed,
  `ClosedWindow`, and the layer members that the table below requires
  (`CAC/Gallery/CenteredViewPublication.cs:21`, `:70-81`, `:107`).
- **Bindings versus bytes.** The validator requires a committed evidence binding for `Ready`, `Partial` and
  `AssessedEmpty` layers, and accepts a supplied evidence identity on a layer in any state (F17). The revision ID
  hashes the complete layer list (`CAC/Gallery/SqliteCenteredViews.Writes.cs:355-356`), so every supplied binding
  (evidence identity, observation time and evidence order) is part of canonical revision identity. The manifest and
  every rebuild keep each binding exactly as supplied, in every layer state, whatever the availability of its bytes.
  Which member bytes block exact reopen is a separate question that depends only on the layer state:

| Layer state | Binding | Bytes whose absence blocks exact reopen | If those bytes are unavailable |
| --- | --- | --- | --- |
| `Ready`, `Partial` | Required and kept exactly, with the drawable | The evidence payload | Not exactly reopenable; the missing member is named |
| `AssessedEmpty` | Required and kept exactly | None; the empty result is shown from the binding | Reopens; evidence availability is reported separately |
| `Incompatible` | Kept exactly when supplied | None | Reopens with the recorded reason |
| `Waiting`, `NotConfigured` | Kept exactly when supplied | None | Reopens; later evidence arrives only as a later revision |

- **Compute set C.** This is what is needed to recompute or reprocess a revision: the raw and linear inputs,
  `Contributors`, registration artifacts, calibration and normalization-plan masters, and recipe inputs
  (`processing_output_sources`). Late archive enrichment creates a new revision and never changes an earlier
  revision's R or C.
- **Closure.** Closures are computed over manifest `dependencies` with the existing safety bounds: depth below 512 and
  cycles refused (`CAC/Capture/Processing/SqliteCaptureProcessingStore.cs:2580` onward). They are exact by
  `(artifactId, revision, sha256)` and deduplicated by exact version.
- **Rules.** A published ID alone does not prove reopenability. A revision reopens exactly when its manifest and each
  required member of R verify through at least one eligible locator ([4.2](#42-logical-identities-and-exact-versions)).
  That is the named locator, or a committed copy or remap bound to the same identity, revision, length and SHA-256.
  After a durable local eviction, the archive copy serves the same revision. Otherwise the revision stays closed with
  an outcome from [5.3](#53-group-publication). Policy may expire members of C without breaking reopen; a
  reprocess request then names the exact missing inputs (#1971 acceptance 4).

### 6.2 Availability states

Availability is tracked per copy and per role (artifact revision). A copy state never implies a role state:

| State | Meaning | Evidence |
| --- | --- | --- |
| `local` | Verified version in the local Silo | `copy` record plus exact-version read |
| `archived` | Verified version in an archive store | `copy` record with archive receipt |
| `pending-archive` | Published locally; archive receipt not yet verified | Copy backlog |
| `temporarily-unavailable` | Store or tier unreachable | Probe failure; never inferred to be missing |
| `evicted` | This exact copy deliberately deleted under a copy-scoped expiry, with absence proven; the role may survive elsewhere | Terminal `deleted` or `already-absent` disposition for that copy |
| `policy-expired` | The role deleted under role-scoped authorization | Role-scoped `expiry-intent`, an `expiry-disposition` for every copy, and a role `availability` record |
| `missing` | Expected version absent or wrong digest with no expiry record | Physical verification failure |
| `quarantined` | Retained but refused for use | `availability` record |
| `conflict` | Role: authenticated evidence contradicts itself, and no verified consistent candidate exists | Role `availability` record; the P3 record is kept |
| `unverified` | Recovered history not yet checked by GET ([7.5](#75-recovery)) | Recovery state |

Physical verification, including proof of a surviving copy, reads the exact version by GET with its version ID and
computes length and SHA-256 over the returned bytes. HEAD may locate a version or prove absence (`NoSuchVersion`), but
it returns stored metadata (F13) and never proves bytes. A metadata-only check is insufficient: the #1059
[negative control](../validation/artifact-history-1059-fault-evidence.md#explicitly-unqualified-acceptance) passed with
a referenced payload deleted. The fault matrix exercises both a deleted version and damaged bytes with intact metadata
and a valid catalog.

### 6.3 Issued-live and archive-only revisions

This section maps the revision behavior observed at `87b238c`. The accepted #1922 and #1923 contracts reachable at
the integration source govern where they differ ([9](#9-branch-and-release-strategy)).

- Every revision is published by `(baseId, revisionId)`. `RegisterBaseAsync` revisions are published but not live.
  `PublishLayerAsync` issues live only for the current base. Late layer revisions on an older base are
  **archive-only**.
- **Pointers.** Each base keeps three independent pointers: the first committed revision, the latest enrichment and the
  last live-issued revision. The default reopen target is the last live-issued revision, or the first when none was
  issued (`CAC/Gallery/CenteredViewPublication.cs:138-141`). Rebuild reconstructs all three from `publish` change
  order and `live-issue` records, and checkpoints carry them forward.
- **Exact links.** An explicit link names the reference capture, base and revision. Reopen checks that the base belongs
  to that stream and reference capture, as the archive read does (`CAC/Gallery/CenteredViewPublication.cs:160`), and
  refuses a mismatch.
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
  disabled. After a durable local eviction, rebuild and exact reopen resolve each required member through its
  committed archive `copy` or `remap` entry, and verify that archive version by GET and SHA-256
  ([6.1](#61-reopen-and-compute-sets)).

## 7. Holds, writer generations, retention and recovery

### 7.1 Hold model

A hold is `(holdId, holderKind, holderRef, target artifactId [revision | any], scope reopen|compute|bytes, expiresUtc?,
createdGeneration)`. It resolves to exact versions only when a deletion is evaluated.

| Source today | New hold and release condition |
| --- | --- |
| Raw-ingress holds for committed captures with pending lane work (`CAC/RawIngress/SqliteRawCaptureJournal.cs:827`) | Compute hold on the raw artifact; released when its lanes complete |
| Transient candidate sources: raw stays held while a candidate's source hold is unreleased or its capture work is pending, even after lane completion (`CAC/RawIngress/SqliteRawCaptureJournal.cs:928-931`) | Compute hold from the candidate on each exact source raw; released when the candidate releases its source hold |
| Processing store holds (`CAC/Capture/Processing/SqliteCaptureProcessingStore.cs:2580`): recent available outputs window, pending standard-lane captures, latest 100 raw, unexpired `centered_exposure_holds` and products, live view revisions and layers, terminal window registration artifacts, unreleased execution input pins, transitive sources | Display hold for the recent window; compute holds for pending lanes, the recent buffer (with expiry), registration and execution pins; reopen hold for live view revisions |
| Clear references (`CAC/Capture/Processing/CameraAgentClearReferenceLoader.cs:10`) | Configuration pin |
| Calibration library, active family masters, frozen normalization-plan masters (`CAC/Capture/Processing/ProcessingRetentionContracts.cs:37-80`) | Compute holds |
| Daily time-lapse assembly opens exact hourly videos (`CAC/TimeLapses/TimeLapseGenerator.cs:135`, `:140`) | Compute hold from the daily job on each exact hourly revision while the job is `Queued` or `Working`. It is released when the job reaches a terminal state: `Produced`, `NoSources`, `Unavailable` or `Failed` (`CAC/TimeLapses/CameraAgentTimeLapseContracts.cs:10`; `CAC/TimeLapses/TimeLapseWorker.cs:48-56`). The daily manifest lists the hourly videos as compute dependencies, not reopen members. |
| Nightly assembly reads prior nightly products (`CAC/NightlyProducts/NightlyProductGenerator.cs:579`, `:583`) | Compute hold from the nightly job on each exact prior product while it is pending (`Running` or `Stalled`); released when it is produced or rejected, for example over its source-byte bound (`CAC/NightlyProducts/NightlyProductGenerator.cs:560`, `:832-839`) |
| Satellite work: raw stays held while prediction support exists or the capture has a qualifying archive reference, and support stays held while another node or archive needs it (`CAC/Capture/Processing/CaptureSatelliteWorkStore.cs:125-155`); work references an orbital snapshot (`:112`) | Support products hold their raw. Each qualifying archive reference also holds the raw directly, even with no support product (`:144`, `:149-150`), and unknown archive history counts as a reference. That direct hold is released when the last qualifying archive reference is dispositioned and its absence confirmed. Consumers hold the support; the orbital snapshot reference stays a State hold released with the work. |
| Retained projected scenes stay while any consumer payload or archive copy exists, or when history is unknown (`CAC/Capture/Processing/ProjectedSceneRetentionGuard.cs:52`, `:73`, `:89`) | Each consumer manifest names the exact scene revision as a compute dependency; the scene is released when no retained consumer references it; unknown history keeps it held |
| Saved view selections | Reopen pin |
| Upload outbox holds (`CAC/Background/RetentionBackgroundService.cs:353`) | None in the standalone profile |
| New: restore and replay leases (#1971), operator pins (#1970) | Lease hold with expiry, revalidated per batch; journaled pin |

- **No cycles.** Holds point only from a consumer to its inputs, so the hold graph is acyclic. A scene is independent
  support and is never treated as a satellite consumer, which would form scene → raw → satellite → scene
  (`CAC/Capture/Processing/CaptureSatelliteWorkStore.cs:147`). Expiry evaluates consumers before inputs.
- **Interruption versus terminal state.** A restart or crash while a job is `Queued`, `Working`, `Running` or
  `Stalled` is resumable: the holds live with the durable job state and survive. Only a terminal state releases them.
- **Retry.** An audited retry of a `Failed` or `Unavailable` video
  (`CAC/TimeLapses/SqliteTimeLapseStore.Retry.cs:8`, `:36-44`), or a re-run nightly job, reacquires holds on the job's
  exact recorded inputs. It revalidates each input by catalog availability and exact-version GET before running. An
  input that expired meanwhile ends the retry `Unavailable` with that input named; it is never re-held.
- **Expiry.** An input already policy-expired before a job planned is reported as a gap in that job's output.
- **Backup.** Holds stored in SQLite are State and covered by the backup. Pins and policy are also journaled.

### 7.2 Writer generations and fences

- **Bootstrap.** Clean install creates `meta/instance.v1.json` and `meta/fence.json` (generation 1) with
  `If-None-Match: *`. An existing object refuses the install, because the bucket is then not empty or not this
  instance's.
- **Why credentials are not enough.** Silo authorizes a delete when the request arrives and takes the object lock
  later (F15). An accepted delete can therefore still run after the client exits and after its credential is disabled.
  Neither a client timeout, a credential change nor a fence re-read proves quiescence.
- **Storage-enforced exclusion.** Only the instance's Silo process mutates its data root. Stopping that process and
  verifying that it has exited ends every accepted mutation, finished or not. Restart then runs Silo's own recovery.
- **Takeover sequence.** Recovery, replacement and any new primary follow these steps in order:
  1. Stop the superseded writer (installer fence; container removed).
  2. Disable its credentials: its Silo credential and, when it used a different source Silo, that source's
     replication credential at the archive.
  3. Stop the local Silo service and verify its process exit. Verify that no other container references the Silo data
     root, as the installer already checks for instance roots (`DCLI/DockerClient.cs:827`). On empty storage this step
     is vacuous.
  4. Start Silo with only the new generation's credential enabled.
  5. Reconcile authoritatively: read the journal tail and, for every open `expiry-intent`, issued delete, survivor
     reservation and staged publication, read the exact versions and record the actual outcome.
  6. Open the new generation through its witnesses ([5.4](#54-loss-guarantees-and-recovery-cutoffs)) and commit
     `generation-open`.
  7. Only then admit, publish or expire.
- **Fenced branch.** If step 1 or 3 cannot be verified, takeover stops there: for example, the daemon does not confirm
  the Silo exit, or another container still references the data root. The generation does not advance, and no
  admission, publication or expiry runs.
- **Qualification.** #1966 qualifies Silo stop, exit verification and data-root exclusivity on the pinned release, and
  #1972 owns the takeover sequence. No new service is involved.
- **Identities.** Recovery onto empty storage, or onto a replacement host or daemon, advances the generation. Identity
  floors follow [5.4](#54-loss-guarantees-and-recovery-cutoffs).

### 7.3 Expiry protocol

1. Dry run: deterministic candidates and reasons from per-role policy and pressure (#1970).
2. Resolve holds and every retained reference to each candidate exact version. Shared content-addressed versions are
   deleted only when no retained revision references them.
3. Choose the scope. A **copy-scoped** eviction removes one exact copy while the role survives in another copy. A
   **role-scoped** expiry removes the role itself, including its last copy, and needs role-scoped authorization under
   the explicit last-copy policy.
4. For a copy-scoped local eviction, place a **non-expiring per-version legal hold** on the exact archive survivor
   with the archive custody credential. Verify the survivor by exact-version GET and SHA-256, then commit
   `reserve-survivor` with the hold evidence. This is the first-release rule.
   - A finite retain-until is not protection, because worker failure or a missed renewal can let it lapse while an
     accepted delete is still unresolved. A receipt alone is not protection either.
   - If the hold cannot be established, fail closed and send no delete, unless role-scoped last-copy authorization
     exists.
5. Append `expiry-intent` with its scope and each copy's custody.
6. Before issuing or retrying a local delete, confirm the hold in the archive's own protection state. A GET that still
   succeeds proves nothing about protection.
7. Delete the local exact version by version ID.
8. Resolve the delete conclusively, then record exactly one branch:
   - **Absent.** An authenticated `NoSuchVersion` for the intended local version, including after a worker restart.
     Re-verify the survivor by GET, then append the terminal `deleted` (or `already-absent`) disposition and
     `availability evicted` for that copy. Charge its unique eligible allocation to reclaim-pending once for this
     intent and copy; replaying the result adds no second charge. Physical reclamation is verified separately
     ([7.4](#74-physical-reclamation)).
   - **Present and quiesced.** The exact local version verifies by GET, and a verified Silo exit
     ([7.2](#72-writer-generations-and-fences)) proves the accepted delete can no longer complete. Append a terminal
     `not-deleted` disposition for this attempt. The local copy stays available, and its bytes stay in ordinary
     allocation with no reclaim charge. A later retry starts again with fresh authorization and hold verification.
   - **Uncertain.** The outcome cannot be established, or the absence of an in-flight delete cannot be proven. Keep
     the durable attempt and the non-expiring archive hold, keep this delete fenced, and reconcile after restart. No
     terminal disposition, eviction or reclaim charge is recorded, and allocation stays as it is. This branch never
     ends an attempt; only an absent or present-and-quiesced result does. That holds while the worker is down, across
     restarts, and while an accepted delete may still complete later (F15).
9. Release the survivor hold only after a terminal branch is durably recorded, and only when no other hold or
   survivor check still needs it. Otherwise leave the version to the archive's own retention.

- **Role expiry.** A role-scoped expiry deletes every copy under its authorization. It appends a disposition per copy
  and `availability policy-expired` for the role.
- **Restart.** Reservations, intents and issued deletes are journaled, and a restart resumes at step 6 with the hold
  still in place. If the survivor vanished before step 7, the delete is abandoned. The archive's sweep cannot remove a
  held survivor. Any later loss of it is unexpected, and the role follows
  [5.4](#54-loss-guarantees-and-recovery-cutoffs) (`missing` or `temporarily-unavailable`) and is escalated.
- **Archive credentials.** The source's replication credential at the archive must lack object and version delete,
  lifecycle, retention and hold changes. #1969 proves this with a refused delete, and the race test runs the
  archive's own sweep between reservation and local delete.
- **Idempotence.** A crash between steps re-checks version presence and completes idempotently. A repeated delete is
  `already-absent`.
- **Pauses.** New expiry decisions wait under operator and installer pauses, as today. Under a pressure pause, expiry
  and reclamation continue, because that is how the pause clears.

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
- The denominator is the unique eligible allocated bytes: the on-disk allocation, after compression and sharing, of
  exact versions no retained revision still references. Logical size is reported beside it, never instead.
- Reclaim-pending bytes count as used. They are bounded ([10.3](#103-proposed-budget-targets)). Only the absent
  branch of [7.3](#73-expiry-protocol) charges them, once per intent and copy.
- If sweeps make no physical progress, further expiry stops and the pressure pause holds admission.
- Restart is a valid reclamation trigger. It is never the routine mechanism.

### 7.5 Recovery

Recovery moves through these states, and each claims only what its evidence supports.

| State | Entered when | What it claims |
| --- | --- | --- |
| `restoring` | Recovery starts on empty or retained storage | Nothing |
| `resume-ready` | State restored (SQLite, keys, identity, configuration and spool); catalog rebuilt from the newest trusted anchor, or from generation 1, to the verified cutoff, with manifests and journal verified by hash; witness set complete and the new generation opened; takeover complete ([7.2](#72-writer-generations-and-fences)); installer readiness verified (authenticated readiness, binding, camera allocation, no central traffic); authority recorded | Identity safety, the admission and publication cutoffs, and the catalog to that cutoff. Every history role starts `unverified`. |
| `capturing` | An authorized resume passes the atomic latest-command check ([8.4](#84-pause-precedence)) | New admissions are durable; history is only as verified as its roles |
| `partially-verified` | The lazy phase has given some roles a terminal disposition ([5.4](#54-loss-guarantees-and-recovery-cutoffs)) | Exactly those roles. Others stay `unverified` or `temporarily-unavailable`, and reads and restores report them so. |
| `fully-verified` | Every expected role has a terminal disposition | The complete recovered history, with its evidence cutoff |

- **Catalog-only loss.** When State and the local Silo survive and only the catalog is lost, the writer, its floors
  and its generation stay valid. Rebuild reaches `resume-ready` without a takeover or a new generation.
- **Lazy phase.** Exact-version verification by GET and SHA-256, and transfer of payload bytes from the backup or
  archive into the local Silo, run after resume at lower priority than capture. Neither gates resume. A required
  member that cannot be established yet stays `temporarily-unavailable` and is retried, and its P3 record stays
  authoritative ([5.3](#53-group-publication)).
- **Held work.** Replication stays held until the archive receipts and versions are re-verified (#1972 acceptance 2).
  Expiry stays held until the operator confirms, because holds created after the backup cutoff are unknown. The
  canary keeps expiry held from the start (#1970, #1975).
- **Fence.** Admission stays fenced while any floor or the highest issued generation is unproven
  ([5.4](#54-loss-guarantees-and-recovery-cutoffs)).

## 8. Installation and cutover

### 8.1 Clean target and first boot

- **Clean roots.** The installer verifies empty, instance-owned State and Silo roots, a versioned bucket, and the
  `meta/instance.v1.json` binding before initialization (#1966).
- **No central dependency.** Central integration, upload and environmental delivery stay disabled
  (`DCLI/ComposeDeployment.cs:306-308`). Readiness never requires LogicHost. The canary verifies the absence of central
  traffic (#1975).
- **Persisted paused admission.** The first boot must start paused. Before the container first starts, the installer
  must seed an installer-owned pause with a known command identity and version, so a version-0 `Running` state never
  exists. It must not rely on the provisioning gate, which the installer does not set today (F6). The pause must
  survive startup, owner bootstrap and restart.
- **Atomic first resume.** The first resume is accepted only if the seeded pause is still the latest accepted command,
  with `changed = 1`, at the expected version, checked inside the resume's write transaction. This is the existing
  owned-pause check (F12). A later operator pause, including a no-op that leaves the version unchanged, refuses the
  resume. #1966 tests that interleaving.
- **Device allocation.** Because the module initializes while paused (F7), the real camera is mapped to the candidate
  only after the former owner's container is removed and no process holds the device. That mapping is then the only
  one until the handoff ends. #1966 documents the boundary.

### 8.2 Capture handoff

1. Persist the former instance's pause and record it as the **handoff pause**: command identity, actor, version and
   UTC time, read back from `capture_control_commands` (`CAC/RawIngress/SqliteRawCaptureJournal.cs:2596`). Then run
   the data-preserving `hvo-sky-install cameraagent uninstall`, or the site equivalent, and record its lifecycle
   operation ID. Uninstall pauses and drains (`DCLI/CameraAgentLifecycleManager.cs:1403`, `:1440`).
2. Confirm the camera is released and the former container is inactive after a restart.
3. Start the candidate paused. Verify authenticated readiness, camera and rig, catalog, endpoint, storage binding and
   absence of central traffic.
4. Resume once through the atomic first resume ([8.1](#81-clean-target-and-first-boot)). Exactly one capture owner
   remains.

### 8.3 Early-abort ordering

The legacy reinstall route at `87b238c` cannot hold control authority or camera exclusivity across its own restart and
automatic resume:

- It starts the former container and exposes its controls before resuming
  (`DCLI/CameraAgentLifecycleManager.cs:1522-1527`, `:1548`).
- Its resume carries no expected version and no owned-pause check (F8). A newer operator pause accepted in that window,
  including a no-op that leaves the version unchanged (F12), would be overwritten.
- An interrupted reinstall later resumes through `--resume` with the same unconditioned resume
  (`DCLI/CameraAgentLifecycleManager.cs:1486`, `:1504`).
- The camera is mapped to the former container from its start, so a device check made before the call cannot hold
  exclusivity across it.

A one-time check of the stopped former instance therefore cannot authorize its later resume. The abort procedure never
invokes legacy reinstall.

| Step | Action | Pass condition |
| --- | --- | --- |
| E1 | Stop the candidate with no restart policy and remove its camera mapping | Container exited; no candidate process holds the camera; admission persisted `Paused`, or the container is absent |
| E2 | Read the stopped candidate's admission ledger and provider stores read-only, including WAL: copy each database with its `-wal` and `-shm` files and open the copy | `raw_captures` has 0 rows; no reserved `raw_capture_sequences`; spool empty; no `publish` record in its journal; authoritative provider state at its initial values, or its newer facts reconciled ([8.5](#85-request-floors-and-identity-floors)). Record the results, each file's SHA-256 and the UTC time. |
| E3 | Fence | Candidate stopped and paused; former instance left uninstalled; camera unmapped from both. Record the fence and escalate. |
| E4 | Restore the former owner only through an authority-preserving route, with positive authority recorded | See below |
| E5 | Verify | Former instance `Running` with one owner; candidate stopped, its data preserved for closeout |

An **authority-preserving route** must meet all of these:

1. The former instance starts with admission held by a pause the restore operation owns.
2. Its resume is accepted only atomically, when that pause is still the latest accepted command at its version (F12).
3. The camera stays allocated to the restore operation from E1 through that resume, including across interruption and
   any resumed operation.
4. Provider facts are reconciled first ([8.5](#85-request-floors-and-identity-floors)).
5. The accountable operator or site has recorded positive authority to restore the former instance as the active
   capture owner: who, when and why.

No such route exists for the filesystem release at `87b238c`. Providing one is a remaining decision
([14](#14-remaining-decisions)). Until then an early abort ends at E3.

- **E2 failure.** If E2 finds an admission, or cannot prove zero, the candidate owns new data. Recover forward with
  new-layout recovery (#1972) and escalate.
- **Acceptance test (#1975 rehearsal and any automation).** Assert:
  1. The abort procedure never invokes legacy reinstall, and E3 leaves admission and the camera fenced.
  2. E2 fails on any admission and routes to forward recovery, including a fault right after the first durable
     admission.
  3. With an authority-preserving route, an operator pause accepted between the former start and its resume refuses
     the resume, including a no-op pause with an unchanged version.
  4. With such a route, an interruption between the former start and its resume leaves admission paused and the camera
     with the restore operation.
  5. A candidate provider fact during the window, including a retained halt behind an empty or pruned attempt table,
     keeps the former provider acquisition fenced until reconciled.

### 8.4 Pause precedence

- There are three pause owners: installer (a lifecycle operation), operator (a UI or API actor) and pressure (storage).
  Clearing one never clears another.
- A resume is accepted only when its own pause is still the latest accepted command, with `changed = 1` at the
  expected version, checked inside the resume's write transaction. Today that check runs only through the control-plane
  receipt path (`CAC/Capture/CaptureAdmissionCoordinator.cs:426-433`, `:815-818`, `:896-911`).
- The version alone is insufficient, because a no-op pause leaves it unchanged (F12). The lifecycle resume used by
  reinstall and upgrade sends neither (F8), and the expected-version recovery resume
  (`DCLI/CameraAgentLifecycleClient.cs:128-134`) also lacks the latest-command check.
- The Silo first-boot resume ([8.1](#81-clean-target-and-first-boot)) and any authority-preserving restore route
  ([8.3](#83-early-abort-ordering)) must use the latest-command form.
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

  On early abort, the former stores are intact.
- **Abort-window fence.** Provider acquisition stays disabled on the candidate from its first start until the handoff
  is final, when the former instance is retired as owner after the observation window. Attempt tables are audit only
  and are pruned (F16), so an empty table proves nothing. E2, and any release of the former instance's provider fence,
  read the candidate's authoritative provider state:
  - SWPC: `swpc_meta.provider_halt`, and each `swpc_products` row's `halt`, `checkpoint`, `last_attempt` and
    `last_outcome`.
  - Orbital: each `orbital_provider_state` row's last attempt start, halt fields and quarantine observation.

  If any of these differs from its initial value, one of two things must happen before the former owner can request
  from that provider:
  1. The newest floor, retry deadline, checkpoint and halt are reconciled into the former stores, keeping whichever
     fact is newer.
  2. The former instance's acquisition for that provider stays disabled until those deadlines pass and halts clear.

  The #1975 rehearsal includes an empty attempt table with a retained halt.
- **Identity floors.** The installer gives the candidate a new application identity, which is its `AgentId`
  (`DCLI/CameraConfiguration.cs:88`). Capture IDs and sequences therefore start a new namespace that cannot collide
  with the former instance's. Within the new instance, every issued-identity counter follows the
  generation-partitioned ranges in [5.4](#54-loss-guarantees-and-recovery-cutoffs) through backup and recovery (#1972).

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
timing semantics come from those accepted contracts, not from this document. Payload-volume independence applies
only to reaching `resume-ready`, because verification and transfer are lazy. History availability scales with bytes
and is reported by the separate transfer and verification rows.

| Budget | Proposed target | Derivation |
| --- | --- | --- |
| Cadence | 20 s sustained for the #1974 week; zero missed admissions attributable to storage | Local PUT exceeds W6 ingest about 103 times |
| Storage publication (group ready to visible) | p95 ≤ 5 s, p99 ≤ 10 s at W6 size on the Pi. The clock starts when every member of the group is produced and spooled. | W6 at the serial committed rate is 0.65 s; the targets are at most 25 % and 50 % of the interval |
| End-to-end single, stack and late-layer timing | Measured and reported separately, from the admission of the last exposure each needs. A stack waits for later contributors: a stack centered on exposure 103 needs 105, about 40 s later at 20 s cadence. Targets come from the accepted #1922, #1923 and #1928 contracts; storage adds at most the publication budget. | #1921 windows; launch hold (9) |
| Spool | Steady backlog ≤ 2 captures (p99). Bound ≥ 2 h at cadence (2.54 GB POC, 9.08 GB W6 raw). After an outage of D, drain within max(60 s, 0.1 D). | Cadence × size; ingest headroom at least 100× |
| Switchover pause (former owner's last admission to candidate's first) | Automated steps ≤ 5 min: drain ≤ 2 min, candidate ready (paused) ≤ 2 min, resume ≤ 15 s. Attended total recorded, ≤ 30 min for the canary. | Installer budgets; Silo replacement about 2.6 s |
| RPO, archive enabled | Verified receipt lag p99 ≤ 5 min on a nominal link | Replication exceeds W6 ingest about 83 times; catch-up 2.14 s |
| RPO, local only | Sealed backup interval ≤ 24 h, as configured by the operator | Bounded loss accepted by local-only sites |
| RTO, catalog loss with Silo intact | ≤ 30 min from recovery start to `resume-ready` | Metadata only: anchors, journal and manifests; 30,240 manifests × 0.89 ms ≈ 27 s. Payload verification is lazy. |
| RTO, primary loss with archive | ≤ 30 min from recovery start to `resume-ready` for the #1974 one-week fixture, with the witness set readable | State backup plus metadata from the archive; payload verification and transfer are lazy |
| RTO, primary loss local only | ≤ 30 min from the start of the recovery command on empty storage to `resume-ready`, with the sealed backup reachable on the site LAN, for a sealed backup of the #1974 one-week fixture (30,240 captures). Add ≤ 1 min for each further 10,000 captures of retained history. The endpoint requires the proven floors and witnessed generation, completed takeover, recorded authority and verified readiness ([7.5](#75-recovery)). | Gating work reads State and metadata, not payload bytes; manifests at 0.89 ms take about 9 s per 10,000 |
| RTO, Silo container replacement on a retained volume | ≤ 10 s Silo outage; admission continues into the spool | Observed 2.35 s amd64 and 2.61 s Pi |
| Full payload transfer | ≥ 70 % of same-route TCP (≥ 655 Mb/s): one POC day ≤ 373 s, one W6 raw day ≤ 1,331 s. At the observed 820.9 Mb/s these take 297 s and 1,062 s. | Floor from the 936.38 Mb/s TCP baseline |
| Full-history transfer | Reported separately from RTO. At ≥ 655 Mb/s: one POC week ≤ 44 min, one W6 raw week ≤ 156 min. At the observed 820.9 Mb/s: about 35 and 124 min. | Week bytes at the transfer floor |
| Role verification | Exact-version GET and SHA-256 of every expected role, reported as elapsed time and rate, separately from RTO and transfer | Lazy phase ([7.5](#75-recovery)) |
| Concurrent restore and LAN | Hour, day and week restores at ≥ 655 Mb/s while capturing, with publication latency in budget and zero missed admissions | Measured GET 96.6 % and restore 87.7 % of TCP, without concurrent capture |
| Physical reclamation | ≥ 95 % of unique eligible allocated bytes reclaimed within 10 min | Twice the observed ~5 min trash period ([7.4](#74-physical-reclamation)) |
| Reclaim-pending bound | ≤ min(5 % of the Silo filesystem, 1 h of capture) | Bounds unreclaimed trash at cadence |
| Startup readiness | ≤ 60 s at any history size | Bounded by unreleased spool objects plus the journal after the cursor (F5) |
| Peak disk | Headroom reserve covering the spool bound, the reclaim-pending bound, catalog and journal growth for 24 h, the restore cache limit, and a capture reserve | Explicit sum checked by #1974 |

## 11. Verification design

### 11.1 Clean-install matrix

Run on amd64 and ARM64, each in local-only and archive-enabled profiles, with the centered profile at an integration
source that meets the launch hold ([9](#9-branch-and-release-strategy)):

- Fresh-root and binding refusal; create-only fence bootstrap, refused on a non-empty or foreign bucket.
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
| Kill before identity reservation | No identity consumed; no capture | Existing; #1968 |
| Kill after the reservation commits (`CAC/RawIngress/SqliteRawCaptureJournal.cs:426-481`), before the payload | Sequence consumed and never reissued; no capture after restart. An in-process retry of the same capture reuses its assignment (`:437`). | #1968 |
| Kill after the payload, before the sidecar | Reconciliation quarantines the payload-only file (`CAC/RawIngress/RawIngressReconciler.cs:264`); sequence consumed; no capture | #1968 |
| Kill after a complete pair, before the journal commit | Reconciliation verifies the pair against its descriptor and recovers one committed capture (`CAC/RawIngress/RawIngressReconciler.cs:191`, `:236`). A mismatched pair is quarantined (`:193`). | #1968 |
| Kill after the journal commit | Admission survives; spool transfer resumes; one qualified exact version per payload | #1968 |
| Uncertain PUT of a payload, manifest or segment | Exact-byte retry; equal digest is success; a different digest stops the writer | #1967, #1968 |
| Interruption before a durable P3, including a crash after the manifest PUT | The group stays `staged`, hidden and outside the rebuilt publication index, even though the manifest object may exist. The spool is kept for exact-byte replay or staged abandonment, and only a new qualified locator is ever named. | #1967, #1968 |
| Crash after a durable P3, before P4 | Authority, identity and exact-byte checks pass through an eligible locator for the manifest and each required member, and P4 indexes the original P3 sequence, so the same revision is indexed once | #1967, #1968 |
| Required GET during P3 recovery times out, then later succeeds with the expected hash | `temporarily-unavailable` while it fails; readers stay closed, and P3, its retry state and the verified cutoff are kept. The later success brings the same revision to P4 with no `missing` or conflict record. | #1967, #1968 |
| P3 recovery or exact reopen after every eligible source is checked | All authorized copies return authenticated absence: `missing`. A conflicting revision digest with no verified consistent candidate: `conflict`. Either way the reader stays closed and P3 is kept; a damaged copy keeps its own integrity finding. | #1967, #1971 |
| Named original locator lost while a verified colder replica survives | The replica never qualifies the original, and the original P3 facts stay. A previously committed eligible copy is verified by GET and serves the same revision. A matching key or hash without a committed `copy` or `remap` entry is not eligible, and the reader stays closed. | #1967, #1969 |
| P3 named local `L@v1`; a durable eviction settles `L@v1`; archive copy `A@v7` has a committed entry bound to the same member | After rebuild the same revision reopens from `A@v7` by GET and SHA-256, and `L@v1` stays `evicted`. If `A@v7` is later confirmed lost, the member is `missing`; while it times out, `temporarily-unavailable`. | #1967, #1969, #1971 |
| Fault between staging stack planes and group commit | Readers see only settled stacks | #1968 |
| Abrupt host power loss after acknowledged **admission** | Admission survives | #1968, #1974 |
| Abrupt host power loss after acknowledged **publication** | Reopen the same logical revision, and GET with SHA-256 every required member at the exact versions its manifest and `publish` record name. All verify without remap, because each named locator was qualified before P3. | #1974 |
| Catalog deleted before any trusted anchor exists | Ordered replay from the first segment of generation 1 rebuilds the same groups and pointers | #1967 |
| Damaged newest checkpoint | Replay falls back to the previous trusted anchor | #1967 |
| SQLite catalog deleted | Rebuild to the cutoff with stable IDs, first, latest and last-live pointers, layer bindings, previews, original view revisions, and expired versus missing | #1967 |
| Reopen with `AssessedEmpty` evidence bytes unavailable; with `Ready` evidence bytes missing | Reopens and reports evidence availability; not exactly reopenable, with the missing member named | #1967, #1971 |
| Valid `Waiting` or `NotConfigured` layer with a supplied evidence binding | Binding and the same revision ID preserved through publication and rebuild; reopen does not require its bytes | #1967, #1971 |
| Silo down during capture | Spool grows, then pressure pause; drains on recovery | #1968, #1970 |
| Deleted version with a valid catalog and metadata (#1059 negative control on the new layout) | `missing`, found by exact-version GET | #1967, #1968 |
| Damaged payload bytes with intact metadata and stored checksum | `missing`, found by independently computed SHA-256 or a Silo read error; HEAD alone passes and is not accepted as proof | #1967, #1968 |
| Expiry interrupted after intent or after delete | Idempotent completion through the [7.3](#73-expiry-protocol) branches: `evicted` only after confirmed absence, `policy-expired` for a settled role-scoped expiry | #1970 |
| Archive sweep between survivor reservation and local delete | The legal-held survivor stays readable by GET; without a legal hold no non-last-copy delete is sent | #1969, #1970 |
| Accepted local delete is conclusively absent after a worker restart | Authenticated `NoSuchVersion` settles it with a `deleted` disposition, copy `evicted` and one reclaim-pending charge; replaying the result adds no second charge | #1970 |
| Silo exits with an accepted delete outstanding; after restart the exact local version verifies | Terminal `not-deleted` for that attempt; local read succeeds; reclaim-pending does not grow; this attempt's hold is released after settlement if otherwise eligible | #1970 |
| Worker exits after the server accepts a delete, and the replacement cannot establish the outcome | No terminal disposition, eviction or reclaim charge. The non-expiring archive hold stays, and the delete stays fenced until an absent or present-and-quiesced result. | #1970 |
| Only expiring protection (retain-until) is available | Refused before a non-last-copy delete is sent | #1969, #1970 |
| Local copy deliberately evicted while the archive survivor is protected; the archive copy is later verified absent without role-scoped authorization | The role stays available through the archive after the eviction. After the verified loss it is `missing`. While the archive cannot be checked it is `temporarily-unavailable`. | #1970, #1972 |
| Replication credential attempts a target delete | Refused | #1969 |
| Takeover with an accepted delete stalled beyond client exit and credential disable | The generation does not advance until the Silo process exit is verified; reconciliation records the delete's actual outcome | #1966, #1972 |
| Silo exit or data-root exclusivity cannot be verified during takeover | Takeover stays fenced; no admission, publication or expiry | #1972 |
| Generation 2 recorded only at an unavailable archive witness while a readable backup snapshot shows generation 1 | Recovery stays fenced; once the archive is readable it opens generation 3 | #1972 |
| Uncertain write of a generation witness record | No identity is issued in that generation; the next opening uses the following generation | #1972 |
| Backup newer than archive; archive newer than backup | Cutoffs reported independently; identities above every witnessed generation; no identity reissued | #1972 |
| Capture present only in the newer sealed backup, with no archive receipt | `recoverable` from the backup's exact bytes and reopens; the older archive does not make it missing | #1972 |
| Resume before payload verification completes | Capture resumes at `resume-ready`; unverified roles are reported as such and never claimed available | #1971, #1972 |
| Satellite capture with a qualifying archive reference and no support product | Raw stays held until the last qualifying archive reference settles | #1970 |
| Video or nightly input expiry while the job is interrupted, after a terminal failure, and during a retry | Held while interrupted; released at the terminal state; the retry revalidates and ends `Unavailable` naming any expired input | #1970 |
| Early abort with zero admissions | Legacy reinstall not invoked; candidate, former instance and camera stay fenced | #1975 |
| Early abort after a durable admission | Forward recovery | #1975 |
| Operator pause, including a same-version no-op, between the former start and its resume on an authority-preserving route | Resume refused | Route owner, #1975 |
| Operator no-op pause before the Silo first resume | First resume refused | #1966 |
| Candidate provider fact during the abort window, including an empty attempt table with a retained halt | Former provider acquisition stays fenced until reconciled | #1975 |
| Wrong endpoint or bucket | Refused before mutation | #1966 |
| Trash cleanup stalled | Reclaim-pending bound reached; pressure pause; no assumed space | #1970 |

### 11.3 Focused acceptance

Acceptance uses disposable volumes, bounded sizes and dry runs. The only long campaign is #1974's defined week. No
test operates on live storage or alters published evidence.

## 12. Downstream contract slices

The gates copy the GitHub dependencies of each issue.

| Issue | Implements | Gate |
| --- | --- | --- |
| [#1966](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1966) runtime | Topology, release record and artifact verification (2); pinned stock Silo, bucket, marker, fence bootstrap and per-generation credentials (4.3, 7.2); sync setting and cost, and durable acknowledged versions for P4 (5.2, 5.3); Silo stop, exit verification and data-root exclusivity (7.2); private listeners and credentials; binding preflight; paused first boot with an atomic first resume; device allocation (8.1, 8.4) | After #1965; independent of #1967 |
| [#1967](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1967) catalog | Identities, manifests, journal, exact-byte replay, trusted anchors and ordered replay from generation 1, publication states, P3 recovery and eligible-locator checks, catalog, format identities, copy- and role-scoped availability, pointers and layer bindings, rebuild (4.2–4.7, 5.2, 5.3, 6.1–6.3) | After #1965; any S3 fixture; independent of #1966 |
| [#1968](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1968) store | Every 3.2 disposition, spool and persistence condition, bounded startup, power-loss proof (3, 5). Records the delta between the accepted centered contracts at its integration source and 3.2 and 6.3. | After #1966 and #1967; release integration also needs the launch hold (9) |
| [#1969](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1969) replica | Copy and custody records, archive confirmation, coverage of `m/`, `j/`, `c/`, replication credential restriction, non-expiring per-version legal hold qualification, archive generation witness (4.5, 5.4, 6.4, 7.3) | After #1968 |
| [#1970](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1970) retention | Holds with terminal-state release and retry revalidation, the satellite archive edge, copy- versus role-scoped expiry, a non-expiring survivor hold with absent, present-and-quiesced and uncertain settlement, reclamation, pressure pause (5.5, 7.1, 7.3, 7.4) | After #1969 |
| [#1971](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1971) restore | Reopen by layer state through eligible locators and compute closure, leases, cache, and reporting of unverified roles during lazy recovery (5.4, 6, 7.5) | After #1969 |
| [#1972](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1972) recovery | Cutoffs and per-role dispositions, generation witnesses and identity ranges, takeover sequence, recovery states, lazy verification, local-only RTO, held expiry (4.5, 5.4, 7.2, 7.5, 8.5, 10.3) | After #1970 |
| [#1973](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1973) operations | States, pauses, alarms against budgets (5.5, 6.2, 10) | After #1970 and #1971 |
| [#1974](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1974) qualification | Budgets and matrices (10, 11) on the centered profile at the exact integration source. Measures RTO from recovery start to `resume-ready` with the one-week fixture, and times full-history transfer and role verification separately. Retains concurrent replica catch-up during capture and restore, the stock compression on and off comparison, resource ceilings (CPU, memory, temperature, NVMe), recovery measurements (RPO, RTO, temporary bytes), and the #1922 and #1923 merge SHAs, relevant #1928 cases, integration SHA and artifact digests. | After #1972 and #1973; qualification also needs the launch hold (9) |
| [#1975](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1975) canary | Handoff, early abort and fence, pauses, authoritative provider-state checks and floors (8.2–8.5) | After #1974 |
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
| Authority-preserving restore route for the former owner (8.3) | Provide a route meeting the 8.3 conditions before the canary, as an installer change outside #1965. Until then an early abort leaves the candidate, the former instance and the camera fenced. | Roy, before #1975 |
| Survivor protection mechanism (7.3) | Qualify non-expiring per-version legal holds on the pinned archive release; without them, every local eviction needs role-scoped last-copy authorization | #1969 evidence |
| Storage drain for takeover (7.2) | Qualify Silo stop, exit verification and data-root exclusivity on the pinned release; until then takeover on retained storage stays fenced | #1966 evidence |
| Durable acknowledged versions on the pinned Silo (5.3) | #1966 qualifies the disk-sync profile; if it cannot, publication needs a contract revision for a closed-state remap alternative | #1966 evidence, then Roy |
| Generation witness for local-only sites without an off-primary target (5.4) | Configure the sealed-backup target as a witness before the first resume; otherwise any recovery stays fenced | Roy, #1972 |
| Per-generation identity range size and any counter that cannot be partitioned (5.4) | Use a range far above any feasible per-generation count; fence any counter that can be neither partitioned nor proven | #1972 |
| Provider floors: capsule import or hold until elapsed (8.5) | Keep providers disabled on the candidate until the operator records that floors elapsed and no halt is active. Import a capsule only if a site needs provider data immediately. | Roy, #1966 or #1975 |
| Spool release versus the Silo sync setting (5.2) | Keep each spool copy until its version is persistence-qualified; the measured `MINIO_FS_OSYNC` cost selects the profile | #1966 evidence |
| Proposed budget targets (10.3) | Accept as the qualification bar or revise | Roy, before #1974 |
| Roadmap entry for #1964 | Add an index row when the epic is scheduled | Coordinator |
