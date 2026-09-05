# CameraAgent Execution Evidence v1

`hvo-cameraagent-execution-evidence-v1` is the transport-neutral, versioned
contract a completed CameraAgent uses to expose its immutable graph revisions
and graph executions to a future LogicHost receiver. It is a vocabulary and a
set of rules, not a sender or a receiver: issue #536 defines it, #537 adds the
CameraAgent durable exporter, and `RM-018` adds the LogicHost import half.

The contract lives in `src/HVO.SkyMonitor.Processing`
(`GraphExecutionEvidenceContracts.cs` and `GraphExecutionEvidenceJson.cs`), so
both hosts consume it from the shared processing assembly and neither host
references the other. No transport envelope was added to
`HVO.SkyMonitor.Fleet.Contracts`: that assembly has no project references and
owns the heartbeat channel, and evidence export is explicitly separate from the
heartbeat, so putting the envelope there would either duplicate the graph
vocabulary or force `Fleet.Contracts` to depend on `Processing`, AgentCore,
Astronomy, and Imaging. The envelope is therefore part of the same shared
vocabulary as the bodies it carries.

## Boundary

- The contract carries identity, hashes, timing, outcomes, reason codes, and
  lineage. It never carries image bytes, payload paths, sidecar paths,
  credentials, or tokens; a contract test enforces that no exported member is a
  byte array, a stream, or a path/credential-shaped name, and the W6 harness
  measures that no execution or availability member carries an opaque
  payload-sized string.
- A graph revision does carry two documents by design: the canonical graph
  definition and the frozen plan. The definition includes each node's effective
  options, which are operator-supplied JSON, and the contract does not inspect
  or redact inside them because doing so would change the content address the
  definition identity is built from. Host and runtime configuration, secrets,
  and endpoints are never carried, and `HVO.SkyMonitor.Processing`'s graph
  validation rejects host-incompatible nodes
  (`processing.graph.host-inapplicable`). It does not scan node options for
  secrets, so keeping an operator secret out of a node's options remains an
  operator responsibility on both hosts.
- Local correctness never depends on export. Nothing in this contract is read
  during acquisition, raw ingress, live processing, publication, or replay.
- `AgentCore` is unchanged. #536 added only a read-only projection
  (`ProcessingGraphEvidenceProjection`) over the delivered durable execution
  store plus one read-only accessor for a persisted revision snapshot. #537 adds
  the exporter described below: it adds read-only accessors to the delivered
  store and a separate SQLite database of its own, and it still modifies no
  delivered durable schema, execution, or persistence behaviour.

## Identities

| Identity | Member | Notes |
| --- | --- | --- |
| Origin installation | `origin.originInstallationId` | The CameraAgent installation. |
| Agent instance | `origin.agentInstanceId` | Matches the fleet heartbeat identity. |
| Boot session | `origin.bootSessionId` | Distinguishes restarts. |
| Observatory | `origin.observatoryId` | Optional; absent for an unregistered agent. |
| Logical camera installation | `origin.logicalCameraInstallationId` | Optional. |
| Installation public id | `origin.installationPublicId` | Optional. |
| Origin identity | `origin.identitySha256` | Canonical SHA-256 over every other origin member; the stable key a receiver orders sequences by. |
| Capture | `execution.captureId` | |
| Artifact | `…artifact.artifactId` | |
| Graph revision | `graphRevision.revisionId`, `execution.graphRevisionId` | |
| Graph definition | `definitionIdentitySha256` | Content address of `canonicalDefinition`. |
| Shared plan / local plan | `sharedPlanIdentitySha256`, `localPlanIdentitySha256` | Frozen plan identities. |
| Node plan | `nodes[].planSha256` | |
| Attempt | `nodes[].attempts[].attemptNumber` | |
| Trigger | `execution.triggerKind`, `execution.triggerReference` | |
| Input / output | `nodes[].inputs[]`, `nodes[].outputs[]` | |
| Checksum | `payloadSha256`, `descriptorSha256`, `artifact.payloadSha256` | |
| Outcome / reason | `attempts[].outcome`, `*.reasonCode` | |
| Lineage | `inputs[].artifact`, `outputs[].artifact`, `execution.primaryArtifactId` | |
| Evidence unit | `evidenceId` + `originSequence` | |

## Bodies

An envelope carries exactly one body, named by `kind`:

