# Nominal offline causal evaluation (#530 preparation)

These additive commands exercise unchanged Processing input/temporal factories and
Imaging extraction. They do not implement production association or certify
measured geometry, sensitivity, accuracy, five-second cadence, Pi performance or
live acquisition. Existing `satellite-tracks`/`satellite-reference` behavior and
canonical products are unchanged. All published ISS, two-Starlink, Vanguard and
fade scenes remain exploratory/tuning inputs. No held-out set or thresholds are
frozen here.

```bash
dotnet build tools/HVO.SkyMonitor.VirtualSkyPoc/HVO.SkyMonitor.VirtualSkyPoc.csproj --configuration Release -warnaserror
poc_dll=tools/HVO.SkyMonitor.VirtualSkyPoc/bin/Release/net10.0/HVO.SkyMonitor.VirtualSkyPoc.dll
dotnet "$poc_dll" nominal-evaluate /absolute/public-raw /absolute/new-evaluation.json
dotnet "$poc_dll" nominal-compare /absolute/new-evaluation.json /absolute/public-predictions /absolute/new-comparison.json
```

The parent output directory must already exist. Reports use exclusive creation,
flush to disk and never overwrite an earlier report. Inputs must be quiescent
local files for the duration of a run. This is an offline allowlist boundary,
not an operating-system sandbox against a concurrent malicious filesystem writer.
Archive the executable/assembly hashes and commit with each experiment; report
bytes are deterministic for identical manifests, raw bytes and binary.

## Public raw package

The evaluator reads exactly `input.json` and each explicitly listed
`raw/<UPPERCASE_SHA256>.raw`, in supplied frame order. It never enumerates the
package or follows a path from a descriptor. The JSON document is at most 512 KiB;
all fields are case sensitive, duplicate/unknown fields and integer enum values
are rejected, including inside nested descriptors. No camera-module settings,
`CameraFrame.Metadata`, scenario configuration, expected identity, brightness,
seed, light curve, diagnostic PNG, labels, event exclusions, or prediction
corridor are accepted. Prediction files are inaccessible to the evaluation code
path. A later scorer may use private truth in its own process; no scorer is
provided by this increment.

Every file path component is checked for symlink/reparse points. Absolute paths,
backslashes, `..`, out-of-root paths and names containing `truth`, `scenario`,
`expected`, `label`, `metadata`, `diagnostic`, `brightness` or `seed` are rejected,
including in ancestor directories. Use a dedicated neutral public export root.
All manifest structure/layout constraints and all raw hashes/lengths are checked
before any detector conversion or pixel extraction. The report records the
exact files, byte counts and hashes opened by the evaluator.

The schema below is complete. Repeat `frames` entries in strictly increasing
source sequence order, keeping their actual IDs and gaps. Never renumber a drop.
`captureId` is the ordinary public capture identity, not a simulator object ID.
Hashes use 64 uppercase hexadecimal digits. Timing is explicit UTC with a
positive exposure of at most 60 seconds; the maximum adjacent start interval is
explicit, positive and at most 3600 seconds.

```json
{
  "schema": "nominal-causal-raw-v1",
  "maximumAdjacentStartIntervalSeconds": 5.001,
  "frames": [
    {
      "captureId": "00000001-0000-0000-0000-000000000000",
      "captureSequence": 1,
      "role": "raw",
      "path": "raw/<UPPERCASE_SHA256>.raw",
      "sha256": "<UPPERCASE_SHA256>",
      "recipeIdentitySha256": "<PUBLIC_RAW_RECIPE_SHA256>",
      "observationStartedUtc": "2026-10-20T13:03:32.287674Z",
      "observationEndedUtc": "2026-10-20T13:03:37.287674Z",
      "timingQuality": "estimated",
      "layout": {
        "width": 640, "height": 640, "strideBytes": 1280,
        "pixelFormat": "bayerRggb16", "byteOrder": "littleEndian",
        "sampleDepthBits": 12, "containerDepthBits": 16,
        "packing": "byteAligned", "cfaPattern": "rggb",
        "blackLevel": 64, "whiteLevel": 4095, "byteLength": 819200,
        "storedCodeTransform": "rightAlignedV1", "levelCodeSpace": "nativeSample"
      },
      "levels": { "blackLevel": 64, "whiteLevel": 4095, "saturationLevel": 4095 },
      "compatibility": {
        "rig": "<PUBLIC_RIG_IDENTITY>", "orientation": "configured-orientation-v1",
        "calibration": "raw-v1", "mask": "declared-aperture-v1",
        "sensor": "native12-v1", "setpointRegime": "fixed-v1",
        "processingProfile": "nominal-offline-v1"
      },
      "sensitivity": { "responseIdentity": "fixed-response-v1", "numerator": 1, "denominator": 1 },
      "masks": {
        "sky": "explicit-empty-v1", "horizon": "explicit-empty-v1",
        "obstruction": "explicit-empty-v1", "badPixel": "explicit-empty-v1",
        "star": "explicit-empty-v1",
        "imageCircle": { "mode": "circle", "centerX": 320, "centerY": 320, "radius": 313.6 }
      },
      "geometryIdentitySha256": null
    }
  ]
}
```

