# CameraAgent UI Implementation Inventory

This handoff covers the approved static public pages, local account boundary,
editable configuration, derived-product and event policy, runtime operations,
device registration, and Focus study. It records what the repository can support
now, what must be added before Blazor conversion, and where that work belongs.
Proposed names and routes are design contracts, not committed API compatibility.

## Approved Route Set

| Route | Visibility | Primary data |
| --- | --- | --- |
| `/` | Public | Current published preview, station summary, conditions |
| `/calendar` | Public | Month sky-day index and selected sky-day summary |
| `/calendar/{date}/frames` | Public | Bounded published-frame query and selected frame |
| `/calendar/{date}/time-lapse` | Public | Full sky-day generated sequence |
| `/calendar/{date}/keogram` | Public | Full sky-day time-versus-sky product |
| `/calendar/{date}/star-trails` | Public | Night-only product inside the sky day |
| `/frames/{publicId}/dewarped` | Public | Projection view of one published frame |
| `/events` and `/events/{publicId}` | Public | Sanitized local candidate list and detail |
| `/login` and `/Account/Login` | Public | Local CameraAgent operator sign-in |
| `/Account/Register` | Remove or owner-gate | Existing anonymous local self-registration is not approved |
| `/Account/Manage/*` | Owner only | Local profile, email, and password management |
| `/system` | Owner only | Read-only startup-validated configuration overview |
| `/system/configuration` | Owner only | Active revision, immutable drafts, history, diff, validation, apply/rollback state |
| `/system/configuration/drafts/{revision}` | Owner only | Composed acquisition source and immutable profile selections |
| `/system/configuration/profiles/cameras` | Owner only | Reusable camera-model geometry, modes, controls, and declared capabilities |
| `/system/configuration/profiles/lenses` | Owner only | Nominal reusable lens definitions |
| `/system/configuration/profiles/calibrations` | Owner only | Camera-lens-mode projection fits and evidence |
| `/system/configuration/profiles/installations` | Owner only | Station mount, alignment, horizon, and obstruction revisions |
| `/system/configuration/drafts/{revision}/pipeline` | Owner only | Cadence, control envelope, registered operation graph, typed options |
| `/system/configuration/drafts/{revision}/overlay` | Owner only | Ordered registered layers, templates, fonts, palette, and object styles |
| `/system/configuration/drafts/{revision}/catalogs` | Owner only | Read-only package inventory and editable query-policy selection |
| `/system/configuration/drafts/{revision}/environment` | Owner only | Observatory and optional virtual-environment settings |
| `/system/configuration/drafts/{revision}/storage` | Owner only | Named-root retention and artifact publication/upload policy |
| `/system/products` | Owner only | Time-lapse, keogram, and star-trail generation/publication policy |
| `/system/events` | Owner only | Transient runtime mode plus separate public local-event policy |
| `/system/operations` | Owner only | Acquisition/worker lifecycle and scoped read-only system facts |
| `/system/identity` | Owner only | Registered device identity and non-secret central metadata |
| `/system/identity/register` | Owner only | Local identity handoff and LogicHost envelope import |
| `/system/camera/focus` | Owner only | Exclusive camera focus session |
| LogicHost `/devices/register` | Central authenticated user | Companion pending registration and envelope issuance |

All archive dates use the station-local interval from sunrise through the next
sunrise. Public coverage describes data that exists; it does not claim what the
camera can acquire or what a schedule intended.

## Existing Repository Capability

### Latest Frames

- `GET /api/v1.0/frames/latest`, `/raw`, and `/processed` encode the current
  in-memory snapshot as JPEG. They are latest-only, currently anonymous, and use
  `no-store`; they are not durable publication history:
  `src/HVO.SkyMonitor.CameraAgent/Controllers/v1/FramesController.cs:12-79`.
- `ILatestFrameAccessor` stores Preview, Raw, and Combined bytes in singleton
  memory and does not restore them after restart:
  `src/HVO.SkyMonitor.CameraAgent.Common/Frames/LatestFrameAccessor.cs:9-89`.
- The latest accessor is updated after file storage succeeds:
  `src/HVO.SkyMonitor.CameraAgent.Common/Capture/Processing/NoOpFileStorageProcessingStep.cs:101-116`.

### Local Frame History

- Local files use `frames/yyyy/MM/dd/<Role>/...`, sidecars, and a daily JSONL
  compatibility index:
  `src/HVO.SkyMonitor.CameraAgent.Common/Storage/FileSystemFrameStorageService.cs:54-85,150-168`.
- `IFrameStorageService.List` queries one UTC date and role and returns path,
  timestamp, and role only:
  `src/HVO.SkyMonitor.CameraAgent.Common/Storage/IFrameStorageService.cs:5-37`.
- Listing validates payload/index/sidecar agreement, tolerates legacy records,
  orders deterministically, and caps results at 10,000:
  `src/HVO.SkyMonitor.CameraAgent.Common/Storage/FileSystemFrameStorageService.cs:464-535,552-609,674-688`.
- There is no station-local sky-day query, cursor paging, nearest-time lookup,
  month aggregation, safe content opener, or public DTO.

### Artifact Metadata

- `ArtifactManifestV2` binds a safe relative path to reconstruction metadata:
  `src/HVO.SkyMonitor.AgentCore/ArtifactManifestV2.cs:5-35`.
- `ReconstructionDescriptor` includes capture controls, layout, artifact role,
  variant, media type, checksum, recipe, and lineage:
  `src/HVO.SkyMonitor.AgentCore/ReconstructionDescriptors.cs:25-41,73-128`.
- CameraAgent preview/annotation recipes currently request packed image output,
  not a persisted final public JPEG:
  `src/HVO.SkyMonitor.CameraAgent.Common/Capture/Processing/PreviewCaptureProcessingStep.cs:50-71` and
  `src/HVO.SkyMonitor.CameraAgent.Common/Capture/Processing/AnnotationCaptureProcessingStep.cs:144-189`.

### Local Events

- Edge/Hybrid transient processing has a durable SQLite journal containing
  candidates, workflow state, evidence, finalization, submission, and
  acknowledgement payloads:
  `src/HVO.SkyMonitor.CameraAgent.Common/Transients/SqliteTransientCandidateJournal.cs:14-119`.
- The journal reads operational/resumable work, not a bounded public history
  projection:
  `src/HVO.SkyMonitor.CameraAgent.Common/Transients/SqliteTransientCandidateJournal.cs:474-511`.
- Local finalization can produce Validated, Rejected, or NeedsReview events, but
  currently writes no local display derivatives, reviews, or notifications:
  `src/HVO.SkyMonitor.CameraAgent.Common/Transients/TransientWorkerService.cs:265-456`.
- Shared event contracts define candidate/event states, classifications,
  severity, assessments, reviews, notifications, and derivative kinds:
  `src/HVO.SkyMonitor.Processing/TransientEventContracts.cs:6-105,287-330`.
- Central submission acknowledgement confirms accepted/duplicate receipt only;
  it carries no authoritative review result:
  `src/HVO.SkyMonitor.Processing/TransientCandidateDeliveryContracts.cs:5-34`.
- Mode availability is not uniform. Off and Central modes have no local event
  history. Hybrid persists a submitted `TransientCandidateV1` without an
  assessment/classification, while Edge finalization creates assessed local
  events. A public projection must expose fields as unavailable in Hybrid rather
  than inventing a classification:
  `src/HVO.SkyMonitor.CameraAgent.Common/Transients/TransientWorkerService.cs:281-286,401-456,565-585`.

### Central-Only Capability

- LogicHost frame/artifact history and retrieval are SQL/MinIO-backed and
  authenticated:
  `src/HVO.SkyMonitor.LogicHost/Controllers/FrameHistoryController.cs:11-66`,
  `src/HVO.SkyMonitor.LogicHost/Controllers/ArtifactHistoryController.cs:10-109`, and
  `src/HVO.SkyMonitor.LogicHost/Controllers/ArtifactRetrievalController.cs:9-43,148-225`.
- LogicHost event list/detail, review, retry, release, reprocessing, and central
  derivatives are authenticated central workflows:
  `src/HVO.SkyMonitor.LogicHost/Controllers/TransientEventsController.cs:7-53,140-188,234-289`.
