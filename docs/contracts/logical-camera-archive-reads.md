# Logical-camera archive and current selection

Issue #1236 supplies `ILogicalCameraArchiveReadService` for #1239. It reads existing
normalized SQL records and uses existing credential, retrieval and object-reader
services. It adds no persisted policy, schema, importer or UI.

## Protected scope and routes

All routes begin `/api/v1.0/observatories/{observatoryId}/cameras/{cameraId}` and
require the existing `ArtifactRetrieval` policy. The service independently checks
one canonical owner credential, its observatory restriction, current membership,
and the camera/observatory relationship. Viewer, Manager and Owner can read.
Global role or membership in another observatory grants no access to this pair.
Membership is rechecked after reads/object verification. Responses are `no-store`.

- `GET captures`: archive page; inaccessible scope returns an empty page.
- `GET current`: bounded selection; inaccessible scope returns 404.
- `GET captures/{captureId}`: exact detail; inaccessible/mismatched capture returns
  404. Existing metadata, reasons, provenance and bounded artifact/profile
  projections remain available even when content is unavailable. This call never
  selects or retrieves another capture's image.

Invalid endpoint credentials retain the existing policy response. Invalid page
sizes, supported-filter values or continuations return 400 without echoing them.
The service returns empty/null for denied resources and throws `ArgumentException`
for invalid requests. Artifact bytes remain behind the existing exact protected
content endpoint and its availability/authorization rules.

## Archive semantics

`take` defaults to 25 and must be 1..50. SQL applies every filter before `Take`:

- `FromUtc` inclusive and `UntilUtc` exclusive compare **capture time**. Values
  normalize to UTC; a supplied interval must have positive width.
- `Product` is an existing `FrameArtifactRole` value.
- `ObjectState` and `ReconstructionState` are the existing persisted enums. When
  combined with product, all predicates must match the **same artifact**. These
  are recorded state, not a claim that physical bytes were checked by the list.
- `Text` is an exact central capture UUID, source frame UUID, or non-negative
  capture sequence. UUID spelling and leading sequence zeros normalize. This is
  identity lookup, not substring/full-history search.

Unsupported free text, labels, weather/cloud/quality classifications, imported
execution status, prototype presentation policy and arbitrary product heuristics
are deferred. Unknown enum values and unsupported text are rejected. There is no
fabricated total or history-draining operation. List entries contain scalar capture
and retained installation identities, capture/receipt time and sequence, with no
child collection or image transfer.

Ordering is descending `(CapturedAtUtc, central capture Id)` using SQL Server's
UUID ordering. Retired/replaced installations remain members through the immutable
capture installation relationship; current registration does not rewrite history.
The existing installation/capture-time/identity index is used. Selective predicates
can require examining retained SQL index rows; `Take` bounds materialized rows,
not every physical page the database may read. The measurement harness reports
both actual rows read and logical reads, including rare/no-match filters.

Continuations are authenticated encrypted Data Protection payloads, bound to
actor, observatory, camera, operation and normalized filters. Changing any of those
requires restarting. Page size may change. Tokens expose no plain capture IDs.
Tampered/oversized tokens are rejected; lost protection keys invalidate old tokens.
Keyset paging is a live view, not a durable snapshot: refresh from the beginning
for newly arriving/reconciled records before the current boundary.

## Current semantics and limits

One request examines at most five captures, with one extra scalar lookahead to
report continuation. It does not automatically drain history. Within each capture,
the selected presentation candidate is the latest SQL-eligible Preview by
`(ReceivedAtUtc, ArtifactId)` descending. Capture ordering never uses receipt,
heartbeat, processing completion or publication time. The candidate must be a
complete, available retained JPEG/PNG Preview of 1..32 MiB. Annotation, manifest
and public release are optional. Raw, annotated-only, structured products and
non-browser encoded preview formats remain archive metadata; this service does
not add a compositor or decoder. It does not try older variants of the same
capture when the selected candidate's bytes fail verification.

The existing artifact retrieval authority and object reader verify exact length,
SHA-256 and current storage generation. At most five payloads (160 MiB) are read,
using the reader's streaming buffers. Missing/integrity-failed candidates may be
skipped in favor of an older capture, whose different capture and artifact identity
are returned explicitly. No cached selected image is retained. Selection performs
no repair write; the existing retrieval/reconciliation paths retain that authority.
A storage outage stops with `dependency-unavailable`, never a successful empty
archive or substitute image. Current persisted availability and membership are
rechecked before returning a selection.

Results are `found`, `continue`, `no-result` or `dependency-unavailable`, with the
number of captures examined. `continue` includes the opaque next boundary and
means only that this window found no eligible presentation. `no-result` means the
window exhausted the history visible to this request. An outage retains the input
continuation for an explicit retry. `found` returns exact capture/Preview identity,
checksum and protected content path. It is a point-in-time observation, not a
content lease: subsequent content reads must reauthorize and can fail if membership,
retention, storage generation or bytes change. Consumers must clear failed images
and preserve exact metadata rather than silently choosing another image.

## Reproducible validation

Run `LogicalCameraArchiveIntegrationTests` in the LogicHost integration project
against the normal SQL Server/Testcontainers/filesystem fixture. It covers filter
ordering, interval/identity semantics, page bounds, scope/filter/actor/purpose
substitution, same-time IDs, replacement history, credential restriction,
Viewer/Manager/Owner, B-only/both/removed/unauthenticated users, exact metadata,
late/missing bytes, state eligibility, outage and revocation during verification.
Existing retrieval/presentation suites retain image, raw-grant, checksum, layer,
lineage and authorization boundary ownership.

The Manual `LogicalCameraArchivePerformanceTests` uses W3M: 1,000/10,000 captures,
two installations, two expired previews and one physically retained Preview;
concurrency 1, five warmups and thirty measured service/serialization requests per
scenario. It records exact dataset identities, source revision, SQL commands,
actual-plan rows/indexes/logical reads, response bytes, median/p95/maximum latency,
CPU, process allocation and working set. Baseline is the existing unfiltered
`NetworkOperationsReadService`; filtered/current routes are new absolute baselines.
Allocation/RSS include fixture background work and do not imply isolated browser
memory. There is no browser/render change in this backend prerequisite.

Set `HVO_ISSUE1236_EVIDENCE` to an ignored absolute JSON output path and
`HVO_ISSUE1236_REVISION` to the tested source commit/fingerprint, then run:

```bash
dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj \
  --configuration Release --no-build \
  --filter FullyQualifiedName~LogicalCameraArchivePerformanceTests
```

Runtime evidence uses existing artifact authorization log event 2100 and object
verification/read telemetry. SQL selection has no worker, queue, retry loop,
mutation, new health check or metric label. Backlog, rendering and browser memory
are N/A for this service; these belong to the consuming UI or existing workers.
