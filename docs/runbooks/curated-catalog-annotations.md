# Curated expected-position annotation

Issue #525's reduced delivery uses the installed, approved composed HYG/OpenNGC
catalog. It draws expected catalog positions and approximate extents over an
existing image. It does not detect objects or synthesize their light.

The `messier-bright-ngc-v1` preset resolves M1–M110 plus NGC7000, NGC1499,
NGC6960, NGC6992, NGC7635, NGC0869, NGC0884 and NGC7789 once per immutable
catalog instance. Disputed or ambiguous aliases are omitted. Canonical identities
are deduplicated; a new catalog instance gets a new cache. The plan exposes
unresolved aliases. Its object cap applies before view-dependent projection,
with Messier-number/ID order determining the bounded selection. The scene
reports candidates omitted by that cap. This is a curated subset, not catalog
completeness or a brightness/detection threshold.

The default ceiling is 128 objects, 32 basic footprints and 24 collision-bounded
labels. The producer never queries the full catalog or reads source contours
per frame. Unknown V/B magnitudes, axes and position angles stay nullable.
Equal supplied axes support a circle without an orientation; unequal axes need
a supplied PA. Missing orientation/size produces a symbolic size or unknown
marker. Footprints support semi-major axes up to 10 degrees; larger extents
fall back to a symbolic marker. Geometry uses the existing shared spherical
footprint/projector, including J2000 PA precession. These display outlines are
approximate, not arbitrary-contour certification.

## Explicit capture configuration

Use a composed local catalog and enable a retained `ProjectedScene` step. Set
`CuratedAnnotations` in that step's options for non-VirtualSky acquisition, or
in the VirtualSky module options:

```json
{
  "CuratedAnnotations": {
    "Enabled": true,
    "MaximumObjects": 128,
    "MaximumFootprints": 32,
    "MinimumGlyphPixels": 8
  }
}
```

The existing `Annotation` step must consume the retained projected-scene product
(`RequireProjectedSceneDependency: true`). VirtualSky refuses a curated capture
without its retained scene staging route. Defaults omit this option; no catalog
installation or automatic default replacement is performed.

Real-image staging uses acquisition midpoint, effective deployment location,
actual layout and the capture rig. Curated geometry is then retained with its
scene/catalog/time/calibration/source identities. Annotation and archived replay
consume those bytes; they never apply the current rig to an old image. Optical
projection and ROI/binning/mirror/quarter rotation occur before the explicit
preview scaling. VirtualSky uses the same producer before readout and excludes
annotation options from its raw renderer's seed. Annotation changes derivatives,
not borrowed raw or base image bytes.

The minimal `projected-scene-v3` extension carries a separately typed curated
section, preserving nullable facts without turning objects into magnitude-zero
stars. Its contract is `projected-deep-sky-curated-v1`; source contours are
rejected. v1/v2 serialization and HYG-only behavior remain supported. Staging,
processing manifests, archived replay selection and central readers accept v3
together. Ordinary star/Sun/Moon annotation excludes v3's curated footprints,
which are rendered once with dashed expected-position styling and a visible
`CATALOG POSITIONS - NOT DETECTIONS` legend.
The actual annotation recipe places that caption after existing top-left
metadata and reserves the legacy star-label rectangles. Shared presentation
groups keep the same 64-text-block ceiling, reserving a caption block and
spending only the remaining label budget on curated objects.
The caption's two lines, stellar markers, solar footprints and curated primitives
share the final layer's 4096-primitive ceiling. Geometry falls back to a glyph or
is omitted in deterministic priority order when its complete outline cannot fit.
The bounded footprint sampler breaks unresolved projection discontinuities;
it never substitutes a chord through the fisheye image interior.

Central v3 annotation jobs retain the scene as an authenticated auxiliary
artifact, including its capture identity and frozen recipe/source lineage.
Local execution and remote-runner preparation use the same inputs. Released
v1/v2 selectors keep their legacy annotation mapping and JSON shape. A pre-fix
v3 selector missing the payload schema binding is rejected by the strict scene
resolver; schema compatibility does not silently upgrade a frozen job.
If an emitted-image crop removes every admitted object, v3 retains an empty
curated selection. It does not invent a center, magnitude or extent to keep the
schema alive. A view with no curated admission in the first place keeps v1/v2.

## Validation and cost

Focused tests cover cached selection, alias deduplication/dispute, nullable
fields, independent basic geometry, transformed/retained reconstruction and
the actual source-bound annotation recipe. Existing frozen scene, staging,
replay and host tests remain applicable. Runtime results belong to their exact
source fingerprints; no historical broad PR result is new-candidate evidence.

The private Astronomy scene serializer orders immutable type metadata by ordinal
JSON property name. It recomputes canonical content on every call; it does not
cache scene hashes or trust caller-owned collections. The UTC scalar retains the
original primitive DOM formatting/escaping; the typed date writer alone bypasses
string encoding and would change the offset's canonical bytes. Twelve added Unit cases
compare the original DOM pipeline's bytes and identities for 500-star v1/v2/v3
scenes, circle/ellipse/unknown extents, escaping, nullable/default/numeric fields
and array order, and reject caller mutation. Three cases cover UTC offset and
fractional-second encoding. A graph check rejects newly reachable dynamic JSON,
dictionaries or non-scalar custom converters. These twelve cases are
declared in the owned inventory; whole-solution discovery remains a separate gate.

