# Pipeline Operations Static Prototype

This prototype explores a GitHub Actions-style run view for the CameraAgent and
LogicHost image pipeline. It is deliberately separate from the Blazor hosts and
does not change runtime behavior.

## Preview

From the worktree root:

```bash
python3 -m http.server 4173 --directory docs/prototypes/pipeline-operations
```

Open `http://localhost:4173/dashboard.html` for the CameraAgent or
`http://localhost:4173/logichost.html` for the LogicHost network shell.

The pages can also be opened directly; they have no package, build, network, or
font dependencies. Included image assets are local.

## Pages

- `logichost.html`: query-routed LogicHost base site with network welcome,
  observatory and camera selection, public released-image discovery, and
  distinct LogicHost-, observatory-, and camera-scoped operations. Its camera
  workspace includes a selected latest retained presentation, artifact and
  lineage evidence, a bounded camera archive, and a two-authority Processing
  workspace that assumes complete edge execution evidence is available.
- `dashboard.html`: proposed Current Sky dashboard with stage switching,
  independently selectable measured, expected, and predicted scene layers,
  astrometric fit/calculator evidence, stack lineage, acquisition and rig facts,
  and a link to the capture pipeline run.
- `gallery.html`: Archive capture results with explicit single-frame,
  causal-stack, registered-stack, and raw-only product labels.
- `calendar.html` and `day.html`: local-noon observing-day calendar and detail
  views joining capture coverage, products, automation runs, and events.
- `products.html` and `product.html`: multi-source derivative archive and
  immutable product lineage for timelapses, star trails, keograms, and daily
  summaries.
- `events.html` and `event.html`: detected-event review, centered evidence, and
  scientific values with single-camera limits and ranked orbital-correlation
  evidence kept explicit.
- `operations.html`: routeable local administration workspace with grouped
  setup, capture, processing, automation, data, and system sections, including
  immutable celestial-package selection and separate orbital-data freshness.
- `index.html`: detailed per-capture pipeline execution graph.

## Interactions

- Move through network, observatory, and logical-camera scopes without carrying
  camera-owned selections into another observatory.
- Switch a selected LogicHost capture between its exact retained Preview and,
  where available, that same Preview plus SVG groups generated from a complete
  retained manifest and structured layer payloads. Raw,
  Calibrated, and Combined roles remain inventory evidence rather than being
  synthesized into inline display stages.
- Toggle generated groups for the central layered presentation and
  compare fleet health, latest submission, capture time, first receipt, object
  state, reconstruction, and artifact release as independent facts.
- Filter the six loaded camera-archive fixtures by text, central state, and
  display-artifact availability; switch grid/compact layout without implying a
  total count or loading another camera's records.
- Inspect separate received CameraAgent and LogicHost-owned graphs, select nodes
  for dependency/input/output detail, and never interpret topological levels as
  proof of concurrent execution.
- Start an immutable successor central-graph draft while the effective graph and
  historical jobs remain unchanged; received edge evidence is never edited.
- Select imported edge executions and central jobs using the same outcome,
  attempt, reason, artifact, and lineage vocabulary while retaining origin and
  mutation authority.
- Compare one exact edge execution with one exact central execution; filter to
  differences without declaring one processing origin universally preferable.
- Configure a draft central presentation policy and toggle generated overlay
  groups without mutating the retained Preview or an existing publication.
- Preview the boundary for a future CameraAgent pipeline proposal. The prototype
  does not push, deliver, accept, stage, or activate a local revision.
- Switch the CameraAgent Current Sky image among processed, live-stack,
  calibrated, and raw stages without changing detector evidence.
- Follow the LogicHost five-source lineage by explicit capture and source
  artifact identity. Archive fixtures without exact image bytes remain
  metadata-only; no current image is reused as a historical thumbnail.
- Search and filter Archive captures by a bounded UTC range, outcome, or
  displayed product, and switch between grid and compact layouts.
- Move from the observing-day calendar into complete, partial, sparse, or empty
  day records without substituting another day's evidence.
- Filter generated products and inspect their source window, recipe, automation,
  generation history, integrity, and retained predecessor versions.
- Filter Events, switch between list and calendar views, and open the selected
  event without substituting the fireball record for other classifications.
- Open any Archive image or its matching `?run=<capture>` pipeline projection.
- Navigate the Operations workspace with `?section=<name>` routes. Overview,
  Camera & Rig, Schedule, Pipeline, and manual Focus are detailed prototypes;
  the remaining sections establish the shared page and authority patterns.
