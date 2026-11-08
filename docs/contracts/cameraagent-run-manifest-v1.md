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
These are journal facts at download time; an active run may acquire later attempts.
They never refer to the currently active graph definition.

The explicit field list excludes free-text reasons, trigger references, lease
owners, configuration, variants, storage paths, credentials and diagnostics.
Artifact payloads are never opened. Retained checksum and lineage evidence remains
exportable after the corresponding payload becomes unavailable.

The database read holds one SQLite WAL snapshot. It allows at most 64 nodes and
4096 rows in each attempts/inputs/output-input-pins/outputs collection, and at most
2 MiB total serialized plan, node-contract and referenced output descriptor data
before deserialization. The encoded download is separately capped at 2 MiB.
Oversized evidence is rejected as a whole, without truncation or partial JSON.
Unknown executions return 404; missing node evidence, incomplete artifact hashes
or excessive evidence return 409 with a fixed reason. Other read failures return
a fixed 500 response. Cancellation propagates. The run page enables the download
only after the same bounded projection succeeds, otherwise displaying its reason.
The empty prototype More run actions button is removed; existing capture,
revision and replay links remain available directly on the page. #1715
