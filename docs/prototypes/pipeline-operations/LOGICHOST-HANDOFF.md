# LogicHost Pipeline Operations Prototype Handoff

## Status

- Design branch: `prototype/pipeline-operations-preview`.
- Accepted LogicHost design commit:
  `11ee3f7f469cfb4f906d1005d60ae0c7fb507362`.
- Production baseline inspected while writing this handoff:
  `origin/main@031bc976a92d7de2362ab0cbf5f6d540e7981bc8`.
- This is a static design study. It does not change Blazor, persistence,
  transport, scheduling, or publication behavior.
- Start production work from the then-current `main`, not from this prototype
  branch. Re-audit the delivered contracts because RM-005 continues to change.
- Do not start production work without the owning roadmap issue's claim and
  `READY` signal.

The prototype can be served from the repository root with:

```bash
python3 -m http.server 4173 --directory docs/prototypes/pipeline-operations
```

Open `http://localhost:4173/logichost.html`. The most complete processing
fixture is:

```text
logichost.html?view=processing&observatory=hualapai&camera=main-fisheye
```

## Intended Outcome

The design adds a protected network shell around existing LogicHost concepts
and explores camera-scoped Current Sky, Archive, and Processing workspaces. The
Processing page deliberately presents two authorities side by side:

- Received CameraAgent graphs and executions are immutable imported evidence.
- LogicHost owns central graph revisions, successor jobs, reprocessing,
  presentation policy, archive, and publication.

The page is not evidence that every represented runtime contract exists. The
Hualapai Main Fisheye fixtures assume complete edge execution evidence that is
not currently delivered to LogicHost. Other camera fixtures intentionally show
missing-evidence states.

## Authority Invariants

- CameraAgent remains authoritative for acquisition, focus, physical
  calibration acquisition, camera and readout controls, local admission,
  schedule, activated local graph revision, and immediate live execution.
- LogicHost is not part of capture correctness. CameraAgent remains useful
  through LogicHost and network outages.
- The prototype assumes LogicHost may prepare an immutable successor proposal
  over an agent-initiated channel. CameraAgent still validates, persists, and
  explicitly activates any accepted revision.
- Proposal retrieval, acceptance or rejection, staging, activation, and
  heartbeat are distinct facts. Heartbeat does not prove proposal-channel
  availability.
- Received edge graphs, executions, attempts, outcomes, and artifacts are never
  edited centrally. Recovery and reprocessing create successor work.
- Globally managed graph definitions may be reusable catalog entries. Effective
  assignments and camera, execution, artifact, and image reads remain scoped by
  observatory membership and require resource-level authorization.
- Public visibility is an explicit decision for an exact artifact and release
  record. It does not follow a camera, artifact role, or latest-image pointer.
- Current Sky means the latest eligible complete retained presentation, not a
  live feed. Missing bytes remain metadata-only and never borrow another
  capture's image.
- Graph levels express dependency ordering only. They do not prove overlapping
  runtime execution.

## Existing Production Seams

Use these existing seams rather than recreating their behavior.

| Capability | Existing production seam | Important limitation |
| --- | --- | --- |
| Protected shell | `Components/App.razor`, `Components/Routes.razor`, `Components/Layout/MainLayout.razor`, and `MainLayoutNavigation.razor` | No reusable observatory/camera scope sidebar, breadcrumbs, or scope tabs. |
| Authorization | `ObservatoryMembershipService`, `ObservatoryMembershipAccess`, and `AuthorizationPolicyNames` | Routes authenticate broadly; services must continue enforcing resource membership and mutation authority. |
| Network and observatory reads | `INetworkOperationsReadService` and `NetworkOperationsReadService` | Keep keyset paging and bounded reads; do not infer totals. |
| Observatory pages | `OperationsDashboard`, `OperationsObservatories`, and `OperationsObservatory` | Search and richer scope navigation are not delivered. |
| Stable logical camera | `LogicalCamera`, `LogicalCameraInstallation`, `ILogicalCameraService`, and `OperationsCamera` | No current-presentation selector spans installation replacements. |
| Central archive | `OperationsCaptures`, `OperationsCaptureDetail`, `CentralFrame`, and `CentralArtifact` | Current server reads do not provide all prototype text, state, role, and date filters. |
| Layered presentation | `ICentralLayeredPresentationService`, `CentralPresentationController`, and the viewer in `OperationsCaptureDetail` | Capture-specific; no logical-camera selector or persisted presentation-policy revision. |
| Publication | `IObservatoryPublicationService`, `IPublicRecordPublicationService`, `IPublicNetworkReadService`, and `PublicArtifactController` | Publication must remain artifact-specific and independent of materialization. |
| Central jobs | `OperationsProcessing`, `CentralDerivativeJob`, `CentralDerivativeJobAttempt`, `CentralArtifactProcessingEvidence`, and the central derivative services | Durable jobs exist, but a persisted arbitrary central graph catalog and camera-scoped graph view do not. |
| Shared graph semantics | `HVO.SkyMonitor.Processing/ProcessingGraphContracts.cs` and `ProcessingGraphCompiler.cs` | Shared definitions and frozen plans exist; central graph catalog and assignment remain open work. |
| Local graph evidence | `ProcessingGraphExecutionContracts.cs`, `SqliteCaptureProcessingExecutionStore.cs`, and `CameraAgentProcessingGraphOperationsEndpoints.cs` | CameraAgent exposes local registry, replay, execution, node-attempt, input, output, and lineage evidence only through its authenticated local API. |

