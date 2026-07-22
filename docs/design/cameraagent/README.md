# CameraAgent UI Design Studies

These static files contain the selected CameraAgent public experience, protected
setup and identity flows, plus the original landing-page comparisons. From the
repository root, serve them with:

```bash
node docs/design/cameraagent/serve-mockups.mjs
```

The devcontainer forwards the resulting site as **CameraAgent UI Mockups** on
port `4173`. The studies have no build step, external assets, or application
dependencies.

The devcontainer installs an exact Node.js 22 release, package-lock-pinned
Playwright tooling, Chromium, its native dependencies, and the Playwright VS
Code extension. Browser provisioning is nonfatal so a temporary package or apt
outage does not prevent the rest of the development environment from starting.
This supports repeatable desktop/mobile rendering now and future browser tests
when the selected concept moves into Blazor.

## Page Set

- `observatory-window.html`: image-led, place-oriented, and adapted to the
  established HVO dark shell and navigation pattern.
- `night-instrument.html`: compact and data-forward without exposing operator
  diagnostics.
- `quiet-horizon.html`: immersive and image-first, with supporting information
  below the fold.
- `focus.html`: protected Camera setup subpage showing the proposed safe focus
  session, analysis, manual-lens workflow, and restoration behavior.
- `calendar.html`: public month browser and selected sky-day summary, with
  daylight/night data coverage, local event markers, and derived-view links.
- `frames.html`: bounded sky-day frame browser with direct time selection,
  nearby thumbnails, paged overview, and selected-frame detail.
- `time-lapse.html`: full sky-day playback viewer with source coverage and event
  context.
- `keogram.html`: full sky-day time-versus-sky view with explicit orientation.
- `dewarped.html`: selected-frame horizon panorama with projection context.
- `star-trails.html`: night-only product within the selected sky-day record.
- `events.html`: sanitized local candidate browser and public-safe event detail.
- `sign-in.html`: local CameraAgent operator sign-in and explicit separation from
  LogicHost human identity.
- `register.html`: proposed closed-registration and deployment-managed owner
  recovery boundary. It intentionally replaces unsafe public self-registration.
- `settings.html`: owner-only read-only view of the startup-validated host,
  camera, pipeline, observatory, storage, and identity configuration.
- `configuration.html`: composed rig setup selecting immutable camera, lens,
  calibration, installation, capture, and processing profile revisions.
- `camera-profiles.html`: reusable camera-model geometry, readout modes,
  declared capabilities, and typed control ranges, separate from device binding.
- `lens-profiles.html`: nominal reusable lens definitions without sensor-specific
  projection authority.
- `calibration-profiles.html`: camera-lens-mode projection fits and immutable
  checksum-bound calibration evidence.
- `alignment-profiles.html`: station installation, boresight, roll, horizon, and
  obstruction revisions.
- `pipeline-settings.html`: cadence, exposure envelope, explicit dependency
  graph, registered operation catalog, and apply preconditions.
- `overlay-settings.html`: one ordered overlay composition with bounded templates,
  fixed palette/fonts, object styles, and a trusted-plugin boundary.
- `catalogs.html`: read-only installed catalog packages and editable query policy
  with package, topology, ephemeris, license, and checksum provenance.
- `products-settings.html`: planned time-lapse, keogram, and star-trail
  generation, publication, provenance, gap, and retention policy.
- `events-settings.html`: transient mode, current masking/association options,
  and separate anonymous local-event publication policy.
- `operations.html`: proposed acquisition/worker lifecycle controls plus
  read-only process, container, filesystem, thermal, and network monitoring.
- `account.html`: local profile, email, and password-management study with the
  current email-delivery and configured-owner limitations made visible.
- `identity-register.html`: unregistered CameraAgent identity, operator-carried
  device code, and envelope import workflow.
- `identity.html`: registered device state, non-secret central metadata, and
  destructive local secret-clear behavior.
- `logic-registration.html`: LogicHost companion study for observatory selection,
  pending registration review, and short-lived envelope issuance.

The approved pages use the same fictional station, core dataset, and synthetic
assets. All pages compose for desktop, narrow portrait, and short landscape
viewports.

## Shared Product Decisions

- Public views are anonymous, read-only, and cannot mutate system state.
- The final published JPEG preview is shown instead of raw evidence.
- The published preview is treated as a final canvas. The UI centers it, limits
  only its display height, and never crops, stretches, or adds image borders.
  Sensor ROI and processing/annotation layers may produce square, 4:3, 16:9, or
  another final aspect ratio, including borders that are part of the image.
- The last valid image remains visible when acquisition is delayed or offline.
- Image and environmental freshness are reported independently.
- Locally detected events are labeled as provisional unless authoritative review
  state is available through a defined durable contract.
- Exact coordinates, device identifiers, serials, storage details, queue state,
  credentials, and diagnostic failures are excluded from public views.
- Future public daily/monthly calendar, time-lapse, keogram, dewarped horizon,
  and event views should inherit the selected visual language. Focus, setup, and
  control views remain authenticated and should adapt that language for operator
  workflows.
