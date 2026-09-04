# Deployment-Location Reconciliation

LogicHost owns Observatory membership and fallback location. CameraAgent owns its
protected deployment snapshot while capturing offline. A pending or rejected
central disposition never changes local geometry or stops acquisition.

## Runtime Behavior

- New v2 bootstrap requires the active protected deployment snapshot and source kind.
- Active CameraAgents retry the same immutable version at
  `POST /api/device/deployment-location` using the per-device key.
- Unknown, pending, or mismatched capture provenance is retained. Only
  location-dependent central annotation work is quarantined.
- Acknowledgment binds exact central frames and revives their quarantined
  annotation jobs. It does not rewrite the frame's reported provenance.
- Observatory location changes append a new Observatory version and return
  previously acknowledged deployments to `Pending` for owner review. Historical
  frame facts remain unchanged.

## Manual Local Coordinates

An owner can enter observer coordinates locally in the Operations **Sky map &
catalog** section, or through `POST /api/v1/operations/deployment-location/manual`.
The command is owner-only, antiforgery-protected, carries an `Idempotency-Key`
header and an `expectedVersion` body field, and records the actor and reason. It
is completely offline: no geocoding, tiles, browser location, or network request
takes part in it.

- **Where it is stored.** The entry is written to its own protected file,
  `<RawIngressRoot>/.location/manual-deployment-location.v1.protected`, next to
  the protected history. The history document keeps schema `1` and its exact
  property set, so a deployment rolled back to an earlier image can still open
  it; that baseline simply ignores the sidecar. Both files are Data Protection
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
- **Central acknowledgement.** With `CentralIntegration:Mode=Enabled` the manual
  version becomes the protected local candidate instead of activating. The
  reconciliation worker proposes it, LogicHost acknowledges or rejects it, and an
  acknowledged version is staged and activated at the following restart.
  LogicHost never replaces local coordinates with its own; it can only
  acknowledge or reject the exact local proposal. If a central acknowledgement is
  already staged when the entry is made, that staged version activates first and
  the manual entry is then proposed as its successor.
- **Validation.** Latitude, longitude, the portable IANA time zone, and the
  canonical hash are validated by the deployment-location contract; elevation is
  additionally bounded to -500 m to 9000 m for a manual entry. That bound is
  command policy only: it is never applied when reading an existing record, so
  tightening it can never make a recorded entry unreadable. Coordinates equal to
  the ones already governing return `Unchanged` and create no version.
- **Concurrency.** A command carries both `expectedVersion` (the highest version
  the protected history knows, including a candidate or staged one) and
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
- **Rollback.** A rollback to a baseline image discards the manual override
  because the baseline does not read the sidecar. The protected history stays
  valid, so the baseline starts, appends a configured version, and the versions
  recorded while the manual entry was active remain intact with their captures.
  The sidecar file itself survives the rollback, so rolling forward again with an
  unchanged configuration seed makes the same entry govern once more and appends a
  further version. Delete the sidecar before rolling forward to avoid that.

## Owner API

Owner reads require cookie, read-capable API key, or owner bearer credentials.
Mutations require cookie credentials, a read-write API key, `api.owner.write`, or
`api.admin`. Cross-owner proposal IDs return `404`.

List pending proposals:

```bash
curl -fsS \
  -H "Authorization: Bearer $OWNER_TOKEN" \
  "https://logichost/api/internal/deployment-location-proposals?status=Pending&take=100"
```

Read one proposal and retain its strong `ETag` response header:

```bash
curl -fsS -D /tmp/deployment-location.headers \
  -H "Authorization: Bearer $OWNER_TOKEN" \
  "https://logichost/api/internal/deployment-location-proposals/$PROPOSAL_ID"
```

Acknowledge an exact proposal:

```bash
curl -fsS -X POST \
  -H "Authorization: Bearer $OWNER_TOKEN" \
  -H "If-Match: $PROPOSAL_ETAG" \
  -H "Content-Type: application/json" \
  -d '{"status":"Acknowledged","reason":"owner-approved"}' \
  "https://logichost/api/internal/deployment-location-proposals/$PROPOSAL_ID/resolution"
```

Use `Rejected` instead of `Acknowledged` when the reported deployment is not
valid for the selected Observatory. A missing `If-Match` returns `428`; malformed
input returns `400`; a stale ETag returns `412`; an already resolved immutable
proposal cannot be changed and returns `409`. Correct the protected local
location or Observatory assignment and submit a new location version instead.

## Health and Telemetry

- LogicHost check `deployment-location` is `Degraded` while proposals or capture
  mismatches need attention and `Unhealthy` for persistence invariants.
- CameraAgent check `deployment-location-reconciliation` is independently
  `Degraded` while pending or offline. Local `deployment-location` remains
  `Healthy` while protected geometry is usable.
- Meter `HVO.SkyMonitor.LogicHost.DeploymentLocation` reports bounded operation,
  duration, pending-count, oldest-age, and backfill instruments.
- Meter `HVO.SkyMonitor.CameraAgent.DeploymentLocation` reports bounded local and
  reconciliation outcomes, including the `manual` operation with `applied`,
  `replayed`, `unchanged`, `conflict`, `invalid`, and `failed` outcomes.
- Deployment-location authority logs, metrics, and spans never include
  coordinates, hashes, credentials, protected payloads, owner reasons, or entity
  IDs. General bootstrap audit logs retain bounded device and registration IDs.

## Troubleshooting

1. Confirm CameraAgent local `deployment-location` health is healthy. Do not
   replace a valid local snapshot merely because LogicHost is unavailable.
2. Confirm `deployment-location-reconciliation` is not reporting rejected
   credentials. Re-bootstrap only when device credentials are invalid or expired.
3. Inspect the owner proposal API and compare Observatory membership, timezone,
   radius, source kind, accuracy, and effective interval.
4. Resolve the proposal with its current ETag. On `412`, fetch it again before
   deciding; do not retry a stale decision blindly.
5. Confirm LogicHost health returns to healthy and the annotation job leaves
   `Quarantined`. Non-location-dependent evidence should have remained available.
6. A future acknowledged successor reports `restart-scheduled` and remains staged
   while the current geometry stays active. If its interval expires before a
   restart, the next startup preserves the active snapshot, clears the unusable
   stage, and exposes the protected candidate for reproposal or replacement.
   Correct an expired configured interval before reproposing; an expired central
   acknowledgment reports `acknowledgment-expired` and is never activated.