- `GraphRevision` — the content-addressed canonical graph definition plus the
  frozen plan. `canonicalDefinition` hashes to `definitionIdentitySha256`;
  `frozenPlan` is the persisted CameraAgent frozen-plan document that carries
  the shared-plan identity, the local-plan identity, and every node's frozen
  plan. `origin` is `LocalOnly` or `CentrallyAssigned`; a centrally assigned
  revision must carry `assignment` (proposal, catalog revision, assignment,
  registration, installation, capability snapshot, issue/accept times) from the
  #426 delivery flow, and a local-only revision must not.
- `GraphExecution` — one immutable execution: class, status, capture, primary
  artifact, revision and plan identities, trigger, priority, timing, attempt
  count, cancellation, and every node with its plan hash, required flag,
  status, reason, inputs, attempts, and outputs. Retries are additional
  attempts; nothing is ever rewritten.
- `ArtifactAvailability` — timestamped observations of whether an artifact's
  bytes are `Available`, `Missing`, or `Quarantined`. Availability changes over
  time, so it is a separate sequenced unit rather than a member of the
  immutable execution. A later report supersedes an earlier observation of the
  same artifact and never corrects a production fact; a correction on an
  availability envelope is rejected.

## Artifact reconciliation without image bytes

A processing output is content-addressed: `artifactId` must equal
`ProcessingIdentity.CreateArtifactId(outputIdentitySha256)`, and the contract
rejects any other value. A raw capture input carries no output identity and
reconciles through `descriptorSha256` and `payloadSha256`. Optional
`payloadLength` and `mediaType` let a receiver match an object it already holds.
No byte ever travels through this contract.

`outputs[].ordinal` is the dense export ordinal the delivered store surfaces,
not the durable `output_ordinal` column; order is preserved because the store
reads outputs ordered by the durable ordinal.

`inputs[].windowPosition` is the input's position relative to the execution's
own capture, as the durable window selector records it: `0` is this capture and
a negative value is that many captures earlier in a trailing window. It is a
signed integer with no contract-level bound, because the durable column has
none.

### When a derived window is resolved

A raw window is resolved when the execution is created, because every raw
capture it can name is already committed. A derived window - one whose inputs
are another node's outputs - is resolved differently by execution class:

- A **replay** execution resolves and pins its derived window when it is
  submitted, and consumes exactly those pins. Re-running a replay never
  reselects.
- A **live** execution is created when its own raw capture is accepted, before
  the earlier captures of a trailing window have finished processing. It
  therefore resolves and pins its derived window when the consuming node runs,
  replacing any earlier selection for that node, so the pinned inputs are
  exactly the inputs the attempt consumed and the retention hold covers them for
  the life of the execution.

A live derived window is always trailing; centered windows are replay-only. Only
an earlier capture whose execution has completed and published the producing
node's output is eligible, so an earlier capture that failed, was skipped, or is
still running is **excluded, never waited for**. The selector then takes the most
recent eligible outputs up to the window's maximum input count, so an excluded
capture is skipped over and an older eligible capture takes its place rather
than leaving a hole. A trailing window is still allowed to be shorter than its
configured maximum - a freshly started agent has no history - and the
combination records only the sources it actually used, with `stackCount`
reporting that count.

## Sequencing, idempotency, conflict, and acknowledgement

- `originSequence` is a stable, strictly increasing sequence per
  `origin.identitySha256`. `(originIdentitySha256, originSequence)` is the unit
  key.
- `payloadSha256` is the canonical SHA-256 over the whole envelope with the
  hash member replaced by 64 zeros. Two envelopes with equal hashes are
  byte-identical after canonicalization, so duplicate and conflict detection
  are exact.
- A receiver emits four distinct facts per accepted unit, in order: `Received`
  (bytes durably stored), `Validated` (parsed, hash matched, limits satisfied),
  `Accepted` (committed), and `Acknowledged` (terminal; the origin may release
  its retention hold).
- Re-submitting the same sequence with the same payload hash is idempotent: the
  same four facts return with `duplicate: true` and the stored unit is not
  rewritten.
- Re-submitting the same sequence with a different payload hash is a conflict:
  exactly one `Rejected` fact with `evidence.sequence-conflict` and the stored
  hash. Terminal evidence is never silently replaced.
- An invalid unit yields `Received` then `Rejected` with the validation reason
  code and field path.
