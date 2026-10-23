# LogicHost Execution Evidence Import

LogicHost receives the existing
[`hvo-cameraagent-execution-evidence-v1`](../contracts/cameraagent-execution-evidence-v1.md)
protocol in an independent metadata lane. A terminal acknowledgement means the
exact canonical evidence unit committed to SQL. It does not acknowledge an
artifact upload, establish object availability, or modify an edge execution.
Imported revisions and executions remain separate from LogicHost-owned graph
catalogs, assignments, jobs, and execution history.

## Authorize an installation

The current CameraAgent exporter derives its agent and origin-installation IDs
locally. Its agent ID is not the centrally issued ID used by fleet heartbeats.
The default exporter also omits the optional observatory, logical installation,
and installation-public IDs. LogicHost therefore requires an explicit authorized
binding before negotiation or submission. Negotiation never creates that
authority.

1. Register the CameraAgent and assign its logical camera installation using
   the existing LogicHost ownership workflow.
2. Enable the existing CameraAgent evidence exporter for the approved central
   endpoint. It creates its local origin before attempting negotiation; a
   receiver without a binding returns an authentication-blocked response.
3. On that CameraAgent, read its existing identity tuple without modifying the
   outbox:

   ```bash
   sqlite3 -readonly "$RAW_INGRESS_ROOT/evidence/execution-evidence-outbox.db" \
     'SELECT origin_installation_id, agent_instance_id, origin_identity_sha256,
             boot_session_id, software_version
      FROM execution_evidence_origins
      ORDER BY created_unix_ms DESC LIMIT 1;'
   ```

   The two local IDs are compact GUIDs. The origin hash identifies one boot and
   software-version sequence space; it is not the stable installation binding.
   The current CameraAgent status API exposes counters, not this identity tuple.
4. Using an owner credential authorized for the observatory, submit
   `POST /api/internal/execution-evidence/bindings`:

   ```json
   {
     "observatoryId": "<central observatory GUID>",
     "registrationId": "<central registration GUID>",
     "logicalCameraInstallationId": "<central installation GUID>",
     "originInstallationId": "<local origin installation GUID>",
     "agentInstanceId": "<local agent GUID>"
   }
   ```

   Use the existing authenticated API client; do not put credentials in a
   checked-in request or shell history. LogicHost obtains the installation
   public identity from its own registration and installation records.

The binding pins the local identity pair to the approved central authority.
Every supplied optional central identity must agree with it. An absent or
wrong binding yields the same nondisclosing authorization failure. A new boot
or software version creates a new sequence origin under the existing binding,
so earlier pending origins can still drain after restart. Moving an installation
or re-registering a device does not silently retarget retained evidence.

## Transport and receipts

The existing device transport uses these routes:

| Route | Body | Response |
| --- | --- | --- |
| `POST /api/device/execution-evidence/negotiate` | Canonical shared negotiation JSON | Selected version and bounded limits |
| `POST /api/device/execution-evidence` | Canonical envelopes separated by LF, including a final LF | Shared per-unit facts, contiguous prefix, missing ranges, retention |

Both routes authenticate `X-HVO-Device-Id` and `X-HVO-Device-Key`.
Submission also requires `X-HVO-Evidence-Origin` and media type
`application/x-hvo-execution-evidence-v1`. All units in one batch belong to the
same origin. A missing final separator is an incomplete transfer, never an
implicitly complete shorter batch. Unknown versions, duplicate JSON members,
invalid hashes, and oversized units fail validation.

The receiver publishes a 64-unit batch/resynchronization limit so four accepted
facts per unit fit the shared 256-fact response limit. Feedback ranges contain
at most 64 missing sequence positions in total across all inclusive ranges.
Larger gaps set `MissingRangesTruncated` and recover through successive bounded
replies. Each origin has its own sequence space. Negotiating the current origin does not prevent delivery of
already sealed units from older boots.

