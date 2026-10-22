# LogicHost workspace API audit and independent UI slices

Discovery for [#855](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/855),
under [#539](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/539) and
`RM-018` [#531](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/531).
Source baseline: `development/v1@27ec07492ac14245c99fe936089c06d73ef4e579`,
audited 2026-10-15 UTC. This document describes existing behavior and proposed
follow-up acceptance; it enables no pages, persistence, or protocol.

## Authority and current delivery

[#847](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/847) authorizes
this discovery independently of #538. Only the imported-evidence slice below
needs that importer. The parent workspace still retains its #538 integration
gate. Creating these stories does not claim them or authorize automatic start.

The static interaction checkpoint is
`11ee3f7f469cfb4f906d1005d60ae0c7fb507362`. Its
[handoff](../prototypes/pipeline-operations/LOGICHOST-HANDOFF.md) and
[fixture implementation](../prototypes/pipeline-operations/logichost-pages.js)
guide navigation, state language, and inspectors; they are not runtime evidence.
The handoff's old production baseline and continuation order are superseded by
this audit and current issue authorization:

- #426 graph catalog/assignment and #427 central graph execution are closed and
  their services exist. Do not recreate their persistence or reopen those issues.
- #536 shared evidence contracts and #537 edge exporter are delivered. #538 owns
  the LogicHost receiver and imported read services, and is outside this audit's
  implementation scope. The handoff's claim that evidence delivery has no owner
  is obsolete.
- Live #531 records #499 supported filesystem adoption and #535 standalone
  qualification as satisfied. #513 is closed. These are not new UI blockers.
  LogicHost #538–#541 retain their own integration and exact-candidate release
  evidence. #856 separately owns packaging preparation; this audit adds none.

Architecture and requirement ownership remain in
[project-plan sections 3.1–3.2, 3.6 and phase 13](../project-plan.md),
[requirements crosswalk](requirements-crosswalk.md) (`CENTRAL-*`, `WORKER-*`,
`UI-001`–`UI-008`, `CENTRAL-PRODUCT-*`), and the
[roadmap](../roadmap.md). CameraAgent owns acquisition, local configuration,
activation and immediate execution. LogicHost owns central jobs and retained
history. Neither host references the other. Received edge evidence is immutable;
recovery or reprocessing creates successor work. Redis is an accelerator, SQL
owns central durable state, and `IObjectStore` owns central byte access.

## Source inventory against the prototype

Paths below are relative to `src/HVO.SkyMonitor.LogicHost/` unless linked
otherwise. A server-side Blazor service is a valid API seam; a new HTTP facade
is not required merely to render a page.

| Prototype capability | Current authorized seam and actual bounds | Remaining gap / owning story |
| --- | --- | --- |
| Protected network shell, observatory selection, breadcrumbs and camera tabs | [App](../../src/HVO.SkyMonitor.LogicHost/Components/App.razor), [Routes](../../src/HVO.SkyMonitor.LogicHost/Components/Routes.razor), [MainLayoutNavigation](../../src/HVO.SkyMonitor.LogicHost/Components/Layout/MainLayoutNavigation.razor); [NetworkOperationsReadService](../../src/HVO.SkyMonitor.LogicHost/Services/NetworkOperationsReadService.cs) lists only the caller's memberships. Observatory, camera, registration and installation pages accept `take=1..50`, fetch `take+1`, and return `NextCursor`. Camera detail starts with 50 installations and exposes continuation. The separate `GET /api/internal/observatories` materializes an unpaged list; prefer this paged service. | Reusable path-scoped navigation and bounded scope selection: S1. Existing `OperationsProcessing.OnInitializedAsync` drains every observatory page; do not copy that pattern into the new shell. |
| Logical-camera identity across replacements | `GetCameraAsync`, `ListCameraInstallationsAsync` and `ListCapturesAsync` in the same read service; [LogicalCameraService](../../src/HVO.SkyMonitor.LogicHost/Services/LogicalCameraService.cs) permits owner-managed assignment only within one observatory. Capture reads join the retained installation relationship, including historical replacements. | Validate the observatory/camera pair, rather than trusting either route segment independently: S1 and B1. No new identity store. |
| Current Sky | [FrameHistoryController](../../src/HVO.SkyMonitor.LogicHost/Controllers/FrameHistoryController.cs), `GET /api/v1.0/frames/latest`, selects by capture time and optional agent/role; it checks membership plus credential observatory scope. Role existence does not require usable bytes. Its external `FrameId` differs from the central capture `Id` required by presentation routes. | This is not a logical-camera latest eligible complete retained presentation selector. B1 must define and implement bounded selection returning the central capture identity; S2 consumes it. |
| Camera archive | `ListCapturesAsync(userId, observatoryId, logicalCameraId, take, cursor)` has stable descending `(CapturedAtUtc, capture Id)` keyset paging, `take<=50`, scoped SQL reads and no total. [OperationsCaptures](../../src/HVO.SkyMonitor.LogicHost/Components/Pages/Operations/OperationsCaptures.razor.cs) consumes it. `/api/v1.0/frames` and [artifact history](../../src/HVO.SkyMonitor.LogicHost/Controllers/ArtifactHistoryController.cs) instead return bounded latest lists (`take<=500`), optional agent/role, without continuation. | Prototype text/state/product/date filters are not present on the camera keyset service. B1 owns supported server filters and cursor semantics; S2 must not client-filter one page and call it complete history. |
| Capture metadata, provenance and trace | `GetCaptureAsync`/`ListCaptureArtifactsAsync` retain exact artifact identity, checksum, size, role, recipe, object/reconstruction state and release eligibility. Artifact pages use `take<=50`; capture profiles cap at 50. `GetCaptureTraceAsync` caps lineage at 200 and jobs/environment/events at 50, job inputs/attempts at 50 each, with `TraceIsTruncated`. | A truncated trace is not a complete graph inspector. S2 shows truncation; B2 supplies central execution drill-down where required. |
| Layered viewer and exact protected bytes | [CentralLayeredPresentationService](../../src/HVO.SkyMonitor.LogicHost/Services/CentralLayeredPresentationService.cs) selects one available, reconstruction-complete manifest for one authorized capture, rechecks persisted input identities/states and manifest bytes on cache reuse, and reports unavailable/dependency failures. It does not prove physical base-byte availability. [CentralPresentationController](../../src/HVO.SkyMonitor.LogicHost/Controllers/CentralPresentationController.cs): `GET /api/v1.0/captures/{captureId}/presentation`, `presentation.svg`, `presentation/base`. [ExactCaptureViewer](../../src/HVO.SkyMonitor.LogicHost/Components/Shared/ExactCaptureViewer.razor), extracted from [OperationsCaptureDetail](../../src/HVO.SkyMonitor.LogicHost/Components/Pages/Operations/OperationsCaptureDetail.razor) under #1534, renders it for one exact capture identity. | Reuse that viewer for S2; do not create another composition algorithm, use another capture's bytes, or equate artifact role with availability. |
| Artifact content and raw download | [ArtifactRetrievalController](../../src/HVO.SkyMonitor.LogicHost/Controllers/ArtifactRetrievalController.cs) and [CentralArtifactRetrievalService](../../src/HVO.SkyMonitor.LogicHost/Services/CentralArtifactRetrievalService.cs) authorize exact device/artifact metadata, content and raw download authorization. Human raw GET requires a one-minute audited authorization, bound to actor/artifact/observatory/range with membership rechecked; raw HEAD does not require that grant. It is separate from inline presentation. Worker access is bound to its lease; lease credentials do not belong in UI models. | Reuse these gates in S2/S4. Missing content or authorization loss must clear image display, including cached images. |
| Global graph catalog and effective scoped assignments | [ProcessingGraphsController](../../src/HVO.SkyMonitor.LogicHost/Controllers/ProcessingGraphsController.cs), `/api/internal/processing-graphs`: list/create revisions, exact revision GET at `revisions/{revisionId:guid}`, publish/retire, assignments, `assignments/effective`; [ProcessingGraphCatalogService](../../src/HVO.SkyMonitor.LogicHost/Services/ProcessingGraphCatalogService.cs). Revision list is a global reusable catalog, `take<=100`, no cursor. Exact-ID reads under [#1262](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1262) use the same `ApiKeyRead` authority and existing revision view, including historical/retired definitions beyond that list; they expose no assignments or executions. Mutation requires platform-editor or scoped authority according to operation. Effective assignment validates credential scope, membership (or platform editor) and camera/observatory relationship. | Delivered #426 replaces the handoff's catalog gap. B2 owns complete bounded operator browsing/assignment history; S4 consumes current authority checks without turning global definitions into globally readable camera state. |
| Proposal delivery and local activation facts | [DeviceProcessingGraphController](../../src/HVO.SkyMonitor.LogicHost/Controllers/DeviceProcessingGraphController.cs): `POST /api/device/processing-graphs/pull` (64 KiB) and `/facts` (32 KiB) manually validate device credentials; [ProcessingGraphDeliveryService](../../src/HVO.SkyMonitor.LogicHost/Services/ProcessingGraphDeliveryService.cs) persists proposal/fact transitions. This is an agent-initiated channel separate from heartbeat. | Transport and durable facts exist. No operator-facing, scope-bound keyset history is exposed here. B2 projects existing records; S4 displays retrieved/accepted/rejected/staged/activated/rolled-back/expired facts separately. No new transport or activation authority. |
| Central graph executions, replay and cancellation | [ProcessingGraphExecutionsController](../../src/HVO.SkyMonitor.LogicHost/Controllers/ProcessingGraphExecutionsController.cs), `/api/v1.0/processing-graph-executions`: list (`take<=100`, no cursor), exact detail, replay POST with bounded source list and one `Idempotency-Key`, cancel POST. [CentralProcessingGraphExecutionService](../../src/HVO.SkyMonitor.LogicHost/Services/CentralProcessingGraphExecutionService.cs) filters membership and credential scope, resolves exact authorized sources, and returns graph/node/output facts. | Delivered #427 replaces the handoff's runtime gap. Existing list has no logical-camera filter or continuation; detail is not a full attempt/input history projection. B2 owns bounded scoped inspector reads; S4 uses them. |
| Central job operations and policy | `ListJobsAsync` keyset-pages `take<=50` by observatory, not logical camera. [NetworkOperationsMutationService](../../src/HVO.SkyMonitor.LogicHost/Services/NetworkOperationsMutationService.cs), [DerivativeJobsController](../../src/HVO.SkyMonitor.LogicHost/Controllers/DerivativeJobsController.cs), [CentralProcessingPolicyService](../../src/HVO.SkyMonitor.LogicHost/Services/CentralProcessingPolicyService.cs) and [OperationsProcessing](../../src/HVO.SkyMonitor.LogicHost/Components/Pages/Operations/OperationsProcessing.razor.cs) provide central cancel/requeue/reprocess and future-job policy. The direct UI mutation service requires Manager/Owner; the legacy derivative-jobs HTTP list/mutations instead require global `api.admin` and are not scoped workspace adapters. | B2 adds camera scope and projection coverage; S4 preserves same-job retry versus successor reprocessing and existing supersession semantics. Current cloud/validation policy is not evidence of a versioned arbitrary presentation-policy editor. |
| Events and publication | [TransientEventsController](../../src/HVO.SkyMonitor.LogicHost/Controllers/TransientEventsController.cs), [OperationsEvents](../../src/HVO.SkyMonitor.LogicHost/Components/Pages/Operations/OperationsEvents.razor.cs), [PublicRecordPublicationService](../../src/HVO.SkyMonitor.LogicHost/Services/PublicRecordPublicationService.cs), [ObservatoryPublicationService](../../src/HVO.SkyMonitor.LogicHost/Services/ObservatoryPublicationService.cs), [PublicNetworkReadService](../../src/HVO.SkyMonitor.LogicHost/Services/PublicNetworkReadService.cs) and [PublicArtifactController](../../src/HVO.SkyMonitor.LogicHost/Controllers/PublicArtifactController.cs) already separate protected operations from explicit publication. Event lists have `take<=100` keyset pages; public browse services use `take<=50` keyset pages. Event access requires every contributing source/background observatory (with explicit admin exception), not any one contributor; ordinary review requires Manager/Owner across contributors. Retry/payload-release/reprocessing endpoints require `api.admin`; ordinary Owner membership is insufficient. | S4 composes existing bounded event/publication reads. Camera-scoped event navigation/filtering needs B2. The bounded [#1256](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1256) slice normalizes supported initial content-lookup storage/IO failures to the existing sanitized 503; streaming failures retain 503 or post-header abort behavior. Release remains one exact artifact and release record, independent of materialization. Anonymous-site expansion is excluded. |
| Imported edge executions and edge/central comparison | [GraphExecutionEvidenceContracts](../../src/HVO.SkyMonitor.Processing/GraphExecutionEvidenceContracts.cs) and [canonical JSON](../../src/HVO.SkyMonitor.Processing/GraphExecutionEvidenceJson.cs) are shared delivered contracts. Local CameraAgent endpoints and static fixtures are not LogicHost imported read services. | #538 owns receiver, immutable persistence, reconciliation and authorized bounded read services. S3 consumes its reviewed handoff only. Artifact presence, central jobs and heartbeat cannot synthesize edge execution evidence. |

## Availability and authorization cautions

`Found` presentation metadata is not a content lease. Generation verifies
manifest/layer bytes, while base selection checks SQL/layout/product identity.
The base endpoint can subsequently fail with 503 (storage/missing) or 409
(integrity). Unlike ordinary content retrieval, that endpoint does not call the
ordinary retrieval service's unavailable-state repair. The existing capture
viewer has no image-error transition, and some artifact controls check object
state without reconstruction completeness. S2/#1239 owns truthful display and
image-error handling; B1/#1236 owns selection semantics. Neither may claim SQL
eligibility proves physical bytes, verify every image in an archive page, or
create a second reconciliation system. Any durable repair extension needs its
own contract scope.

Ordinary content retrieval conceals denied resources as 404; persisted expiry
maps to 410, pending/reference to 503 and quarantined/incomplete/conflicting
content to 409. Physical loss records `object.missing` and invalidates dependent
availability through the existing retrieval path. Operations metadata is retained
separately; the direct artifact-metadata endpoint itself follows availability
status. Stream failures may abort after headers start. No cross-capture image
substitution was found. Existing image-generation/admission/cache limits must
survive extraction; the base controller bounds packed/PNG inputs to 32 MiB and
32 Mi-pixels. Preserve endpoint-specific behavior rather than hiding it behind a
single optimistic “available” flag.

The operations services receive a human `userId`, while HTTP credential adapters
must also intersect any observatory-scoped credential. Broad route policy alone
does not establish resource access. Existing cookie operations routes include
`/app/observatories/{ObservatoryId}`, `/app/cameras/{CameraId}`, `/app/captures`,
`/app/processing` and `/app/events`; S1 introduces coherent nested scope without
assuming existing flat routes already validate a nested pair.

## Shared UI acceptance and denial cases

These requirements apply to all four UI stories and their backend prerequisites.
They are future acceptance, not a claim that new flows were tested by #855.

1. Use authenticated path-segment observatory and logical-camera identity.
   Query strings hold filters, layout and cursors. Resolve the pair server-side;
   never authorize a resource from a label, route segment, tab or hidden button.
2. Exercise a Viewer, Manager and Owner in observatory A, a member only of B,
   a user in both, a removed member, and an unauthenticated caller. Include an
   A-scoped credential belonging to a user who is also a B member. Substitute B's
   camera, installation, capture, artifact, execution, job, event and release IDs
   in A's route, request and continuation. Deny without B metadata, image bytes,
   counts, cursor contents or mutation. A global reusable graph definition does
   not grant access to any assignment or execution using it.
3. Preserve each seam's denial semantics: services return empty/null or
   `NotFoundOrDenied`; endpoint policies reject invalid credentials. Graph cancel
   returns 404 for inaccessible execution, and 403 when a caller can read it but
   lacks management authority. Do not blanket-rewrite all outcomes to 403.
4. Every collection has a server-enforced page/child bound and explicit
   continuation or truncation. Cursor reuse under a changed scope/filter must
   reject or restart without cross-scope disclosure. No automatic all-page drain,
   offset sweep, fabricated total, or full-history client search. Record query
   count, rows read, response bytes, render latency and browser memory against a
   seeded large history, tied to exact source/candidate and workload size.
5. Current Sky is a selected latest eligible complete retained presentation,
   with capture time independent of receipt, heartbeat, object availability,
   reconstruction, processing completion and publication. A plain Preview may be
   eligible without an optional annotation; unavailable content remains metadata.
   Archive capture detail never falls back to another capture's image.
6. Cover missing, expired, partial, quarantined, unavailable and unauthorized
   bytes; object-store outage; role-only metadata; and late object reconciliation.
   Preserve metadata/reasons and show no substituted image or stale cached bytes.
   Keep structured layer inputs, checksums, provenance and immediate lineage exact.
7. Cover loading, empty, stale, degraded, failed, unauthorized and reconnect
   states with bUnit, service/endpoint authorization and browser evidence.
   Exercise desktop, narrow portrait and short landscape; keyboard, touch,
   screen reader, focus restoration and no horizontal overflow. Follow sibling
   `.razor`, `.razor.cs`, scoped CSS/optional JS and `data-theme="hvo-dark"` rules.
8. Mutations use existing audited services and revalidate resource authority and
   current state. Do not expose worker leases, device keys or storage credentials.
   No CameraAgent settings editor, local activation override, new protocol,
   persistence model, public release policy or enabled unfinished control.

## Follow-up stories and prerequisites

Each story below is independently claimable only after its named prerequisites
and start authorization are checked. All are `component:logichost`, under #539.
The UI stories are Tier B candidates; backend read changes require risk review
and promotion if they expose a durable/security boundary. None is started here.

### B1 — Logical-camera current selection and filtered archive reads

Owning issue: [#1236](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1236). Prerequisites: delivered logical-camera,
capture, retrieval and presentation services above; no #538 dependency.

Deliver an authorized server-side current selector and camera archive filters
over existing normalized records. Define supported date/state/product/text
semantics explicitly; defer unsupported prototype filters rather than inventing
facts. Specify eligibility, deterministic capture/identity ordering, replacement
installation membership, late arrival and byte-loss behavior. Keep metadata for
an explicitly selected unavailable capture; selection of another eligible capture
must identify that different capture openly. Bound current candidate examination
and report no result/continuation honestly; do not scan unlimited history.

Acceptance: tests prove newer ineligible versus older eligible captures, equal
timestamps, installation replacement, no captures, late objects, disappearing
bytes and revoked membership. Archive filters execute before `Take`, use stable
keyset continuation, validate scope/filter changes and return no total. Preserve
`take<=50` and bounded child projections, or document and measure any justified
lower limits. Test the shared denial/content matrix and indexed SQL query/row
bounds against representative large history. No migrations, importer semantics
or presentation-policy persistence are included; split any such need before work.

### B2 — Scoped central operation and delivery evidence reads

Owning issue: [#1237](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1237). Prerequisites: delivered #426/#427 services;
no #538 dependency.

Project existing central graphs, assignments, proposal/fact records, executions,
node attempts/inputs/outputs, jobs and event associations for authorized
observatory/camera operations. Add keyset continuation where lists currently
stop at the latest 100 and camera filters where only observatory scope exists.
The bounded [#1262](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1262) leaf provides exact-ID revision access for historical executions, even outside the
first catalog page, under existing global catalog authority, with no private assignment, installation or execution projection. The broader #1237 browsing and inspector acceptance remains open. Expose explicit bounds or
continuation for every nested inspector; retain truncated capture trace behavior.

Acceptance: endpoint/service tests cover shared definitions versus private
assignment/execution state; multi-observatory and credential-scope denial;
camera/installation replacement; pagination with equal timestamps and concurrent
inserts; and exact execution/job/input/attempt/output lineage without lease tokens.
The bounded [#1270](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1270)
leaf adds `GET /api/v1.0/observatories/{observatoryId}/cameras/{logicalCameraId}/installations/{installationId}/processing-graph-proposals/{proposalId}`
for one exact retained proposal. `installationId` is the central
`LogicalCameraInstallation.Id`, distinct from `InstallationPublicId`. Current
User membership and credential observatory scope apply even to replaced,
retired installations and inactive cameras; global graph catalog access grants
no private proposal authority. The response projects stored proposal/fact
identities, reasons and UTC fields only. `take` defaults to 50 and accepts 1–50;
latest facts sort by recorded UTC then SQL Server GUID identity, both descending.
One sentinel supplies `FactsAreTruncated`; there is no proposal discovery or
continuation and no claim of a complete history when truncated. Three bounded
SELECTs read the exact proposal, at most 51 facts through the existing index,
and recheck authorization before returning. Dedicated service and real HTTP/SQL
tests cover authorization, historical replacement, cancellation, exact fields,
equal timestamps, concurrent append, bounds and large retained/unrelated history.
The broader B2 browsing, continuation and inspector acceptance remains open.

Delivery history reports each persisted lifecycle fact and UTC/reason separately
from heartbeat. Staged is currently a poll disposition derived from accepted
inactive revision evidence, not a separately persisted Staged fact. Label that
derivation explicitly; do not manufacture a timestamp. Preserve Superseded facts.
The bounded event-content storage-error slice is implemented under
[#1256](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1256): supported
initial lookup failures use the existing unavailable response, with fault tests
before and during streaming, cancellation, and contributor/credential denial.
It adds no schema, recovery machinery or mutation-authority change; the other
B2 requirements remain open. Preserve existing catalog, job, event and mutation authorities.
Measure query/row/response bounds on large histories. Any missing durable fact or
arbitrary presentation-policy revision requires a separately owned contract story,
not a schema or transport extension inside this read prerequisite.

Delivered under #1237: `ICameraOperationsReadService` (usable directly by Blazor
pages) and `CameraOperationsController` at
`/api/v1.0/observatories/{observatoryId}/cameras/{cameraId}/`. Assignment history
(`processing-graph-assignments?targetHost=`), executions, the execution inspector's
node page, proposals and proposal facts are keyset pages over existing indexes:
one bounded `TOP` seek per retained installation merged by `UNION ALL`, because
EF decorrelates a per-installation `Take` into `ROW_NUMBER` over the whole history.
Jobs (`jobs`) and event associations (`transient-events`) have no camera index, so
each request walks at most 50 of the camera's own captures, a resumed capture
included, and reports `ExaminedCaptures`; a short or empty page may still continue.
On SQL Server the proposal, fact, job and event reads force their existing index
seeks from the page's own identities, and a proposal summary reads only its
terminal, latest and latest Activated/RolledBack facts, so no read follows the
size of the network's history. Event associations apply the all-contributor
rule to the window's candidate events and recheck it before returning. The job inspector
pages attempts, inputs and outputs independently and omits lease, trace and
free-form error fields; artifact identities from another observatory are null.
Continuations are protected and bound to caller, camera, collection, subject and
filter. Lists return an empty page and exact reads 404 when the camera cannot be
read; event associations still apply the all-contributor rule. A proposal's
`DerivedStatus` labels "Staged" as derived (no timestamp): an Accepted fact with
no lifecycle fact, or whose latest Activated/RolledBack fact is a rollback. The global catalog pages at
`/api/internal/processing-graphs/revision-summaries` without definition documents.
No schema, index, protocol, mutation or authority change.

### S1 — Protected scope shell and navigation

Owning issue: [#1238](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1238). Prerequisites: existing authorized network/camera
read services; no B1, B2 or #538 dependency.

Deliver reusable network/observatory/camera navigation, breadcrumbs and tabs
using path identity, preserving existing operations entry points. Fetch scope
choices one bounded page at a time and restore scope/filter/focus on navigation.
Validate a camera's observatory before showing its name or tab state. Links to
undelivered slices stay absent until their own acceptance is met.

Acceptance: shared UI and denial matrix passes, including deep links, a camera
from another observatory, access removal during a circuit, empty memberships,
more than 50 observatories/cameras, malformed/stale cursors, browser history,
keyboard/touch menus and reconnect. bUnit plus browser evidence proves bounded
reads, focus restoration, dark theme, accessible active navigation and no
horizontal overflow. No workspace content, new data model or edge mutation.

### S2 — Current Sky, archive and reusable exact-capture viewer

Owning issue: [#1239](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1239). Prerequisites: S1 and B1; no #538 dependency.

Extract the capture-detail layered viewer, then compose current selection,
camera archive, capture metadata and enlargement over existing presentation and
retrieval services. Keep archive query/cursor and focus when returning from
detail. Offer only filters delivered by B1; show bounded trace truncation.

Acceptance: shared UI/content/denial matrix passes, with independent capture,
receipt and fleet freshness; exact Preview without optional annotation; layers
off/on without byte or publication mutation; all unavailable-byte states; raw
download authorization expiry; same-time captures and replaced installations.
bUnit, service/endpoint and browser flows demonstrate no borrowed images,
image-load failure transitions to metadata-only state, object/reconstruction
checks, authorization-aware cache behavior, bounded scrolling/page memory and measured
image transfer/render costs for realistic history. No imported inspector or new
compositor, selector policy persistence, protocol or public-site implementation.

### S3 — Immutable imported execution inspector

Owning issue: [#1240](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1240). Prerequisites: S1 plus #538's reviewed importer
contracts and evidence. Exact edge-versus-central comparison additionally needs
B2's central inspector. S2 is needed only for linked image viewing, not metadata.

Consume #538's authorized read models for graph/execution/node attempts, exact
inputs/outputs, reason/availability/provenance/lineage and receipt/gap/conflict
status. The handoff must name actual service signatures/routes, contract version,
immutable origin/execution/sequence/hash identities, page/child limits and cursor
semantics, denial outcomes, reconciliation statuses and current-source fixtures
and combined evidence. #855 does not select an importer table, DTO or receipt
schema. If that handoff lacks a required fact, return it to its owning contract
issue before enabling the affected view.

Acceptance: a camera with no imported evidence shows an explicit unavailable
state. Object-late/missing/conflicting evidence remains truthful and metadata-only;
an acknowledgement never implies image availability. Shared denial/bounds/UI
matrix passes. Compare two explicit execution identities, never mutable camera
state; show missing facts rather than infer node success from role presence or
graph levels. No edit/retry of received evidence; any central successor action
uses existing central authority through S4. Test immutable receipt/provenance
display, page continuation and auth loss with #538's delivered fixtures and an
exact-version combined flow. No exporter, importer, protocol or schema changes.

### S4 — Central operations workspace

Owning issue: [#1241](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1241). Prerequisites: S1 and B2; no #538 dependency.
S2 is required only for integrated image-viewer links. Imported comparison waits
for S3 and does not gate central-only operation.

Compose central graph/revision and effective-assignment inspection, scoped
execution/job/attempt/input/output history, proposal/fact history, existing
central recovery and policy controls, event review and artifact-specific
publication. Preserve distinct central versus received authority and explicit
successor/retry/supersession language. Prototype arbitrary presentation-policy
editing remains absent until a separately approved durable contract exists.

Acceptance: shared UI/denial/bounds matrix passes. Test Viewer read-only,
Manager/Owner scoped job authority, global-admin legacy job/event operations,
all-contributor event membership and review authority, platform catalog authority, existing replay-owner
cancellation rules, state races, audited mutations, exact-source replay
idempotency, and unauthorized cross-scope event/publication actions. Proposal
retrieval/acceptance/staging/activation/rollback/expiry and heartbeat remain
separate facts, with derived staged status identified. Event mutations retain
strong `If-Match` and `Idempotency-Key` requirements and stale/conflict outcomes.
Requeue is a new attempt on the same job; reprocessing follows
existing successor/supersession semantics. Release one exact eligible artifact
without changing another artifact or treating materialization as publication.
bUnit, endpoint/service and responsive browser evidence includes failure,
reconnect, large-history costs and preserved immutable execution history.
No inbound CameraAgent control, new worker/catalog/importer persistence or
protocol, arbitrary policy editor, anonymous site or release distribution.

## Validation and handoff evidence

#855 is Tier A documentation/discovery. The review ledger records the immutable
range, `scripts/ci:classify` selector output and `profile=development-v1`, focused
source/link and acceptance checks, all four inexpensive CI-control guards,
independent exact-range review and protected Preflight plus Build and Unit.
No runtime source changes, local full qualification, Docker campaign or
performance claim are needed for this audit. Follow-up implementation must
produce its own tier-appropriate evidence; source inspection here is not test
execution or production qualification.

Existing evidence to inspect when implementing: [network projection
tests](../../tests/HVO.SkyMonitor.LogicHost.Tests/Services/NetworkOperationsProjectionTests.cs),
[membership tests](../../tests/HVO.SkyMonitor.LogicHost.Tests/Services/ObservatoryMembershipServiceTests.cs),
[catalog/delivery tests](../../tests/HVO.SkyMonitor.LogicHost.Tests/Services/ProcessingGraphCatalogAndDeliveryServiceTests.cs),
[central execution authorization
tests](../../tests/HVO.SkyMonitor.LogicHost.Tests/Services/CentralProcessingGraphExecutionServiceTests.cs),
[retrieval tests](../../tests/HVO.SkyMonitor.LogicHost.IntegrationTests/ArtifactRetrievalTests.cs),
and [presentation generation
tests](../../tests/HVO.SkyMonitor.LogicHost.Tests/Services/CentralPresentationGenerationGateTests.cs).
These are starting points, not evidence that the planned UI already passes.
