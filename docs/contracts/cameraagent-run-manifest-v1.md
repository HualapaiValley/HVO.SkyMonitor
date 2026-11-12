# CameraAgent run manifest v1

`GET /api/v1/operations/processing-graphs/executions/{executionId}/manifest`
requires `OperationsReadV1`, like the run detail. A successful response is an
`application/json` attachment named `pipeline-run-{executionId}-manifest-v1.json`,
with `schemaVersion: hvo-cameraagent-run-manifest-v1`, `Cache-Control: no-store`
and `X-Content-Type-Options: nosniff`.

The manifest contains the retained execution and capture IDs, graph revision and
frozen graph/shared/local plan hashes, run status and timestamps, node plan hashes,
node attempts and typed outcomes/routes, input artifact and capture IDs, payload
and available descriptor hashes, producing output identities, and output artifact
IDs, payload hashes and source artifact IDs. Output source IDs and input bindings
provide lineage, including rolling inputs from other captures. A processing input
uses its producer's output identity; its descriptor hash is null when not recorded.
Current dependency inputs are resolved from this run's immutable output source IDs;
historical window positions come from retained pins. Cross-capture auxiliary sources,
including a provisioned cloud clear reference, are resolved by immutable artifact ID
against retained raw/processing metadata. They use `AuxiliaryRawCapture` or
`AuxiliaryProcessingOutput`, the real capture/artifact/hash identities and a null
window position; they are not invented rolling-window members. Calibration master entries
come from the frozen normalization plan, with kind `CalibrationMaster`, payload
hashes and null capture/window identities rather than an invented single capture.
Ordinary reference-calibration masters are resolved by the produced output's source
IDs against retained immutable library metadata, in the same snapshot; today's
active library selection is never substituted. This scalar projection is bounded
by the source/row limits and the schema's fixed kinds and 64-character hashes.
No later replay's mutable
node cache supplies input bindings. These are journal facts at download time;
an active run may acquire later attempts.
They never refer to the currently active graph definition.

The explicit field list excludes free-text reasons, trigger references, lease
owners, configuration, variants, storage paths, credentials and diagnostics.
Artifact payloads are never opened. Retained checksum and lineage evidence remains
exportable after the corresponding payload becomes unavailable. When retention
removes artifact metadata or historical input bindings, export is refused with
a specific reason rather than returning partial evidence. Output association
ordinals retain their original values after partial deletion. Declared output contracts
are upper bounds: conditional outputs that were never produced do not count as
missing evidence. Every completed node attempt, including a retryable or terminal
failure after recording a `Produced` outcome, records the cardinality of the node's
committed output set (zero when nothing has committed) in the independent
execution manifest ledger, atomically with its output associations. Original plan
members and frozen normalization-plan values retain their bytes. Counts survive
output retention, so partial or complete metadata deletion still refuses export.
Processing schema 9 stores these counts in a separate bounded table; the image
state boundary selects the existing paired upgrade checkpoint. Older runs without
recorded counts remain available when a full declared output set (including a run ending in failure) or an outputless
node with no attempts or only uncommitted running/interrupted/skipped attempts proves completeness.
Terminal sweeps can mark an unstarted or interrupted node failed without committing any outputs;
such Live failure/expiry and Replay failure/expiry/cancel paths remain downloadable. A latest Skipped
or failed state alone cannot prove zero outputs after earlier retries; unprovable retry/skip/failure
history is refused rather than exporting a partial manifest. An older ambiguous run
receives the fixed reason that its original produced-output evidence was not recorded completely, rather
than claiming that it was deleted. The primary plan retains its full 2 MiB capacity independently of optional
count evidence. Each input/output ledger admits at most 64 node counts.
Exceeding that independent capacity refuses the manifest with the size-limit
reason without stopping processing or concealing ordinary detail. New completions record conditional and zero-output
counts without consulting mutable current-capture state. A successful retry updates
the zero-output attempt's count atomically with its committed output associations.
The largest committed ordinal set is preserved across attempts: a later outputless
failure cannot hide earlier retained outputs, and deletion of those outputs is
still detected.
Refusal for older runs applies only when their output-history completeness cannot
be proven. It does not permit other manifest failures or an unreadable ordinary journal.