- Operator accounts are local to one CameraAgent. They are not LogicHost users,
  do not use central SSO, and never expose central credentials to the local UI.
- Public self-registration is not part of the approved design. The existing
  anonymous `/Account/Register` behavior is a known unsafe boundary that must be
  removed or owner-gated before untrusted-network deployment.
- The current authenticated bootstrap route is also unsafe because it permits any
  signed-in local account to import or clear device credentials. Disable it until
  deterministic owner reconciliation, owner-only authorization, confirmation,
  antiforgery, and the coordinated local/central state machine are implemented.
- Current LogicHost registration listing and envelope issuance must be fixed to
  filter and transactionally revalidate ownership before any credential is issued.
- Device registration is a three-part operator-carried workflow: copy local
  CameraAgent identity, issue an envelope while signed in separately to
  LogicHost, then import that envelope on CameraAgent.
- The verification code is self-attested by the operator. LogicHost does not
  independently obtain it from CameraAgent, so UI language must not claim proof
  of possession or cryptographic device verification.
- Device keys, registration tokens, OAuth client secrets, API keys, filesystem
  paths, and connection strings are never rendered. A protected envelope has one
  narrow exception: LogicHost may render it once in the authenticated issuance
  response and CameraAgent may hold the pasted value only for the import POST.
- Bootstrap and provisioned identity endpoints require HTTPS outside an explicit
  loopback/development exception. The current default HTTP service URL is not an
  acceptable production confidentiality boundary.
- Clearing local device secrets does not revoke the central registration and
  does not delete the persistent local device identity. The running process may
  retain loaded OAuth configuration or cached tokens until restart, so clearing
  the persisted file is not an immediate runtime credential eviction.
- Configuration is currently read-only and reflects the revision loaded at
  startup. The target editor creates append-only immutable whole-configuration
  drafts, composes immutable profile revisions, validates an exact saved hash, and
  activates it through restartable acquisition/processing generations while the
  web host stays online. Product, event, catalog-query, and overlay policies are
  sections of that same revision, not independently applied mutable settings.
- Editing any saved draft creates a successor revision; a revision ID/hash is
  never reused for changing content. Apply progress belongs to a separate durable
  attempt record rather than mutating configuration revision identity.
- Deployment-owned paths, credentials, central endpoints, catalog installation,
  container ports, network configuration, and durable-lane topology remain
  read-only and outside UI-managed drafts.
- Transient durable storage is deployment-pre-provisioned and may remain dormant.
  Editable event mode changes routing and worker generations only; it never creates,
  moves, or deletes SQLite ownership or lane roots.
- Derived product policies apply only to future versioned output unless an
  explicit bounded reprocessing job is requested. Gaps remain visible and old
  output is never silently replaced.
- Acquisition source is separate from camera model. `VirtualSky` projects a
  catalog-driven synthetic sky through the composed rig, `RandomImage` remains a
  developer fixture, and physical cameras require deployment-installed adapters
  plus station-specific device bindings.
- VirtualSky binds one immutable simulation-response revision whose declared
  camera-profile dependency must exactly match the selected model; no independent
  free-standing sensor selector may silently override it.
- Physical activation selects an immutable station binding with adapter version,
  stable hardware selector, expected model profile, and discovery evidence and
  fails closed on missing, ambiguous, drifted, or mismatched hardware.
- Camera capability UI distinguishes declared profile metadata, source-reported
  facts with physical-adapter or virtual-source provenance, operational
  verification, policy allowance, and the effective safe intersection.
- Projection coefficients and local angular scale belong to a camera-lens-mode
  calibration. A fisheye does not have one truthful frame-wide plate scale.
- Physical calibration additionally binds the device/optical assembly,
  installation, focus/spacing/window state, exact mode, fit algorithm, evidence,
  uncertainty, and coefficient convention. Simulation evidence stays explicitly
  simulated and cannot be relabeled physically verified.
- Pipeline presets are safe versioned data over registered operations and typed
  schemas. Executable plugins are trusted deployment-installed code only; the UI
  never accepts assemblies, scripts, arbitrary types, paths, URLs, or raw JSON.
- Graph-plan identity includes operation and implementation/package versions,
  option schema and normalized hash, exact edge selectors/cardinality, and
  missing/failed optional-input semantics. Commit, publication, delivery,
  telemetry, and temporal processing are orchestration scopes outside the pure DAG.
- Overlay text uses allowlisted tokens and fixed color tags with bounded line,
  font, palette, escaping, and placement rules. All visual layers are composed in
  deterministic order and encoded once.
- Public overlay tokens resolve only from sanitized public projections and carry
  source, formatter, bounds, sensitivity/output scope, registry version, and
  normalized-template provenance. Escaping alone is not disclosure control.
- Catalog databases are immutable checksum-verified deployment packages. The UI
  edits query policy only; star/deep-sky rows, constellation topology, and
  solar-system ephemerides retain separate source and version provenance.
- Validation resolves deployment pointers to exact package, manifest, database,
  topology, compatibility, and ephemeris identities and rejects drift. Body
  annotation remains disabled until an exact compatible ephemeris is available.
