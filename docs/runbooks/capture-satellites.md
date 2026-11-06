# Retained capture satellite predictions

Issue #1641 connects the [capture recipe](../contracts/capture-orbital-prediction-v1.md)
to the ordinary CameraAgent standard lane. It is an explicit software opt-in.
No shipped profile enables it. Capture admission, base image pixels, detector
inputs, Observatory full-pass planning and provider refresh schedules are unchanged.
Configured predictions establish neither detections nor physical visibility,
brightness, accepted calibration or physical accuracy. #1395 owns the integrated
cadence/accuracy gate; #530 owns measured satellite association.

## Opt-in graph

Use the existing version 2 layered graph. Add this node, selecting the exact
bounded population intended for this camera:

```json
{
  "type": "CaptureSatelliteTracks",
  "id": "capture-satellites",
  "dependsOn": ["$raw"],
  "required": false,
  "options": {
    "outputVariant": "capture-satellites-v1",
    "catalogNumbers": ["25544"],
    "allowStaleDiagnostics": false
  },
  "publication": { "persistence": "durable-local" }
}
```

Add a durable `CaptureSatelliteLayer` node with id `capture-satellite-layer`,
dependency `["capture-satellites"]` and default options (or `inputVariant`
matching the producer's base output variant). The analyzer and presentation
transform use separate graph nodes, preserving the shared graph operation
contracts. Add both node IDs to the existing `OverlayManifest` dependencies
and set its options `satelliteVariant` to `capture-satellites-v1-layer` and
`satelliteStatusVariant` to `capture-satellites-v1-status`. Preserve its existing
scene, environment and base-image dependencies. Existing `Storage` dependencies
may include both new nodes, with `queueForUpload: false` for their layoutless
metadata. Durable-local publication already owns their canonical copy; `Storage`
does not create a second local copy of those outputs. Current Sky and the archive
view consume the canonical retained products. Retention also protects copies in
recorded historical archive roots made through existing publication primitives.
The optional central reconstruction boundary is the portable typed products;
this slice adds no upload policy or automatic secondary archive publication.

Installed orbital storage and the existing local time source supply the inputs.
The source/time loader's ordinary startup/refresh warmup remains authoritative.
This node performs any remaining local work after raw admission, on the existing
standard lane. It never fetches a provider or starts a scheduler. Refresh may be
disabled while an installed snapshot remains readable.

Enable the existing local `CameraAgent:SatellitePlanning:Enabled` seam. Its
time-source settings choose the
verified bundled/refreshed pair or an explicit pinned override. An override also
requires this node's `overrideTimeDataAvailableUtc`, an explicit UTC attestation
of when those exact bytes were available. A bundled/refreshed pair uses the
later of its two retained retrieval times. Neither a later snapshot nor later
time data is relabelled as originally available: original knowledge is cut off
at this raw capture's recorded durable-ingress UTC. No retrospective
reassessment is silently substituted.

The capture must retain its actual readout and protected deployment-location
identity. The capture-time location history and frozen rig/optics determine
observer/projection; current settings never supply replacement coordinates.
The projection uses readout-view coordinates and the identity image transform,
so crop/bin/flip are not applied twice. Timing is reported command/status
evidence; clock and physical uncertainty remain unknown when not supplied.

## Products and presentation

The two enabled nodes publish ordinary typed Metadata V3 artifacts:

- `*-inputs`: `hvo-capture-satellite-evidence-v1`, containing the exact raw
  descriptor, rig/optics, observer, complete canonical recipe plan, snapshot
  manifest/reference, actual EOP/leap bytes and time provenance. Normalized
  records stay in the existing pinned orbital store, avoiding a full snapshot
  copy per capture. The shared helper also accepts an embedded bounded snapshot
  for portable callers; compact carriers require an explicitly resolved exact
  `OrbitalSnapshotReadResult` for reconstruction.
- The base variant: unmodified `hvo-capture-orbital-prediction-v1` result and
  canonical `hvo-orbital-exposure-track-v1` children, when a valid plan exists.
- `*-status`: independent availability/computational status, reason, population
  coverage, source retrieval, epoch age, geometry basis and renderability.
- `*-source`: exact `OrbitalTrackPresentation.CreatePayloadAndSourceFacts`
  source bytes for nonempty renderable results, with immediate result/status lineage.
- `*-layer`: the existing typed presentation payload. Unavailable/no-result
  products carry empty geometry and their separate status, never invented tracks.

Current Sky and the archived capture view use the existing Diagnostics toggle,
disabled when no geometry can be rendered, and initially off for retained tracks.
The status describes missing/expired time, missing/retired sources, selection
withholding, partial computation, no exposure segments and presentation limits.
Population completeness is independent of computational completion. A no-result
does not establish absence outside the requested population/exposure. Source
retrieval/epoch age and configured geometry remain visible. Exports use the
existing immutable manifest and selected layer identities.

Only samples inside each declared exposure segment are connected. The existing
1px stroke, 2px endpoints, bounded labels and prediction/unknown/partial/stale
captions remain. A partial query qualifies even a prefix containing complete
child tracks. No full-pass arc is introduced into capture imagery.

## Restart, replay and retention

The bounded intent at `journal/capture-satellites/<capture-node-key>/` records
the frozen inputs before acquiring the orbital reference. The reference is
acquired before publication. A publication-input decision and the first
completed canonical result are flushed and atomically published before the
ordinary processing artifacts. A restart reuses these bytes; it never replaces
a deadline's useful prefix under the same recipe identity. An interrupted
pre-intent input write is adopted by the next attempt. Repeated reference
acquisition/removal is idempotent.

Archived replay submission pins the original committed input/result artifacts
in the existing replay transaction. Replay reconstructs the products from those
bytes, without a current-pointer/time/site lookup. The node records in-process
execution, including when other archived nodes use the optional local runner.
Missing/corrupt/ambiguous original evidence fails closed. A changed assessment
requires an explicit future reassessment path; replay preserves original inputs.

Existing capture/artifact retention owns cleanup. While another canonical node
or a retained archive artifact needs the prediction, all satellite support
products remain held. Independent canonical projected-scene support is excluded
from that consumer test: its existing guard waits for raw, and counting it here
would form a scene/raw/satellite hold cycle. Raw is held while prediction products remain. Once other
products expire, a later sweep expires support products; raw then becomes
eligible under its other holds, followed by independent scene support. Only after raw and all canonical/archive
consumers are absent does reconciliation release the orbital reference and
remove the intent. The orbital store protects the original provider bytes,
normalized records and manifest while referenced. Uncertain archive history,
unsafe paths, storage faults or discovery bounds postpone reclamation.
All deletion runs through the existing maintenance/lifecycle barriers; a pause
keeps reconstruction inputs. Do not manually remove the intent or its reference.

This adds versioned immutable files and typed products under current roots;
there is no SQLite schema conversion. It follows the keep-current primitives
decision in [#1607](../validation/cameraagent-filesystem-decision-1607.md) and
the simplify-on-existing-primitives disposition in
[#1615](../validation/upgrade-recovery-cost-1615.md). Supported inputs are
current manifest v2 capture evidence and the #1640 recipe contract. Unsupported
or corrupt evidence is refused; prior binaries are not promised to understand
this explicitly selected new graph node.

## Declared bounds and signals

Default numerical budget: 32 explicitly requested objects, 120s exposure,
100,000 propagation evaluations, 32 tracks, 8,192 samples, 200ms engine work,
1s sample step and 0.1px chord tolerance. The #1640 hard maxima still apply.
The compact producer uses the installed snapshot's existing bounds, a 4MiB
plan and a 40MiB serialized input carrier; EOP/leap bytes retain their existing
8MiB/64KiB limits. Portable callers may embed up to 16MiB of normalized snapshot
bytes instead of resolving its exact reference. Selection-candidate bounds,
missing readout/location or unsupported time coverage produce an unavailable
status. The renderer separately enforces 1,024 samples per track,
8,192 total samples and existing primitive/geometry budgets. A rendering refusal
preserves canonical numerical output and marks presentation withheld.

New work is admitted under the storage lifecycle barrier only while fewer than
262,144 intent directories exist, including interrupted preparations. This is
45.51 days at one capture every 15 seconds (172,800 captures in the default
30-day retention window), for one satellite node per capture. Multiple nodes,
faster captures or longer retention consume the same capacity. At capacity,
the ordinary durable lane retries new work without creating another directory
or acquiring a source reference. Existing intent/input retries still run.
Retention reclaims slots; no operator journal deletion is needed.

Hold discovery streams the complete inventory, including an oversized inventory
created before admission was bounded. It never returns a truncated hold set or
uses the admission limit to stop retention. Small headers, 4,096 products per
capture and the existing archive byte/entry/date bounds still apply. Cumulative
archive discovery budgets renew between captures; the current capture is
rechecked from scratch. A capture that cannot fit a fresh budget, corrupt or
unsafe evidence, missing storage or uncertain archive history still fails
closed. A large inventory can take longer to scan and remains cancellable.

Reconciliation removes empty interrupted directories and reclaims completed
intents only after proving raw and every canonical/archive consumer absent.
Nonempty headerless inputs are checked after one day, at most 16 inputs and
64MiB per sweep. A durable cursor resumes after previously checked candidates,
including live inputs that must remain; byte-budget exhaustion postpones the
next input to a fresh sweep. A restarted sweep rechecks absence rather than
trusting the cursor. Source acquisition/removal remains idempotent.

The node reuses `camera_agent.processing.nodes`, `.outcomes`, `.outputs`,
`.output.bytes`, `.recipe.duration` and `.persistence.duration`, plus
`processing-artifact.persist` spans and ordinary graph/node logs. Units are
counts, bytes and seconds; configured step/recipe/outcome labels remain bounded
by the graph. Capture IDs/source hashes are evidence, not new metric labels.
No new provider request, health check or background queue is added. Availability
degradation is retained metadata, independently of lane failure/retry signals.

Run the focused capture tests and the manual measurement in
`CaptureSatelliteIntegrationTests`, `CaptureSatelliteProductTests` and
`CaptureSatelliteResourceTests`. Set `HVO_CAPTURE_SATELLITE_EVIDENCE_DIR` to a
registered issue scratch/evidence directory for the resource JSON. That harness
measures actual admission, producer+persistence, retained-consumer rendering
composition and export boundaries at 640x640, alongside disabled, unavailable
and missed-deadline outcomes; it provides no full-resolution or physical claim.

After a Release build, reproduce the resource run on the shared worker with:

```bash
HVO_CAPTURE_SATELLITE_EVIDENCE_DIR=<registered-issue-scratch> \
flock -o /tmp/hvo-heavy-gate.lock timeout 900 \
  dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --no-build --configuration Release \
  --filter FullyQualifiedName~CaptureSatelliteResourceTests \
  -nodeReuse:false -p:UseSharedCompilation=false
```

The bounded baseline/after results and acceptance mapping are retained in the
[#1641 validation record](../validation/capture-satellites-1641.md).