- `ExecutionEvidenceRetentionV1` states the acknowledged-through sequence, how
  long acknowledgements stay readable, and how many are retained.

`GraphExecutionEvidenceJson.CreateReceiverFacts` implements exactly these rules
so both hosts agree on them by construction.

## Gap detection and bounded resynchronization

`ExecutionEvidenceFeedbackV1` reports `contiguousThroughSequence` and the
ordered inclusive `missingRanges` above it, capped at 64 ranges with an explicit
`missingRangesTruncated` flag. `GraphExecutionEvidenceJson.CreateResyncRequest`
turns that feedback into an `ExecutionEvidenceResyncRequestV1` clipped to 64
ranges and 256 total units, so a large gap resynchronizes over several bounded
requests instead of one unbounded replay.

## Append-only corrections

A correction is a new evidence unit with a strictly greater `originSequence`
that names its predecessor (`correctsEvidenceId`, `correctsOriginSequence`) and
an explicit `reasonCode`. The predecessor is retained. A correction may not
reference itself, may not carry a sequence at or above its own, and may not be
attached to an availability body.

## Version negotiation and forward incompatibility

`ExecutionEvidenceNegotiationRequestV1` offers the producer's supported schema
versions; the response selects the most preferred shared version and publishes
`ExecutionEvidenceLimitsV1`. With no shared version the disposition is
`Unsupported` with `evidence.unsupported-schema` and the producer must not send
evidence. A producer that has negotiated is expected to apply the minimum of the
published and its own local value for every limit; this contract validates only
that a published limits record is well formed, and the sender that applies the
minimum is delivered by #537. On the wire, a payload whose root `schemaVersion`
is unknown — such
as a future `hvo-cameraagent-execution-evidence-v2` — is rejected before any
member is interpreted, so a forward-incompatible unit is never partially
applied. Unknown members, duplicate JSON keys, numeric enums, non-UTC
timestamps, and lowercase SHA-256 values are all rejected: SHA-256 members are
uppercase hexadecimal in canonical form so one payload has exactly one
canonical byte sequence and one hash.

## Limits

| Limit | Value | W6 measurement |
| --- | --- | --- |
| Envelope bytes (absolute) | 8 MiB | revision 64,580 |
| Execution envelope bytes | 2 MiB | 9,347 |
| Availability envelope bytes | 1 MiB | 1,658 |
| Canonical definition bytes | 2 MiB | 27,197 |
| Frozen plan bytes | 2 MiB | 35,701 |
| Nodes per execution | 128 | 14 declared and 14 exported |
| Attempts per node | 32 | 1 observed |
| Inputs per node | 512 | 3 observed |
| Outputs per node | 128 | 1 observed |
| Inputs per execution | 2,048 | 3 observed, 49 declared upper bound |
| Outputs per execution | 1,024 | 1 observed, 17 declared upper bound |
| Availability observations | 1,024 | 1 |
| Facts per feedback | 256 | |
| Missing ranges / resync ranges | 64 / 64 | |
| Resync units | 256 | |

Every value above travels in the negotiation response, so a producer learns
them before it sends anything and applies the minimum of the published and its
own local value. The following caps are enforced by validation but are not
negotiated, because they are fixed by the durable schema or by the message
shape:

| Limit | Value | Source |
| --- | --- | --- |
| Feedback bytes | 256 KiB | Message shape |
| Resync request bytes | 16 KiB | Message shape |
| Negotiation bytes | 16 KiB | Message shape |
| Supported schema versions | 8 | Message shape |
| Identifier length | 128 | Durable `node_id`, `trigger_reference`, `lease_owner` |
| Reason-code length | 128 | Durable `reason`, `failure_reason` |
| Software-version length | 64 | Fleet contract |
| Media-type length | 128 | Message shape |

The per-node input and output caps mirror the durable ordinal constraints; the
per-execution aggregates are the binding limit and are checked after every
per-node check, so an execution can satisfy every node cap and still be
rejected on the aggregate.

The byte caps are canonical-form caps, and validation measures the canonical
form. A receiver additionally refuses a payload whose *raw* length exceeds the
cap for its declared body kind before deserializing it, because raw length is
what bounds the work of materializing it. A conformant producer always sends
canonical bytes, for which the two lengths are identical; a whitespace-padded
payload can therefore be refused with `evidence.payload-too-large` even when its
canonical form would have fit.

