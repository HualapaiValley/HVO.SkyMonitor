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
  reconciliation outcomes.
- Logs, metrics, and spans never include coordinates, hashes, credentials,
  protected payloads, owner reasons, or entity IDs.

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