For a committed unit, feedback contains `Received`, `Validated`, `Accepted`,
and `Acknowledged`. Repeating its sequence and payload hash returns duplicate
facts for the same durable unit. A different payload for an occupied sequence
produces `evidence.sequence-conflict`, with bounded durable conflict evidence;
the original bytes remain unchanged. A lost response after commit is recovered
by duplicate delivery. SQL failure before commit never produces an
acknowledgement.

An execution arriving before its graph revision, an availability report before
its execution, or a correction before its predecessor remains retryable until
its dependency arrives. The referenced execution must belong to the same
authorized binding. It must not receive
a terminal rejection merely because delivery was reordered: the current
CameraAgent quarantines a unit when it receives a `Rejected` fact. Missing
ranges and resynchronization are bounded; neither side expands an arbitrary
sequence gap into an unbounded list.

## Inspect imported evidence

The read API applies observatory membership and credential scope before
selecting records. Missing and unauthorized resources have the same result.
List operations use increasing record IDs and bounded `take` values, rather
than offsets or unbounded history reads.

Discover binding IDs for one retained installation with:

```text
GET /api/internal/observatories/{observatoryId}/cameras/{logicalCameraId}/installations/{installationId}/execution-evidence/bindings
```

`installationId` is exactly the central `LogicalCameraInstallation.Id` primary
key, also exposed as `LogicalCameraInstallationId` in the existing binding view.
It is **not** `InstallationPublicId`; the endpoint never tries the other identity
when a lookup fails. The retained installation links `LogicalCameraId` and
`RegistrationId`; its camera supplies `ObservatoryId`. Binding creation verifies
that registration and installation relationship and pins the then-approved
installation public identity. Discovery resolves this full route relationship,
current user membership and API credential observatory scope before selecting
bindings for that registration and exact central installation.

`ExecutionEvidenceService.ListInstallationBindingsAsync(observatoryId,
logicalCameraId, installationId, userId, observatoryScope, cancellationToken)`
returns `ExecutionEvidenceInstallationBindingsView`: `Bindings` contains only
existing `ExecutionEvidenceBindingView` fields, ordered by binding ID, and
`Truncated` explicitly reports an additional retained row. The fixed limit is
16; SQL selects at most 17 rows through the existing `(RegistrationId, Id)`
index, including one sentinel. There is no cursor, offset, total count, or
all-installation search. Creation retains its configurable per-registration
maximum of 1–16 (default 4); discovery's fixed ceiling does not change that
policy. A truncated result is incomplete and must not be interpreted as the
installation's complete binding history.

Viewer, Manager and Owner members can read within credential scope. An
A-scoped key cannot read B even when its owner belongs to both. An authorized
installation without bindings returns `200` with `Bindings: []` and
`Truncated: false`; missing or denied relationships return nondisclosing `404`
(service `null`). Existing credential policy rejects unauthenticated/invalid
credentials. Cancellation propagates through authority and binding SQL reads.
Retired/replaced installations and revoked registrations remain readable under
current membership; no active-registration requirement or successor fallback
changes their original binding identities. This discovery read performs no
writes and returns no keys, canonical bodies, artifact availability, or inferred
execution success. Existing binding-ID reads below remain the next explicit
step; discovery does not fetch their children.

Availability reports can include artifacts from earlier captures or additional
outputs. Associating a report with an execution does not assert that every
reported artifact was produced by that execution.

| Path under `/api/internal/execution-evidence/bindings/{bindingId}` | Purpose |
| --- | --- |
| `/status` | Receipt progress, dependency/gap/conflict and capacity status |
| `/origins?afterId=0&take=50` | Boot/software sequence origins |
| `/units?afterId=0&take=50` | Imported unit metadata; optional origin and kind filters |
| `/units/{unitId}` | Exact retained canonical evidence document |
| `/origins/{originIdentitySha256}/feedback?afterSequence=0&take=64` | Bounded durable acknowledgement reread |
| `/observations?afterId=0&take=50` | Central reconciliation observations |

