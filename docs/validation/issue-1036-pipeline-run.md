# Issue #1036: Pipeline Run prototype fidelity

## Scope and source

Tier B CameraAgent UI/read-projection work, deep independent review for
authorization and stale-navigation interactions. Base is `639a724a567d27049cdb5779c04bfb0d00539105`
on `development/v1`. Design authority is the committed
`docs/prototypes/pipeline-operations/index.html`, `app.js`, and `styles.css`.
The issue's `p-graph.png` is not present in the repository; the actual prototype
page is used as the reference.

The port retains one native card per frozen execution node. A node that produces
several layers remains one stage rather than being split into fictitious
successful prototype nodes. Semantic names come from recorded output contracts;
custom nodes retain their own IDs. Required/optional dependency flags remain
captured graph facts. The existing shared `PipelineEdgeRouter` owns edge routing.
All dependencies remain in the accessible graph description and technical
journal; cyclic/backward graph edges cannot be routed as forward success paths.

## Same-run browser evidence

Genuine retained capture #42, `252fcd46-45aa-8682-bafb-b5bb9b564218`, execution
`5782c562-1c8f-257c-334c-f7133c77061f`: 12 captured stages and 14 output records.
Independent transient milestones include capture-scoped staging/scan and the
separate candidate's allocation, persistence and finalization. Neither pending
milestones nor absent central receipts are labeled successful.

Host evidence is retained in `/home/roys/.cache/hvo/1036/`:

- `baseline.json`, `before-{1440,390,320}.png`, `before-graph.png`:
  baseline on the preserved #1117 host, whose execution page is unchanged from
  the target used for this issue.
- `reference-{1440,390,320}.png`: actual static prototype page. Scientific values
  on that page are illustrative and are not acceptance data.
- `after.json`, `after-{1440,390,320}.png`, `after-graph.png`:
  same genuine run after the port and operator-requested thinner connectors.
- `interactions.json`, `verified-{1440,390,320}.png`: native keyboard/fullscreen,
  real output/attempt inventory and responsive checks.

At 1440/390/320 px the document scroll width equals the viewport width. The
diagram is locally scrollable and fit uses its measured viewport with the
prototype's 50% readability floor. At desktop fullscreen, the root measures
1440×900 and the scrollable graph viewport measures 1440×786. The graph may
still scroll at the floor rather than reducing all text to an unreadable scale.

Keyboard Enter selects a native node button, arrow keys move the inspector tabs
and focus, and fullscreen exit returns focus to its trigger. Both explicit Exit
and Escape work. The selected calibration node has one actual attempt; the
Artifacts tab exposes the run's 14 real output rows. No browser page errors were
observed. Operator requested thinner connectors because selected blue routes
merged visually; all paths now use a 1-native-unit stroke, selection by color
rather than added thickness, and matching thin legend samples.

## Honest facts and missing capabilities

The run shows captured graph revision, recorded trigger/acceptance time, actual
duration, unique output count, and a small schedule-admission projection from
the verified retained source manifest. Capture #42 records schedule
`profile-0000...`, profile `continuous`, reason `WeeklyWindow`. Current schedule
settings are never substituted for historical admission. Missing admission is
explicitly unavailable. This projection changes no persisted schema or writes.

The two-column inspector shows recorded start/end/attempt/route/reason facts,
dependency/output tokens, actual attempts, and output availability. The event
list is explicitly labeled recorded journal facts; it is not fabricated log
text. Transient milestone contracts have no attempt duration or artifact
inventory, so those slots state "Not recorded"/"unavailable". Cloud and central
slots remain visible without fictitious successful stages or causal edges.
Current central integration mode is labeled "Disabled now / no receipt" rather
than being treated as historical receipt evidence.

Primary and optional reads are generation-fenced. Authorization denial clears
protected page facts and stops further enrichment. Exact execution/capture
identity is checked before accepting a result. Disposal cancels reads and
disconnects graph observation/fullscreen callbacks.

## Validation

- Debug CameraAgent warning-clean build: zero warnings/errors.
- Focused execution/router/service/gallery tests: 75 passed, no skips.
- Dedicated Manual `OwnerPipelineRunPresentationAcceptanceAsync`: one passed,
  no skips in Release. It starts a genuine VirtualSky fixture run, then checks
  1440/390/320 overflow, native keyboard node/tab behavior, measured fullscreen,
  zoom/fit and focus return. Fixture catalog is explicitly distinct from the
  production-catalog native capture used for visual acceptance.
- Earlier attempts through the full Archive Manual chain exposed prerender/
  interactive cursor timing and an inspector module import race. Failures and
  logs are preserved; they are not counted as passes. Inspector import now
  retries after execution data exists, and the dedicated run test waits for the
  graph's actual interop readiness.
- Candidate gates are selected by `scripts/ci:classify` and recorded with exact
  head/commands in the issue/PR ledger. Four new Unit methods and one Manual
  method are aligned with owning test inventories and the runbook totals.

## Runtime and acceptance

Private preview: `http://192.168.2.45:5134/operations/pipeline/executions/5782c562-1c8f-257c-334c-f7133c77061f`.
Runtime was copied from the preserved paused preview and its settings/configuration
paths redirected to `/home/roys/.hvo-ui-1036`; no installed instance was changed.
Original accepted Archive/star previews remain available on 5132/5133. Read-only
captures are available on the existing bounded evidence server without a new
password (`/pipeline-1440.png`, `/pipeline-390.png`, `/pipeline-graph.png`).

Runtime reconciliation reports retained captures and products without missing or
quarantined records; the capture remains paused. No new worker or telemetry
boundary is introduced, so comparative performance/metric/trace manifests are
N/A for this UI/read-only projection. Queries remain bounded to ten recent
executions per class and finite captured node/output collections. No general
Manual campaign or full-host performance qualification is claimed.

Page-specific operator visual acceptance is still required before completion.
