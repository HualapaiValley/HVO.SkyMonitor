# Pipeline Operations Static Prototype

This prototype explores a GitHub Actions-style run view for the CameraAgent and
LogicHost image pipeline. It is deliberately separate from the Blazor hosts and
does not change runtime behavior.

## Preview

From the worktree root:

```bash
python3 -m http.server 4173 --directory docs/prototypes/pipeline-operations
```

Open `http://localhost:4173/dashboard.html`.

The pages can also be opened directly; they have no package, build, network, or
font dependencies. Included image assets are local.

## Pages

- `dashboard.html`: proposed Current Sky dashboard with stage switching,
  independently selectable presentation layers, stack lineage, acquisition and
  rig facts, and a link to the capture pipeline run.
- `gallery.html`: Archive capture results with explicit single-frame,
  causal-stack, registered-stack, and raw-only product labels.
- `calendar.html` and `day.html`: local-noon observing-day calendar and detail
  views joining capture coverage, products, automation runs, and events.
- `products.html` and `product.html`: multi-source derivative archive and
  immutable product lineage for timelapses, star trails, keograms, and daily
  summaries.
- `events.html` and `event.html`: detected-event review, centered evidence, and
  scientific values with single-camera limits kept explicit.
- `operations.html`: routeable local administration workspace with grouped
  setup, capture, processing, automation, data, and system sections.
- `index.html`: detailed per-capture pipeline execution graph.

## Interactions

- Switch the Current Sky image among processed, live-stack, calibrated, and raw
  stages; toggle independently retained presentation layers without changing
  detector evidence.
- Follow the five-frame lineage to captures available on the bounded Archive
  page. Frames outside that page remain identified but are not given fabricated
  links or thumbnails.
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
- Inspect stars and labels, constellation lines, cardinal corners, image
  boundary, cloud mask, and environment facts as separately timed overlay
  producers with independent artifacts.
- Compare the normal per-capture presentation path with the independent V1
  Hybrid fireball track: linear detector input, causal `N-2,N-1,N` scan,
  restart-safe candidate evidence, central `N-2..N+2` window resolution,
  assessment, derivatives, and review-gated owner notification.
- Use the graph fit and zoom controls to inspect fan-out and fan-in.
- Switch between stage summary, immutable artifacts, and execution attempts.
- Open **Reprocess** to compare retry delivery, reprocess source, and repeat
  acquisition semantics. Prototype actions do not mutate data.

## Deliberate Boundaries

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