Status includes the number of origins with gaps and the oldest gap detection
time. It also reports due artifact checks and their oldest due time; the count
is capped at 1,000 with an explicit truncation flag. These are central receipt
and reconciliation backlogs. Read the CameraAgent outbox status separately for
pending sender units and retry age.

Retained canonical documents preserve graph definitions and plans, node
attempts, outcomes, reasons, selected inputs, outputs, and lineage. Frozen-plan
identities are authenticated-origin claims tied across the imported documents.
The protocol independently verifies the definition content hash and canonical
envelope hash; it does not export every input needed to recompute the edge's
local-plan hash.

## Artifact availability and recovery

Evidence acceptance does not wait for artifact bytes. Reconciliation separately
matches the authenticated installation, capture, artifact identity, and supplied
checksum/length metadata against central ingest. Object metadata checks use
the object-store stat operation. Image bytes are never carried by an evidence
request or read as part of receipt processing.

An artifact absent from central ingest, a missing object, or a quarantined
object remains an explicit reconciliation observation. Later upload or object
recovery can produce a new observation without rewriting the original producer
evidence. An edge availability report is also an observation; it is not proof
that LogicHost possesses those bytes.

Storage and cardinality quotas apply to origins, retained units, bytes, and
diagnostics. Pressure is explicit retryable backpressure on this lane. Accepted
graph/execution history is not silently deleted to make room: the exporter may
have released its acknowledged copy and cannot be used to reconstruct a gap
created by central retention. Investigate capacity and the approved retention
policy before increasing quotas. Receiver failure must remain independent of
capture, raw acceptance, artifact upload, and central processing.

## Receiver limits

Configure these values under LogicHost's `ExecutionEvidence` section. Defaults
apply when the section is absent; invalid values fail startup validation.

| Setting | Default | Allowed range |
| --- | ---: | ---: |
| `MaximumConcurrentRequests` | 2 | 1–8 |
| `SqlTimeoutSeconds` | 5 | 1–30 |
| `RequestTimeoutSeconds` | 30 | 1–120 |
| `MaximumBindingsPerRegistration` | 4 | 1–16 |
| `MaximumOriginsPerBinding` | 256 | 1–4,096 |
| `MaximumUnitsPerBinding` | 100,000 | 1–1,000,000 |
| `MaximumStoredBytesPerBinding` | 1,073,741,824 | 1–107,374,182,400 |
| `MaximumConflictsPerBinding` | 256 | 1–4,096 |
| `MaximumObservationsPerBinding` | 100,000 | 1–1,000,000 |
| `ReconciliationIntervalSeconds` | 60 | 1–3,600 |

The receiver has a separate SQL connection pool named `HVO.ExecutionEvidence`,
bounded to `MaximumConcurrentRequests + 2` connections. Saturation, quota
pressure, SQL failure, and the receiver's request timeout return HTTP 503 with
`Retry-After: 5`. Device authentication failures return 401; an unapproved origin
returns 403. Correct the binding or credentials before retrying an authorization
failure. Do not clear the edge outbox or central evidence tables to recover a
temporary outage.

Submission has fixed protocol limits of 64 units and 16 MiB of envelope bytes
plus LF separators. Reconciliation processes at most 32 references per sweep
and performs object metadata I/O outside its SQL transaction. Quotas bound
retained history; they are not an automatic retention or deletion schedule.

The `HVO.SkyMonitor.ExecutionEvidence` meter exposes request counts and duration,
unit outcomes, and reconciliation observation outcomes. Its tags contain bounded
outcome/state names. The activity source has the same name and traces receive
and reconciliation operations. LogicHost registers both with its existing
OpenTelemetry pipeline. Warning events 2480, 2481, 2482, and 2483 identify
temporary reconciliation failure, a durably audited sequence conflict, receipt
quota pressure, and temporary receiver failure respectively, without including
credentials or canonical payloads.

