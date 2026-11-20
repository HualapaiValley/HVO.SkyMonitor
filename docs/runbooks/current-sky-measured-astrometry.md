# Current Sky measured astrometry (#1901)

Current Sky displays the latest accepted **retained** result in the existing
Measured Image Solution / Astrometry card, semantically separate from its
selected-image assessment. The summary identifies the result capture, midpoint
UTC, artifact and solution hash. The card identifies when the result belongs to
a different capture and can show it when no image is selected. Archive
inspection, overlays, residual diagnostics and pixel/sky calculators continue
to require the selected capture's compatible mapping. No fit runs in a browser or read request; no fitted calibration is
activated automatically.

Center RA/Dec are the accepted boresight in mean-of-date coordinates at exposure
midpoint. Roll is the retained native right-handed rotation about boresight;
zero roll puts local vertical up, with north-up at zenith. The local scale is the
angular separation between points ±0.5 source pixel on each X/Y axis. The card
names the reference point. It tries the measured boresight first, then the
source image center and a measured star if the readout crop or aperture prevents
calculation at the earlier points. The accepted pointing and rotation remain
visible when no scale is available. Source output pixels are zero-based,
X right/Y down, after crop/bin/flip. The scale describes the named pixel
neighborhood, and the values come from the verified retained fit.

The summary refresh runs independently of image selection. It looks up retained
solution candidates in newest exposure order, verifies an accepted candidate
and its source evidence, and returns summary values. Current Sky reports a
temporary read failure while keeping an already verified result and its timestamp.
A completed search with no verifiable accepted fit clears that summary.
Retention governs how long a result remains available.

## Opt-in Virtual Sky graph

Use the existing explicit-v2 graph and the retained contracts documented in
[retained astrometry](retained-astrometry.md). The owner command
`hvo-sky astrometry configure --reference-magnitude 3.25` derives the pinned
reference settings for an existing compatible graph. A new VirtualSky preset
defaults to 3.25. Preserve the existing, required
`ProjectedScene` node and its dependencies, including the presentation, stack,
and Storage branches. Keep an enabled retention owner. Append three optional
nodes (`required:false`) with `publication.persistence:durable-local`:

1. `AstrometryPrediction`, depending only on `$raw`, with
   `maximumMagnitude:3.25` and `maximumResults:2000`.
2. `MeasuredStellarAssociations`, depending on `AstrometryPrediction`, with
   `useAstrometryPrediction:true`.
3. `MeasuredAstrometry`, depending on both `AstrometryPrediction` and measured
   associations, with `useAstrometryPrediction:true`, `captureStride:60`, and a
   `catalogSelection` pin from the verified local adapter
   (`catalogIdentitySha256`, `selectionIdentitySha256`,
   `maximumMagnitude:3.25`, `maximumEntries:2500`). Set
   `recipe.solver.maximumCatalogMagnitude:3.25` to match the pin and prediction.

`AstrometryPrediction` builds catalog-star predictions from capture-time
rig/site and the verified installed catalog after raw admission. Its separate
recipe and output variant keep it out of canonical scene recovery, upload, and
presentation. Replay pins each scene by its own producer. The solver discovers
correspondences from raw measured centroids and retains acceptance/rejection
evidence. Configured pointing supplies the starting prediction. Existing
VirtualSky graphs that use the single-scene `independentPrediction` option keep
their recorded revisions and canonical scene. When the graph has a storage
retention owner, new captures use a compatible successor with a separate
prediction product. Scene-only graphs without that owner continue publishing
the canonical scene.

Materialize the selection once to record those two identity hashes. The runtime
resolves only the installed local adapter, verifies both hashes, package
provenance and completeness, and freezes the rows in the existing durable
context. A changed/missing/truncated catalog fails the optional node. Replay
uses the original frozen context without consulting the current catalog. The
existing inline `catalog` snapshot remains supported; supply exactly one of
`catalog` and `catalogSelection`. The compact pin keeps the full catalog
selection out of the shared 64 KiB graph-node options envelope without raising
that bound.

Build the astrometry snapshot through the local catalog adapter, including
package provenance and its completeness declaration. The qualified catalog is
HYG 4.2 production package `hyg-v4.2-p3-s2-r1`, 119,625 database rows, SHA-256
`B51D18B722199E89AA8FE4622EBE507346C75EFFB375E546881452A263F0B9E2`;
its complete magnitude-3.25 selection contains 226 stars, below the 2,500-row
contract bound. For a magnitude-5 profile, use
`hvo-sky astrometry configure --reference-magnitude 5` or set the prediction,
pin and solver magnitudes all to 5. This HYG selection contains 1,637 stars.
Use the installed catalog's verified completeness during execution.
Five-second capture and stride 60 sample one solve per five minutes. New graph
revisions record the declared fit as a supplementary input to Storage and as a
supplementary outcome for Telemetry. Storage archives its required image and
canonical-scene inputs between solves, and Telemetry can report the fit's outcome.
Other declared inputs remain required. Fresh work on an older compatible named
revision uses an append-only successor. Historical runs and archived replay
keep their original frozen definitions and identities. The existing 20-second
cooperative recipe budget and serialized numerical gate apply.
This qualification does not enable a default or installed profile.

## Inspect residuals on a single exposure

