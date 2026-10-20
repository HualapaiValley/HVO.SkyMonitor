# Orbital virtual-camera engineering harness (#529)

This tool adapts shared orbital products to the existing, generic
`VirtualTransientScenarioDefinition` sky primitives. The normal
`VirtualSkyCameraModule` then runs its transient signal renderer, optics,
stellar exposure, sensor response/noise, CFA and readout. No production camera,
detector, host configuration, or orbital contract is changed. No orbital
propagation or coordinate/projection math is duplicated here.

The canonical input mode calls `OrbitalPredictionJson.ParseTrack` on each file's
**exact bytes**. Do not pretty-print, append a newline, or reserialize products
with another JSON writer. Root's #529 engine must supply products using
`OrbitalPredictionJson.SerializeTrack`. No private orbital DTO exists here.

The independent/reference mode consumes the retained Seattle ISS POC fixture,
which explicitly uses WGS72 **mode i**, Skyfield, pinned USNO EOP and DE421.
That fixture remains exploratory evidence; it is never recast as mode a or new
engine qualification. The offline `satellite_fixture.py` and pinned
`satellite-requirements.txt` are reference/conformance utilities, **not runtime
Python dependencies**. Neither command refreshes GP data. Obey the provider's
two-hour minimum and halt policy outside this tool.

## Build and inputs

Use the stable SDK from `global.json`:

```bash
dotnet build tools/HVO.SkyMonitor.VirtualSkyPoc/HVO.SkyMonitor.VirtualSkyPoc.csproj \
  --configuration Release -warnaserror
poc=tools/HVO.SkyMonitor.VirtualSkyPoc/bin/Release/net10.0/HVO.SkyMonitor.VirtualSkyPoc.dll
dotnet "$poc" satellite-config /outside/repository/settings.json
```

`settings.json` is the ordinary `CameraModuleConfig`: `observatory`, `module`,
`rig`, and `pipeline`. A caller can instead extract an existing module's
settings into that same type. The harness uses its observer, sensor, optics,
orientation, readout and night gain; it does not replace them with satellite
fields. The explicit optical profile controls exposure duration and seed. The
harness removes `fixedSceneUtc`, sets `fixedSequenceStartUtc` to the first
exposure, and adds the optional generic scenario only to injection/repeat
configuration. Existing transient scenarios/recurrence are refused. Other
module options remain configuration driven. The exported default is a reduced
640-square RGGB16 all-sky rig at HVO; for the retained Seattle reference change
only the ordinary observer settings to its declared 47.6062, -122.3321, 50 m,
`America/Los_Angeles`. This is an assumed ellipsoidal height, not a survey.

Create `/outside/repository/optical.json` with every field explicitly supplied:

```json
{
  "schemaVersion": "satellite-synthetic-optical-v1",
  "isSynthetic": true,
  "magnitude": -3,
  "angularWidthDegrees": 0.05,
  "redWeight": 1,
  "greenWeight": 1,
  "blueWeight": 1,
  "exposureSeconds": 5,
  "seed": 527,
  "lightCurve": []
}
```

Magnitude, width, RGB weights and light curve are **synthetic stimulus shared by
every supplied object**, never inferred from orbital identity, sunlight or
asserted physical type. Individual object brightness is not modeled. An empty
curve means constant magnitude. A nonempty curve is an ordered array of
`{"offsetSeconds": 0, "magnitudeDelta": 0}` knots spanning 0 through
`5 * exposureSeconds`; magnitude deltas interpolate linearly. Knots must occur
at supplied orbital sample times so the tool need not resample orbital
coordinates. Renderer integration still uses its existing spherical direction
interpolation and 64 temporal midpoint samples. The tool does not claim the
input chord tolerance establishes a raster-integration tolerance.

Supply a diagnostic font and its redistribution notice as adjacent files:

```bash
cp /usr/share/fonts/truetype/dejavu/DejaVuSans.ttf /outside/repository/diagnostic.ttf
cp /usr/share/doc/fonts-dejavu-core/copyright /outside/repository/diagnostic.ttf.license
```

The catalog root must resolve verified `hyg-v42-production`, package
`hyg-v4.2-p3-s2-r1`. Input files are bounded to 16 MiB each. New empty output
directories are mandatory. Initial limits are five equal contiguous exposures,
0 < exposure <= 20 seconds, at most 640x640 output, tight little-endian RGGB16
native12 codes, black 64, white 4095. The current presentation/detector converter
supports that bounded engineering lane; this is not a full sensor matrix.

## Canonical engine mode: exact input contract

Supply **1..4 distinct object groups**, each containing **five individual**
`OrbitalExposureTrackV1` files in capture order: exactly **5, 10, 15 or 20 input
files**. Each object's five files must be contiguous in the argument list; all
groups cover the same five exposure intervals. These are individual canonical
products, not an array or `OrbitalPredictionResult` wrapper. A single group:

```bash
dotnet "$poc" satellite-tracks /outside/repository/new-engine-run \
  /path/to/hyg-v42-production /outside/repository/settings.json \
  /outside/repository/optical.json /outside/repository/diagnostic.ttf \
  /outside/repository/track-01.json /outside/repository/track-02.json \
  /outside/repository/track-03.json /outside/repository/track-04.json \
  /outside/repository/track-05.json
```

For two objects, append the second object's entire five-file group:

```bash
dotnet "$poc" satellite-tracks /outside/repository/new-two-object-run \
  /path/to/hyg-v42-production /outside/repository/settings.json \
  /outside/repository/optical.json /outside/repository/diagnostic.ttf \
  /outside/repository/object-a/track-01.json /outside/repository/object-a/track-02.json \
  /outside/repository/object-a/track-03.json /outside/repository/object-a/track-04.json \
  /outside/repository/object-a/track-05.json \
  /outside/repository/object-b/track-01.json /outside/repository/object-b/track-02.json \
  /outside/repository/object-b/track-03.json /outside/repository/object-b/track-04.json \
  /outside/repository/object-b/track-05.json
```

In addition to strict shared parsing, the adapter requires:

- `Complete` status, full requested/covered interval, exact exposure endpoints
  and equal contiguous intervals matching the optical exposure duration. Every
  group must match the first group's five intervals exactly.
- `ConfiguredRig` basis; matching observer and rig-profile SHA256 from
  `RigProjectionContextFactory.CreateProfileHashSha256(settings.Rig)`;
  `GeometricHorizon`; the same geometry, model, observer, snapshot, normalized
  payload and selection policy across all groups. Each group must retain its
  own selected record, object identity, source dataset and epoch throughout its
  five products. Duplicate object identities across groups are rejected, even
  if different records are supplied for that object.
- `Projection` equals `CreateNative(settings.Rig)` for `NativeSensor`, or
  `Create(settings.Rig)` for `ReadoutView`. `Readout` equals the ordinary
  settings' resolved descriptor (or full native identity RGGB descriptor with
  CFA origins zero when no explicit readout exists).
- `ImageTransform` is the remaining shared crop/bin transform, with no added
  image mirrors/quarter rotation. Optical orientation/flips stay in the shared
  projection. `NativeSensor` uses its native crop/bin once; `ReadoutView` uses
  the identity remaining transform. For each nonnull `sample.Pixel`, the tool
  checks `Apply(ImageTransform, projector.Project(sample.Horizontal))` against
  the supplied emitted-image pixel to 1e-8 px. Supplied pixels themselves are
  never transformed a second time. Actual capture readout and logical timing
  are checked after every frame.
- `Timing.ExposureBasis = Estimated`, `ReadoutCompletedUtc = null`,
  `ReadoutBasis = Unknown`. Use an evidence reference such as
  `virtual-harness-logical-exposure-v1` and honest clock facts (for example
  `AstrometricClockFacts.NotSupplied`). These are modeled virtual intervals,
  not measured physical sensor timing. The request timestamp is the logical
  start; accelerated module acquisition does not wait five seconds.
