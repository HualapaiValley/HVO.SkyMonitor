# Transient Extraction and Assessment V1

This document defines the software-complete V1 boundary for extracting measured
transient candidates and producing deterministic assessments. It complements
the event/input contract and temporal-background contract; it does not define a
CameraAgent lane, LogicHost job, persistence policy, notification policy, or
physical sensitivity claim.

## Ownership

- `HVO.SkyMonitor.Imaging` owns residual qualification, saturation topology,
  connected components, fragment grouping, geometry, and measured profiles.
- `HVO.SkyMonitor.Processing` owns canonical options, candidate and observation
  creation, extraction receipts, assessments, reason codes, identity, and
  lineage.
- Hosts resolve immutable inputs and caller-owned IDs/timestamps. The shared
  implementation does not read current registration, storage, scenario truth,
  ambient clocks, or random identity.

## Inputs and masks

Extraction consumes one verified Mono16 detector target and one compatible
causal or centered temporal-background product. The target/background layouts,
target detector identity, background pixel checksum, effective/no-support-mask checksums,
ordered source evidence, and mask identities must agree before pixel work.

The temporal effective mask includes persistent exclusions, no-background-
support pixels, and target saturation. Extraction separates these semantics:

```text
hard = persistent OR no-support OR (effective AND NOT saturation)
```

The persistent term preserves an exclusion when a pixel is both persistently
masked and saturated. Hard exclusions never contribute geometry or bridge a
component. Saturation-only pixels do not seed candidates, but one bounded
saturation island adjacent to qualified residual support may bridge clipped
support. Its samples increment `SaturatedSampleCount`; clipped values do not
contribute recovered photometry. Every such candidate carries the
`transient.saturated-photometry-unrecoverable` limitation. Saturation alone is
never sufficient for fireball severity.

## Residuals and components

For every non-hard-masked, non-saturated detector pixel:

```text
residual = max(target - background, 0)
foreground = residual >= minimumResidualAdu
```

Both the foreground support count and integrated valid residual must meet their
canonical thresholds. Foreground uses 8-connectivity. A saturation bridge is
accepted only when its island is adjacent to foreground and does not exceed
`maximumSaturationBridgePixels`.

Disconnected components may be grouped as fragments only when their pixel-edge
bounds are within `maximumFragmentGapPixels`, their principal axes meet
`minimumFragmentAlignmentCosine`, and the center-to-center direction meets the
same alignment. Grouping occurs before the candidate limit. Raw component work
is bounded to sixteen times `maximumCandidates`; any remaining overflow returns
an atomic `candidate-limit` outcome with no partial candidates.

Candidates are ordered by the first row-major foreground pixel before consuming
caller-provided candidate/event identity slots. IDs are never inferred from
pixels or artifact lists.

## Geometry and features

Geometry uses detector pixel-edge coordinates. Bounds are the union of all
qualified foreground and accepted saturation-bridge pixels. The two-point V1
polyline follows the residual-weighted principal axis. Isotropic covariance
uses the positive X axis; otherwise axis sign is fixed to positive X, then
positive Y. Endpoints are the minimum and maximum support projections.

Bounds and saturation counts include accepted saturation bridges. Principal-axis
endpoints and width statistics use only qualified foreground support because a
clipped bridge has no recoverable residual measurement.

`LengthPixels` is the projection span plus one. Mean width is support area over
length; maximum width is the perpendicular span plus one. Brightness and width
profiles use the configured fixed bin count at strictly increasing normalized
positions from 0 through 1,000,000. Brightness contains valid positive residual
only. `FragmentCount` is the number of disconnected foreground islands after
accepted saturation/collinearity grouping.

The implementation scans two borrowed frames and packed masks. Detector work is
limited to 10,000,000 pixels. It owns one byte state array, one integer traversal
array, foreground-support arrays, and bounded component/profile metadata. It
does not materialize or retain a full residual frame.

## Candidate outcomes and receipts

- `Produced` contains one or more measured candidates and a receipt.
- `NoCandidate` is a successful zero-candidate receipt.
- `Invalid` is a reason-coded malformed, incompatible, lineage, or mask result.
- `LimitExceeded` is atomic and contains no partial candidates or receipt.
- Cancellation throws before a partial outcome is returned.

Causal candidates are `Provisional`. A centered candidate is `Complete` only
when the caller explicitly records centered-context convergence; centered
background availability alone does not prove that all event-bearing neighboring
sources were discovered and excluded.

`transient-candidate-extraction-v1` is a strict, bounded receipt separate from
the already shipped candidate V1 schema. It binds literal options and their
identity, target detector identity, complete temporal-background descriptor,
hard/no-support/saturation mask checksums, convergence state, exact ordered source
references, algorithm versions, and exact candidates. Candidate V1 remains
unchanged.

Observation promotion consumes that receipt. It preserves candidate geometry,
features, provenance, extraction producer, recipe identity, and originating
candidate ID. Background artifacts are exactly the immutable source artifacts
whose background lineage disposition is `Included`; the background descriptor
itself is not represented as a raw/calibrated artifact.

Assessment input additionally binds each promoted observation to its candidate
event ID and exact extraction receipt identity. Every input event must equal the
assessment event, preventing mixed-event observations from being assessed as a
single track.

## Deterministic assessment

`transient-deterministic-assessment-v1` binds literal assessment options, their
identity, ordered observation IDs, prior assessment IDs, and the produced
assessment. Classification is conservative and follows this precedence:

Assessment work is bounded to 64 ordered observations, 256 prior assessments,
four background artifacts per observation, 64 samples per measured profile, 64
polyline points, and 32 reasons per prior assessment. The receipt remains capped
at 256 KiB.

1. Compact or single-observation detector-thin residuals support
   `SensorArtifact`; repeated stationary compact residuals strengthen it.
2. Broad, non-elongated residuals support `EnvironmentalArtifact`.
3. Elongated high-signal residuals with measured flare, fragmentation, or
   clipping support `Meteor/Fireball`.
4. Smooth persistent motion with measured brightness variation supports
   `Aircraft`.
5. Smooth persistent motion without that variation supports `Satellite`.
6. Other elongated evidence supports `Meteor/Meteor`.
7. Ambiguous evidence remains `Unknown` with an explicit limitation.

Observation count is supporting context only. A one-observation elongated track
can be assessed as a meteor, and a multi-observation track is not rejected as a
meteor solely because it persists. Every meteor assessment has a severity;
non-meteor assessments never do.

Assessment IDs, authority, creation time, and optional predecessor are supplied
by the caller. Reprocessing returns the unchanged prior history plus a new
assessment. Supersession is explicit, may target only an earlier unsuperseded
assessment from the same deterministic producer/version, and never overwrites
an independent producer root.

## V1 limitations

- Thresholds establish deterministic virtual behavior, not physical
  sensitivity or false-positive acceptance.
- The V1 polyline is a principal-axis segment; curved temporal reconstruction
  remains later work.
- RGB color discrimination, external aircraft/satellite catalogs, ML/AI,
  GPU/NPU acceleration, video/stream inputs, and volatile pre-trigger buffers
  are excluded.
- #119 owns the final approved scenario confusion matrix and durable
  non-regression baseline. #63 and #116 own edge and central orchestration.