The Processing-private payload serializer also uses immutable ordinal property
metadata, retaining the original strict parser, snapshots, structural validation
and live identity checks. Its tile-mask base64 scalar keeps the old DOM string
encoding, including escaped `+`; the complete payload no longer needs a DOM.
Twelve added Unit cases compare the independent old pipeline's full bytes and
hashes for all three schemas, primitives/styles, mask scalars, numeric/text
encoding, array order and a 4096-segment payload. They also check mutation
rejection, defensive copies, strict parsing and the closed typed graph. These
are declared cases; actual execution results are bound to the tested head.

Full-envelope warm cost acceptance remains **pending**. Failed attempts' partial
measurements do not qualify the complete three-view envelope. Before running a
focused measurement or the full candidate gate, the owner supplies the exact
commands, source identity, bounded scene/preset/image envelope and acceptance
criteria to the coordinator for allocation. Catalog first-open/load/index cost
and composition/encoding cost are reported separately from warm projection.
First-open is not a claim of cold OS storage caches. Full F7 stays on #1188.

## Raw identity and executable graph compatibility

VirtualSky raw scene identity uses the original default typed JSON serialization,
excluding only `CuratedAnnotations` through private immutable serializer metadata.
It does not roundtrip options through a JSON DOM: typed UTC offsets, nested
transient/cloud parameters, enum converters, property order and scalar escaping
remain part of the original byte/hash contract. Three bounded parity cases use
an independent default-typed oracle with annotation settings null, disabled and
enabled. Existing raw exposure checksum goldens are unchanged.

The W6 desired graph, rig, processing, schedule and local profile stay pinned.
Its effective executable identity deliberately advances from `9C37FFDB...` to
`EFBD0878...`: scene-presentation and environment-presentation now explicitly
admit projected-scene v1/v2/v3. The producer declaration remains the v1 family
root and cloud-presentation retains its cloud-assessment contract. The Unit test
reconstructs only those two node preimages and requires removal of only v3 to
recover the complete historical effective hash, leaving all twelve other nodes
unchanged. The matching current Manual pin advances without running a Manual
workload. Historical graph/evidence records retain their original hashes.

Expanded reader capability can supersede an old frozen/named executable graph
revision. Existing replay still rejects mismatched frozen runtime identities;
neither a current configuration nor schema-family compatibility rewrites a
retained revision. Old scene bytes remain supported by the strict readers.

The retained 55bd complete Unit TRXs contain 6540 cases, including 23 explicit
NotExecuted results even though the raw summary counter reports notExecuted=0.
Both records stay unchanged. They reconcile Astronomy515, LogicHost685 and
CameraAgent3431; the three new raw-identity parity rows declare CameraAgent3434
and total Unit6543. These declarations combine retained actual discovery with
new source counts; subsequent affected execution is separate evidence.
The initial-review correction adds three Astronomy cases and two Processing
primitive-budget cases, declaring Unit6548. Three actual-central-job cases
(legacy, graph and remote-runner paths) declare Integration753. The normal
performance project has seven Manual methods, and the repository aggregate is
Manual217. These new declarations require the separately allocated execution
and focused normal-project Manual discovery; source inspection is not a pass.

The focused cost entrypoint is the Manual-only
`CatalogPerformanceTests.CuratedAnnotationsWarmProjectionEvidenceAsync`.
It uses the approved HYG 4.4/OpenNGC package, pins all 117 canonical preset IDs
(M102 remains disputed), and interleaves disabled/enabled component operations
with five warmups and 100 measurements. The proposed measured envelope is
484×304 and 1920×1080 perspective, plus 3552×3552 equidistant, at the default
128-object/32-footprint/24-label ceiling. Projection, layer construction and
Mono8/RGB24 composition have separate timings, allocations, CPU and hashes;
verification CPU is outside the operation interval. This is an annotation
cost check, not a full capture benchmark or native qualification.

## Source attribution

The scene/transform/reader extensions, J2000 position-angle helper and portions
of the shared presentation producer selectively reuse historical #1185 source
at `7827ffceaa73d477cc4fea0a990aa63c9b90f382`, retaining its original Claude
provenance. The bounded curated cache/producer, reduced contract, capture and
annotation wiring, new tests and this document are authored by OpenAI Codex
`gpt-6.1-sol/xhigh`, owner `t3code:codex:home-dev-02:e72fb39e` in
`/home/roys/review-wt/525-curated-annotations-v1`. The included roadmap scope
amendment is coordinator-authored OpenAI Codex `gpt-6-astra/xhigh`.

Expanded presentation/source contours (#1192/#1191), measured association,
calculators, raw DSO rendering and native/full-pipeline campaigns remain
separate. Material defects reachable in this delivery still block it.