- CameraAgent must not reference or proxy those host-private DTOs. A future
  review mirror requires a separate versioned transport-neutral contract.

### Local Configuration

- `CameraAgentHostOptions` binds host storage, capture distribution, transient,
  environmental delivery, retention, upload, agent, and observatory settings:
  `src/HVO.SkyMonitor.CameraAgent.Common/Options/CameraAgentHostOptions.cs`.
- Camera/module/rig/pipeline configuration is loaded from the configured JSON
  file and validated before publication:
  `src/HVO.SkyMonitor.CameraAgent.Common/Configuration/FileCameraAgentConfigurationLoader.cs` and
  `CameraAgentConfigurationInitializer.cs`.
- Validation covers required module and agent identity, dimensions, cadence,
  control ownership, setting bounds, processing-step identity, dependencies,
  cycles, roles, compound inputs, and option data annotations.
- `CameraAgentConfigurationAccessor` accepts one configuration exactly once.
  There is no configuration page, sanitized DTO, validate-only endpoint, reload,
  draft, apply, rollback, or revision history in the current host.
- Configuration source precedence remains standard .NET host configuration plus
  the configured camera JSON file. The UI must not imply that values from
  environment variables, user secrets, or protected provisioning state can be
  safely revealed or edited.

### Local Operator Identity

- CameraAgent uses a separate SQLite ASP.NET Identity store and cookie named
  `CameraAgent.Auth`; it does not share LogicHost users or cookies:
  `src/HVO.SkyMonitor.CameraAgent/Program.cs` and
  `Components/Account/IdentityRevalidatingAuthenticationStateProvider.cs`.
- Startup creates the configured owner or reconciles its email and password.
  `ApplicationUser.IsSiteOwner` is set only when a user is newly created; an
  existing configured user is not promoted and a previous configured owner is
  not demoted. The property is not currently enforced by an authorization policy:
  `src/HVO.SkyMonitor.CameraAgent/Data/CameraAgentIdentitySeeder.cs`.
- `/Account/Register` is currently anonymous and immediately signs in ordinary
  users because confirmed accounts are not required. This is not approved for
  the target UI and is unsafe while protected pages require only authentication.
- Existing `/Account/Logout` mutates the authentication cookie during GET
  initialization. Replace it with a non-mutating confirmation GET and an
  authenticated antiforgery POST with a validated local return URL.
- The email sender intentionally does not deliver or log token-bearing links.
  Forgot-password, confirmation, and email-change UI therefore cannot claim a
  working delivery path:
  `src/HVO.SkyMonitor.CameraAgent/Services/LoggingEmailSender.cs`.
- The configured owner password is reconciled at startup. A password changed
  only through account UI can be replaced on restart unless protected deployment
  configuration is changed too.

Target account-route disposition:

| Existing route | Target disposition |
| --- | --- |
| `/login`, `/Account/Login`, `/Account/Logout` | Retain; static SSR/form POST, safe local return URL, antiforgery on sign-out |
| `/Account/Register`, `/Account/RegisterConfirmation` | Remove from anonymous navigation and disable unless an owner-managed invitation contract is added |
| `/Account/ForgotPassword`, reset, resend, and confirmation routes | Disable with deployment-managed recovery guidance until real token delivery exists |
| `/Account/Manage` | Retain owner-only profile surface |
| `/Account/Manage/Email` | Owner-only but disable mutation until confirmation delivery exists |
| `/Account/Manage/ChangePassword`, `/SetPassword` | Disable for the configured owner while deployment configuration remains authoritative |
| Lockout, access-denied, invalid-user states | Retain truthful terminal states without leaking account existence |

Cookie-changing Identity forms remain static SSR/form-post pages through the
existing `[ExcludeFromInteractiveRouting]` boundary unless explicit replacement
HTTP endpoints are designed. Do not move passwords or Identity tokens through an
interactive Blazor circuit.

### Device Registration and Bootstrap

- `/devices/bootstrap` currently requires any authenticated local user, creates
  a persistent 32-hex-character device ID and 10-character verification code,
  accepts an envelope, and displays non-secret registration metadata:
  `src/HVO.SkyMonitor.CameraAgent/Components/Pages/Devices/DeviceBootstrap.razor`.
- LogicHost `/devices/register` is a separate authenticated wizard. It selects an
  owned active observatory, records device and operator-entered verification
  values, creates a Pending registration, and issues a short-lived envelope:
  `src/HVO.SkyMonitor.LogicHost/Components/Pages/Devices/RegisterDeviceWizard.razor`.
- Pending creation checks observatory ownership, but the existing registration
  list is not owner-filtered and alternate envelope-issuance paths do not
  revalidate ownership. Owner filtering and authorization inside every issuance
  service/API path are prerequisites for the companion design.
- The verification code is self-attested. LogicHost hashes what the operator
  enters but does not independently retrieve or compare a code from CameraAgent.
- CameraAgent anonymously redeems the envelope, decrypts the returned payload,
  protects it with local Data Protection, persists `device-secrets.dat`, and
  seeds the rig profile:
  `src/HVO.SkyMonitor.CameraAgent/Services/DeviceBootstrapWorkflow.cs` and
  `DeviceSecretStore.cs`.
- The bootstrap response carries its AES decryption key alongside ciphertext.
  Response encryption is not protection from a network observer; TLS is the
  transport confidentiality and integrity boundary.
- Current central-service defaults permit plain HTTP. Require HTTPS for bootstrap
  and provisioned identity endpoints outside an explicit loopback/development
  exception and fail closed in non-development environments.
- Clearing secrets deletes only persisted local credential state. It does not
  delete device identity, revoke the central registration, or evict configuration
  and cached tokens already loaded by the running process. Restart is required to
  eliminate retained process state. The active central registration must be
  revoked before replacement to satisfy the unique active-device constraint.
- The per-device key/registration expiration is separate from the fleet-shared
  OAuth client included in provisioning. Fleet credential rotation and revocation
  require a separate operational boundary and are not implied by device expiry.

## Missing Public Read Models

### Station and Current Preview

`PublicStationProfile`

- Display name, public description, approximate location label, timezone,
  attribution, and disclosure policy.
- Never project exact configured latitude, longitude, elevation, device IDs, or
  storage details.
- Optional sensor/optics and approximate setting text require an explicit public
  disclosure allowlist; they must not be projected directly from rig or
  observatory configuration.

`PublishedPreviewCurrent`

- Opaque public frame ID, capture/publication times, final JPEG dimensions,
  source count/integration, checksum-backed ETag, freshness, and content URL.
- Durable latest-published pointer that survives restart and retains the last
  valid image.

`PublicStationState`

- Small allowlisted state such as Current, Delayed, Stale, or Offline plus
  independent image/environment timestamps.
- No health-check names, queue depth, exception, storage, or quarantine data.

`PublicEnvironmentalSnapshot` and `PublicEnvironmentalObservation`

- Allowlised measurement kind/display label, nullable value, public unit,
  observation time, and per-value Current/Stale/Missing state.
- Optional bounded history for cloud and environmental summaries. Preserve
  missing/stale values rather than carrying them forward as current.
- Existing neutral contracts already model value kind, quality, validity, and
  staleness:
  `src/HVO.SkyMonitor.Processing/EnvironmentalObservationContracts.cs:15-48,81-97,166-177`.

`PublicEnvironmentalObservationPage`

- Query parameters: inclusive `from`, exclusive `to`, allowlisted `kinds`,
  opaque seek `cursor`, and bounded `take` (default 100, maximum 500).
- Newest-observation-first ordering with stable observation-ID tie-breaker;
  response includes items and `nextCursor`. Current snapshot retrieval remains
  separate from history paging.

### Calendar and Frames

`SkyDayBounds`

- Local date, station timezone, sunrise start, next-sunrise end, and UTC bounds.
- Boundary calculation must handle configured IANA timezone rules. The existing
  `SolarAltitudeClassifier` classifies an instant but does not calculate sunrise
  crossings: `src/HVO.SkyMonitor.Astronomy/SolarAltitude.cs:17-57`.

`SkyDaySummary`

- Open/finalized state, daylight/night published coverage, candidate count,
  representative frame links, first/last frame, count, published span, cadence,
  gaps, conditions summary, and adjacent available dates.

`PublishedFrameSummary`

