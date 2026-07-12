# CameraAgent Retention and Outbox Recovery

CameraAgent stores capture payloads beneath each configured storage root and
queues upload manifests in that root's `outbox/` directory. Retention treats a
pending manifest as a durable hold on its payload, companion metadata, and
daily index entry.

## Safety Invariant

After every successful retention sweep, every pending outbox manifest still
references an existing payload beneath the same storage root. Expiration does
not override this hold. Once central ingestion acknowledges the manifest and
the outbox removes it, the next eligible sweep may delete the artifact.

Retention fails closed for a storage root before deleting anything when:

- an outbox manifest is malformed;
- a pending path is absolute or escapes the configured root;
- a pending payload is missing;
- cancellation is requested while pending manifests are scanned.

The completion log records the storage root, deleted file count, and number of
protected pending artifacts. A repeated `Retention sweep failed` event requires
operator investigation; do not manually delete outbox manifests until central
ingestion or an explicit abandonment procedure accounts for the artifact.

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
