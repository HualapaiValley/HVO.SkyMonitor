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
committed output set (zero when nothing has committed) in the existing
execution evidence JSON, atomically with its output associations. Original plan
members and frozen normalization-plan values retain their bytes. Counts survive
output retention, so partial or complete metadata deletion still refuses export.
There is no SQLite schema, installer or image-boundary change. Older runs without
recorded counts remain available when a full declared output set or an outputless
node whose retained attempts are all skipped proves completeness. A latest Skipped
state alone cannot prove zero outputs after earlier retries; unprovable retry/skip
history is refused rather than exporting a partial manifest. An older ambiguous run receives the fixed reason
that its original produced-output evidence was not recorded completely, rather
than claiming that it was deleted. If the existing frozen-plan byte limit leaves
no room to record a count, processing continues and export applies the same
fail-closed completeness rule. New completions record conditional and zero-output
counts without consulting mutable current-capture state. A successful retry updates
the zero-output attempt's count atomically with its committed output associations.
The largest committed ordinal set is preserved across attempts: a later outputless
failure cannot hide earlier retained outputs, and deletion of those outputs is
still detected.
Refusal for older runs applies only when their output-history completeness cannot
be proven. It does not permit other manifest failures or an unreadable ordinary journal.

Optional count metadata is excluded before primary plan parsing, including when it
is deeply nested. Primary normalization-plan depth, shape and identity validation
remain unchanged. Corrupt optional counts, including malformed Unicode keys, do not roll back node
completion. If export bookkeeping cannot parse the frozen evidence, completion
preserves those bytes and records the completed attempt; the manifest still refuses
corrupt evidence with its specific reason. Primary evidence readers keep their
existing validation. The ordinary execution API, run journal, replay detail and central evidence
export remain readable; the run page disables the download with that reason.
Removing the corrupt member restores the legacy completeness rules above.
Historical pinned-output descriptors are inspected for checksums only in the bounded
manifest snapshot. Ordinary detail and central evidence preserve the pre-existing
null payload hash for those pins; corruption in a historical descriptor disables
only the consuming run's manifest, without hiding its journal. Manifest-only
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
Valid node identifiers with spaces or Unicode retain their identities; path-like
identifiers have a separate sanitized refusal reason. The empty prototype More run actions button is removed; existing capture,
revision and replay links remain available directly on the page. #1715