- Opaque public ID, capture/publication time, sky-day period, dimensions, media
  type, variant/recipe display identity, thumbnail/content URLs, and ETag.
- Must omit relative/absolute paths, Agent/Rig identity, internal artifact IDs,
  processing-profile hashes, and raw evidence locators.

`PublishedFrameDetail`

- All summary fields plus publication time, exposure, integration/source count,
  available conditions/cloud estimate, adjacent opaque frame IDs, full-content
  URL, and optional dewarped-product relationship.
- Every optional fact has an explicit Unavailable state; legacy records do not
  gain inferred exposure, publication, recipe, or condition facts.

`PublishedFramePage`

- Deterministic seek cursor, bounded page size, total when cheaply available,
  selected/nearest frame, and previous/next cursors.
- Filters: sky day, Full/Daylight/Night, nearest local time, and optional event
  proximity. Support bounded filmstrip and paged overview independently.

### Derived Products

`SkyDayDerivedProductSummary` and `DerivedProductDetail`

- Kind: TimeLapse, Keogram, or StarTrail.
- State: Unavailable, Pending, Current, Stale, Failed, Withdrawn,
  PayloadExpired, or EvidenceExpired. Atomic current-pointer transitions never
  silently fall back to a prior version; deliberate republishing creates a new
  version and an expired/withdrawn public ID resolves to a stable tombstone.
- Sky-day/period, generated time, source span/count/cadence, missing intervals,
  recipe/version, media type, dimensions/duration, orientation, ETag, and opaque
  content URL.
- No corresponding product contract, recipe, persistence model, controller, or
  content endpoint exists in this checkout. Rolling mean, not time-lapse, is the
  current window-model proof:
  `src/HVO.SkyMonitor.LogicHost/Services/CentralDerivativeRecipeCatalog.cs:50-69,101-134` and
  `docs/project-plan.md:629-652`.

`FrameProjectionDetail`

- Frame-scoped opaque product ID, source public frame ID, kind
  `DewarpedHorizon`, generated time/state, projection/orientation metadata,
  dimensions, ETag, and content URL.
- It is discovered from `PublishedFrameDetail` and resolved by the frame-scoped
  dewarped endpoint; it is not a peer in the sky-day product collection.

### Public Events

`LocalTransientEventPage` and `LocalTransientEventSummary`

- Bounded opaque cursor containing ordering time plus a stable public-ID
  tie-breaker; sky-day/period filter, local provisional state, display
  classification/confidence, observation interval, and display-product flags.

`LocalTransientEventDetail`

- Public ID, sanitized observations, display classification/confidence,
  duration/track facts, explicit authority label, limitations, related public
  frames, and display-safe artifacts.
- Never serialize the full journal/event contract anonymously. It contains
  internal agent, evidence, artifact, checksum, recipe, profile, submission, and
  workflow data.
- Public authority is separate from internal workflow state. Edge `Validated`,
  `Rejected`, and `NeedsReview` remain local outcomes and never mean centrally
  confirmed/rejected. Hybrid candidates expose classification/confidence as
  unavailable unless a separate local assessment has been persisted.

Public mode/state mapping:

| CameraAgent mode/source | Public list availability | Public state and fields |
| --- | --- | --- |
| Off | `Disabled`, empty items | No local event claims |
| Central | `CentralOnly`, empty items | No central result is mirrored locally |
| Hybrid submitted candidate | Item only with a display-safe derivative | `LocalUnassessed`; classification/confidence unavailable |
| Edge Pending/Provisional | Configurable public item | `LocalProvisional`; persisted assessment fields only |
| Edge Validated | Public item | `LocalAssessedProvisional`; never “confirmed” |
| Edge NeedsReview | Public item | `LocalNeedsReview`; authority remains local |
| Edge Rejected | Excluded from anonymous list | Opaque detail may return a publication tombstone; protected diagnostics remain hidden |

The page response carries mode-level availability separately from item state, so
an empty page distinguishes no results from no local event source.

`LocalCandidateDisplayArtifact`

- JPEG crop/preview/overlay URL, media type, dimensions, generated time, ETag,
  availability, and safe alt-text facts.
- Raw evidence, packed masks, and linear reconstruction remain protected.

`CentralReviewMirror` (future, optional)

- Versioned event/version identity, authoritative review state, effective
  classification/severity/confidence, reviewed time, sanitized reason category,
  derivative availability, and synchronization freshness.
- Without this contract, CameraAgent public output remains local provisional.

### Focus Session Contracts

`FocusCapabilitySummary`

- Manual versus motorized mechanism; bounded exposure/gain support; one-shot and
  loop capture support; analysis-only ROI; available focus metrics; unsupported
  hardware ROI, binning, offset, movement, and autofocus capabilities.

`FocusSessionState`

- Opaque session ID, owner-only state, lease expiry, version/ETag, prior capture
  mode label, pause-boundary state, restoration state, and terminal outcome.
- State machine: Reserving, Active, Restoring, Ended, Expired, FailedRestore.
  Exactly one session owns camera mutation; expiry, cancellation, disconnect,
  host shutdown, and restart all trigger idempotent restoration.

`FocusRestorationSnapshot`

- Persisted privately before normal capture is reported paused: session ID,
  camera module/configuration generation, capture-runner ownership/version,
  prior operating mode, actual exposure/gain/offset setpoint when supported,
  cadence/loop state, pause boundary, and configuration revision.
- Restoration compares generation/version before applying the snapshot, records
  every attempt/outcome, and never overwrites a newer operator/configuration
  change. Startup recovery resumes Restoring sessions before normal acquisition.

`FocusCaptureSettings` and `FocusMeasurement`

- Requested and actual exposure/gain/refresh/loop limit; analysis ROI; frame
  timestamp; opaque protected preview URL; focus score, median FWHM, valid/rejected
  stars, saturation, and explicit unavailable metrics.
- Preview stretch/overlays are client display state and never mutate pixels or
  quantitative measurements.

Owner-only endpoints must cover capability discovery, session create/read,
lease renewal, settings update, capture once, bounded measurement history, and
end-and-restore. Mutation uses optimistic session versioning and cancellation.
Reservation/restoration orchestration belongs in `CameraAgent.Common`; protected
HTTP and Blazor composition belongs in `CameraAgent`. Existing camera contracts
provide capture and setpoint application only and do not provide these session
semantics:
`src/HVO.SkyMonitor.AgentCore/ICameraModule.cs:6-28` and
`src/HVO.SkyMonitor.AgentCore/CaptureContracts.cs:34-51`.

### Protected Configuration Contracts

`ConfigurationSnapshot`

- Revision/hash, loaded and validated times, source display name, typed apply
  impact, and sanitized camera, rig, pipeline, observatory, storage,
  retention, delivery, environmental, and registration summaries.
- Every field is explicitly allowlisted. Secret values, credentials, connection
  strings, absolute paths, raw option JSON, and arbitrary exception messages are
  excluded even for owners.
- Pipeline steps expose stable display identity, required/optional state,
  dependency display IDs, and validation outcome without assembly-qualified type
  names or arbitrary options.

`ConfigurationValidationRequest` and `ConfigurationValidationResult`

- Owner-only validation targets a persisted immutable draft or the configured
  startup source; callers cannot supply JSON, arbitrary paths, or type names.
- Validation runs the same loader, module, option, and graph checks as startup in
  an isolated scope, disposes constructed modules/steps, and never publishes,
  applies, reloads, or mutates the active configuration accessor.
- Result contains a bounded list of sanitized field/step/error categories plus a
  candidate hash. Do not return raw exception text that can disclose paths,
  implementation types, or secret option values.
- Concurrency is bounded to one validation per host, cancellation is supported,
  and audit records include owner, candidate hash, start/end time, and outcome.
- UI states are Idle, Validating, Valid, InvalidCandidate, Cancelled, and
  Unavailable. A result belongs to the candidate hash and is never presented as
  the active runtime revision unless that exact revision was loaded at startup.

`ProtectedSystemOperationalSnapshot`

- Separate from configuration: bounded capture state, storage readiness,
  registration state, last heartbeat outcome/time, and rig-profile sync state.
- Sanitized owner-only projection with explicit freshness and unavailable values;
  no health-check payloads, paths, queue payloads, endpoint details, or exception
  text. It supplies the live status shown beside configured values in Settings.

