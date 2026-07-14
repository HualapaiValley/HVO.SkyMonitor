# CameraAgent Retention and Outbox Recovery

CameraAgent stores capture payloads beneath each configured storage root and
queues upload manifests in that root's `outbox/` directory. Retention treats a
pending manifest as a durable hold on its payload, companion metadata, and
daily index entry.

## Safety Invariant

After every successful retention sweep, every pending outbox manifest still
references an existing payload beneath the same storage root. Expiration does
not override this hold. Once central ingestion acknowledges the manifest and
the outbox removes it, the next eligible sweep may delete a derivative. Raw
ingress evidence remains held by `raw-ingress.db` until durable
required-consumer acknowledgements are added.

Retention fails closed for a storage root before deleting anything when:

- an outbox manifest is malformed;
- a pending path is absolute or escapes the configured root;
- a pending payload is missing;
- cancellation is requested while pending manifests are scanned.

The completion log records the storage root, deleted file count, and number of
protected pending artifacts. A repeated `Retention sweep failed` event requires
operator investigation; do not manually delete outbox manifests until central
ingestion or an explicit abandonment procedure accounts for the artifact.

## Disk Pressure

Each retention sweep measures available capacity for every configured storage
root. Pressure begins below `DiskPressureThresholdPercent` (default 10%) and
remains active until capacity reaches `DiskPressureRecoveryPercent` (default
15%). While active, eligible history uses the smaller of its configured
retention and `DiskPressureRetentionDays` (default one day).

The current UTC day and every pending outbox payload, sidecar, and index entry
remain protected. Probe or outbox validation failures delete nothing for that
root. `/health` reports pressure as degraded and capacity-probe failure as
unhealthy; transition logs record entry and recovery. This policy does not pause
capture or discard accepted frames.

## Outage Recovery

1. Restore LogicHost or network connectivity.
2. Confirm pending manifests remain under `<storage-root>/outbox/`.
3. Confirm each manifest's `relativeArtifactPath` exists beneath the same root.
4. Allow the normal outbox drain to upload and acknowledge artifacts.
5. Confirm acknowledged manifest files disappear.
6. Allow the next retention sweep to remove artifacts older than policy.

Outbox manifests and payloads survive CameraAgent restart. Retention scans the
filesystem-backed outbox on every sweep and does not depend on in-memory state.

## Restart Browsing

The daily `<storage-root>/index/frames_yyyy-MM-dd.jsonl` append is the browsing
visibility commit point. A fresh CameraAgent storage service reconstructs a
date/role listing from that index and requires the matching payload and JSON
sidecar to exist and agree on artifact identity, role, timestamp, dimensions,
and pixel format. Malformed, duplicate, or incomplete entries are skipped and
logged rather than exposed as valid artifacts or blocking the rest of the day.
New callers that supply a complete `ReconstructionDescriptor` store an
`ArtifactManifestV2` sidecar containing the same validated descriptor used for
transport. Existing callers continue to write the unversioned legacy sidecar,
and browsing reads both forms without rewriting or inventing missing legacy
facts. A malformed or unsupported versioned sidecar is never downgraded to the
legacy parser.
Before publishing a v2 sidecar, storage verifies every overlapping frame and
artifact fact, writes the payload, then streams SHA-256 from the published file.
An ordinary v2 save failure makes a best-effort attempt to remove payload or
sidecar files published by that invocation without hiding the original error.
Browsing also requires the sidecar path and byte length to match the selected
payload; checksum verification remains mandatory when bytes are reconstructed
or transferred rather than forcing a full-frame scan for every gallery listing.
Raw ingress reconciles residual files and repairs this projection at startup;
the JSONL index remains compatibility browsing state rather than durable truth.
Before appending, the writer terminates any torn final line so a later valid
commit remains independently browseable. Listings also inspect adjacent daily
indexes to retain discovery of pre-hardening entries written with non-UTC offsets.

Listings use persisted UTC capture time, then artifact ID as a stable tie-break,
and apply the caller's result limit. Files committed before an interrupted index
append remain hidden until a future reconciliation operation; do not manually
add index lines without validating the sidecar and payload pair.

During graceful host shutdown, acquisition cancellation happens first. The
processing channel then completes and drains every accepted frame before the
camera module is disposed. A `Capture processing channel drained` event confirms
that completion-driven shutdown path finished. If the host shutdown deadline
expires, the drain is canceled, the module is still disposed, and a `Capture
processing channel drain aborted` warning records that accepted work may remain
unfinished.

Capture failures use deterministic exponential retry delays configured by the
rig pipeline (`captureFailureInitialDelay`, default 250 ms, and
`captureFailureMaximumDelay`, default 30 seconds). A successful capture resets the sequence; shutdown
cancellation interrupts either capture or backoff immediately. Structured logs
record the failure count/delay and the subsequent recovery transition.

## Upload Drain

The upload drain runs outside the acquisition pipeline and processes at most
`CameraAgent:UploadBatchSize` manifests per poll. Failed uploads remain in the
filesystem outbox and retry with exponential delays between
`UploadRetryInitialDelaySeconds` and `UploadRetryMaximumDelaySeconds`. Restarting
the agent preserves every manifest and may retry it immediately; LogicHost
idempotency prevents a duplicate central record.

Set `CameraAgent:UploadBandwidthLimitBytesPerSecond` to a positive value to
limit streamed payload reads, or leave it at `0` for no application-level
limit. LogicHost acknowledges only after payload checksum verification, MinIO
storage, and SQL metadata persistence. That response removes the outbox manifest
and a derivative's local artifact. It does not remove an ingress-owned raw
payload or manifest-v2 sidecar while the SQLite retention hold remains active.

## Raw Ingress Recovery

`CameraAgent:RawIngressRoot` is mandatory and must identify persistent local
storage. The authoritative journal is
`<raw-ingress-root>/journal/raw-ingress.db`; do not edit it or delete its WAL/SHM
files while CameraAgent is running. `/health` reports `raw-ingress` as healthy
only after schema verification, integrity checking, reconciliation, and a
passive checkpoint complete.

On restart, complete valid manifest-v2 pairs are recovered exactly once, stale
temporary files are recorded and removed, compatibility indexes are repaired,
and malformed or conflicting evidence moves beneath `quarantine/`. A committed
row with missing evidence makes ingress unhealthy and requires restoring the
exact payload/sidecar or an explicit operator disposition. Never clear the
journal merely to make health green: committed rows are retention holds and the
only durable record of unfinished raw work.

Disk exhaustion, failed capacity probes, inaccessible storage, SQLite lock
timeout, integrity failure, and unsupported newer schemas stop further capture
acceptance. Restore capacity or access, preserve all evidence, then restart the
agent and confirm `raw-ingress` health, pending count/bytes, oldest age, and
quarantine totals before resuming normal operation.

## Soak Validation

The normal test suite runs a reduced-resolution VirtualSky day from 289
five-minute captures and verifies exact raw/derivative counts, committed
payload/metadata pairs, bounded scene caching, and no temporary files. Channel
tests separately prove capacity blocking and accepted/dequeued equality.

An actual overnight or 24-hour run is optional and never gates pull requests.
Dispatch `CameraAgent Real-Duration Soak` on a runner labeled
`self-hosted`, `linux`, and `skymonitor-soak`, or run
`./scripts/run:cameraagent-soak 24h`. The workflow retains compact logs and a
JSON inventory; it does not upload the complete image tree.
