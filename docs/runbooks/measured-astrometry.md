# Explicit measured astrometry recipe

Issue #1642 exposes the existing shared Astronomy solver through Processing's
`measured-astrometry` analyzer (`1.0.0`, implementation `measured-astrometry-v1`).
It produces `measured-astrometry-solution-v1` typed Metadata. Availability in the
shared registry does not enable a CameraAgent step, central job, installed
profile, live capture path, or calibration write-back. Host adoption is separate.

Run fitting only as explicitly selected bounded work **after durable raw
admission**, in a background/operator operation. Serialize expensive cold solves
according to the host's resource capacity. A rejected or unavailable frame never
silently launches cold reacquisition or replaces a last-good calibration/prior.

## Frozen input envelope

The raw artifact is the primary `Raw` selector. Supply exactly three auxiliary
inputs:

- `measured-stellar-associations`: an explicit artifact selector and artifact ID
  for the retained #1187/#1541 product. Preserve its Metadata schema, semantic
  content identity, producer recipe descriptor and immediate raw source ID.
- `scene`: the canonical JSON of that capture's **Predicted** scene, with its
  semantic scene identity and independent payload checksum.
- `astrometry-context`: use `MeasuredAstrometryJson.ContextInput(context)` to
  encode a `MeasuredAstrometryContextV1` frozen snapshot. Its semantic identity
  is its canonical payload SHA-256, even when a checksum is supplied separately.

The context pins the raw capture/artifact/descriptor/payload, observer identity
and location, UTC exposure endpoints, detector/settings, numeric initial
calibration and version, exact raw layout/readout identity, installed catalog
package provenance and complete bounded catalog selection, measured artifact,
measured content/recipe identities, reported clock facts and optional accepted
prior. Use `MeasuredAstrometryJson.ComputeReadoutIdentity(raw.Layout)` for the
readout identity; this includes stored-code/CFA conventions and explicit absence
of a readout descriptor. The raw observation interval must agree; an instantaneous
accelerated observation expands using the existing
`ProcessingArtifact.ResolveObservationEndedUtc` rule and declared integration.

The initial calibration must exactly match the scene's projection in the raw
image coordinates. This slice requires an identity scene image transform and
geometric, unrefracted coordinates at the exposure midpoint. Calibration may
already describe a readout's ROI/binning; it must describe the emitted raw size.
A virtual-render-authoritative scene cannot substitute for measured evidence.
Catalog metadata, package preprocessing, IDs, magnitudes and selected J2000
coordinates are checked against the retained scene. Unsupported coordinate
models and incomplete selections retain an unavailable assessment.

The catalog adapter remains the owner of installed package verification and
completeness. Resolve immutable data locally; never download or mark a truncated
selection complete. The envelope accepts at most 2,500 catalog rows and 2 MiB.
The complete catalog/scene/calibration snapshots are inputs, not per-frame result
fields. Retain their immutable originals once and resolve them by identity.
These hashes are integrity/provenance checks, not signatures or evidence that a
physically inaccurate but well-formed clock/site is correct.

## Exact measurement conversion and bounds

The recipe requires the current measured-association producer descriptor and
algorithm vocabulary. Qualified rows are `LabelEligible`, positive-flux,
unsaturated, untrailed associations. Each becomes exactly
`AstrometricDetection(DetectionIndex, PixelPoint(MeasuredX, MeasuredY), Flux)`;
there is no coordinate transform, remeasurement, prediction-to-centroid copy or
renumbering. The existing solver discovers its own correspondences. An accepted
fit is withheld if any solver `(catalog ID, detection index)` differs from the
qualified measured product. There is no second association product or solver.

Covariance is taken only from positive-definite retained centroid matrices.
Six-decimal retention can round a small matrix to zero/singular; it becomes
missing covariance, never an invented precision floor. The existing residual
analyzer and uncertainty estimator retain conditional/model-validity evidence
or explicit withhold reasons. This slice supplies no shared session calibration
covariance: total uncertainty remains withheld when that input is absent.
Quality remains an engineering score, not a calibrated probability. Wider #526
covariance/SNR/adverse/native/physical qualification is unchanged.

Options normalize all existing solver, diagnostics and uncertainty settings.
The solver retains its own finite cold/warm work bounds; the enclosing recipe
adds a cooperative budget, default 20 seconds and maximum 60 seconds, including
its input preparation after executor dispatch. Cancellation during numerical
work, diagnostics and uncertainty uses the same linked token. Caller
cancellation propagates without publishing a result. An expired internal budget
retains `BudgetExceeded` without a mapping. Generic executor validation, bounded
JSON parsing and serialization are cooperative bounded work, not hard real-time
preemption. Raw layout is capped at 16,777,216 samples; more than 2,500 qualified
centroids yields an explicit bound outcome without a silently truncated fit.

## Retention and separate application

The compact output pins Processing recipe/context, measured artifact/content/
recipe, scene, layout/readout and package identities, and embeds the existing
`AstrometricFrameAssessment` using `AstrometricEvidenceJson`. It retains accepted,
rejected, unavailable and budget outcomes; accepted fit quality, fit/verification/
outlier detection-index membership, RMS/maximum residual/conditioning, and the
existing uncertainty result or a withhold reason. These small membership lists
refer to the separately retained measured rows; they copy no catalog/centroid
rows. The native assessment retains the solver's association hash. Failure
results have no fitted parameters. Malformed or mismatched lineage fails the
Processing request without publishing a misleading assessment.

Canonical JSON disallows missing/duplicate/unknown members, validates native
assessment/uncertainty identities and caps the result at 128 KiB. The semantic
solution identity is the canonical payload SHA-256. Processing output identity
also binds the recipe and immediate raw/measured source artifact IDs. Timings do
not enter semantic identity. Keep the measured product for at least as long as
its solution; ordinary generic metadata publication must preserve both parents.

`MeasuredAstrometryJson.ReconstructMapping(solution, calibration, measured)`
validates accepted native evidence and the exact immutable calibration/measured
identities before constructing `AstrometricMapping`. It does not fit again.
`ApplyPixels(mapping, pixels, cancellationToken)` is a separate operation capped
at 4,096 points; invalid/outside-aperture sky points are unavailable. Native
pixel/sky/frame mapping remains owned by Astronomy. No new resampler, epoch,
proper-motion, auto-activation, UI or hardware qualification is introduced.

See [reference validation](../validation/issue-1642-measured-astrometry.md) for the
software envelope, exact commands, output hashes and resource comparison.