### Editable Configuration Revisions

`CameraAgentEditableConfigurationV1`

- Contains only UI-owned acquisition-source alias/typed options, exact immutable
  camera/lens/calibration/installation/capture/processing profile selections,
  cadence/control envelope, explicit registered processing graph, overlay and
  catalog-query policy, observatory, and retention/upload selection against
  deployment-defined storage aliases.
- Deployment overlays remain immutable: paths, roots, connection/endpoints,
  credentials, Identity/provisioning/catalog state, ports/network, lane topology,
  SQLite ownership, and worker lease parameters.
- Ordinary editors accept registered aliases and registered typed option schemas.
  They never accept assembly-qualified types, arbitrary JSON, absolute paths, or
  operator-authored recipe/profile lineage IDs.
- Every field descriptor classifies the value as Editable, Derived,
  ProfileSelected, DeploymentOwned, Secret, or Unsupported. Hidden or disabled
  controls are presentation only; the server allowlist remains authoritative.

`RigProfileRevision` and capability contracts

- Camera, lens, camera-lens-mode calibration, installation/alignment, capture,
  processing, overlay style, and catalog-query profiles have stable IDs,
  immutable revisions/hashes, schema versions, provenance, lifecycle state, and
  bounded dependency references. Editing or cloning always creates a revision.
- Activated configuration materializes a complete snapshot of selected revisions.
  Durable capture and work records persist configuration revision, full hash,
  schema version, runtime generation, graph plan, and all semantic policy IDs.
- Physical camera binding is station-specific and separate from reusable model
  profiles. An immutable `PhysicalDeviceBindingRevision` contains adapter alias/
  version, stable protected hardware selector, expected camera-profile revision,
  discovery evidence, lifecycle state, and compatibility result. It is selected
  by physical configurations, and activation fails closed on missing, ambiguous,
  drifted, or mismatched hardware. SDK index and USB topology remain observations.
- Camera capabilities preserve Declared, AcquisitionSourceReported,
  OperationallyVerified, PolicyAllowed,
  and EffectiveSafeIntersection layers with source, time, adapter/profile version,
  range/step/unit, mode constraints, and explicit unknown/unsupported values.
- `AcquisitionSourceReported` is source-neutral and carries physical-adapter or
  virtual-source provenance. Physical reported ranges remain Unknown without a
  probed binding. A VirtualSky simulation-response revision depends exactly on the
  selected camera profile; duplicate source-option model selectors are rejected.
- Calibration binds exact camera/lens revisions, dimensions, binning, ROI, pixel
  format, focus/optical state, projection coefficients, fit evidence/checksum,
  residual/uncertainty, image circle, and local angular-scale behavior.
- A physically verified calibration additionally binds physical-device and
  optical-assembly/installation revisions plus focus, spacing, filter/window, and
  evidence provenance. Model/simulation fits carry Nominal or Simulated evidence
  class and cannot satisfy a physical-verification requirement.
- The storage-neutral Astronomy schema defines projection coefficient convention,
  coordinate system, valid domain, transform order, algorithm/version, residual
  statistic, uncertainty, and evidence checksum. Until implemented, only the
  existing ideal-model intrinsics are claimable.
- Normalized sensor layout records native valid bits, container bits, alignment/
  scaling, black/white levels, packing, endianness, and normalization semantics.

`ProcessingOperationDescriptor` and `OverlayCompositionProfile`

- A startup registry exposes stable alias/version, operation kind, execution
  scope, input/output contracts, option schema, required dependencies, resource
  bounds, deterministic/idempotent behavior, provenance rules, and availability.
- Immutable graph plans also bind implementation/package/dependency-closure digest,
  host/descriptor API compatibility, normalized option hash, exact role/variant/
  recipe selectors, cardinality, and missing/failed optional-input semantics.
- Built-in and trusted deployment-installed operations are selectable. Browser
  input never supplies executable code, assemblies, arbitrary type names, scripts,
  paths, URLs, credentials, or unregistered JSON.
- The pure graph produces packed display canvases. The overlay composer consumes
  an exact packed canvas, applies deterministic registered layers in z-order, and
  returns a packed annotated canvas; codec orchestration then performs one lossy
  encode for each unannotated/annotated output.
- Replace the existing side-effecting virtual-cloud publisher step with a pure
  registered Analyzer that returns versioned environmental metadata; the separate
  EnvironmentalDelivery scope publishes it.
- Final JPEGs use explicit versioned output recipes over canonical `Preview` and
  `AnnotatedPreview` roles. Their descriptors bind immediate source IDs, role/
  variant, semantic/implementation identity, codec/environment/options, checksum,
  and deterministic outcome semantics even when codec execution is orchestrated.
- Templates accept only allowlisted escaped tokens and fixed palette tags with
  bounded lines/length/slots/fonts/styles. Each token declares sanitized source,
  formatter, bounds, sensitivity, and permitted output scopes; public templates
  never read protected configuration. Registry version and normalized template
  hash enter recipe provenance.
- Custom presets are immutable safe data. In-process plugins use registry-only
  resolution and are trusted deployment code bound to pinned digest or verified
  signature/trust root, dependency closure, API range, alias-collision validation,
  package/version/checksum, graph plan, and artifact provenance. Untrusted
  extension code requires an out-of-process sandbox and is not part of this design.
- Durable artifact commit, publication/outbox delivery, codec, and telemetry are
  orchestration policies, not ordinary pure recipe nodes. Their idempotency,
  transaction, retry, and provenance contracts remain explicit.

`CatalogPackageIdentity` and `CatalogQueryPolicyRevision`

- Installed star SQLite snapshots are immutable, read-only,
  checksum/size/row-count/schema/preprocessing/source/license verified packages.
  Constellation topology and solar-system ephemerides have separate versioned
  provenance identities and compatibility declarations.
- HYG is a star package under the current scene classifier. Deep-sky selection is
  Unsupported until a separate exact package and versioned classifier add it.
- Package installation paths and activation are deployment-owned. Drafts may
  select an available package alias and bounded query policy only; they never edit
  rows, paths, checksums, package manifests, or ephemeris implementations.
- Validation resolves any deployment alias/pointer to exact package ID, manifest
  hash, database checksum, topology identity, and ephemeris identity, seals those
  values into the runtime generation, and rejects pointer drift or absence.
- Topology identity keeps source-line, HIP-lookup, and generated-artifact hashes,
  preprocessing, catalog compatibility, unresolved endpoints, and omitted
  segments separately. The HYG 4.2 package truthfully records missing HIP 55203.
- Solar-system selection is unavailable until an `EphemerisIdentity` provides
  provider/model/data/kernel versions/hashes, reference source, frame/effects,
  body/time capabilities, and compatibility with the sealed catalog context.
- Generated artifacts persist package manifest/hash, database checksum, topology
  identity, ephemeris provider/version, query-policy revision, source object IDs,
  and required attribution through storage-neutral Astronomy contracts.

`ConfigurationRevisionSummary` and `ConfigurationApplyAttempt`

- Immutable revision ID/sequence/schema/hash, state Draft/Validated/Active/
  Superseded, base revision, actor/times, validation summary, apply impact, and
  optional rollback relationship. Every edit/save creates a successor revision;
  no API mutates the content under an existing revision ID/hash.
- Validation is bound to the exact draft hash. Optimistic apply requires expected
  active revision and validated draft hash. A separate durable apply attempt binds
  command ID, expected values, deadline, confirmation impact, cancellation point,
  safe boundary/drain watermark, phase receipt, and sanitized failure.
- Draft persistence, audit, active pointer, and historical snapshots belong in
  `CameraAgent.Common`. Secrets and deployment values never enter revisions,
  diffs, validation issues, or audit payloads.

`ConfigurationApplyCoordinator`

- Uses one serialized idempotent apply command and persists durable phases before it
  stops new captures. It waits for a safe capture/ingress boundary, quiesces the
  selected worker scopes, creates a new runtime generation from the validated
  immutable snapshot, and leaves ASP.NET/Identity/UI running.
- Phases cover Accepted, Quiescing, OldGenerationDisposed, Initializing,
  StartingWorkers, CommittingActivePointer, RollingBack, Completed, Cancelled,
  TimedOut, Failed, and RollbackFailed with startup reconciliation at each crash
  boundary. The revision becomes Active only after required workers start.
