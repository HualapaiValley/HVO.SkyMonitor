# Rig installation horizon obstruction v1 (#1394)

Contract frozen before consumer implementation, 2026-11-08. Software/virtual
acceptance only; physical survey accuracy is outside this contract.

AgentCore owns the bounded transport-neutral manual/synthetic angular profile.
It belongs to an installed rig, never to a lens or a star/orbit catalog. A stable
installation ID, site/revision hash, geometry hash, profile ID and increasing
revision identify it. Geometry covers sensor, pointing/roll, optical calibration,
parity and readout ROI/binning. Moving an installation requires a new installation
ID or explicit revalidation even if its coordinates are unchanged. Coordinates
are not embedded in the coordinate-free local profile. Site identity hashes
latitude, longitude and elevation together with the retained location ID/version
when versioned location evidence exists. Replacing a site or advancing its
revision requires revalidation even at identical coordinates. Explicit legacy or
standalone synthetic inputs without location history use only the coordinates.

Azimuth is degrees eastward from true north, normalized to [0,360). Samples are
strictly ordered, unique azimuths; 360 is represented by 0. Elevation is geometric
altitude in degrees above the local astronomical horizontal plane, [-90,90].
At most 720 samples and a maximum interpolation gap in (0,90] degrees are allowed.
A null sample elevation marks unknown terrain. Interpolation is linear across
north wrap only when both endpoints are known and the circular gap is within
the declared bound. An exact known knot is usable even next to a gap. Empty,
single-knot surroundings, null knots and wider gaps are unknown. At or below
the interpolated elevation is obstructed. Below geometric zero is independently
ineligible. Unknown is never evidence of clear sky. V1 has no polygon or imported
image-mask representation; neither automatic segmentation nor cloud inference
is supported.

Astronomy owns angular evaluation and compatibility checks, using existing
projectors and the canonical crop/bin/mirror/quarter-rotation transform. Outside
projection/sensor support is distinct from unknown terrain. Imaging samples pixel
centres and returns separately named bit planes for obstruction, unknown and
outside-domain. Generation is bounded to 16,777,216 output pixels, cancellation
is checked by row, and there is no persistent or unbounded global mask cache.
Products bind source, profile content hash, rig geometry, calibration, projection,
transform and mask checksums. Reusing incompatible geometry fails closed.

Processing owns source-bound mask and predicted-point eligibility products.
A star, satellite or meteor direction uses the same angular test. Display segments
also test intervening horizon knots and interval midpoints using a declared
linear-altitude, shortest-azimuth approximation, so clear endpoints cannot bridge
an obstruction peak or an unknown gap. This is a display eligibility model,
not a physical trajectory guarantee; these are
eligibility facts, never detection or actual visibility assertions. Host consumers
retain raw pixels and predicted tracks. Hard detection eligibility is the union
of fixed obstruction, unknown obstruction terrain, sensor/image-circle bounds,
geometric horizon, defective-pixel/calibration exclusions, saturation and existing
rectangular exclusions. Star rejection remains separately identified. Dynamic
cloud evidence belongs to #1004 and is never relabelled fixed obstruction.
Legacy rectangular exclusions keep their existing half-open coordinate behavior.
An absent profile preserves the baseline geometric-horizon behavior and supplies
no obstruction-clear assertion; an explicitly active empty profile is unknown.

CameraAgent owns persistence and UI through existing immutable named rig and
capture-profile revisions. Preview writes nothing. Saving appends a candidate rig
revision, and the ordinary compare/preview and explicit restart-only stage flow
activates it; this work never restarts a host. Prior rig revisions remain readable
and can be explicitly restaged for rollback. Archived captures use their retained
configuration, never a current mutable obstruction pointer. Geometry/site changes
require explicit revalidation (a newly bound revision); incompatible old profiles
remain present for diagnosis and exclude rather than silently align pixels.
No new DB schema/layout or recovery backend is needed (#1607/#1615 keep-current
primitives). Host UI and persistence do not move into shared libraries.

Regression gates cover wrap, gaps, crossing boundaries, all projector families,
ROI/bin/mirror/rotation, invalid data, empty profiles, content identity, revision
history/rollback, rectangular masks and full-resolution bounded cost.

Capture satellite products retain every original track and attach authenticated
sample and segment eligibility. The current capture overlay conservatively
withholds an entire track if any sample/segment is ineligible. Planning arcs
split at ineligible samples/segments and show the profile limitation explicitly.

Star/constellation presentation consumes a source-bound eligibility view of the
original retained scene. The effective, native sensor, or native ROI projection
must exactly match the retained calibration and installation geometry. Native
views must also carry the configured readout crop/bin relationship; recorded
post-readout mirrors and quarter rotations are inverted before angular testing.
ImageRegistered scenes are supported when those checks pass. Incompatible views
remain in retained evidence and provide no eligible annotation. Atmospheric
refraction is removed over the supported above-horizon domain before eligibility.
Both scene-layer aliases and flattened annotation use this check during archived
execution. Layer product identity binds the horizon view even when all primitives
are excluded. Sky-map star selection applies eligibility before its brightness
bound, and truncation counts eligible stars.

Editor previews render actual fractional sample knots and only supported linear
intervals, split at north. Isolated known knots are points; null or oversized
intervals stay unknown. Capture policy and exposure-envelope edits do not change
installation geometry or prevent horizon preview/save. Calibration candidate reuse
requires the current horizon content identity; a new candidate can reuse optics
equipment while retaining an incompatible horizon for explicit revalidation.

Curated deep-sky composition consumes a frozen `horizon-curated-selection-v1`
selection of at most 128 sorted object IDs. It binds the retained scene identity
and the evaluated horizon-view identity (profile, geometry and calibration).
Known footprints require clear supported points and segments; glyphs require an
eligible source position. Both scene-layer aliases use this selection, and
flattened processing carries it as canonical auxiliary JSON through the existing
replay transport. Its hash participates in recipe identity. Wrong scene, object,
schema or payload identity fails closed; an empty selection draws no catalog
objects or object labels. The scientific caption remains. Retained scene bytes,
source identity and raw pixels are preserved, and no-profile output identities
remain unchanged.