- Use the canonical #529 mode-a model facts and qualified numerical inputs;
  the shared parser enforces its model/time contract. The harness does not
  independently establish their numerical or physical accuracy.

Every object's declared segments are injected/drawn as separate paths. Each
frame's private predictions include all supplied objects' segments, without
joining objects or interpolation across horizon/aperture/projection gaps or
budget gaps. Single-sample segments fail explicitly because they have no
integration support. Long continuous segments
split at 128 keyframes with a shared endpoint and half-open temporal support.
The existing 128-total-primitives/1,024-total-keyframes scenario bound and its
64-sky-track limit apply to the combined scenario across all objects; overflow
fails rather than silently dropping/downsampling samples. Diagnostic
predictions contain the photographed segments only, with no invented full-pass
arc. This does not support accepted-measured astrometry or #530 association.

## Retained independent/reference mode

```bash
dotnet "$poc" satellite-reference /outside/repository/new-reference-run \
  /path/to/hyg-v42-production /outside/repository/seattle-settings.json \
  /outside/repository/optical.json /outside/repository/diagnostic.ttf \
  /home/roys/.codex/artifacts/satellite-research-2026-10-19/iss-seattle-fixture.json
```

At five seconds, this selects the first 25 seconds of the retained 100-second
fixture. Exact supplied endpoints are required; reference pixels must agree
with the ordinary configured projector. Mode i, object/source identifiers,
original GP hash, reference hashes and original sampling claims are retained.
No new propagation is performed and no new mode-a qualification is implied.
The existing offline generator can be replayed using retained GP,
`finals2000A.all` and `de421.bsp`; it disables Skyfield download access. Its
original five-by-20-second fixture format and licenses remain intact.

## Detector boundary and evidence

All fifteen actual frames (five control, five injection, five repeat) retain
`.raw`, fixed gamma display `.png`, ordinary layout/time JSON and raw SHA256.
Repeat checks compare all raw bytes. Simulator metadata, injected settings,
original track/fixture files and separate geometric comparisons live under
`private-truth/`; no oracle corridor, identity, magnitude, label, source path or
simulator metadata crosses into the detector.
Every canonical input is copied to a unique `input-01.json` through
`input-20.json` path there (up to the supplied count), in group/capture order;
repeated original filenames across object directories cannot overwrite inputs.
The generic rendering primitives have only sequential `p-001` style IDs,
without source identities or object labels.

The actual `TransientDetectorInputFactory` converts ordinary raw
`ProcessingArtifact` pixels, layout, compatibility and estimated logical timing.
The actual `TransientTemporalBackgroundFactory` runs `CausalProvisional`:
frames 1 and 2 return `Missing` on cold start; frame 3 uses only N-2/N-1 (frames
1 and 2). There are no future-frame dependencies. Existing
`Linear16TransientExtraction` and the frozen exploratory thresholds are reused
unchanged. The centered four-neighbor old result is later refinement and cannot
satisfy a live deadline. Empty star masks are explicit; confusers from stars
remain possible. No durable worker/event pipeline is exercised.

`injected-diagnostic.png` paints supplied predicted segments in gold and every
measured component in magenta **on a separate derivative**. Raw captures and
detector inputs never contain these painted labels. Residual displays are
320-square for 640-square raw frames and use a fixed positive 0..500 ADU scale.
Endpoints have no inferred temporal direction. All candidates are compared to
private synthetic truth after extraction, without truth-based filtering.

`comparison.html` is self-contained, including base64 PNGs, settings,
provenance, measured counts and limitations. `validation.json` retains command,
input lineage/model, catalog identity, checksums/repeat outcome, changed-pixel
counts and one-run wall/CPU/allocation/working-set evidence. Timings include
rendering and output I/O; they are not a reproducible full-host performance
qualification. `sha256-manifest.json` hashes every other retained file,
including inputs and HTML; it deliberately excludes itself. Hash that manifest
separately when handing it off. Sources, binaries' identities and the explicit
font/license are retained.