- Failure disposes the failed generation and starts a new monotonic generation
  from the previous known-good revision; generation IDs are never reused. The
  attempt records `RolledBackFrom`. Historical queued captures keep
  their capture-time configuration and graph-plan hash.
- Safe first editable scope is camera/module, rig, cadence/control, explicit
  graph, observatory, and named-root storage policy after workers are revision-aware.
  Raw ingress roots, lane topology, delivery identity, and deployment options do
  not become editable merely because an acquisition supervisor exists.
- Transient mode/required/timeout apply is blocked while active durable transient
  state exists. Required lane removal is blocked with unfinished work. A graph
  change never reinterprets existing node outcomes under a new plan hash.

### Derived Product and Event Policy

`DerivedProductPolicyRevision`

- Versioned settings per TimeLapse, Keogram, and StarTrail: enabled/public state,
  sky-day period, generate-after-close delay, minimum coverage, source selector/
  sampling, gap behavior, output format/dimensions/quality, orientation, event
  markers, retention, and recipe identity selected from registered definitions.
- Policy changes affect future products only. Manual regeneration creates a new
  immutable version through a bounded durable job and never silently replaces a
  previous output. Missing intervals remain explicit unless a versioned recipe
  deliberately defines and discloses interpolation.
- No product recipe, scheduler, persistence model, job, or endpoint currently
  exists. Implement one product end-to-end, preferably time-lapse or keogram,
  before enabling its policy controls.
- A durable idempotent job manifest records computed sky-day bounds, close and
  late-frame cutoff, deterministic idempotency tuple, selected policy/config/graph/
  runtime identities, exact source role/variant/recipe compatibility, eligible
  slot timeline/tolerance/denominator/tie-break, ordered source IDs/checksums and
  descriptor/manifest hashes, exclusions/gaps, orientation/projection, recipe
  semantic/implementation, algorithm/codec/environment/options, output checksum,
  prior version, retries, publication, and terminal state.
- Canonical semantic identity is station/agent, sky-day bounds identity, product
  kind, policy revision/hash, exact resolved source-selector/recipe identity,
  cutoff and source-set identity, plus explicit Initial/LateFrame/Manual generation
  intent. Retry command IDs are excluded and return the original receipt.
- Job creation transactionally snapshots and pins ordered sources before runnable.
  Pressure respects active pins or atomically terminates the job with an audited
  missing-source reason. Manifest/publication/checksum/tombstone survive at least
  as long as any public payload/ID; later source expiry yields an explicit evidence
  availability state.
- Before job creation, either apply prospective sky-day source holds from capture
  time or validate that source-role retention exceeds the maximum sky-day span,
  close delay, and scheduler jitter. Pre-pin pressure loss is explicitly classified
  as a visible gap, coverage failure, or terminal retention failure by policy.
- The job manifest survives at least the tombstone lifetime. Tombstones have an
  explicit duration and retain product/version identity, state/reason, output
  checksum, ordered source/manifest hashes, policy/recipe identities, predecessor,
  publication state, and retention expiry.

`TransientRuntimePolicyRevision` and `LocalEventPublicationPolicy`

- Runtime mode Off/Edge/Central/Hybrid, required flag, candidate timeout, poll,
  adjacency, star mask, extraction, assessment, and association fields require
  one immutable semantic runtime-policy ID/hash. Existing option contracts exist,
  but profile registration, host configuration, persistence, and replacement of
  hard-coded defaults remain required.
- Publication is separate: enable public archive, require display-safe derivative,
  eligible local states, classification/confidence disclosure, derivative kinds,
  product markers, and metadata/derivative retention.
- Off and Central expose no local feed. Hybrid classifications remain unavailable
  without persisted local assessment. Rejected events are never anonymous, and
  all local outcomes remain provisional without a durable central review mirror.
- Resolve the latest event version first, then apply publication eligibility. A
  latest Rejected event removes the list item and may return a stable publication
  tombstone; never fall back to an older eligible provisional version. Withdrawn,
  PayloadExpired, and EvidenceExpired are publication states separate from the
  existing `TransientEventState` workflow enum.
- Deployment pre-provisions one dormant-capable durable transient lane and retains
  SQLite/root ownership. Editable Mode/Required changes routing, required-worker
  policy, and generation only; it never creates, moves, or removes durable storage.
- Staged frames, candidates, associations, and event versions persist resolvable
  policy revision/hash and generation. Association across revisions is permitted
  only by declared compatibility; otherwise retain old workers through the
  adjacency horizon or establish a durable drain/cut boundary.
- Producer, assessment authority, workflow state, review disposition/availability,
  and public trust label are independent persisted/projected dimensions. Metadata,
  display derivatives, raw evidence, related frames, and provenance have separate
  retention states; metadata duration never implies reconstructable evidence.
- Project the actual `TransientAssessmentAuthority` enum independently from
  `TransientAssessmentProducerV1`; current Edge may be Authoritative within the
  local assessment contract while its producer is deterministic-v1 and its public
  trust label still says not centrally confirmed.
- Anonymous publication activation is blocked until evidence retention, pressure
  release, and tombstone policy are explicit. Review disposition remains
  Unavailable until an append-only authorized/audited local or central mirror exists.

### Runtime Control Contracts

`AcquisitionSupervisor`

- Desired Running/Paused/Stopped and observed WaitingForConfiguration, Starting,
  Running, Pausing, Paused, Stopping, Stopped, Recovering, or Faulted. Snapshot
  also carries version, generation, phase, active revision hash, safe-boundary
  state, freshness, categorized failure/retry, and allowed commands.
- Start/Pause/Resume/Stop/Restart commands are owner-only, serialized, idempotent
  by command ID, optimistic by expected version, bounded by deadline, and audited.
  Pause acknowledges only after no new exposure starts and any readout plus raw
  ingress handoff settles. Stop disposes the module; Restart never starts a new
  generation until the old generation is disposed.
- Every command ID binds canonical request hash, actor, and scope. An identical
  retry returns the original receipt; reuse with changed expected version,
  deadline, watermark, command, or payload returns Conflict.
- Cookie-authenticated command mutations require the current owner policy and
  antiforgery. Confirmation presents scope, expected version/generation, safe
  boundary or watermark, deadline, impact, cancellation point, and rollback.
  Receipts expose bounded progress and Completed/Cancelled/TimedOut/Failed/Conflict
  terminal state without leaking raw exceptions.
- A long-lived supervisor owns replaceable child generations. Controllers must
  never call `StartAsync`/`StopAsync` on the current hosted-service singleton.

`ProcessingSupervisor`

- Explicit worker scopes: StandardGraph, TransientDetection, ArtifactDelivery, and
  EnvironmentalDelivery. Retention, fleet heartbeat, health refresh, and web
  hosting remain active by default.
- Observed states Starting, Running, Quiescing, Paused, Draining, Stopping,
  Stopped, Recovering, or Faulted with generation, pending/leased/retry/terminal
  counts, optional drain watermark, pressure, freshness, and allowed commands.
- Pause stops new claims after settling the current lease. Drain uses a durable
  work-ID watermark, never an unbounded "until empty" while acquisition produces.
  A paused required lane is pressure-aware and may request acquisition pause.
- Durable intake/staging and worker claims are distinct. Pausing workers leaves
  staged work durable; quiesce stops new claims after current leases, and a drain
  targets a persisted work-ID watermark/deadline rather than waiting for empty.
- Model raw ingress/distribution separately with accepted/staged watermarks,
  backlog/pressure/freshness, always-on dependency rules, and no worker command
  that can stop staging while acquisition continues. Split current combined claim
  loops before exposing independently controllable worker scopes.

`ContainerRestartCoordinator`

- Capability Unsupported, ExternalOperatorRequired, or SupervisorAvailable.
  A narrow out-of-process supervisor may gracefully restart only this application
  container with the same deployment and mounted state, then verify liveness.
- CameraAgent never receives Docker socket access. No UI host reboot, Docker
  daemon control, arbitrary start/stop, rebuild/recreate, network mutation, or
  data reset is exposed. Without a supervisor, restart remains disabled.
