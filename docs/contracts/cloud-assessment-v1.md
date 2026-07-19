# Cloud Assessment v1

`cloud-assessment-v1` is the canonical image-derived cloud evidence envelope emitted
by the `cloud-assessment` recipe as one layoutless `Metadata` product. It is shared by
CameraAgent and LogicHost and does not represent a reconstructable camera frame.

## Evidence

The envelope contains:

- `status`, `quality`, ordered reason codes, nullable coverage, and fixed-point confidence;
- grid dimensions, transmission threshold, valid/cloudy region and sample counts;
- row-major per-region bounds, support, saturation, transmission, and classification;
- an optional `row-major-lsb-first` tile mask;
- current and clear-reference artifact identities and ordered source lineage;
- linear levels plus calibration, mask, sensor, and processing-profile identities;
- solar/precipitation state and the canonical environmental input identity;
- recipe identity and ordered algorithm identities.

All fractions use integer millionths in `[0, 1_000_000]`. `coverageMillionths: null`
means no defensible percentage. It is distinct from quantified zero cloud.

## Mask

The mask has one bit per tile. Tile ordinal is `row * columns + column`; byte index is
`ordinal / 8`, and bit position is `ordinal % 8`. A set bit means that a valid tile's
regression transmission is below the configured threshold. Invalid tiles and unused
padding bits are zero. Region records distinguish invalid tiles from valid clear tiles.

## Assessment

V1 requires one explicit compatible clear-reference image for quantified output.
Current and reference layouts, linear levels, integration/setpoint regime, and capture
compatibility identities must agree. Mono16 and RGGB16 are supported; RGGB values are
compared at the same CFA positions without demosaic.

Valid geometry is configured through normalized image-circle, radial-horizon, and
half-open exclusion rectangles. Each valid tile estimates multiplicative transmission
with a fixed-point linear regression slope, allowing an additive background term.
Coverage is accepted-sample weighted. Confidence uses valid reference support and
side-normalized separation from the threshold.

The production algorithm never accepts `CloudScenarioProvenance`, fixture labels,
simulator expected coverage, or truth masks.

CameraAgent's opt-in `CloudAssessment` step designates the reference through
`clearReferenceManifestPath`, a path relative to the configured CameraAgent storage
root. The loader requires a physical, reconstructable v2 manifest and verifies its
payload before execution. Omitting the setting produces explicit insufficient evidence.
Same-capture derivatives are rejected through source lineage.

LogicHost designates one reconstructable raw reference per device/rig through
`PUT /api/v1.0/devices/{devicePublicId}/rigs/{rigId}/clear-reference`. Automatic central
scheduling creates an assessment only when that designation is usable. Job creation
freezes the current and reference artifact IDs, a canonical capture-time environmental
snapshot (including an explicit missing state), and the resulting bound recipe identity.
Retries and designation or weather changes do not reselect those inputs.

When the canonical central preview and cloud assessment are both available, LogicHost
schedules `weather-cloud-overlay` with the exact preview and assessment artifacts and a
copy of the assessment's frozen environmental input. Layoutless assessment metadata is
verified from its persisted processing evidence and passed to the shared recipe without
inventing a camera-frame layout.

## Validation

Parsing rejects duplicate or unknown properties, missing required members, unsupported
schema versions, invalid ranges and dimensions, inconsistent region/mask bits,
coverage that disagrees with valid/cloudy sample counts, invalid source/calibration
lineage, and malformed environmental or algorithm provenance. Canonical serialized
payloads are bounded to 1 MiB.