The absolute envelope cap must hold a maximum-size revision: the durable store admits a
2 MiB canonical definition and a 2 MiB frozen plan for one revision
(`processing_graph_revisions` blob constraints and
`ProcessingGraphJson.MaximumDocumentBytes`), so the definition and frozen-plan
caps match those values exactly and the absolute envelope cap exceeds their sum.
A tighter cap would leave a legally persisted revision permanently
unexportable. Execution and availability envelopes carry no document blob and
keep their own tighter caps, enforced by body kind.

The measurement runs the representative `W6` workload (ASI676MC 3552x3552
Bayer12-in-16, 25,233,408 raw bytes, `cameraagent.standalone-w6.json`, 14
effective nodes) through the delivered durable SQLite execution store and
projects the result into this contract:
`GraphExecutionEvidencePayloadMeasurementTests` (Manual, CameraAgent test
project). The declared bounds are computed from the frozen plan — one entry per
declared node, per declared output, and per declared input multiplied by its
window's maximum input count — so they bound any execution of that revision
rather than only the one measured. The harness also asserts that the
execution and availability evidence together stay under a thousandth of the
25,233,408-byte frame, that no execution or availability member carries an
opaque string longer than 128 characters, and that every durable byte
(`raw-ingress.db`, its WAL and its shared-memory file) is unchanged across each
projection — one before/after pair brackets the revision projection and a second
brackets the execution and availability projections, so the projection is
read-only by measurement, not only by inspection. Retained evidence is written to
`TestResults/issue-536/w6-evidence-payload-measurement.json` (override with
`HVO_ISSUE536_EVIDENCE_ROOT`).

The measured run is partly degraded and its byte figures are therefore not
upper bounds. The harness environment provisions no calibration reference
library, so the capture lane reports `calibration.library.missing`; the durable
execution still reaches `Completed` with all fourteen node plans recorded, but
only one of the seventeen declared outputs and one of the thirty-two permitted
attempts per node are exercised. The evidence file records the lane outcome
alongside every measurement. The *cardinality* claim is sound because the
declared bounds come from the frozen plan rather than the run; the *byte*
figures are a representative lower bound: the 9,347-byte execution envelope sits
about 224 times below its 2 MiB cap, and the 64,580-byte revision envelope about
130 times below the 8 MiB absolute cap. Neither is a proof of the worst case.

Reproduce with:

```bash
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release \
  --filter "FullyQualifiedName~GraphExecutionEvidencePayloadMeasurementTests"
```

## CameraAgent exporter

Issue #537 adds the CameraAgent half: a durable source outbox that seals evidence
once and retains it until a terminal acknowledgement or an explicit operator
disposition. It is entirely separate from the fleet heartbeat and from the
manifest-v2 artifact upload lane, and an acknowledgement here never means a
receiver holds artifact bytes.

### Durable store

The outbox lives in its own SQLite WAL database at
`<RawIngressRoot>/evidence/execution-evidence-outbox.db`, not inside
`raw-ingress.db`. That is deliberate: the installer campaign's rollback contract
lets an operator return to a baseline image, and a forward-only schema change to
a store the baseline opens would break that rollback. A separate file is simply
never opened by a baseline image, and no schema this milestone already shipped
changes shape. `SqliteExecutionEvidenceOutbox` pins schema version 1, compares
the whole `sqlite_master` definition against a canonical in-memory build on every
open, runs `PRAGMA integrity_check`, and refuses a drifted store rather than
migrating it. Pragmas are `journal_mode=WAL`, `synchronous=FULL`,
`foreign_keys=ON`, and a configured `busy_timeout`; every mutation runs in one
`BEGIN IMMEDIATE` transaction.

| Table | Holds |
| --- | --- |
| `execution_evidence_schema` | The pinned schema version. |
| `execution_evidence_state` | The sweep cursor, the deferred key, and the source-pruned counter. |
| `execution_evidence_rejections` | The newest executions this contract version could not express, keyed by execution and bounded; the loss count itself is a monotonic counter in `execution_evidence_state`. |
| `execution_evidence_origins` | One row per boot session, with its own `next_sequence`. |
| `execution_evidence_units` | Sealed canonical envelope bytes, payload hash, status, attempts. |
| `execution_evidence_audit` | Quarantine and operator disposition history; retained past the units it describes. |
| `execution_evidence_operations` | Operation-key receipts, so a repeated request is a detected duplicate. |
| `execution_evidence_conflicts` | Bounded receiver sequence conflicts with both hashes. |