- Restart is asynchronous and status ownership remains in the external supervisor
  across the process outage. It rejects command-ID payload reuse and persists an
  opaque request ID, deadline, original boot session, and Accepted/Stopping/
  Starting/HealthChecking/Completed/TimedOut/Failed/Unknown state. CameraAgent
  never claims synchronous success before its own process exits.
- The browser never receives deployment credentials or an unauthenticated restart
  endpoint. An owner/antiforgery-protected CameraAgent initiation exchange sends a
  signed single-purpose short-lived request over a mutually authenticated backend
  channel; supervisor status is opaque, bounded, and owner-gated.

### Scoped System Facts

`ProtectedSystemFactsSnapshot`

- Every value carries scope Process/Container/FileSystem/NetworkInterface/
  NetworkEndpoint/Camera/Ambient/Host, availability Current/Stale/Unsupported/
  Unavailable/PermissionDenied/ProbeFailed, source, observed time, and maximum age.
- Existing sources can supply process CPU/working set, filesystem capacity and
  pressure, queues, software/config hashes, and camera temperature. New bounded
  adapters are required for cgroup CPU/memory/PID/OOM facts, interface/address/
  route/DNS/counters, endpoint probes, managed runtime memory, and mount identity.
- Host NIC/IP, host CPU/RAM, SoC/NVMe temperature, throttle/power state, and host
  networking are unavailable unless a separate read-only host agent is explicitly
  deployed. The UI never labels container or process values as host measurements.
- Expensive filesystem sizing and probes are cached/rate-limited; raw paths,
  credentials, arbitrary exceptions, and unrestricted network targets are omitted.

### Owner Authorization and Account Boundary

- Make `LocalIdentity:AdminEmail` the single owner authority. Startup reconciliation
  must transactionally promote the matching normalized account, demote every
  previous owner, reject ambiguous duplicates, and fail closed if it cannot
  establish exactly one owner. The authorization policy performs a server-side
  lookup and requires both the current normalized configured email and persisted
  `IsSiteOwner`; it does not trust a stale cookie claim alone.
- Apply that owner policy to `/system`, Focus, device bootstrap, account mutation,
  and protected operational APIs; ordinary `[Authorize]` is not enough.
- Remove anonymous self-registration or require an explicit owner-managed
  invitation contract. The approved mock does not introduce a first-user claim,
  roles, central SSO, MFA, or email recovery because those capabilities do not
  exist.
- Preserve safe local return-URL validation and POST/antiforgery sign-out. API
  challenges remain `401` rather than HTML redirects.
- Account pages must state that operator identity is local and that changing the
  configured owner password only in Identity is not durable across restart.

### Device Registration State and Actions

`DeviceRegistrationSnapshot`

- Local state: LocalUnregistered, Bootstrapping, Current, Expired,
  CentralAccessRejected, LocallyClearedCentralUnknown, SecretsUnreadable, or
  CentralUnreachable. Repeated authorization failure is not labeled Revoked.
- Central state remains separate: Pending, Active, Revoked, or Unknown. CameraAgent
  does not claim an authoritative central state without a defined mirror or
  verifiable response; its durable marker records only last-known local lifecycle.
- Includes only the persistent local device ID/code metadata, non-secret central
  registration metadata, device-credential expiry, heartbeat freshness, rig-sync
  state, and explicit allowed actions. The fleet OAuth credential has separate
  rotation state and no rendered values.
- Add a durable non-secret `DeviceRegistrationMarker` written atomically before
  destructive clearing. It contains schema version, local device ID, central
  registration ID, device public ID, observatory ID, issued/expiry times,
  HadRegistration/LocallyCleared lifecycle, update time, and no credential,
  endpoint, token, key, or secret. Bootstrap must return the registration ID so
  the marker and encrypted secrets can be reconciled after partial failure.
- Redemption is idempotent by registration/envelope identity. LogicHost durably
  retains and replays the same protected credential result for a bounded recovery
  window after activation. CameraAgent persists bootstrap phases and completes an
  atomic protected temp-write, fsync, rename, marker update, and rig seed before
  reporting Current; retry resumes rather than stranding an Active registration.
- State is derived from persistent local identity, this marker, encrypted secrets,
  current process state, and bounded central observations. Absence of a secret
  file must not erase registration history or be reported as central revocation.

Allowed transitions:

| From | Action | To |
| --- | --- | --- |
| LocalUnregistered | Import one-time envelope | Bootstrapping, then Current or truthful error state |
| Current/Expired | Owner revokes in LogicHost | Central state Revoked; local state remains unchanged until local coordination |
| Current/Expired after central revocation | Confirm, write marker, evict runtime credentials, clear secrets, restart | LocallyClearedCentralUnknown |
| LocallyClearedCentralUnknown | Import replacement envelope after LogicHost enforces no active predecessor | Bootstrapping, then Current |
| Current with repeated authorization refusal | Persist refusal/freshness without inferring cause | CentralAccessRejected |
| CentralAccessRejected | Bounded retry after backoff | Current, Expired, CentralUnreachable, or remain rejected without inferring revocation |
| CentralAccessRejected after owner-confirmed central revocation | Coordinated marker update, credential eviction, secret clear, external restart | LocallyClearedCentralUnknown |
| SecretsUnreadable | Restore key ring/state together and re-evaluate expiry/connectivity | Current, Expired, CentralAccessRejected, or CentralUnreachable |
| SecretsUnreadable after owner-confirmed central revocation | Quarantine unreadable file, update marker, evict credentials, external restart | LocallyClearedCentralUnknown |
| CentralUnreachable | Retry bounded status/bootstrap operation | Prior durable state or Current |

- Bootstrap and coordinated clear are owner-only antiforgery-protected form POSTs. Envelope
  values are excluded from prerendered state, interactive circuit persistence,
  model-state redisplay, logs, telemetry, traces, and exception messages, and are
  cleared immediately after the POST completes.
- LogicHost may render a newly issued envelope exactly once in the authenticated
  issuance response. It is cleared on dismissal/navigation and never appears in
  list/detail read models or later redisplay.
- Retire or secure the existing `/devices/bootstrap` route as part of migration;
  do not leave an alternate merely-authenticated clear/bootstrap surface.
- CameraAgent does not stop or restart itself. A successful coordinated clear
  returns `RestartRequired`; the owner restarts the host through the deployment
  supervisor, and startup reports `LocallyClearedCentralUnknown` from the marker.
- LogicHost retains the existing owner-validating
  `POST /api/internal/devices/delete` revocation operation, adds an authenticated
  `/devices/{registrationId}/revoke` confirmation page/form, and fixes the list
  and envelope paths so an owner can only reach their own registration.

Proposed protected endpoint methods:

Every cookie-authenticated mutation below enforces the current owner policy,
antiforgery, bounded request size, optimistic version/hash preconditions where
applicable, and a sanitized audit record. HTTP method idempotency never creates
an antiforgery exemption.

