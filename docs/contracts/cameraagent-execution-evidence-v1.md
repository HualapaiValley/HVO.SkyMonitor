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
  validation already rejects host-incompatible nodes and secrets in a
  definition; keeping an operator secret out of a node's options remains an
  operator responsibility on both hosts.
- Local correctness never depends on export. Nothing in this contract is read
  during acquisition, raw ingress, live processing, publication, or replay.
- `AgentCore` is unchanged. The only CameraAgent addition is a read-only
  projection (`ProcessingGraphEvidenceProjection`) over the delivered durable
  execution store plus one read-only accessor for a persisted revision snapshot;
  no durable schema, execution, or persistence behaviour was modified.

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
evidence. On the wire, a payload whose root `schemaVersion` is unknown — such
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
| Inputs per execution | 2,048 | 3 observed, 49 declared upper bound |
| Outputs per execution | 1,024 | 1 observed, 17 declared upper bound |
| Availability observations | 1,024 | 1 |
| Facts per feedback | 256 | |
| Missing ranges / resync ranges | 64 / 64 | |
| Resync units | 256 | |

Every value above travels in the negotiation response, so a producer learns them
before it sends anything and applies the minimum of the published and its own
local value. The following caps are enforced by validation but are not
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
(`raw-ingress.db`, its WAL and its shared-memory file) is unchanged across the
projection — the projection is read-only by measurement, not only by
inspection. Retained evidence is written to
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
figures are a representative lower bound with roughly a thirty-fold margin to
the execution envelope cap, not a proof of the worst case.

Reproduce with:

```bash
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release \
  --filter "FullyQualifiedName~GraphExecutionEvidencePayloadMeasurementTests"
```

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
| `execution-live-v1` | Live execution, required node with a retry, optional node skipped. |
| `execution-replay-v1` | Replay execution, cancellation, interrupted then terminal attempt. |
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