### Origin, sequencing, and restart

`origin.identitySha256` is the canonical hash over every origin member including
`bootSessionId`, so each boot session is its own origin with its own sequence
space starting at one. A restart therefore never renumbers or rewrites evidence
already sealed: earlier origins keep their rows and drain alongside the new one,
and the exporter re-exports the active revision under the new origin so a
receiver can still resolve the plan identity from a lower sequence. Origin rows
are removed only when they hold no unit and are not the live boot session.

`origin_sequence` is allocated inside the enlistment transaction from the
origin's `next_sequence`, in the order units are offered, so a graph revision
always receives a lower sequence than the executions that depend on it.
`payload_sha256` stores the contract's canonical envelope hash — the value the
receiver acknowledges — not a hash of the transport bytes.

### Discovery

A bounded forward sweep reads terminal executions from the delivered durable
store ordered by `(accepted_unix_ms, execution_id)` from a durable cursor. That
ordering is used because it is immutable and because it is the only one the
delivered `(execution_class, status, accepted_unix_ms, execution_id)` index can
serve: an ordered scan over a `COALESCE` of the completion time would scan the
whole `processing_executions` table and walk the two multi-megabyte document
blobs that precede the ordering columns in every row, which is exactly the page
pressure this lane must not put on the capture path. The sweep is therefore one
index-only range scan per `(execution class, status)` pair, merged and bounded.

Acceptance time is not completion time, so a forward cursor could pass an
execution that is still running and miss it when it later becomes terminal. The
sweep therefore reads one more indexed value first: the oldest acceptance time
that has *not* reached a terminal status. That is the barrier. Everything
strictly below it is already terminal, so the cursor may advance to the barrier
and no further, and the sweep never has to re-read anything it has passed. When
nothing is active the barrier lifts and the sweep drains to the end.

The evidence identity of every unit is derived from the origin identity and the
unit key, and a revision's and an execution's produced-at time is the execution's
own completion time (its acceptance time when it never completed), so those two
bodies are reproducible from immutable facts. An availability body is by
definition an observation and carries the time it was made. A unit key already
present with *different* bytes is a durable conflict, recorded for an operator in
a bounded table, and the stored unit stays authoritative.

The cursor advances only past executions the exporter actually sealed, rejected,
or explicitly deferred. When a bound refuses enlistment, the oldest refused
ordering key is persisted as the deferred key and only ever lowered; if source
retention later removes everything at or below it, the exporter records a
bounded `export.source-pruned` event and reports an explicit degraded state
instead of skipping silently. An execution this contract version cannot express —
a durable value with no mapping, or a sealed unit above the configured byte
bound — is counted once, sampled in `execution_evidence_rejections`, and passed
by the cursor, and the lane reports `export.projection-rejected` for as long as
the count is non-zero. It is never allowed to fault the host or wedge the
sweep.

With no configured sink, or after a negotiation that found no shared schema
version, the exporter performs no sweep at all, so a standalone deployment never
accumulates evidence it has nowhere to send and stays healthy indefinitely.

### Sending, retry, conflict, and resynchronization

Version negotiation runs before anything is sent, and the producer applies the
minimum of the published and its own local value for every negotiated limit. An
`Unsupported` disposition stops sending for the process rather than retrying.

Each submission carries one origin's units in ascending sequence order, framed as
newline-delimited canonical JSON under
`application/x-hvo-execution-evidence-v1`; the canonical serializer never emits a
raw newline, so framing preserves every envelope byte for byte and therefore its
hash. Exactly one request is in flight at a time, which both bounds concurrency
and preserves per-origin order.

Feedback is applied per unit: `Acknowledged` settles it against its exact stored
hash, `Rejected` quarantines it with the receiver's reason, a
`evidence.sequence-conflict` additionally records both hashes, and any unit
without a terminal fact is deferred and offered again. Reported missing ranges
produce a bounded `ExecutionEvidenceResyncRequestV1` that is replayed on the next
cycle. A unit that spends its bounded attempt budget is quarantined rather than
retried forever; it stays durable and operator-visible.

### Limits, pressure, and operator surface