| Method and route | Purpose |
| --- | --- |
| `GET /api/v1.0/focus/capabilities` | Capability and unsupported-feature summary |
| `POST /api/v1.0/focus/sessions` | Reserve, snapshot, and pause at safe boundary |
| `GET /api/v1.0/focus/sessions/{sessionId}` | Lease/session/restoration state |
| `POST /api/v1.0/focus/sessions/{sessionId}/renew` | Bounded lease renewal |
| `PUT /api/v1.0/focus/sessions/{sessionId}/settings` | Versioned setpoint and loop settings |
| `POST /api/v1.0/focus/sessions/{sessionId}/captures` | One-shot protected focus capture |
| `GET /api/v1.0/focus/sessions/{sessionId}/measurements` | Bounded seek-paged results |
| `GET /api/v1.0/focus/sessions/{sessionId}/frames/{frameId}` | Protected preview content |
| `DELETE /api/v1.0/focus/sessions/{sessionId}` | End and idempotently restore snapshot |
| `GET /api/v1.0/system/configuration` | Owner-only sanitized active configuration snapshot |
| `GET /api/v1.0/system/configuration/revisions` | Bounded active/draft/history summaries |
| `POST /api/v1.0/system/configuration/drafts` | Clone active revision into a new owner-audited draft |
| `POST /api/v1.0/system/configuration/drafts/{revisionId}/successors` | Create immutable typed successor from expected base hash; no arbitrary JSON/path/type input |
| `POST /api/v1.0/system/configuration/drafts/{revisionId}/validate` | Validate exact persisted draft hash without apply |
| `POST /api/v1.0/system/configuration/drafts/{revisionId}/apply` | Versioned safe-boundary runtime-generation activation with rollback |
| `GET /api/v1.0/system/configuration/apply-attempts/{attemptId}` | Reconnectable phase/progress/terminal apply receipt |
| `POST /api/v1.0/system/configuration/apply-attempts/{attemptId}/cancel` | Versioned cancellation before the irreversible boundary |
| `POST /api/v1.0/system/configuration/revisions/{revisionId}/rollback` | Create and apply a new revision from known-good history |
| `GET /api/v1.0/system/configuration/profiles/{kind}` | Bounded camera/lens/calibration/installation/profile revision summaries |
| `POST /api/v1.0/system/configuration/profiles/{kind}` | Create a custom immutable typed profile revision |
| `GET /api/v1.0/system/configuration/physical-devices/discovery` | Sanitized installed-adapter discovery and ambiguity/compatibility state |
| `GET /api/v1.0/system/configuration/physical-device-bindings` | Bounded immutable binding revision summaries without protected selectors |
| `POST /api/v1.0/system/configuration/physical-device-bindings` | Create binding revision from one discovery token and expected camera profile |
| `GET /api/v1.0/system/configuration/operations` | Installed registered operation/plugin descriptors and availability |
| `GET /api/v1.0/system/configuration/catalog-packages` | Sanitized installed package/topology/ephemeris inventory and integrity |
| `GET /api/v1.0/system/status` | Owner-only sanitized operational/freshness snapshot |
| `GET /api/v1.0/system/configuration/drafts/{revisionId}/product-policy` | Typed future-product policy section of one immutable draft |
| `POST /api/v1.0/system/product-jobs` | Bounded manual regeneration job after recipes/jobs exist |
| `GET /api/v1.0/system/configuration/drafts/{revisionId}/event-policy` | Typed transient runtime/publication sections of one immutable draft |
| `GET /api/v1.0/system/acquisition` | Desired/observed acquisition state, phase, generation, version, commands |
| `POST /api/v1.0/system/acquisition/commands` | Idempotent versioned Start/Pause/Resume/Stop/Restart command |
| `GET /api/v1.0/system/acquisition/commands/{commandId}` | Reconnectable acquisition command receipt |
| `POST /api/v1.0/system/acquisition/commands/{commandId}/cancel` | Versioned cancellation before replacement begins |
| `GET /api/v1.0/system/processing` | Per-scope worker generation, lifecycle, pressure, watermark, commands |
| `POST /api/v1.0/system/processing/commands` | Idempotent versioned Start/Pause/Resume/Drain/Stop/Restart command |
| `GET /api/v1.0/system/processing/commands/{commandId}` | Reconnectable per-scope processing receipt |
| `POST /api/v1.0/system/processing/commands/{commandId}/cancel` | Versioned cancellation before its declared boundary |
| `GET /api/v1.0/system/facts` | Sanitized scoped process/container/filesystem/network/thermal facts |
| `GET /api/v1.0/system/container-restart` | Restart capability plus external-supervisor status link when available |
| `POST /api/v1.0/system/container-restart/requests` | Owner/antiforgery initiation; backend exchanges one signed short-lived supervisor capability |
| External supervisor `POST /cameraagent-restarts` | Accept narrow idempotent restart request and own status across outage |
| External supervisor `GET /cameraagent-restarts/{requestId}` | Opaque bounded restart progress and terminal receipt |
| `GET /api/v1.0/system/device-registration` | Owner-only registration state and allowed actions |
| `POST /api/v1.0/system/device-registration/bootstrap` | Owner-only antiforgery envelope import with no body logging |
| `POST /api/v1.0/system/device-registration/clear-secrets` | Owner-only antiforgery coordinated clear after central revocation confirmation; returns `RestartRequired` for external supervisor action |
| LogicHost `POST /api/internal/devices/delete` | Centrally revoke registration with transactional owner and device-ID validation |

## Proposed Public API Shape

All routes below are read-only, explicitly anonymous, bounded, sanitized, and
separate from operational APIs.

| Endpoint | Purpose |
| --- | --- |
| `GET /api/v1.0/public/station` | Public profile and bounded station state |
| `GET /api/v1.0/public/preview/current` | Durable current published preview |
| `GET /api/v1.0/public/environment/current` | Sanitized current values and per-value freshness |
| `GET /api/v1.0/public/environment/history` | Bounded condition/cloud history |
| `GET /api/v1.0/public/sky-days?month=YYYY-MM` | Month coverage index |
| `GET /api/v1.0/public/sky-days/{date}` | Selected sky-day detail |
| `GET /api/v1.0/public/sky-days/{date}/frames` | Cursor page, period, nearest time |
| `GET /api/v1.0/public/frames/{publicId}` | Sanitized frame detail |
| `GET/HEAD /api/v1.0/public/frames/{publicId}/content` | Final JPEG with ETag/range |
| `GET /api/v1.0/public/frames/{publicId}/dewarped` | Frame-scoped projection relationship/detail |
| `GET /api/v1.0/public/sky-days/{date}/products` | Product summaries |
| `GET /api/v1.0/public/products/{publicId}` | Product detail |
| `GET/HEAD /api/v1.0/public/products/{publicId}/content` | Display-safe content |
| `GET /api/v1.0/public/events` | Cursor-paged local event summaries |
| `GET /api/v1.0/public/events/{publicId}` | Public-safe local event detail |
| `GET/HEAD /api/v1.0/public/events/{publicId}/artifacts/{kind}` | JPEG display artifact |

Existing listing validates existence, root/path binding, index/sidecar agreement,
and byte length, but its read path does not provide the complete anonymous-content
security boundary. Add a race-safe content lease/opener that revalidates root
containment, rejects symlinks, locks against retention deletion, validates media
type and byte length, and verifies checksum immediately before serving. Existing
listing checks are at
`src/HVO.SkyMonitor.CameraAgent.Common/Storage/FileSystemFrameStorageService.cs:510-526,656-670`;
save-path symlink/checksum handling is at the same file `:214-305`.

## Blazor Component Map

| Page | Suggested component composition |
| --- | --- |
| Landing | `PublicStationHeader`, `PublishedPreview`, `CurrentConditions`, `RecentCandidateSummary` |
| Calendar | `SkyDayMonthPicker`, `SkyDayCalendar`, `SkyDaySelection`, `SkyDayCoverageTimeline`, `DerivedProductLinks` |
| Frames | `FramePeriodFilter`, `FrameTimeJump`, `PublishedFrameViewer`, `NearbyFrameStrip`, `FrameOverviewGrid`, `FrameMetadata` |
| Products | `DerivedProductTabs`, product-specific viewer, `DerivedProductMetadata`, `SourceCoverageSummary` |
| Events | `EventPeriodFilter`, `LocalEventList`, `LocalEventDetail`, `CandidateArtifactLinks`, `AuthorityNotice` |
| Sign in | `LocalIdentityBoundary`, `LoginForm`, `SafeReturnDestination`, `AccountAccessNotice` |
| Account | Static SSR `LocalAccountSummary`, `ProfileForm`, `EmailDeliveryNotice`, `DeploymentPasswordNotice`, `SignOutForm` |
| Settings | `ConfigurationStatus`, `ProtectedOperationalStatus`, `CameraRigSummary`, `PipelineGraphSummary`, `ObservatorySummary`, `StorageDeliverySummary`, `ConfigurationValidation` |
| Configuration editor | `DraftStatusHeader`, `RigCompositionEditor`, `AcquisitionSourceSelector`, `ProfileRevisionPicker`, `CadenceEditor`, `ProcessingGraphEditor`, `ObservatoryEditor`, `StoragePolicyEditor`, `ConfigurationDiff`, `ApplyImpactReview`, `RevisionHistory` |
| Profile libraries | `CameraProfileLibrary`, `LensProfileLibrary`, `CalibrationRevisionEditor`, `InstallationRevisionEditor`, `CapabilityEvidence`, `ProfileCompatibility`, `CalibrationEvidence` |
| Pipeline and overlays | `OperationCatalog`, `DependencyGraphEditor`, `TypedOperationOptions`, `OverlayLayerList`, `SafeTemplateEditor`, `ObjectStyleEditor`, `PluginAvailability` |
| Catalogs | `CatalogPackageInventory`, `PackageIntegritySummary`, `CatalogQueryPolicyEditor`, `CatalogProvenanceSummary`, `EphemerisAvailability` |
| Product settings | `ProductPolicySummary`, `TimeLapsePolicyEditor`, `KeogramPolicyEditor`, `StarTrailPolicyEditor`, `GenerationPolicy`, `ProductJobHistory` |
| Event settings | `TransientModeEditor`, `TransientRuntimeOptions`, `FixedDetectorProfile`, `LocalEventPublicationEditor`, `ModeAvailabilityPreview` |
| Operations | `AcquisitionControl`, `ProcessingScopeControl`, `DrainProgress`, `CommandReceipt`, `ContainerRestartCapability`, `ScopedSystemFacts`, `CapacitySummary`, `NetworkFacts`, `TemperatureFacts` |
| Device registration | `RegistrationStatus`, `LocalDeviceIdentity`, `RegistrationHandoff`, `EnvelopeImportForm`, `ProtectedMaterialNotice`, `ClearSecretsConfirmation`, state-specific recovery guidance |
| LogicHost companion | Existing central observatory selector, pending registration review, and envelope issuance components; no CameraAgent reference |
| Focus | `FocusCapabilityNotice`, idle/reserving/active/restoring/expired/failed state presentation, `FocusSessionBanner`, `FocusCaptureControls`, `FocusFrameViewer`, `FocusMeasurementSummary`, `FocusTrend` |

