# Named orbital passes in ordinary VirtualSky captures

The opt-in `namedOrbitalPass` VirtualSky module option takes an absolute local
`packagePath` and an uppercase `packageSha256`. Keep the package and its files
in one operator-owned directory. A profile without this option uses the
ordinary VirtualSky renderer and its existing defaults.

The package is a JSON object with these fields.

| Field | Contents |
| --- | --- |
| `schemaVersion` | `virtual-sky-named-pass-v1` |
| `epochUtc` | UTC epoch for synthetic optical knots and any additional transient controls |
| `snapshotManifest`, `snapshotRecords` | File references to an immutable orbital snapshot and its normalized records |
| `earthOrientation`, `leapSeconds` | File references to the exact finals2000A and IETF leap inputs from the track model |
| `objects` | One to four objects with `catalogNumber`, `recordSha256`, `optics`, and an ordered `tracks` list |
| `allowStaleDiagnostics` | Optional. Match the capture prediction selection policy. Defaults to false. |

A file reference contains `path` relative to the package and uppercase
`sha256`. Each track entry has a `track` file reference to an individual
canonical exposure track, an `exposureStartUtc` pin, and a linear
`transmission` between 0 and 1. An `optics` reference points to a
`satellite-synthetic-optical-v1` file such as the retained
`optical-5s.json`. Magnitude, angular width, channel weights, exposure
duration, and optical light-curve knots are explicit. Transmission zero
deposits no signal, including when orbital illumination is unknown.
Positive transmission attenuates the synthetic magnitude before rasterization.
Light-curve knots must coincide with the track sample times. The optical seed
is added to the ordinary camera noise seed for named captures. Optional
unrelated transient controls use the same epoch.

Describe one observer, configured rig, optics, and delivered readout in the
tracks. Prepare canonical tracks for the exact rig profile, including
cadence, readout, CFA and orientation. The module validates the snapshot,
selected object record, time model, intervals, and projected samples once at
preflight and again at initialization. The capture loop integrates anonymous
segments over the observed shutter interval before sensor response, noise,
CFA sampling and readout. Runtime shutter timing is independent of any
requested scene timestamp. Historical inputs need an isolated test clock
covering their UTC windows. The installed runtime uses its current UTC and
renders no named signal outside a package window. A fixed sequence is
incompatible with a named package. An optional fixed celestial UTC freezes
the stellar field only. The short regression uses this option so the
independently projected catalog masks stay compatible across each causal
window while the named trails move.

Enable `ProjectedScene` for durable capture admission. For prediction and
presentation, configure the existing `CaptureSatelliteTracks`,
`CaptureSatelliteLayer`, `Preview`, and `OverlayManifest` graph steps.
Named captures carry the validated snapshot, time pair, selection policy and
expected record hashes in their raw scene provenance. The prediction step
uses that binding after admission and calculates paths independently for the
observed shutter interval. Explicit graph `pinnedInputs` may confirm the same
snapshot, time pair and expected records. Conflicting options or a different
selected record produce an unavailable prediction and a retained reason.
Configure the local snapshot and time pair through CameraAgent’s orbital inputs.
The package accepts the installed resolver's input bounds. The selected time
pair must cover the observed shutter interval. Historical replays can use a
checksum-pinned local override containing supported rows for that interval.
The prediction step verifies the exact sources and reports an unavailable
result if a pinned input cannot be resolved. A capture-specific predicted layer is generated
from the reported interval and rig. Enable the separate Edge transient
worker to obtain measured trails. The worker consumes raw evidence and
independent masks. Tests can compare its output with the private optical
scenario after measurement.

## Reproduce the short sequence

Use a new output directory and an installed, verified HYG 4.2 production
catalog. The regression uses the original HVO projection and regenerates
capture-profile tracks for the short cadence. The two-element snapshot is
derived from the retained #529 facts with a distinct local raw-carrier
identity. The test uses a simulated clock and ordinary CameraAgent capture,
raw admission, the installed time resolver with the checksummed October
fixture pair, local processing and read services. The November bundled pair
has rapid-only rows for the fixture interval. The runner delays its first shutter by
one millisecond to verify the distinction between requested and observed time.

```bash
HVO_NAMED_PASS_EVIDENCE=/path/to/new-evidence-directory \
HVO_NAMED_PASS_HYG42_ROOT=/path/to/hyg-v42-production \
DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests --configuration Release \
  --filter 'FullyQualifiedName~NamedOrbitalPassCaptureTests'
```

| Slots | Signals and expected evidence |
| --- | --- |
| 101–102 | Causal history builds. Slot 102 includes a named predicted pass during warm-up. |
| 103–104 | 56838 produces a moving measured trail. 65283 has predicted geometry with zero synthetic transmission. |
| 105 | The moving 56838 trail and a spatially independent streak occupy the same raw capture. |
| 106 | Both named predictions remain available and the worker completes assessment with no candidate. |

Each sensor run keeps its exact configuration, private inputs, raw sidecars,
journal and processed products, capture-keyed status and extracted geometry,
and private scores in the evidence root. The slot numbers are labels. Use the
committed capture and artifact IDs and measured source transforms from each
slot for downstream registration and overlay tests. The Bayer profile covers
full-frame, even-sized RGGB16 with origin `(0, 0)`. The detector uses a
half-resolution cell-average representation and records that mapping. The
Mono16 profile uses delivered-image coordinates. Cross-base registration,
odd-origin Bayer preview qualification, production orbital correlation and
full-resolution cadence qualification are tracked in #1922, #1928, #530 and
#1395.

The original ISS and Starlink harness remains available through
[VirtualSky PoC](../../tools/HVO.SkyMonitor.VirtualSkyPoc/SATELLITE-POC.md).