`CameraAgent:ExecutionEvidenceExport` bounds the poll interval, discovery batch,
batches per cycle, request units and bytes, request timeout, retry delays and
attempts, pending units, pending bytes, storage bytes, unit
bytes, pending age, acknowledgement retention, and retained acknowledgements. Every bound refuses new
enlistment or defers a send; none of them discards evidence that is already
durable, and none can apply back pressure to acquisition, raw ingress, live
execution, publication, artifact upload, or replay. Storage pressure on the
raw-ingress root pauses enlistment with an explicit `export.storage-pressure`
state.

The `execution-evidence-export-state` operations-summary section publishes a
bounded sanitized snapshot, and
`/api/v1/operations/outboxes/execution-evidence` exposes owner-only paging,
detail, audit, replay, and abandon in the delivered outbox style. Metrics under
`hvo.cameraagent.evidence_export.*` are tagged only by bounded operation and
outcome values and expose depth, bytes, oldest age, attempts, throughput,
rejects, conflicts, drain, resynchronization, in-flight requests, and durable
size.

## Redaction

`ExecutionEvidenceRedactionPolicyV1` travels with every envelope and says which
operator-identifying members were replaced. `OperatorIdentity` replaces
`execution.triggerReference` and every `attempts[].leaseOwner` with
`redacted:<uppercase SHA-256>`. Redaction changes the canonical bytes, so a
redacted unit and its unredacted original are different evidence units;
re-applying the same policy is idempotent.

## Reason codes

`evidence.invalid-json`, `evidence.unsupported-schema`,
`evidence.payload-too-large`, `evidence.limit-exceeded`,
`evidence.invalid-identity`, `evidence.invalid-origin`,
`evidence.invalid-sequence`, `evidence.invalid-hash`, `evidence.invalid-body`,
`evidence.invalid-time`, `evidence.invalid-node`, `evidence.invalid-attempt`,
`evidence.invalid-input`, `evidence.invalid-output`,
`evidence.invalid-availability`, `evidence.invalid-correction`,
`evidence.invalid-assignment`, `evidence.invalid-redaction`,
`evidence.invalid-fact`, `evidence.invalid-range`,
`evidence.invalid-retention`, `evidence.invalid-negotiation`,
`evidence.sequence-conflict`, `evidence.unknown-predecessor`,
`evidence.unknown-revision`, `evidence.sequence-gap`.

## Golden fixtures

`tests/fixtures/processing/cameraagent-execution-evidence-*.json` hold the
canonical bytes (each file ends with one trailing newline that tests strip):

| Fixture | Covers |
| --- | --- |
| `revision-local-v1` | Local-only graph revision. |
| `revision-assigned-v1` | Centrally assigned revision with #426 provenance. |
| `execution-live-v1` | Live execution; a required node that produced two outputs after one retryable attempt, and an optional node that ends in a terminal failure while the execution still completes. |
| `execution-replay-v1` | Replay execution; cancellation, a required node whose interrupted attempt is followed by a terminal one, a skipped optional node, and optional artifact media type and payload length. |
| `correction-v1` | Append-only correction of the live execution. |
| `availability-v1` | Available, missing, and quarantined artifacts. |
| `feedback-v1` | Every fact kind, a duplicate, a conflict, gaps, retention. |
| `resync-v1` | Bounded resynchronization request derived from the feedback. |
| `negotiation-v1` | Version negotiation response with the published limits. |
| `unknown-future-v1` | Unknown future version, rejected whole. |

`HVO.SkyMonitor.Processing.Tests/GraphExecutionEvidenceContractTests` produce
and verify these bytes; `HVO.SkyMonitor.LogicHost.Tests/GraphExecutionEvidenceReceiverConformanceTests`
consume the same files from the central host with no CameraAgent reference. `HVO.SkyMonitor.CameraAgent.Tests/…/ProcessingGraphEvidenceProjectionTests`
additionally proves that the read-only projection reproduces
`execution-live-v1` byte for byte, so a projection field-mapping regression
breaks a golden rather than passing silently.

To change a fixture, change the deterministic builder in
`GraphExecutionEvidenceFixtures`, regenerate the file with the same canonical
serializer, and update that fixture's pinned SHA-256 in the
`FixtureSha256` table in `GraphExecutionEvidenceContractTests`. The golden tests
fail on any byte difference, and the pinned table means regenerating a fixture
always requires a visible source edit.