For components with logic, keep `.razor`, `.razor.cs`, `.razor.css`, and optional
`.razor.js` siblings. Use enhanced navigation/deep links for date, period, time,
cursor, product, and event state. Keep image bytes outside Blazor circuits.
Event selection uses an opaque route-backed ID, `aria-controls`, and deliberate
focus or live-region announcement when master/detail content is replaced.

## Ownership and Security

- `HVO.SkyMonitor.Astronomy` owns reusable sunrise-bound calculation and timezone
  independent astronomy behavior plus storage-neutral catalog-package, topology,
  query-policy, and ephemeris provenance contracts.
- `HVO.SkyMonitor.Catalog.Sqlite` owns concrete read-only SQLite package access;
  it may reference Astronomy but Astronomy never references this adapter.
- `HVO.SkyMonitor.Processing` owns host-neutral derived-product recipe and
  execution contracts.
- `CameraAgent.Common` owns local durable projections, sky-day query
  orchestration, public query services, immutable configuration revisions/store,
  validation/apply coordination, acquisition/processing supervisors, process/
  cgroup/filesystem fact adapters, storage adapters, retention reconciliation,
  safe content resolution, profile revision persistence/composition, station
  physical-device bindings, operation descriptor registration, and installed
  catalog adapter/query orchestration, local product scheduling, durable job state,
  prospective/source pins, output persistence, and publication coordination.
- `CameraAgent` owns HTTP endpoints, authorization declarations, response
  headers, and Blazor components/routes.
- `AgentCore` remains stable and transport-neutral; do not add ASP.NET, SQLite,
  SkiaSharp, or public-host projection dependencies.
- `CameraAgent` and `LogicHost` must not reference each other:
  `docs/project-plan.md:100-136`.
- Existing raw/processed/latest operational endpoints must not become the public
  archive surface. Publish only designated final JPEGs and sanitized summaries.
- Public content uses opaque allowlisted IDs, `nosniff`, immutable ETag/checksum
  semantics where applicable, and bounded cache policy. Operational/raw content,
  Focus, configuration, diagnostics, and review mutations remain authenticated.
- Local owner identity and central human identity remain separate. CameraAgent
  must not proxy LogicHost sign-in, receive a central human cookie, or describe
  outbound device credentials as operator authentication.
- Device bootstrap must be owner-only. Envelope and secret values are accepted
  only on the mutation boundary and never returned by CameraAgent read models,
  logs, metrics, traces, validation results, or redisplayed page state. LogicHost
  has one narrow exception to render a newly issued envelope once in the
  authenticated issuance response before clearing it.
- LogicHost device-registration listing and envelope issuance must filter and
  revalidate the current owner inside the service transaction. UI filtering or
  the ownership check performed only during pending creation is insufficient.
- Container restart belongs to an optional narrow deployment supervisor outside
  CameraAgent. The app receives capability/status and one bounded restart command,
  never generic Docker or host authority.
- Host network and hardware monitoring requires a separate read-only host agent.
  Its absence is represented as Unsupported; it is never replaced by shelling out,
  mounting the Docker socket, or relabeling container facts as host facts.

## Performance and Retention Requirements

- Month and sky-day projections must reconcile with retention so counts and
  links cannot outlive payloads.
- Frame paging uses seek/cursor ordering, never unbounded offset scans or an
  in-memory 1,824-item render.
- Filmstrip and overview are separate bounded queries. Blazor virtualization is
  optional enhancement, not permission for an unbounded API.
- Generated products store or cache immutable output; unchanged images/products
  must not be repeatedly encoded per client. UI performance gates require
  bounded paging and avoiding repeated encoding:
  `docs/planning/performance-validation.md:154`.
- Existing retention can reduce effective history to one day under pressure;
  public policy must explicitly select role/variant retention and truthful
  unavailable states:
  `src/HVO.SkyMonitor.CameraAgent.Common/Background/RetentionBackgroundService.cs:59-76,122-160`.
- Configuration history, command/audit history, system-fact history, and product
  job lists are bounded and cursor-paged. System probes are cached/rate-limited;
  no UI refresh performs recursive filesystem scans or arbitrary endpoint probes.
- Lifecycle command latency evidence covers safe capture pause, lease quiesce,
  drain watermark progress, generation restart, rollback, and pressure response.

## Recommended Implementation Sequence

1. Define publication policy and persist a final public JPEG plus durable latest
   pointer and sanitized station profile.
2. Add reusable sunrise-bound calculation and `SkyDayBounds` behavior in
   `HVO.SkyMonitor.Astronomy`; add safe content opening, bounded frame paging,
   station-specific month/day projection orchestration, and retention
   reconciliation in `CameraAgent.Common`.
3. Add explicitly public read-only APIs and Blazor landing, Calendar, and Frames
   routes in `CameraAgent`.
4. Add local public event projections and display-safe event derivatives. Keep
   authority local/provisional until an explicit central review mirror exists;
   define Off/Central/Hybrid/Edge field availability and internal-state mapping.
5. Define and implement derived-product contracts/recipes one product at a time,
   beginning with time-lapse or keogram, then add their Blazor viewers.
6. Implement deterministic owner promotion/demotion and enforce the site-owner
   policy; close anonymous self-registration and disposition every account route.
7. Fix LogicHost owner filtering/issuance authorization, then implement the
   device-registration state machine, one-time envelope handling, revocation,
   process-credential eviction/restart guidance, and secured bootstrap migration.
8. Add sanitized active-configuration and protected operational projections plus
   isolated validate-without-apply, then implement the read-only Settings route.
9. Implement long-lived acquisition and processing supervisors with persisted
   desired state, safe boundaries, generation restart, pressure coordination,
   lifecycle telemetry, command APIs, and focused failure/recovery tests.
10. Add immutable profile libraries/composition, physical-adapter capability
    descriptors, operation registry, catalog provenance/query contracts, and
    typed option metadata. Then add whole-configuration drafts/history, diff,
    validation, generation-safe apply, and known-good rollback. Keep all
    deployment-owned settings immutable.
11. Add scoped system-fact adapters and the Operations route. Add container restart
    only when a narrow external supervisor is actually deployed; keep host/network
    mutation permanently out of CameraAgent.
12. Implement product-policy persistence and generation controls only after each
    product recipe, scheduler/job, persistence, provenance, and retention contract
    exists. Add event publication policy after safe derivatives/read models exist.
13. Implement the protected Focus capability/session/lease/restoration and
   measurement contracts before converting its controls to interactive Blazor.
14. Validate public sanitization, identity authorization, secret non-disclosure,
   stale/restart behavior, retention races, cursor
   stability, content checksums/ETags, accessibility, and the canonical UI
   performance workload before exposing CameraAgent to an untrusted network.
