# Capture orbital prediction v1 — #1641 handoff

Issue #1640 supplies an explicit host-neutral Processing recipe/output boundary.
It uses `OrbitalPredictionEngine.PredictExposureAsync`, `OrbitalExposureQuery`,
`OrbitalRecordSelector` and `OrbitalExposureTrackV1` unchanged. There is no
second propagator, sample/segment format, downloader, current-snapshot reader,
registration, publication, reference-retention lifecycle or UI implementation.
The [roadmap activation](../roadmap.md#bounded-capture-satellite-and-measured-astrometry-ui-priority--2026-10-23)
records the authorized sequence and exclusions.

## Exact source and entry points

- `src/HVO.SkyMonitor.Processing/CaptureOrbitalPredictionContracts.cs` defines
  `CaptureOrbitalPredictionInput`, `CaptureOrbitalPredictionPlanV1`,
  `CaptureOrbitalPredictionV1` and `CaptureOrbitalPredictionJson`.
- `src/HVO.SkyMonitor.Processing/CaptureOrbitalPredictionRecipe.cs` defines the
  public `IProcessingRecipe` implementation. Name `capture-orbital-prediction`,
  semantic version `1.0.0`, implementation `capture-orbital-prediction-v1`.
- Engine/query/track sources are
  `src/HVO.SkyMonitor.Astronomy/Orbital/OrbitalPredictionEngine.cs`,
  `OrbitalPredictionProducts.cs`, `OrbitalPredictionFacts.cs`,
  `OrbitalRecordSelection.cs` and `Time/OrbitalTimeData.cs`.
- The existing consumer renderer is
  `src/HVO.SkyMonitor.Processing/OrbitalTrackPresentation.cs`.

Resolve and protect the exact raw reconstruction descriptor, snapshot and
actual time bytes **outside** the recipe. Construct `CaptureOrbitalPredictionInput`
with that descriptor, explicit observer, configured geometry, timing facts,
model, bounds, availability cutoffs, `OrbitalSnapshotReadResult`, selection
policy and requested NORAD identities. Construct
`CaptureOrbitalPredictionRecipe(input, timeData)` and supply it explicitly to
`ProcessingRecipeExecutor`. Request options must be `{}`; the recipe normalizes
them to its full pinned plan. It is intentionally absent from default built-in
registration. #1641 owns any adapter/catalog/product-contract registration.

The source must be exactly one Raw artifact selected by the request, with
capture/artifact/sequence IDs, descriptor identity, raw recipe identity,
variant, media type, creation UTC, layout/readout, observed start/end,
effective integration and raw checksum matching the descriptor. No pixel
interpretation or copy is needed; execution reads raw bytes to verify SHA-256.
The exposure is the descriptor's recorded interval, without deriving an end
from artifact creation or silently expanding an instantaneous simulated capture.
The first contract rejects `Actual` sensor timing claims: the current descriptor
records command/status observations. Estimated/Reported/Unknown remain explicit.

The observer and configured projection are caller-supplied retained facts, not
inferred coordinates or measured calibration. Rig identity must match the
capture-time rig profile. Readout identity must hash the actual readout via
`CaptureContractJson.ComputeCanonicalJsonSha256`. Readout content must match
raw layout; the geometry validator enforces NativeSensor versus ReadoutView
transform-once rules. The output dimensions must equal the raw raster.
Calibration *profile* identity in the descriptor and geometry's calibration
*model* identity are separate retained identities; they are not interchangeable.
#1641 must resolve the actual configured profile/optics before making this input.
Geometry, observer and all model/convention changes alter the canonical plan.

## Wire schema and identity

| Item | Exact value / rule |
| --- | --- |
| Input schema | `hvo-capture-orbital-input-v1` |
| Output schema | `hvo-capture-orbital-prediction-v1` |
| Media type | `application/vnd.hvo.capture-orbital-prediction+json` |
| Product role/kind/layout | Metadata / Metadata / null |
| Canonical encoding | `OrbitalContract.SerializeCanonical`; uppercase SHA-256; existing orbital enum, UTC, number and property-order rules |
| Input identity | SHA-256 of `input.SerializePlan()` |
| Recipe identity | Ordinary Processing bound-input identity over full normalized plan, selector, recipe/implementation versions |
| Content identity/checksum | SHA-256 of the complete canonical output bytes |
| Output identity | Ordinary Processing role/variant/recipe/source-artifact identity |
| Immediate artifact lineage | Exact raw artifact ID; snapshot and time references remain explicit in the retained plan |

The plan contains the full raw descriptor and its canonical descriptor SHA,
observer, geometry/readout/transform, timing/clock evidence, complete model
identities/conventions, bounds, availability mode/cutoffs, snapshot manifest
(including provider/raw/parser/normalizer/terms provenance), population coverage,
selection policy version/hash/source priorities, sorted requested objects and
per-object `OrbitalRecordSelection`. Selection includes the selected canonical
record, candidate eligibility/freshness/age/source validity and selection rule.
No-track results therefore retain selection decisions too. Missing snapshots
retain the precise `OrbitalSnapshotUnavailableReason` and Unknown population
coverage, rather than inventing an empty complete population.

The output contains `schemaVersion`, `inputIdentitySha256`, the complete `input`,
`physicalVisibility` (always `Unknown`), engine `status`, `reason`,
`evaluatedObjects`, `propagationEvaluations`, `tracks` and `failures`.
`tracks` are unmodified canonical `hvo-orbital-exposure-track-v1` objects with
their original `productSha256`. Elapsed milliseconds and process diagnostics
are excluded, so independent executions with identical outcomes produce
identical bytes. A real deadline can change the retained result; canonical
identity never implies deadline-independent successful computation.

Retain the exact plan bytes. `CaptureOrbitalPredictionInput.Restore(planBytes,
exactSnapshotRead)` reconstructs/validates all selections and requires canonical
byte equality, including unavailable state. It rejects substituting another
snapshot. `CaptureOrbitalPredictionJson.Parse(outputBytes, expectedInput)`
requires the exact input identity/plan and reuses the orbital result validator
for track hashes, selected records, query/exposure/geometry/time provenance,
status and budgets. It rejects noncanonical bytes and unknown fields. Never
parse against a newly resolved current pointer. The output's checksum and
recipe identity still require the surrounding artifact envelope's validation.

## Availability and truthful result handling

`OriginalAvailability` fixes both cutoffs to the raw descriptor's durable ingress
UTC. `RetrospectiveReassessment` names that same original cutoff plus an explicit
assessment cutoff at or after it. Actual time-data availability UTC and all
snapshot source retrieval/provider-generation times must be at or before the
assessment cutoff. Availability timestamps are retained caller evidence; the
recipe cannot authenticate a provider's historical availability by itself.
Both mode and cutoffs are hashed even if selected records and track bytes match.
A later snapshot/time-data pair cannot be relabelled originally available.
The time model must equal `OrbitalPredictionModel.Create(actualTimeData, ... )`;
forged EOP/leap/propagator/model identities reject before execution. Outer
coverage mismatches reject at query construction; actual EOP gaps and unsupported
leap crossings return existing engine reasons without fabricated tracks.

- `Complete` means bounded computation finished, independently of population
  coverage. Complete with zero tracks is a valid geometric no-result.
- Missing snapshot produces Unavailable / SelectionWithheld plus its snapshot
  reason. Missing object, stale/unapproved/withheld selections and candidate
  diagnostics survive without tracks. Partial and stale diagnostic tracks keep
  their original freshness, covered interval, sampling and failure facts.
- Evaluation, sample, result and deadline exhaustion preserve Partial or
  Unavailable plus the engine reason and bounded useful prefix. Cancellation
  throws and publishes no product, including cancellation during engine work.
- Physical visibility stays Unknown; sample illumination and physical orbital,
  clock and configured calibration uncertainty remain whatever the existing
  engine truthfully reports. No detection, brightness or accepted calibration
  is inferred from an in-frame predicted pixel.
- Render only within each declared segment. Never connect separated segments,
  extrapolate uncovered exposure time or apply readout/crop/bin/display transforms
  again: track samples already contain output pixels. The renderer requires
  nonempty bounded tracks and cannot represent query-level no-result states;
  #1641 must retain/show those metadata states separately.

## Bounds, replay, and consumer responsibilities

Hard caps are 256 requested objects, 4096 retained selection candidates, 32 tracks,
8192 total samples, 100000 propagation evaluations, 120 seconds per window,
10000 ms engine deadline, 4 MiB plan and 16 MiB output. Tighter caller bounds
are included in identity. This recipe does not truncate a larger population
silently. Input construction and raw checksum verification are outside the
engine's numerical work deadline; host cancellation/lease fencing remains the
outer execution boundary. Materialized evidence is reused without copying raw
pixels. Work is bounded by snapshot/selection candidates, engine evaluations,
raw byte length and canonical metadata sizes; there is no frame buffer clone,
background queue or hidden retry.

There is no persistent migration in #1640. V1 is a new opt-in metadata schema;
unsupported or corrupt bytes fail closed. Retry/replay uses exact retained
inputs and ordinary output identity/idempotency checks. A real deadline can
produce a different prefix on another execution with the same recipe identity;
preserve the first published output on resume. Never overwrite its different
checksum under that identity. A new explicit reassessment cutoff or changed
budget creates a new recipe identity when another computation is wanted. #1641 owns durable
publication, crash recovery, unavailable-source presentation, source reference
acquisition/release and retention of raw/descriptor, exact snapshot manifest and
normalized records, EOP/leap bytes with availability evidence, configured
profile/optics, plan, output and producing recipe descriptor. Do not release
those references while a retained prediction needs reconstruction. Nothing in
this slice supplies host registration/publication or changes a shipped default.

## Fixtures and reproducible checks

`tests/HVO.SkyMonitor.Processing.Tests/CaptureOrbitalPredictionFixtures.cs`
builds a 640x640 Bayer16 zero-filled raw descriptor with fixed capture/artifact
IDs and the existing approved geometry/exposure. It does not simulate a physical
signal. Exact approved fixtures live below
`tests/fixtures/orbital-prediction-v1/virtual-camera/`:

- ISS: `snapshots/six-facts/{manifest,records}.json`, `tracks/iss/track-03.json`.
- Constellation: `snapshots/two-starlinks/{manifest,records}.json`,
  `tracks/starlink-56838/track-03.json`, `tracks/starlink-65283/track-03.json`.
- Actual local time bytes: `../time/predicted-2026.finals2000A` and
  `../time/leap-seconds.list` relative to that directory.

The full source snapshot keeps the existing orbital contract limits (100000
records, 128 MiB payload); only requested objects and retained selection candidates
are bounded by this recipe. A normal full active-group snapshot is therefore
usable without manufacturing a different subset snapshot identity.

Committed wrapper goldens are `tests/fixtures/processing/capture-orbital-iss-v1.json`,
`capture-orbital-starlink-56838-v1.json` and `capture-orbital-starlink-65283-v1.json`
in that same directory. Unit cases compare the complete canonical output bytes
against these fixtures.

The recipe tests compare full canonical embedded tracks byte for byte, then
exercise missing/stale/withheld/time gaps, mismatched inputs, knowledge history,
bounded population, clipping/segment gaps, cancellation and exhausted budgets.
The Manual resource test emits canonical capture-output fixtures and
`capture-orbital-resources.json`, comparing the nearest engine path with the
recipe under predeclared bounds. See
[the validation record](../validation/capture-orbital-prediction-1640.md) for
commands, fixture checksums, measured scope and candidate gate evidence. No
full five-second/native ARM/physical-performance claim is made.
