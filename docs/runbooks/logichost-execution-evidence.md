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
facts per unit fit the shared 256-fact response limit. Each origin has its own
sequence space. Negotiating the current origin does not prevent delivery of
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