The production shell and page paths above are beneath
`src/HVO.SkyMonitor.LogicHost`. CameraAgent graph persistence is beneath
`src/HVO.SkyMonitor.CameraAgent.Common/Capture/Processing` and its local API is
beneath `src/HVO.SkyMonitor.CameraAgent/Endpoints`.

## Missing Runtime Contracts

### Central Graph Catalog And Assignment

[Issue #426](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/426)
owns immutable LogicHost graph revisions, assignment precedence and history,
host compatibility, and acknowledged delivery to CameraAgent. It also requires
a global basic revision and deterministic camera or installation, observatory,
then global-default assignment precedence.

The prototype assumes the following proposal details for a future UI. They are
candidate contract requirements, not an expansion of #426 by this handoff:

- Bind a proposal to the exact agent, logical-camera installation, expected
  local base revision, capability snapshot, expiry, and immutable graph
  identities.
- Use an agent-initiated channel without making central availability part of
  local correctness.
- Record retrieved, accepted, rejected, staged, activated, rolled back, and
  expired states independently with exact UTC timestamps and reason codes.
- Preserve the last accepted local revision for offline restart.
- Do not place proposals inside the fleet heartbeat acknowledgement. That
  acknowledgement carries agent, boot-session, sequence, disposition,
  server-time, and recommended-interval facts but no proposal.

At #426 start, its owner must decide which proposal fields, states, and topology
belong in that issue and explicitly defer or split the rest. The prototype does
not select SignalR, polling, long polling, or another transport. No inbound or
outbound topology is authorized by the current #426 text beyond acknowledged
versioned delivery; any refinement needs threat, outage, load, and recovery
evidence.

### Imported Edge Execution Evidence

Current CameraAgent durable contracts can describe execution identity, class,
status, graph identities, trigger, exact inputs, node plan identity, attempts,
outcomes, reasons, outputs, and availability. LogicHost does not currently
receive a complete immutable execution envelope.

Before implementing the edge half of the Processing page, the RM-005
coordinator must assign or split ownership for a bounded delivery contract. The
contract should:

- Version and idempotently acknowledge one immutable execution-evidence unit.
- Preserve origin, observatory, installation, capture, artifact, graph
  revision, definition, shared-plan, local-plan, node-plan, and attempt
  identities.
- Carry explicit node outcomes, reason codes, exact inputs and checksums,
  produced artifact identities, availability, and immediate lineage.
- Allow terminal evidence correction only through an explicit successor or
  append-only receipt, never silent replacement.
- Be independently retryable and backlog-bounded so it cannot delay raw ingest,
  artifact upload, live processing, or acquisition.
- Reconcile artifacts by identity rather than embedding image payloads in an
  execution message.
- Remain separate from fleet heartbeat and manifest-v2 artifact upload because
  those protocols acknowledge different durable units.

This reverse-direction evidence channel has no explicit owner in #421's current
child scopes. It is a dependency of the prototype's future edge-evidence UI,
not a new blocker for #426 or #427. The RM-005 coordinator must assign or split
it before authorizing that UI, and #426 must not absorb it without an explicit
scope decision. Do not hide the gap in a projection or infer node success from
artifact role presence.

### Central Graph Execution

[Issue #427](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/427)
owns expansion of frozen shared graph plans into durable LogicHost node jobs.
Its read model should preserve existing central derivative job evidence:

- Exact frozen predecessor and window inputs.
- Attempts, leases, outcomes, reason codes, timings, and outputs.
- Artifact checksum, descriptor, availability, provenance, and lineage.
- Requeue as another attempt on the same job. Preserve the current distinction
  between non-superseding reprocess and superseding replacement; only the latter
  records predecessor and supersession relationships.

It must add the graph-level evidence that the current derivative model does not
contain:

- Graph revision, definition, plan, node, recipe, and request identities.
- Required versus optional branches and explicit terminal graph disposition.
- Successor relationships for changed graph revisions.

The edge-versus-central comparison must select two exact execution identities.
It must not compare mutable camera state or claim that one host is universally
preferable.

### Production UI Ownership

Closed issue
[#107](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/107)
delivered the current LogicHost network and operations foundation. It should not
be reopened, and it explicitly does not make LogicHost an editor for
CameraAgent-owned settings.

No open issue currently owns the complete LogicHost page translation shown by
this prototype. After #426 and #427 stabilize their APIs, the roadmap
coordinator should authorize a separate UI issue rather than expanding either
runtime issue implicitly. That issue should consume the contracts above, not
redefine their durability or authority semantics.

## Suggested Page Decomposition

Use path-segment identity for production scope. Reserve query parameters for
filters, layout, and keyset cursor state.

- Network home and authorized observatory selection.
- Observatory overview with fleet and registration evidence.
- Logical-camera Current Sky using one authorized current-presentation selector.
- Logical-camera Archive using a reusable keyset-paged archive browser.
- Logical-camera Processing using graph, node inspector, execution, attempt,
  artifact, comparison, and policy components.
- Central Events and separately authorized scoped Operations pages.
- Protected Public Sky discovery based only on explicit release records.

Extract the layered image viewer from `OperationsCaptureDetail` rather than
building another renderer. Edge and central execution cards may share visual
components and evidence vocabulary, but they must use separate read and mutation
services.

## Truthful State Requirements

- Show capture time, first receipt, fleet heartbeat, object availability,
  reconstruction, processing completion, and publication as independent facts.
- Inline display requires exact eligible bytes. Artifact role presence alone is
  insufficient.
- Raw content continues to require short-lived audited authorization.
- Expired, missing, quarantined, partial, and unauthorized content remains
  metadata-only.
- Archive paging remains bounded. Do not fabricate a total count from one page.
- A central graph draft is an immutable successor draft. It never changes the
  effective graph or historical jobs in place.
- Presentation policy selects future central materialization behavior. It does
  not mutate retained Preview bytes or an existing release decision.
- Cameras without imported graph evidence show an explicit unavailable state.
  Do not copy the Hualapai Main Fisheye fixture to fill the gap.

## Continuation Sequence

1. Synchronize with current `main` and re-audit #421, the active #425 claim,
   #426, #427, native blockers, open PRs, and available capacity before claiming
   work.
2. Preserve the accepted static commit as a design checkpoint. Make visual
   prototype corrections on this branch only when they do not imply delivery.
3. Follow #421's approved central grouping only after its coordinator posts a
   claim and `READY` signal: #426 catalog and assignment first, then #427 generic
   central graph-node jobs in the same planned central PR. This handoff does not
   create another implementation slot or dependency.
4. Resolve and record ownership for immutable edge execution-evidence delivery
   independently. It is required before the future edge-evidence UI, not before
   #427.
5. Create and authorize the separate LogicHost UI issue after the production
   contracts are stable.
6. Implement pages from current production services, using this prototype only
   as interaction and state-language guidance.

## Validation

For static corrections, run at minimum:

```bash
node --check docs/prototypes/pipeline-operations/logichost-pages.js
git diff --check
```

Exercise all query routes at desktop and narrow portrait widths, including a
camera without detailed processing fixtures. Verify keyboard navigation, menu
focus, no horizontal overflow, and no borrowed image bytes. Keep screenshots
and temporary Playwright specifications outside the repository.

Production work follows the risk tier and exact gates selected by its owning
issue, `docs/planning/agent-execution.md`, and `docs/runbooks/ci-pipeline.md`.
At minimum it needs focused service/endpoint tests, authorization and
cross-observatory tests, bUnit coverage, browser flows, restart/outage evidence,
bounded read behavior, and performance evidence for any new durable delivery or
worker path.