- Run a manual focus-measurement session without implying focuser motor or
  autofocus support.
- Compare typed Automation definitions, their next-run calendar, and durable run
  history. Capture Schedule remains the exposure-admission authority.
- Select recent captures to compare successful, degraded, failed, and offline
  delivery runs.
- Filter capture history by text or outcome.
- Select graph nodes or the stage list to inspect status, timing, dependencies,
  outputs, attempts, and sanitized events.
- Inspect source associations, deep-sky extents, constellation context,
  Sun/Moon footprints, expected-but-undetected objects, astrometric residuals,
  predicted satellite tracks, image geometry, cloud state, and frame facts as
  independently selectable layers. `Measured`, `Expected`, and `Predicted`
  semantics remain visually distinct.
- Enter native image pixels in the Current Sky astrometry card to preview a
  bounded pixel-to-horizontal-coordinate calculation without changing the rig
  calibration.
- Compare the exact selected HYG/OpenNGC package with side-by-side retained
  packages and an availability check that does not download or activate data.
- Inspect orbital snapshot identity, age, scheduler state, and last-known-good
  semantics separately from immutable celestial packages.
- Compare measured event geometry with ranked predicted satellite tracks. A
  likely match remains append-only assessment evidence and never deletes or
  automatically rejects the event.
- Compare the normal per-capture presentation path with the independent V1
  Hybrid fireball track: linear detector input, causal `N-2,N-1,N` scan,
  restart-safe candidate evidence, central `N-2..N+2` window resolution,
  assessment, derivatives, and review-gated owner notification.
- Use the graph fit and zoom controls to inspect fan-out and fan-in.
- Switch between stage summary, immutable artifacts, and execution attempts.
- Open **Reprocess** to compare retry delivery, reprocess source, and repeat
  acquisition semantics. Prototype actions do not mutate data.

## Blazor Handoff

- Read the [LogicHost implementation handoff](LOGICHOST-HANDOFF.md) before
  translating this design into production pages. It maps the prototype to
  current services, identifies missing contracts, and records where issue
  ownership is established or still requires a coordinator decision.
- The static query routes represent separate Blazor pages, not one intended
  production component: network home, observatory selection/detail, camera
  current sky, camera processing, camera archive, events, and scoped operations.
- The header, authorized scope sidebar, breadcrumbs, scope tabs, and boundary
  notices belong in a shared protected network layout and small reusable shell
  components.
- Camera Current Sky and capture detail should share one authorized central
  layered-image component. Camera Archive and the all-accessible capture list
  should share one keyset-paged archive browser.
- Camera Processing should compose reusable graph, node-inspector, execution,
  attempt, artifact, comparison, and presentation-policy components. Edge and
  central records use a shared evidence vocabulary but separate service and
  mutation boundaries.
- Production route identity belongs in path segments. Query parameters should
  carry archive filters, layout, and cursor state only.

## Deliberate Boundaries

- The LogicHost shell keeps observatories as isolation boundaries. Cameras,
  received schedule evidence, pipeline revisions, camera-scoped jobs, and
  protected images remain inside one observatory scope; they are not shared as
  reusable objects across observatories. Central events may correlate evidence
  from multiple authorized observatories and retain every contributor.
- The cross-observatory Public Sky view contains only illustrative released
  JPEG/PNG/WebP preview roles from active public observatories. Public status
  does not release every image, raw or calibrated evidence is not exposed, and
  displayed locations remain approximate. The prototype shows that projection
  inside the protected owner shell; it is not the anonymous public site.
- LogicHost operations own central trust, observatory membership, logical
  camera installation, ingest, central processing, archive, and publication.
  Camera schedule and capture-pipeline configuration remain CameraAgent-local;
  their LogicHost views are labeled as received capture-time evidence rather
  than central editors.
- The detailed W6 edge graph, node attempts, and artifact delivery records are
  illustrative future contract fixtures. Current manifest-v2 artifact evidence
  alone does not provide every local node outcome shown by this page; later
  implementation issues must define independent durable execution-evidence
  delivery without delaying raw ingest.