Single- or multiple-object engineering fixtures cannot establish sensitivity,
false-positive rates, satellite identity, physical brightness, measured
association (#530), or five-second full-resolution host cadence/publication
(#1395). Multiple-object runs exercise only the supplied track crowding with a
shared synthetic optical profile. Constellation-like paths imply neither
detected identity nor constellation membership, population completeness or
qualified crowding performance. Overlapping paths can produce merged measured
components; comparisons do not assign those components to orbital identities.

## Source notices

The GP reference retains [CelesTrak](https://celestrak.org/NORAD/documentation/gp-data-formats.php)
and underlying Space-Track attribution. No dataset redistribution license is
inferred. Its [usage policy](https://celestrak.org/usage-policy.php) requires the
two-hour minimum and provider halt investigation. This tool performs no refresh.
USNO input: <https://maia.usno.navy.mil/ser7/finals2000A.all>. JPL input:
<https://naif.jpl.nasa.gov/pub/naif/generic_kernels/spk/planets/a_old_versions/de421.bsp>.

The optional offline generator's sgp4, Skyfield and jplephem packages use MIT;
certifi uses MPL-2.0; NumPy has core/bundled notices; Pillow uses MIT-CMU and
bundled notices. Full installed notices are retained at
`/home/roys/.codex/artifacts/satellite-research-2026-10-19/dependency-notices`.
Retain them when sharing that optional Python environment. Those packages are
not loaded by the C# runtime. The generator's single Vallado vector is not the
complete SGP4 qualification suite. Its pinned mode-i source branch is
`research/527-satellite-virtual-poc` at
`d97607b09f5433e3f0f1b8b5582286a37ea5ae43`; that branch is preserved unchanged.

## Retained exposure presentation example (#1393)

The `satellite-overlay` command uses the shared typed prediction converter and
the existing SVG/raster presentation renderers. It reads a checksum-pinned PNG
display derivative and canonical exposure tracks. It does not rerun capture,
detection, propagation or acquisition. The caller supplies the base/track
pairing; it is not authenticated historical-capture lookup.

```bash
dotnet build tools/HVO.SkyMonitor.VirtualSkyPoc/HVO.SkyMonitor.VirtualSkyPoc.csproj --configuration Release -warnaserror
dotnet tools/HVO.SkyMonitor.VirtualSkyPoc/bin/Release/net10.0/HVO.SkyMonitor.VirtualSkyPoc.dll satellite-overlay \
  /tmp/iss-presentation-v1 \
  docs/validation/orbital-prediction-v1/samples/iss-injected-03.png \
  7E30DF3A26A65FC7C80585A2A5F0392E25F8A80167AE5A84AB42EB40BD07B94C \
  tests/fixtures/orbital-prediction-v1/virtual-camera/tracks/iss/track-03.json
```

Use a new empty output directory. The output includes an interactive HTML
comparison, separate base/predicted/disabled PNGs, grouped SVG, source-envelope
bytes, typed payload and standalone overlay manifest, plus checksums and
validation. Disabling the layer must preserve every decoded base RGB byte.
The source-envelope SHA-256 must equal the typed payload's source identity.
Adjacent HYG attribution/license notices are copied and included in the HTML
and checksum manifest for the committed examples. Other input images retain
their original terms; the command does not grant a license to them.

For the paired Starlinks, use `samples/constellation-injected-03.png` with SHA-256
`64B0459DD9271AECC55F8156A693143B6BF3DCCD9B53C1226D5A44B53A3E6411`,
and supply `tracks/starlink-56838/track-03.json` and
`tracks/starlink-65283/track-03.json` from the same virtual-camera fixture root.
The source pixels retain explicit synthetic brightness. Prediction labels are
presentation derivatives, never detector inputs or measured identities.