LogicHost remains unreleased. Initialize an empty database with the candidate's
single canonical baseline using the
[database initialization procedure](logichost-database-initialization.md).
This schema change does not provide an upgrade path for an earlier unreleased
database.

## Exact-artifact reconciliation reads (#1554 / #1240 handoff)

These LogicHost-only projections read existing immutable relationships. They do
not change the producer/transport schema or create storage, reconciliation, or
receipt state. Both routes require `ApiKeyRead` and the existing owner credential
check; current observatory membership and API-key observatory scope are resolved
on every call. Viewer, Manager, and Owner memberships may read. Anonymous/invalid
credentials retain the endpoint policy's 401/403 behavior. Missing or mismatched
authorized resource paths return service `null` / HTTP 404 without association,
count, or cursor disclosure.

The common prefix is:

```text
/api/internal/observatories/{observatoryId}/cameras/{logicalCameraId}/installations/{installationId}/execution-evidence/bindings/{bindingId}/units/{unitId}
```

`installationId` is the retained central installation primary key, never its
public identity. The server checks its camera, observatory, registration and
pinned installation-public identity against the binding, and checks the exact
accepted unit and its origin under that binding. Retired installations remain
readable; current camera selection and mutable registration status do not select
historical evidence.

1. `GET {prefix}/artifacts?take=50&cursor={opaque}` calls
   `ExecutionEvidenceService.ListUnitArtifactsAsync(observatoryId, logicalCameraId,
   installationId, bindingId, unitId, userId, observatoryScope, cursor = null,
   take = 50, cancellationToken = default)`. The response schema is
   `hvo-imported-unit-artifacts-v1`. `Association` includes the binding, pinned
   installation public ID, producer installation/agent IDs, origin identity hash,
   and the complete existing unit-view provenance (unit/origin IDs, origin
   sequence, evidence ID, payload hash, kind, revision/execution/capture IDs,
   producer/receipt timestamps and acceptance). `Artifacts` exposes the retained
   `ArtifactReferenceId`, reference `Ordinal`, capture/artifact IDs, output,
   payload and descriptor hashes, payload length, media type, role and variant.
   Join an input/output's entire canonical artifact-reference tuple to these
   fields; null metadata is an exact absent value, not a wildcard. References
   were deduplicated by the importer across the primary reference and node
   inputs/outputs. Reference ordinal is not a node/input/output ordinal and does
   not establish production. Availability-report membership also does not
   establish production; inspect `Association.Unit.Kind` and canonical bytes.
   `take` must be 1–100 (default 50); invalid bounds return HTTP 400. SQL selects
   at most `take + 1` metadata rows ordered by reference ordinal and never reads
   canonical bodies or observation history. `NextCursor` is null at exhaustion;
   otherwise pass it unchanged on the same selection. Its protected v1 payload
   binds the entire route, unit, user and credential observatory scope. There
   are no extra filters. Page size may change. Tampering, oversize (>4096 chars),
   or replay on another selection returns null/404 after authorization.
2. `GET {prefix}/artifacts/{artifactReferenceId}/observations?captureId={captureId}&artifactId={artifactId}`
   calls `ExecutionEvidenceService.GetArtifactReconciliationAsync(observatoryId,
   logicalCameraId, installationId, bindingId, unitId, artifactReferenceId,
   captureId, artifactId, userId, observatoryScope, cancellationToken = default)`.
   The schema is `hvo-imported-artifact-reconciliation-v1`. The exact reference,
   capture and artifact must all belong to the requested unit and binding.
   `Association` and `Artifact` carry the same immutable identities as above.
   `Observations` contains at most the latest 50 retained observations, newest
   database ID first, including repeated states, reason code, observed timestamp
   and object generation. A 51st sentinel sets `ObservationsTruncated`; no total
   count or history-continuation cursor is returned. Refresh directly to obtain
   the latest observation; consumers must not drain binding history or sweep
   offsets. Empty observations mean no retained central result, never available.

