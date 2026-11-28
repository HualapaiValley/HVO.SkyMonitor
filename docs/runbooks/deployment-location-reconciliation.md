# Local Deployment Location

CameraAgent owns the protected deployment snapshot used for capture. The active
snapshot binds each capture to the observer coordinates and time zone in use.

## Manual Local Coordinates

An owner can enter observer coordinates locally in the Operations **Sky map &
catalog** section, or through `POST /api/v1/operations/deployment-location/manual`.
The command is owner-only, antiforgery-protected, carries an `Idempotency-Key`
header and an `expectedVersion` body field, and records the actor and reason. It
is completely offline: no geocoding, tiles, browser location, or network request
takes part in it.

- **Where it is stored.** The entry is written to its own protected file,
  `<RawIngressRoot>/.location/manual-deployment-location.v1.protected`, next to
  the protected history. Both files are Data Protection
  encrypted, written through a temporary file with `fsync` and rename, and
  restricted to the owning user.
- **When it takes effect.** The command never moves the active snapshot. It
  records the governing local seed, and the next startup reconciliation appends
  the new immutable version exactly as a changed startup seed would.
  Consequently every capture recorded by the running process keeps the
  deployment version it was already stamped with, and captures after the restart
  carry the new version. Earlier versions, their effective intervals, and the
  captures bound to them are never rewritten.
- **Precedence.** While the startup configuration seed is unchanged, the manual
  entry outranks it, so restarts do not silently revert the operator. Changing
  the configured seed itself supersedes the manual entry: the next startup marks
  the record superseded, keeps its audit history, and appends a configured
  version. A superseded record never governs again.
- **Validation.** Latitude, longitude, the portable IANA time zone, and the
  canonical hash are validated by the deployment-location contract; elevation is
  additionally bounded to -500 m to 9000 m for a manual entry. That bound is
  command policy only: it is never applied when reading an existing record, so
  tightening it can never make a recorded entry unreadable. Coordinates equal to
  the ones already governing return `Unchanged` and create no version.
- **Concurrency.** A command carries both `expectedVersion` (the highest version
  in protected history) and
  `expectedManualSequence` (the newest recorded manual entry, or `0`). The second
  token exists because the command deliberately never touches the history, so the
  version alone cannot detect a competing pending entry. A stale value in either
  returns `409` with `manual.expectedVersionConflict` or
  `manual.expectedManualSequenceConflict` and writes nothing.
- **Idempotency.** A replayed `Idempotency-Key` with the same coordinates returns
  `Replayed`; the same key with different coordinates returns `409` with
  `manual.idempotencyKeyConflict`; and a key whose entry belongs to a record a
  configuration change has superseded returns `409` with
  `manual.supersededEntry` rather than a misleading success. Every rejection
  carries its bounded `reasonCode` and `fieldPath` as ProblemDetails extensions.
  The record retains the 500 most recent entries, so the replay window is the
  retained window.
- **Recovery.** The sidecar is validated on every start. An unreadable, re-keyed,
  or tampered record fails CameraAgent startup with
  `Protected manual deployment-location state is unreadable` or
  `… failed integrity validation`, exactly as a damaged history does. Delete
  `<RawIngressRoot>/.location/manual-deployment-location.v1.protected` to recover;
  the deployment then returns to its configured coordinates and every version
  already appended to the protected history is retained.

## Health and Telemetry

- The local `deployment-location` check reports whether protected geometry is usable.
- Meter `HVO.SkyMonitor.CameraAgent.DeploymentLocation` records bounded local outcomes,
  including applied, replayed, unchanged, conflict, invalid and failed manual operations.
- Deployment-location logs, metrics and traces exclude coordinates, hashes, credentials,
  protected payloads, owner reasons and entity identifiers.