Only even dimensions 2..640, tightly packed RGGB16, right-aligned native 12-bit
codes and explicit 64/4095/4095 levels are supported. `layout.readout` must be
absent; this narrow descriptor describes the raw raster after readout. Full
sensor/readout/projection geometry can be pinned separately with
`geometryIdentitySha256`. Mono, full resolution, different levels/transforms,
display images and calibrated roles require a separately reviewed expansion.
`compatibility.locationIdentitySha256` is optional; the other seven compatibility
identities are required, nonempty, trimmed and at most 256 characters.

The five named persistent masks must be explicitly empty. The image-circle mask
may instead be `{"mode":"empty","centerX":null,"centerY":null,"radius":null}`.
A circle is in raw pixel-edge coordinates and reduced with the existing RGGB
mask operation. No arbitrary mask payload can encode an oracle corridor. Empty
star/horizon/obstruction/bad-pixel masks are research limitations retained in the
input report, not qualified real-camera treatment.

An optional geometry pin is the uppercase
`CaptureContractJson.ComputeCanonicalJsonSha256(OrbitalGeometryFacts)` of the
capture's **public capture-time** nominal geometry. Pin it from retained public
geometry facts, never from private labels or from a proposed match. Null pins
allow detection but withhold comparison residuals. Rig identity, raster size,
full geometry hash, readout-view coordinates and identity image transform must
also match the prediction. Other image transforms are explicitly unavailable in
this slice. The inverse `SourceToDetectorTransform` in each actual detector
descriptor maps component endpoints into raw coordinates; no RGGB factor is
hardcoded in comparison.

## Explicit raw export seam

Prepare `public-facts.json` manually from ordinary public capture facts using
the schema above. List every target, including controls and frames without a
detection. The following runnable exporter only fills `path`/`sha256` from the
explicit raw filenames supplied on its command line. It reads no simulator
metadata, makes no scene search and does not change existing products. Match
the CLI file order to the public facts' frame order; use a new destination.
The evaluator remains the authoritative strict validation gate.

```bash
python3 - public-facts.json /absolute/new-public-raw /absolute/frame01.raw /absolute/frame02.raw /absolute/frame03.raw <<'PY'
import hashlib, json, pathlib, sys
facts, destination, *sources = sys.argv[1:]
document = json.loads(pathlib.Path(facts).read_text())
assert document['schema'] == 'nominal-causal-raw-v1'
assert 0 < len(sources) == len(document['frames']) <= 64
root = pathlib.Path(destination)
root.mkdir(exist_ok=False)
(root / 'raw').mkdir()
for frame, filename in zip(document['frames'], sources):
    source = pathlib.Path(filename)
    assert source.suffix == '.raw' and not source.is_symlink()
    assert 0 < source.stat().st_size <= 640 * 640 * 2
    payload = source.read_bytes()
    digest = hashlib.sha256(payload).hexdigest().upper()
    relative = 'raw/' + digest + '.raw'
    target = root / relative
    if target.exists():
        assert target.read_bytes() == payload
    else:
        target.write_bytes(payload)
    frame['path'], frame['sha256'] = relative, digest
(root / 'input.json').write_text(json.dumps(document, separators=(',', ':')))
PY
```

## Outcomes and work bounds

At most 64 targets (at most 50 MiB raw total) are admitted. Each target goes
through `TransientDetectorInputFactory`,
`TransientTemporalBackgroundFactory(CausalProvisional)` and, only if a background
exists, `Linear16TransientExtraction`. The first two frames retain actual
`Missing` outcomes with null extraction. The preceding supplied slots retain
their original sequence IDs; drops cause factory `sequence-gap` failures until
two consecutive predecessors exist. Missing slots, temporal gaps, incompatible
geometry/profiles/response/masks preserve actual factory status, field, reason
and available lineage. No synthetic zero-detection result fills unavailable
context. No known-event exclusions or truth-dependent selection are performed.