Keep the normal five-frame graph available in **Named graphs**. For example,
use **Reference astrometry, NGC and satellites** for observing and
**Single-exposure astrometry diagnostics** for inspection. Reference magnitude
**3.25**, a **2,000** renderer-candidate limit, and **20 s** exposures are studied
VirtualSky settings for this setup.

1. From **Pipeline → Named graphs**, open the active five-frame graph and choose
   **Draft a new revision from this one**. Set **Graph name** and **Revision
   label**. In the rolling combination node, set **Window size** to **1**.
   Keep the existing solve settings and catalog pin, presentation and storage
   nodes, and NGC and satellite overlay settings.
2. Choose **Preview plan** and check the one-frame window and retained nodes.
   Choose **Save immutable draft**. In **Named graphs**, select **Validate**
   and **Confirm validate** for the diagnostic revision. Once validated, use
   **Review activate**, check the revision, and choose **Confirm activate**.
3. Wait for the configured solve cadence to produce a new **Accepted** capture
   from that graph. Open the
   capture and choose **Processed → Diagnostics → Astrometric residuals**.
   The vectors compare measured stellar positions with the solution on that
   exposure. Rejected solves and incompatible bases withhold the vectors.
4. In **Named graphs**, use **Review activate** and **Confirm activate** on the
   original five-frame revision. Check that it is active before resuming normal
   observing.

With compatible retained evidence, the **Deep-sky catalog** layer marks
expected NGC object positions. The **Predicted satellite tracks** layer draws
predicted paths during the exposure. **Deep-sky associations** requires a
separate measured product. In a rejected **15 s** trial, the catalog and track
layers still drew while measured residuals were unavailable.

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
2. Preserve existing operator configuration and all durable state. Retain the
   exact previous image, configuration and catalog selection. On startup,
   #1901 migrates processing schema 9 to 10 in one transaction and adds the
   partial index `ix_processing_outputs_astrometry_lookup` for retained measured
   solutions. Raw ingress remains at schema 16 during this migration. The
   candidate advances `minimum-compatible-revision`, so the installer takes a
   paired pre-upgrade checkpoint before candidate startup when crossing that
   boundary. The supplementary fit contract creates a new graph revision for
   fresh work and preserves previously recorded plans and SQLite rows with
   their original identities. Run the supported installer's **upgrade plan/preflight** on the
   existing instance. Account for other reader and schema changes between the
   installed and qualified images. Confirm the checkpoint and recovery plan
   before proceeding. If preflight cannot establish the upgrade and recovery
   route, stop for Roy.
   [Installer snapshot procedure](deployment-installer.md#swap-upgrade-and-database-snapshots)
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
   measured overlays withheld when incompatible. For an accepted compatible current
   source, toggle Measured Stellar Associations and Astrometric Residuals and
   inspect actual measured markers/vectors against the retained source and
   membership. Exercise pixel-to-sky and equatorial/horizontal sky-to-pixel round
   trips, restore defaults and frozen stack export. Refresh to unsolved and
   mismatched captures: retain the correctly timestamped summary while withholding
   incompatible measured layers, diagnostics, calculators and export selections.
   Verify the configured image geometry, constellation lines, and visible
   solar-system annotations against the canonical scene before and after a
   refresh. Verify that the existing trailing stack, materialized preview,
   and local/archive storage paths continue to produce their normal outputs.
   Record directly related regressions and list unrelated gaps separately.
   Check raw sequences, backlog,
   retention holds, health, errors and resource consumption. Failure leaves the
   rollout incomplete even if the container is running.
6. If the protected upgrade fails before commit, the installer restores the
   paired pre-upgrade checkpoint before restarting the exact previous image
   with its matching configuration and catalog selection. If acceptance fails
   after commit, stop for Roy before restoring anything. For an approved
   recovery, preserve newer captures separately and use the installer's
   supported recovery procedure to verify and restore the paired checkpoint
   before starting the previous image. Restore the prior graph revision through
   the existing profile lifecycle if needed. Verify the original listener,
   identity, provisioning, retained captures, raw sequence continuity and
   homepage again. Record any recovery-point loss explicitly. If recovery
   fails, keep the instance fenced and stop for Roy.
   [Upgrade-time checkpoint recovery](../hvo-install-contract.md#skymonitor-approved-target-upgrade-time-checkpoint-recovery)

## Reproducing development qualification

The Manual acceptance method
`CurrentSkyMeasuredVirtualCaptureRefreshPreservesTimestampAndCaptureContinuityAsync`
uses a disposable loopback Kestrel CameraAgent, owner authentication, production
catalog validation, actual VirtualSky acquisition, durable graph publication and
real Chromium. It uses the checked-in installer graph with disposable roots
and test sensor settings. It exercises the rich scene and optional measurement
together, a deliberate catalog-pin mismatch, and the installer graph alone as
a timing baseline. Set `HVO_ASTROMETRY_CATALOG_ROOT` to the approved installed catalog
root and `HVO_1901_EVIDENCE_ROOT` to a fresh short owned scratch path (Unix owner
socket paths must fit 108 bytes). Serialize it with
`flock -o /tmp/hvo-heavy-gate.lock`; run the CameraAgent.AcceptanceTests project in
Release with `--filter FullyQualifiedName~CurrentSkyMeasuredVirtual`. It preserves
its own instance tree and screenshots for inspection. These virtual results are
software qualification; physical accuracy and production-resolution capacity
remain separate.
