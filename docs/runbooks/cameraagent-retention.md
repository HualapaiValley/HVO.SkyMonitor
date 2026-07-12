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