Extraction options are fixed engineering settings, matching the prior POC:
minimum residual 20 ADU, minimum component 3 pixels / integrated signal 80 ADU,
maximum 32 candidates, 8 profile samples, 64 saturation-bridge pixels, 20000
foreground pixels, fragment gap 2 pixels and alignment cosine .98. These are
not sensitivity thresholds qualified by this increment. Extraction's actual
`candidateLimitExceeded` and limit reason survive, with all returned components.
Every target is attempted even after an earlier unavailable/limited result.

The report retains the exact public manifest/frame facts, input descriptors,
source-to-detector transforms, temporal descriptors/lineage, extraction options,
all measured components/profiles, and file-read hashes. It contains no pixel
arrays. Overall status says when targets were unavailable or limited, even
though all targets were attempted. An emitted report is bounded to 16 MiB;
oversized serialization fails before opening the output. A failed process is
not a completed evaluation. A write interruption can leave a partial file;
retain it as failed evidence and select a new output filename.

## Separate canonical prediction comparison

Only the comparison process reads `predictions.json` and explicit canonical
`tracks/<FILE_SHA256>.json` files. File SHA and canonical product SHA are distinct;
both are retained. Example:

```json
{
  "schema": "nominal-canonical-predictions-v1",
  "maximumSegmentComparisons": 8192,
  "tracks": [ { "path": "tracks/<FILE_SHA256>.json", "sha256": "<FILE_SHA256>" } ]
}
```

Use zero to eight distinct explicit products; an empty array records absent
coverage. Copy exact canonical track bytes, compute their uppercase file SHA256,
and name each copy accordingly. Each product is at most 1 MiB, 1024 samples and
16 segments and must pass `OrbitalPredictionJson.ParseTrack` byte-exact parsing.
No source fetch, orbital recomputation or private truth occurs in comparison.
The segment-comparison budget is explicit, 1..8192. Exhaustion retains all
components/hypotheses, with `comparison-work-limit` on withheld residuals and an
overall partial status. Ordering follows frame order, extraction component order,
track-file hash order and canonical segment order; no identity wins by ordering.

Every component has every supplied prediction alternative, including ineligible
ones with reasons. Per-frame coverage is retained even if no component exists.
Numerical residuals require exact exposure interval and compatible pinned nominal
geometry. Partial products use only their actual returned segments; no gaps are
joined. Each segment reports RMS/maximum perpendicular distance and the minimum
of the two unordered maximum endpoint errors. There is no acceptance threshold,
best-identity selection, confidence ranking, automatic rejection or meteor label.
Single-streak direction/angular speed are explicitly unavailable. Full original
canonical products retain object identity, snapshot/record/selection provenance,
time/clock/geometry facts, staleness, population exclusions and uncertainty.
Unknown clock/geometry and absent/stale/partial coverage remain explicit.
No-data means **unidentified within available coverage**.

## Focused checks

The tool helpers are linked into the existing Processing test assembly without
a tool/host project dependency. Unit tests exercise the exact file-read boundary,
malformed/forbidden input, genuine startup/all targets/gaps/incompatibility,
component retention, transform offsets/unordered endpoints, canonical zero/one/
multiple alternatives, actual bounded-engine partial output, deterministic replay
and work/immutable-output bounds. Synthetic raw squares in these control tests
check orchestration, not orbital accuracy or optical detectability.

```bash
dotnet build tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj --configuration Release -warnaserror
dotnet test tests/HVO.SkyMonitor.Processing.Tests/HVO.SkyMonitor.Processing.Tests.csproj --no-build --configuration Release --filter 'FullyQualifiedName~NominalCausalEvaluationTests|FullyQualifiedName~TransientTemporalBackgroundTests|FullyQualifiedName~TransientDetectorInputTests|FullyQualifiedName~TransientDetectorInputMetadataTests'
```

Actual retained VirtualSky raw replay, independent review, classifier-selected
affected gates and the `development/v1` protected profile remain root-owned.
#528 live snapshots and accepted #523/#526/#1187 measured geometry remain required
for production consumption; #530 production assessment/persistence/review and
#1395 integrated cadence acceptance are not delivered here.