- Acquisition and processing controls operate below the ASP.NET host. Intentional
  pause/stop is not a liveness failure, and stopped scopes expose Start while
  paused scopes expose Resume.
- Network, CPU, memory, filesystems, and thermal values always identify Process,
  Container, Filesystem, Camera, Ambient, or Host scope. Unsupported host facts
  remain unavailable rather than being inferred from the container.
- Container restart requires a narrow out-of-process supervisor. CameraAgent is
  never given the Docker socket, cannot start itself after a stop, and does not
  expose host reboot, network mutation, rebuild, recreate, or data reset.
- Archive dates represent a station-local sky day from sunrise through the
  following sunrise. This keeps one daylight period and its following night in
  a single record. Full-day, daylight, and night views filter that record rather
  than creating separate archives.
- Calendar coverage describes published data that exists, not current camera
  capability or acquisition policy. Daylight/night bars and candidate counts
  remain textual in accessible names and do not rely on color alone.
- The selected sky day exposes its frame browser as a primary action and through
  each representative daylight/night image. Representative links retain their
  period and timestamp so the browser can open near that frame.
- A sky-day frame browser should combine direct time selection, period filters,
  a bounded or virtualized thumbnail strip, and a paged at-a-glance grid. It must
  not render every full-resolution frame or thousands of thumbnails at once.
- Time-lapse, keogram, and star-trail links remain deep-linkable destinations.
  The mocks use full pages because their controls, metadata, provenance, and
  source links need durable URLs. A production shell may additionally open them
  in a modal without replacing those routes.
- Public event pages expose sanitized local candidates only. They never infer
  authoritative review state from local workflow state or central submission
  acknowledgement.

## Blazor Handoff

`implementation-inventory.md` maps the approved pages to existing code, missing
public read models and APIs, architecture ownership, sanitization boundaries,
and a recommended implementation sequence.

## Dependencies Before Blazor Conversion

- A durable latest-published-preview read model that survives CameraAgent restart.
- A configured public station profile with display name, approximate location,
  description, and location-disclosure policy.
- Local latest/history projections for environmental observations and cloud
  assessment.
- A durable, sanitized local event feed with explicit provisional/reviewed state.
- A bounded public station-state projection such as capturing, daylight standby,
  delayed, stale, or offline without detailed health-check output.
- Station-local sunrise boundary calculation, month/sky-day projections, and
  retention-aware cursor paging for frame summaries and details.
- Safe opaque content resolution with checksum/ETag semantics and no filesystem
  path disclosure.
- Versioned product summaries/details plus persisted or cached display content
  for each implemented derived product.
- Mode-specific event availability, display-safe event derivatives, and opaque
  event IDs/cursors. Central review remains unavailable until a review mirror is
  explicitly designed.
- Owner-only Focus capability, reservation, lease, capture, measurement,
  cancellation, and crash-safe restoration contracts.
- Deterministic owner reconciliation: promote only the currently configured
  owner, demote stale owners, and enforce a server-side owner policy. Ordinary
  authentication is not sufficient for setup, Focus, or bootstrap.
- Removal or owner-gating of anonymous local self-registration and an intentional
  owner-recovery policy that does not imply working email delivery.
- Owner-filtered LogicHost registration listing and owner revalidation on every
  envelope-issuance path, not only on pending-registration creation.
- A device-registration state machine covering unregistered, bootstrapping,
  current, expired, central access rejected, locally cleared with central state
  unknown, unreadable secrets, and central-unreachable states. Local and central
  status remain separate, and central revocation precedes replacement.
- A sanitized versioned configuration snapshot plus validate-without-apply
  contract. The current configuration accessor publishes exactly once and does
  not support reload, drafts, rollback, or validate-only execution.
- Immutable configuration drafts/history, registered editable schemas, a
  generation-safe apply/rollback coordinator, and restartable acquisition and
  processing supervisors. Historical queued captures retain their capture-time
  configuration and graph identity.
- Immutable camera, lens, paired calibration, installation/alignment, capture,
  processing, overlay-style, and catalog-query profile contracts. Activated
  snapshots materialize exact selected revisions so later library edits cannot
  reinterpret historical frames.
- Physical camera adapters with structured capability discovery and a safe
  declared/reported/verified/effective capability intersection.
- A stable operation descriptor registry, typed option schemas, generic overlay
  composer, bounded template/style parser, and deployment plugin manifest.
- Complete storage-neutral catalog-package and ephemeris provenance identities;
  package installation and activation remain deployment operations.
- Product recipes, sky-day-close scheduling, durable jobs, product persistence,
  retention pins, lineage validation, and public read models for each enabled
  time-lapse, keogram, and star-trail policy.
- Acquisition and processing desired/observed state machines, idempotent
  versioned commands, safe pause/drain boundaries, durable operator intent,
  pressure-aware coordination, and lifecycle telemetry.
- Sanitized process/cgroup/filesystem/network/temperature fact adapters with
  explicit scope, source, availability, freshness, and bounded probe cost.
- An optional narrow external container-restart coordinator. Without it, the UI
  reports `ExternalOperatorRequired` and keeps restart disabled.
