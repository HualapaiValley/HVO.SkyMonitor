# LogicHost central operations workspace

Open **Central operations** from a protected logical-camera workspace:
`/app/network/observatories/{observatoryId}/cameras/{cameraId}/central`.
Both path identities are checked against current membership. Choosing a graph
from the shared catalog never grants private camera or execution access.

The sections retain one page at a time (25 rows). Use the explicit next/first
links; browser back/forward retains scope, selection, continuation and focus.
Job and event pages examine at most 50 of this camera's captures per request.
They may be empty while still offering continuation. No total is inferred.
Attempts, inputs, outputs, execution nodes and proposal facts continue separately.
Artifact continuations additionally bind the caller, observatory, camera and
exact capture. A rejected continuation offers a fresh first page.

Executions show the exact retained revision, definition and central plan hashes,
frozen sources, node jobs and predecessor/successor identities. Job inspectors
show attempts and exact input/output identities, checksums, timing, reasons and
lineage without lease credentials. Expand the retained-fact disclosures for
technical details. Definitions display at most 8,192 characters and explicitly
mark truncation; catalog lists do not load every definition.

**Assignments** resolves the effective Central or Edge assignment now and
separately pages retained assignment history, including expired and future rows.
**Delivery facts** keeps Retrieved, Accepted, Rejected, Activated, RolledBack,
Expired and Superseded facts separate, with occurrence and recording UTCs and
reasons. Staged is explicitly derived from accepted inactive evidence; it has no
invented timestamp. Heartbeat is independent of delivery and activation.

Authority is checked again when an action runs:

| Action | Existing authority and outcome |
| --- | --- |
| Read camera history | Current Viewer, Manager or Owner membership, intersected with credential scope. Denied exact resources are concealed. |
| Cancel/requeue/reprocess jobs | Manager or Owner in the job's observatory. A changed or ineligible state is reported without overwriting history. |
| Cancel an execution | Manager/Owner, or the requesting actor cancelling their own Replay. A readable execution without cancellation authority reports Forbidden; an inaccessible execution remains concealed. |
| Replay the inspected sources/revision | Workspace Manager/Owner control using the existing central replay service. Sources come only from the inspected execution, in frozen ordinal order. The URL retains the displayed request key for retry after an uncertain response. |
| Review an event | Manager/Owner across every source and background contributor, or the existing global administrative exception. A decision retains the displayed strong If-Match ETag, exact assessment and action-specific Idempotency-Key. A stale ETag or request conflict requires inspecting current durable state. |
| Publish/retire a global graph revision | PlatformEditor. Observatory ownership alone is insufficient. |
| Change future-job policy | Observatory Owner. Cloud threshold and central transient validation are the existing versioned overrides. Frozen historical plans are unaffected. |
| Release/withdraw an artifact | Observatory Owner, for one exact capture artifact on the current page. The existing publication service rechecks eligibility and records the authority, actor and decision. Other artifacts are unchanged. |

Requeue permits a new attempt on the same standalone job, retaining earlier
attempts. Graph nodes link to their exact execution for graph replay; they cannot
be requeued independently.
Reprocessing requests successor work through the existing supersession rules.
Neither operation edits received CameraAgent execution evidence. The workspace
links to **Imported executions** and the reusable exact-capture archive viewer;
it never substitutes another capture's image. Object materialization, processing
completion and public release remain independent states. Unavailable artifacts
retain metadata and reasons, with release offered only for eligible artifacts.

The legacy derivative-job HTTP operations still require global `api.admin`.
Event notification retry, payload release and retrospective reprocessing retain
their existing global administrative requirements; ordinary observatory ownership
is insufficient. This workspace does not add adapters that bypass those policies.
There is no arbitrary presentation-policy editor or inbound CameraAgent control.

**Refresh central operations** checks the current browser credential and current
resource access. Authentication changes, offline/disconnect, page suspension and
failed reads clear protected facts and action results. Reconnect revalidates the
same selection. A 30-second access recheck retains a bounded snapshot; it does
not drain history. If an action response is lost, inspect durable state before
another decision and reuse the retained replay key for an uncertain replay.

## Acceptance harness

`CentralOperationsWorkspaceTests` and `CentralOperationsWorkspaceServiceTests`
cover component behavior and composition over the production scoped reads.
`CentralOperationsWorkspaceAuthorityTests` uses SQL Server and the real audited
job, policy and publication services. Existing graph scheduler/cancellation and
event-review API tests retain the replay, contributor, credential and conflict
matrices at their durable authorities.

`CentralOperationsWorkspaceBrowserAcceptanceTests` is a separate Manual harness.
It uses the production Kestrel host and seeded store-valid metadata histories,
including replaced installations, equal timestamps and lifecycle histories.
The responsive flow covers keyboard/touch, focus, the accessibility tree,
dark theme, overflow, failed credential checks, disconnect/reconnect, cookie
loss and membership removal. The W3M flow measures 1K/10K histories: SQL command
counts and actual plan rows/reads, serialized snapshot bytes, HTTP document
bytes, browser render times, heap/DOM observations and ten consecutive pages.
It records one warmup, five measured browser runs and concurrency one. It
establishes absolute observations for this new UI; it makes no production or
multi-client capacity claim.

From a clean commit, restore/build the Integration project in Release and run
the harness under the host's heavy-gate lock (Chromium and Docker are required).
Bind the requested revision so the evidence pack can attest its source:

```bash
workspace_evidence="$(hvo scratch HVO.SkyMonitor 1241 browser)"
flock -o /tmp/hvo-heavy-gate.lock timeout 1800 \
  env -u HVO_PRODUCTION_CATALOG_BUNDLE HVO_EVIDENCE_REVISION="$(git rev-parse HEAD)" \
  HVO_ISSUE1241_EVIDENCE="$workspace_evidence" \
  dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj \
  --no-build --configuration Release -nodeReuse:false -p:UseSharedCompilation=false \
  --filter "FullyQualifiedName~CentralOperationsWorkspaceBrowserAcceptanceTests"
```

The evidence pack binds the source checkout/dirty fingerprint, relevant assembly
hashes, browser version, workload and measurements. Inspect each result before
claiming acceptance. The protected branch profile remains `development-v1`;
these focused checks do not constitute production qualification.