Historical processing-input cardinalities are initialized to zero on new Live/Replay admission
and updated atomically when window pins are frozen. This evidence lives in the separate execution manifest table independently of
cascading input-pin deletion. Even a node that produces
no outputs must retain every frozen input; a missing historical row refuses the download with
the missing-artifact reason before and after restart. An older outputless window without this
proof remains downloadable when the retained pins fill its frozen maximum historical window with
unique artifact identities and contiguous valid positions. A Live node must have at most one
attempt; Replay uses the single window frozen at admission across attempts. A shorter surviving
window or an ambiguous Live retry history refuses with the original frozen-input evidence
incomplete reason. Current dependency
inputs still use immutable output lineage; no schema or revision identity changes.

Each optional count ledger is read and updated independently. Corruption in one
ledger, including deep nesting, does not prevent the other from recording actual
committed outputs or frozen historical inputs. The corrupt member still disables
export with the corrupt-evidence reason; removing it recovers the download when
the remaining retained evidence proves completeness. Genuine input/output deletion
still refuses export after recovery and restart.

Optional count metadata is excluded before primary plan parsing, including when it
is deeply nested. Primary normalization-plan depth, shape and identity validation
remain unchanged. Corrupt optional counts, including malformed Unicode keys, do not
roll back input resolution or node completion. If export bookkeeping cannot parse the frozen evidence, completion
preserves those bytes and records the completed attempt; the manifest still refuses
corrupt evidence with its specific reason. Primary evidence readers keep their
existing validation. With intact primary evidence, the ordinary execution API,
run journal, replay detail and central evidence export remain readable; the run
page disables the download with that reason.
Removing the corrupt member restores the legacy completeness rules above.
Historical pinned-output descriptors are inspected for checksums only in the bounded
manifest snapshot. Ordinary detail and central evidence preserve the pre-existing
null payload hash for those pins; corruption in a historical descriptor disables
only the consuming run's manifest, without hiding its journal. Auxiliary processing-output
descriptors use the same JSON-validity guard and corrupt-evidence refusal before
checksum extraction; restoring the descriptor recovers the download. Manifest-only
completeness fields are omitted from ordinary detail JSON.

The database read holds one SQLite WAL snapshot. It allows at most 64 nodes and
4096 rows in each attempts/inputs/output-input-pins/outputs/source-reference collection,
and at most
2 MiB total serialized plan, node-contract and referenced output descriptor data
before deserialization. The encoded download is separately capped at 2 MiB.
Oversized evidence is rejected as a whole, without truncation or partial JSON.
Unknown executions return 404; missing node evidence, incomplete artifact hashes
or excessive evidence return 409 with a fixed reason. Other read failures return
a fixed 500 response. Corrupt frozen plans instead return 409 with a fixed corrupt-evidence
reason, distinct from the row/byte-limit reason. Cancellation propagates. The run page enables the download
only after the same bounded projection succeeds, otherwise displaying its reason.
Valid node identifiers with spaces, Unicode, `/` or `\` retain their identities
as JSON strings. They are never used as manifest paths or attachment names.
Empty, overlong, untrimmed or control-containing identifiers receive a sanitized refusal reason. The empty prototype More run actions button is removed; existing capture,
revision and replay links remain available directly on the page. #1715

## Independent count ledgers (Refs #1766)

Processing schema 9 adds `processing_execution_manifest_counts`. It stores one
scalar count per execution, ledger kind and node, plus an independent status row
for each input/output ledger. Each kind has at most 64 node entries; input counts
are bounded to 512 and output counts to 128. Count writes share the original
lease-checked evidence transaction and never update `frozen_plan_json`.
Repeated counts avoid writes, and output counts preserve the largest committed
set across retries. Rows survive pin/output retention and cascade only when the
execution itself is removed. Ordinary detail never depends on these optional rows.

The startup migration accepts canonical processing schemas 5 through 8. Schema 8
counts are validated independently, imported, and removed from the frozen plan
in the same transaction as the schema marker. Existing primary member bytes,
normalization plans, hashes, attempts and associations survive. Missing legacy
counts remain unknown rather than becoming invented zeroes. Corrupt or excessive
legacy ledgers retain explicit refusal states while ordinary processing continues.
Migration cancellation or interruption leaves the previous layout available for retry.

The raw journal remains schema 15. The CameraAgent image advances its
`minimum-compatible-revision` boundary to the schema-9 implementation, selecting
the installer's existing paired checkpoint before startup. Older readers require that checkpoint during rollback; frozen previous-release
SQL images remain unchanged. The manifest's existing 2 MiB aggregate snapshot and
encoded-download limits still apply independently of the ledger capacity.