Central states/reasons remain separate from producer facts and receipt
acknowledgements. Current reconciliation returns `Missing` (artifact not received,
object missing or expired), `Unresolved` (object/descriptor pending or object-store unavailable),
`Quarantined` (ownership, metadata, output identity, descriptor or object mismatch),
or `Available` with object generation.
Conflicting metadata is represented by its quarantine reason; evidence sequence
conflicts remain importer receipt/conflict facts, not image availability. A
producer `Available` report or an `Acknowledged` receipt alone establishes none
of these central results. These are observations, not a guarantee that bytes
remain available after the observation time, and observation pressure can retain
an older result (see the existing pressure semantics above).

Current-source evidence is in `ExecutionEvidenceIntegrationTests.ArtifactReads.cs`:
the delivered #538 revision/execution/availability fixtures are read through both
HTTP routes; tests cover exact full reference fields, denial substitutions,
removed membership, credential scope, malformed cursors, page bounds and latest
repeated-observation truncation. The existing combined
`EvidenceBeforeActualArtifactUploadReconcilesLaterWithoutRewritingProducerFacts`
now resolves artifact mappings and before/after observations through the
authorized service contract; private database access only schedules the test's
reconciliation work. No #1240 UI or CameraAgent contract changes are included.

## Camera imported execution inspector (#1240)

Open **Imported executions** in an authorized logical-camera workspace:

```text
/app/network/observatories/{observatoryId}/cameras/{cameraId}/imported-executions
```

Select a retained central installation, its existing binding, and an exact unit.
Installations include retired identities; selecting one never retargets to a
replacement. An installation without a binding, or a filtered page without
units, explicitly reports unavailable imported evidence. Query strings preserve
the explicit selection, kind/origin filters, independent continuations and focus.
Returning from unit detail restores the selected unit link on its evidence page.

The inspector composes the delivered importer reads through
`IImportedExecutionInspectorReadService`; it creates no durable contracts or
importer state. Installation discovery projects the existing retained central
primary key because the network installation summary exposes only its public
identity. Current camera/observatory membership is checked before selecting and
again after the final read. Bindings and exact units must belong to the selected
retained route. Credential scope is intersected with current membership.

Each view replaces its previous page rather than accumulating history:

| Collection | Display/read bound and continuation |
| --- | --- |
| Retained installations | 25 plus one sentinel; seek by assigned UTC and public identity using the existing camera index |
| Bindings | Existing fixed 16 plus truncation; no automatic child discovery |
| Origins and units | 25 plus one sentinel; increasing retained ID; explicit protected UI continuation |
| Canonical execution nodes | 25 ordered node identities; explicit protected continuation |
| One node's attempts, inputs and outputs | Independently 25 plus sentinel; attempt number/input ordinal/output ordinal continuations |
| Producer availability observations | 25 from the sealed bounded report, with explicit continuation |
| Exact artifact mappings | Existing `ListUnitArtifactsAsync`, take 25 and its protected cursor |
| One exact artifact's central observations | Existing latest 50, repeated states retained, explicit truncation; refresh for latest |
| Exact receipt | One selected origin sequence, at most four receipt facts; existing bounded missing ranges/truncation |
| Explicit central comparison | `ICameraOperationsReadService.GetExecutionAsync`, one exact UUID and independently 25 nodes; frozen sources retain their compiler bound |

UI continuations protect the user, credential observatory scope, complete route,
collection and relevant selection/filter. The canonical document remains inside
the producer contract's byte/cardinality limits; only one node and one page of
each child is returned to the renderer. Revision definition and frozen-plan text
are explicitly truncated at 8,192 display characters each. These snippets do not
claim a complete plan. No automatic page drain, history scan, offset sweep or
inferred total is used.

