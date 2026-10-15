# CameraAgent measured transient presentation V1

Issue #1005 adds an optional measured trail layer to a retained structured
presentation. The operator's acceptance clarification is recorded in
[#1005 comment5991268350](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1005#issuecomment-5991268350):
show measured trails, label positional uncertainty unavailable, and draw no
uncertainty band. Trail width and classification confidence are not positional
uncertainty. The raster and SVG caption uses the shared pinned font and fits the
source canvas; rasters too small for its minimum glyph size retain the disclosure
in the operator control and immutable evidence. This feature performs no orbital
correlation.

## Evidence and eligibility

`cameraagent-capture-trails-v1` is a bounded CameraAgent projection of immutable
local evidence for one exact raw capture. It reads at most 33 indexed candidate
references to detect overflow and accepts at most 32 trails. Candidate window
membership alone never makes a neighboring frame's trail belong to the capture.
The geometry's own evidence ID must resolve to that capture's raw artifact,
content checksum, recipe identity and exact recorded exposure bounds. Missing,
malformed, ambiguous or unsupported evidence disables the measured layer.

The V1 detector descriptor can be recovered from retained source layout,
compatibility, source evidence, linear levels and extraction saturation-mask
checksum without reading image pixels. CameraAgent's supported edge producer
uses the retained white level as its saturation threshold. The recovered
descriptor must reproduce the complete retained detector input identity before
its mapping is accepted. A different or unrecoverable producer descriptor is
unavailable; dimensions never determine a transform. Supported V1 Mono16 is
identity mapping; RGGB cell averaging has half-scale detector coordinates.

The drawing producer accepts 2–256 finite, non-degenerate points per trail and
maps detector pixel-edge coordinates back to their authenticated source grid.
Its two-pixel display stroke is a style, not measured width or uncertainty.
Provisional trails are dashed; confirmed meteor trails are solid. Rejected,
review-needed and other validated events retain truthful state labels and
links to their candidate evidence. A confirmed meteor requires a finalized
Validated event, the exact corresponding observation, and active Authoritative
assessments that agree on Meteor classification. Delivery acknowledgements do
not confirm a meteor.

## Compatible presentation bases

The base must be a reconstructable packed raster with the same source grid,
capture, recorded exposure bounds, rig, orientation, sensor and location.
Membership is checked through at most eight immutable processing lineage hops,
with at most 100 source references per admitted window. Only pinned producer
and algorithm versions known to preserve pixel coordinates are accepted:
full-size fixed/encoded preview, unregistered linear arithmetic rolling mean,
no-correction row normalization, and per-pixel reference calibration. Calibration
follows its first canonical science source; master references cannot establish trail geometry. Unknown
versions, registration, resize, mismatched timing and opaque lineage disable
the layer. A rolling base's accumulated integration is distinct from the
linked candidate's recorded source exposure.

Compact projected scenes remain their exact retained canonical products. No
scene or measured trail is reconstructed from current catalogs or lane counters.
An absent original structured manifest remains explicitly unavailable.

## Canonical composition and saved lineage

The capture-time `overlay-manifest-v1` product is immutable and retains its
unique original selector. A supplemental publication stores three typed
metadata products: `capture-trail-evidence-v1`, the shared versioned presentation
layer payload, and `capture-trail-composition-v1`. The latter's wire payload is
a canonical OverlayManifestV1 containing the original layers plus the measured
layer. Its distinct durable product schema avoids ambiguous original-manifest
enumeration.

The evidence snapshot binds the original manifest, complete source manifest
identity, ordered geometry/detector descriptors, candidate/extraction and
assessment/finalization identities and state. It is limited to 1 MiB. All three
products have deterministic identities and durable source lineage. Publication
uses the existing storage lifecycle lock, atomic metadata publication and
completed-node journal transaction. Retrying the same snapshot reuses its
validated products. An unchanged composition lookup is metadata-only; new
publication validates the base and retained layer products once.

Grouped SVG and packed raster materialization consume the same manifest,
payloads and selected layer identities. A frozen save request includes the
shown manifest identity; changed evidence yields Conflict instead of silently
substituting a different stack. Saved products retain the composed manifest and
selected layer lineage and remain immutable when later evidence changes.
Legacy save requests continue to use the original manifest and cannot select a
supplemental identity implicitly.

## Operator and authorization boundaries

Read metadata and SVG use OperationsReadV1. Save commands use OperationsMutateV1,
the authorized owner identity and the existing API antiforgery boundary. Raw
paths and internal errors are not exposed in unavailable states. API timestamps
and durable contracts remain UTC; operator timestamps use the observing-site
time helper, including its labeled UTC fallback.

Set `CameraAgent:ArtifactRead:MeasuredTransientTrailsEnabled=false` to disable
measured-trail queries and supplemental publication, including historical captures.
The original retained presentation remains available and reports the configuration
opt-out explicitly. This is independent of detector mode: turning detection off
does not invalidate already retained evidence.

The capture-addressed SVG URL uses private, no-cache revalidation because later
evidence can change its immutable composition. An unchanged checksum supports
304; an old ETag receives the new body and checksum after evidence changes.
The URL must not advertise immutable caching.

The measured layer starts off. Explicit evidence refresh preserves opt-out and
other selected layer kinds across immutable identity changes. Capture generation
checks prevent late results and saves from attaching to another capture, and the
full-screen view retains its existing frozen-image behavior. The SVG cache stores
no source manifests or geometry snapshots; fresh evidence is attached to each
returned presentation without expanding the cache's byte budget.
