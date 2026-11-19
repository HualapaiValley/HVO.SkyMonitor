# Current Sky measured astrometry (#1901)

Current Sky reads the latest accepted **retained** result independently of its
selected-image assessment. The summary identifies the result capture, midpoint
UTC, artifact and solution hash. An unsolved newer image explicitly labels that
result as previous evidence. Archive inspection, overlays, residual diagnostics
and pixel/sky calculators continue to require the selected capture's compatible
mapping. No fit runs in a browser or read request; no fitted calibration is
activated automatically.

Center RA/Dec are the accepted boresight in mean-of-date coordinates at exposure
midpoint. Roll is the retained native right-handed rotation about boresight;
zero roll puts local vertical up, with north-up at zenith. The local scale is the
angular separation between points ±0.5 source pixel from the measured boresight
on each X/Y axis. Source output pixels are zero-based, X right/Y down, after
crop/bin/flip. This explicitly local fisheye scale is not a constant for the
whole image. Values come from the verified retained fit and Astronomy mapping.

The authenticated summary lookup pages 16 assessment capture IDs at a time in
newest exposure order. It skips nonaccepted compact products, verifies all
parents of an accepted candidate through the existing retained reader, and
returns only summary values. Current Sky imposes a five-second deadline and the
reader's existing single-reader/no-buffer-queue gate. Busy, invalid, expired or
missing evidence yields unavailable summary rather than invented pointing. A
result outside one page remains discoverable; there is no fixed 16-result age
cutoff. Retention governs how long any result remains available.

## Opt-in Virtual Sky graph

Use the existing explicit-v2 graph and the retained contracts documented in
[retained astrometry](retained-astrometry.md). Keep independent raw Storage and
an enabled retention owner. Set both measured nodes optional (`required:false`)
and all three metadata nodes to `publication.persistence:durable-local`:

1. `ProjectedScene`, depending on `$raw`, with `independentPrediction:true`,
   `maximumMagnitude:5`, `maximumResults:2000`.
2. `MeasuredStellarAssociations`, depending on that scene.
3. `MeasuredAstrometry`, depending on both scene and measured associations,
   with `captureStride:60` and a `catalogSelection` pin from the verified local
   adapter (`catalogIdentitySha256`, `selectionIdentitySha256`,
   `maximumMagnitude:5`, `maximumEntries:2500`).

`independentPrediction` builds catalog-star predictions from capture-time
rig/site and the verified installed catalog **after raw admission**. It never
copies or relabels VirtualSky render truth. Replay uses its original frozen
scene. The option supports stars only; retain the default scene path for richer
render-authoritative products. The solver still discovers correspondences from
raw measured centroids and retains acceptance/rejection evidence. Configured
pointing is an initial prediction, never the displayed measured answer.

Materialize the selection once to record those two identity hashes. The runtime
resolves only the installed local adapter, verifies both hashes, package
provenance and completeness, and freezes the rows in the existing durable
context. A changed/missing/truncated catalog fails the optional node. Replay
uses the original frozen context without consulting the current catalog. The
existing inline `catalog` snapshot remains supported; supply exactly one of
`catalog` and `catalogSelection`. The compact pin keeps a full magnitude-5
selection out of the shared 64 KiB graph-node options envelope without raising
that bound.

Build the astrometry snapshot through the local catalog adapter, including
package provenance and its completeness declaration. The qualified catalog is
HYG 4.2 production package `hyg-v4.2-p3-s2-r1`, 119,625 database rows, SHA-256
`B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2`;
its complete magnitude-5 selection contains 1,637 stars, below the 2,500-row
contract bound. Do not manufacture completeness or download during execution.
Five-second capture and stride 60 sample one solve per five minutes. Optional
skips/failures cannot become prerequisites of raw Storage. The existing
20-second cooperative recipe budget and serialized numerical gate apply.
This qualification does not enable a default or installed profile.

## Later home-docker in-place update and rollback plan

This is preparation only. No home-docker access or runtime change is part of
#1901's coding candidate. Roy must separately authorize the actual update after
Kepler's review/CI lifecycle and artifact qualification.

1. On that later authorized operation, inventory the existing instance ID,
   managed installation root, exact image digest, installer version, current
   listener address/port, mounts, limits, configuration/profile revision, active
   graph, catalog identity, Identity/Data Protection/provisioning paths, and
   capture sequence. Retain the inventory and current homepage evidence. Do not
   infer the deployed address or port from development defaults.
2. Preserve existing operator configuration and all durable state. Retain a
   tested supported backup/checkpoint and the exact previous image/artifact and
   configuration. Run the supported installer's **upgrade plan/preflight** on
   the existing instance. Account for all reader/schema changes between rc.1
   and the eventual qualified image; #1901 introduces no schema conversion but
   does not establish compatibility for other intervening changes. If preflight
   cannot prove the upgrade and recovery route, stop for Roy.
3. Use the existing installation's supported in-place upgrade workflow with the
   exact qualified artifact, retaining the same instance, root, address, port,
   credentials and mounts. No install/reset/uninstall operation, new instance,
   fleet action or release promotion is authorized by this plan.
4. Validate/apply a new local profile revision with the explicit optional graph
   above and the pinned complete catalog. Keep the previous profile revision for
   rollback. Preserve custom operator steps; do not replace the profile with a
   development fixture. Confirm durable raw admission continues through cadence
   skips and analysis failures, then wait for an actual accepted retained solve.
5. At the unchanged endpoint, authenticate and compare all four summary fields,
   timestamp, artifact and solution identity with retained evidence. Observe
   multiple capture refreshes between solves; the timestamp must remain the
   solution's, with previous-capture labeling and current-image calculators and
   measured overlays withheld when incompatible. Check raw sequences, backlog,
   retention holds, health, errors and resource consumption. Failure leaves the
   rollout incomplete even if the container is running.
6. On failed acceptance, disable/restore the prior graph revision through the
   existing profile lifecycle and use the supported installer rollback to the
   exact previous artifact. If its reader cannot consume current state, restore
   the paired pre-upgrade checkpoint by the supported recovery workflow; do not
   attempt reverse migrations or a blind old-image swap. Preserve captures made
   since the checkpoint separately before any approved restore that would lose
   them. Verify the original listener, identity, provisioning, retained captures,
   raw sequence continuity and homepage again. Record any recovery-point loss
   explicitly. A failed rollback stops for Roy; it never authorizes a reset.

## Reproducing development qualification

The Manual acceptance method
`CurrentSkyMeasuredVirtualCaptureRefreshPreservesTimestampAndCaptureContinuityAsync`
uses a disposable loopback Kestrel CameraAgent, owner authentication, production
catalog validation, actual VirtualSky acquisition, durable graph publication and
real Chromium. Set `HVO_ASTROMETRY_CATALOG_ROOT` to the approved installed catalog
root and `HVO_1901_EVIDENCE_ROOT` to a fresh short owned scratch path (Unix owner
socket paths must fit 108 bytes). Serialize it with
`flock -o /tmp/hvo-heavy-gate.lock`; run the CameraAgent.AcceptanceTests project in
Release with `--filter FullyQualifiedName~CurrentSkyMeasuredVirtual`. It preserves
its own instance tree and screenshots for inspection. These virtual results are
software qualification; physical accuracy and production-resolution capacity
remain separate.