Receipt times, producer times, origin sequence/hash, execution/revision identities,
corrections and assignment provenance remain separate. An acknowledgement means
the exact evidence unit was accepted; it never proves byte availability. The
entire canonical artifact tuple must match the current mapping page, including
exactly absent null fields, before an input/output receives an observation link.
Other mapping pages can be selected explicitly. A deduplicated reference ordinal
is not a node's input/output ordinal, and an availability report does not prove
which execution produced an artifact.

Central observations retain their state, reason, timestamp and object generation.
Empty results mean unknown availability. Missing, unresolved and quarantined
results stay metadata-only. Even an `Available` observation is point-in-time
evidence; the **Open exact capture viewer** link reuses the existing authorized
archive viewer, which independently checks current access and content. The link
resolves the producer capture UUID (`CentralFrame.FrameId`) to the authorized
central row UUID (`CentralFrame.Id`) through the exact artifact and binding;
these identities are never assumed equal. The
inspector itself requests no image bytes and never substitutes another capture.

For comparison, enter a central execution UUID while an imported execution unit
is selected. Both exact identities stay visible. Definition equality and each
host's distinct plan identity are displayed without implying equivalent success.
Node status, attempts and lineage come from recorded facts. Missing central
execution or node facts stay explicit; roles, graph levels and report membership
do not imply production or success. There are no evidence-edit, retry or central
successor controls in this view.

Refresh removes the old snapshot before awaiting authority, uses a fresh service
scope, cancels older loads and rejects late responses from superseded generations.
Authentication changes clear child components too. The existing authorized HTTP
binding-discovery route rechecks the browser's current credential before later
interactive reads; the protected status endpoint also checks that its actor name
matches the circuit, including before an installation is selected. This handles a circuit that outlives its
login cookie; it supplements server-side resource authorization. Focus, visibility,
online/reconnect and 30-second revalidation all perform fresh bounded reads.
Offline/disconnect events clear received DOM text and links without caching it,
and invalidate pending server loads. Reconnect restores facts only after a new
credential and resource check. Read/credential failures show an explicit cleared
unavailable state with refresh/sign-in guidance.

### Reproduce acceptance evidence

The focused `ImportedExecutionInspectorTests` bUnit suite uses the delivered v1
fixtures, while `ExecutionEvidenceIntegrationTests.Inspector.cs` exercises real
receiver/SQL reads, scope and cursor denial, independent child continuation and
membership revocation during canonical selection. The existing combined
`EvidenceBeforeActualArtifactUploadReconcilesLaterWithoutRewritingProducerFacts`
also reads the resulting exact real-exporter unit through the UI composition.

On Linux with the pinned SDK, Docker and cached Playwright Chromium, build the
exact clean candidate in Release, then run:

```bash
export HVO_EVIDENCE_REVISION="$(git rev-parse HEAD)"
export HVO_ISSUE1240_EVIDENCE="$PWD/TestResults/issue-1240"
dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj --no-build --configuration Release --filter "FullyQualifiedName~ImportedExecutionBrowserAcceptanceTests"
```

Use the worker's heavy-gate lock/timeout wrapper and no node reuse/shared compiler
when building. The harness uses the existing fixture Operator login and writes
screenshots, screen-reader trees, browser version and structured source identity
(Git head/dirty fingerprint and exact loaded assembly hashes). It covers desktop,
390/320-pixel portrait, short landscape, keyboard/touch/focus return, no horizontal
overflow, cookie loss, membership removal, metadata-only states and reconnect.

The W3M case seeds 1,000 and 10,000 valid sealed units. The selected revision,
execution and availability fixtures pass through receiver HTTP; bulk metadata-only
availability history is seeded directly with its reference/check relationships.
It records query count, actual physical SQL rows read/returned and logical reads
from both contexts, serialized view bytes, HTTP document bytes, render latency,
post-GC browser heap and DOM counters. One warmup plus five measured browser runs
and ten explicitly requested pages assess bounded page memory. This is a
concurrency-one candidate measurement, not ingestion throughput or production
qualification. Test preparation and successful compilation alone are not browser,
SQL or combined acceptance evidence; retain actual run results on the exact head.