- A future remote-management workflow may let LogicHost prepare an immutable
  proposal bound to an exact agent, installation, expected base revision,
  capabilities, and expiry. The prototype assumes an agent-initiated channel,
  local validation and admission, and separate retrieval, acceptance, staging,
  and activation facts. Heartbeat never proves proposal-channel availability.
  These are candidate requirements for the owning issue; this prototype does
  not authorize a topology or design the transport.
- CameraAgent-only controls include focus, physical calibration acquisition,
  camera/readout controls, and local capture admission. LogicHost may configure
  central successor recipes and presentation policy but cannot relabel those as
  edits to the local pipeline.
- Protected actions assume an illustrative signed-in Observatory Owner. Cards
  still name the Manager, Owner, or Platform Editor authority required by the
  represented operation; prototype actions do not mutate runtime state.
- LogicHost Current Sky means a selected latest complete retained presentation,
  not a live feed or canonical camera-level latest-image endpoint. The archive
  controls filter only the bounded client fixtures; current production reads
  provide authorized newest-first keyset paging but not these server filters.
- Artifact role presence does not establish content availability. Inline
  display is limited to complete, available Preview or AnnotatedPreview
  fixtures; raw content requires separate short-lived audited download
  authorization, and expired or quarantined content remains metadata-only.
- Camera fixtures without exact Preview bytes show metadata-only placeholders;
  they never borrow a JPEG from another camera or capture.
- Publication is represented by an explicit decision for one artifact identity
  and release record. Public cards resolve content and capture time from that
  released artifact rather than the camera's current presentation. Neither a
  logical camera nor an artifact role is treated as globally published.

- `assets/asi174-20260831-045406-utc.jpg` is the supplied real `1936 x 1216`
  ASI174 frame used to prove native-coordinate overlay alignment. Marker
  coordinates are anchored to image features, while catalog names, astrometric
  fit values, orbital identities, and capture metadata remain illustrative UI
  fixtures rather than a scientific reduction of the JPEG.
- HYG 4.4, OpenNGC, measured source association, astrometric fitting, resolved
  body footprints, orbital acquisition, and satellite correlation are shown as
  coordinated design targets. The prototype does not claim those runtime
  capabilities are delivered.
- Celestial packages are modeled as immutable official assets installed side by
  side with explicit per-instance selection. Availability checks never imply
  unattended upstream download, target-side rebuild, or automatic activation.
- Orbital data is modeled as a separate stale-aware snapshot lifecycle. The
  example provider-neutral snapshot does not select the source or staleness
  policy still owned by orbital-source research, and predicted geometry never
  claims optical visibility.

- The processing branches are shown as dependency-independent. The current
  `FrameProcessingWorker` executes its topologically sorted nodes sequentially;
  true overlapping execution would be a separate bounded runtime change.
- The Pipeline configuration page shows a representative seven-step projection
  of the current 14-step W6 graph and names the configured IDs and registered
  operation types. The per-capture run page additionally composes acquisition,
  local save, outbox relay, LogicHost ingest, and notification into one operator
  projection even though they cross runtime and ownership boundaries.
- The current `ScenePresentationLayer` emits several overlay outputs from one
  processing node. Showing per-overlay runtime requires either durable substep
  timing or splitting those outputs into independently executed graph nodes.
- Registered live-stack cards are explicitly marked as design targets. The
  unregistered causal arithmetic mean remains separately labeled as the current
  baseline and uses capture N as a window endpoint, not registration geometry.
- The fireball track models the delivered still-frame V1 behavior coordinated
  by closed epic #65. It deliberately does not model the stream sessions, video
  clips, volatile pre-trigger buffers, or accelerated/AI providers being
  researched separately under open issue #143.
- The V1 detector uses an exact five-frame context (`N-2` through `N+2`), not a
  `+/-3` window. Hybrid CameraAgent submission initially carries `N-2,N-1,N`;
  LogicHost resolves the two future frames by capture sequence.
- Recovery creates or represents successor work. Historical run evidence and
  artifacts are never rewritten.
- Automation accepts registered task types and compatible triggers only; it does
  not expose arbitrary command execution. Scheduled CameraAgent restart remains
  an unavailable design target until authenticated lifecycle, drain,
  verification, and terminal-receipt support exists.
- CameraAgent owns local candidate evidence and local Automations. LogicHost owns
  authoritative event validation, correlation, reconstruction, scientific
  derivation, and publication review.
- Single-camera events cannot resolve physical speed, altitude, ground track, or
  impact location without multi-site correlation.
