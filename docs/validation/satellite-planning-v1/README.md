# Frozen satellite planning UI evidence

`SatellitePlanningVirtualEvidenceTests` is an explicit **Manual** export, separate
from the Unit lane. It submits the actual `SatellitePassPlanner` form and clicks
its pass-selection buttons in bUnit. The production authenticated UI adapter,
operations-read authorization policy/handlers, `CameraAgentSatellitePlanner`, real
orbital engines, shared SkyMap context and rig projection execute together.

The retained inputs are the existing `six-facts` and `two-starlinks` immutable
snapshots, `predicted-2026.finals2000A`, `leap-seconds.list`, and the existing
Seattle/HVO virtual-camera settings under `tests/fixtures/orbital-prediction-v1`.
Nothing is fetched or regenerated. The examples search one hour starting at
2026-10-20 12:55 UTC for ISS 25544 at Seattle and 03:55 UTC for Starlinks 56838
and 65283 at HVO. Each returned object is selected for its actual full pass arc.

Run one scoped build/test at a time with the pinned SDK, from the repository root:

```bash
dotnet build tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --configuration Release -warnaserror -m:1
HVO_EVIDENCE_REVISION="$(git rev-parse HEAD)" \
HVO_SATELLITE_PLANNING_EVIDENCE_DIR=/absolute/path/to/new-empty-export \
DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj \
  --configuration Release --no-build -m:1 \
  --filter 'FullyQualifiedName~SatellitePlanningVirtualEvidenceTests' \
  --logger 'trx;LogFileName=satellite-planning-real-ui.trx' \
  --results-directory /absolute/path/to/test-results
```

The output directory must be empty; keep test results outside it. Capture the
command, UTC start/end, exit status, build/test logs, TRX, commit and uncommitted
file hashes alongside the export. A test run validates those recorded source
bytes, even if its final commit is created afterward.

The export retains:

- Canonical pass and full arc bytes, search/detail responses, request logs,
  source hashes, observer/rig identities, real model/time facts and output
  checksums. A product's canonical identity and its containing file's byte hash
  are separate fields.
- Actual component markup plus self-contained dark HTML with the compiled
  `HVO.SkyMonitor.CameraAgent.styles.css`, theme and Bootstrap inlined. Compiled
  scope attributes are checked against rendered markup. The wrapper makes the
  recorded controls inert and identifies the page as frozen evidence.
- Selected ISS and both Starlink views, their full-sky/configured-rig SVGs, and
  independent path/label/rig toggle states. Toggling must not call the planner.
- Repeated exact-parent selection preserving canonical arc bytes; original
  source/time hashes; all declared connected projected samples; local timezone,
  offset and UTC displays; anonymous authorization denial before planner work;
  a real clipped ISS pass and a complete bounded ISS search returning no pass.

Browser screenshots are a separate verification of the exported HTML; root owns
that step. Render the `*-selected.html`, `iss-clipped.html` and `iss-no-pass.html`
files, retaining screenshots outside Git with their input HTML checksums.

## Retained integrated examples

The current integrated export at `0a249abd5eb772709d08ee45a010ac983dce6f92`
passed this Manual case, 45 presentation Unit cases and 16 arc-component Unit
cases with warning-clean Release builds. Paths use a one-pixel stroke and
smaller point markers. Root checked all 57 output hashes and inspected the actual frozen
component pages in the native browser. Desktop 1024×1000 CSS-pixel views had no
page overflow or console errors; the retained screenshots are scrolled to the
selected pass. A 390×844 CSS-pixel Starlink view with both provenance panels
expanded also had no page overflow; its wider table scrolls inside its own container. These viewport
checks do not qualify full-resolution camera processing.

| Selected object | Actual component screenshot | Standalone full-sky diagram | Exact canonical arc |
| --- | --- | --- | --- |
| ISS / NORAD 25544 | [PNG](samples/iss-25544-selected.png) | [SVG](samples/iss-25544-full-sky.svg) | [JSON](samples/iss-25544-arc.json) |
| STARLINK-30123 / NORAD 56838 | [PNG](samples/starlinks-56838-selected.png) | [SVG](samples/starlinks-56838-full-sky.svg) | [JSON](samples/starlinks-56838-arc.json) |
| STARLINK-35015 / NORAD 65283 | [PNG](samples/starlinks-65283-selected.png) | [SVG](samples/starlinks-65283-full-sky.svg) | [JSON](samples/starlinks-65283-arc.json) |

The standalone SVGs preserve the exact component geometry and labels; their
wrappers add the SVG namespace and the component CSS from the recorded source so they can be
opened independently. The screenshots retain the actual frozen component page.
The [portable checksums](samples/checksums.json), [browser proof](samples/browser-proof.json)
and [integrated source/input/product evidence](samples/export-evidence.json)
bind this subset. The [full export manifest](samples/export-checksums.json)
also lists the uncommitted, reproducible frozen HTML and alternate toggle views;
it does not claim those files are all copied into this smaller directory.

The ISS arc retains 663 samples; the Starlink arcs retain 664 and 624. The
Starlink pass starts at the original 03:55 UTC search boundary in both examples;
the diagram and row identify this clipped start rather than inventing a rise.
A fractionally below-horizon crossing sample in each case remains canonical while
the shared hemisphere projector omits its drawn point. Selected detail and
repeat-selection identity checks preserve all original facts. These diagrams
contain no HYG star field; the separate [exposure examples](../satellite-presentation-v1/README.md)
retain their HYG attribution and license.

These are consumer preservation checks, not independent numerical references.
The actual retained prediction inputs are used; synthetic `pass-arc-v1/arc.json`
is not an oracle. The test pins only current UTC time for source age/session TTL;
monotonic work limits still run normally. Authentication uses the real policy
with a canonical test cookie principal and mocked owner lookup/security-stamp
storage, rather than a real HTTP login. The celestial catalog in the SkyMap test
harness is empty. The retained reduced virtual-camera rig is configured and
unmeasured; height is assumed ellipsoidal. Illumination and physical uncertainty
stay unknown. A no-pass result describes only the selected record, threshold
and window.

All canonical samples remain in the exported arc. The hemisphere and configured
rig render only samples accepted by their existing shared projectors. Resolved
crossing samples can be fractionally below zero elevation within the producer's
crossing tolerance; their facts remain retained even when projection rejects
their drawn points. The manifest records canonical, below-horizon and rendered
point counts separately, without extrapolating boundary intersections.

This evidence does not qualify optical visibility, shadow/twilight eligibility,
measured association, full-resolution camera cadence, capture prediction
persistence, automatic input refresh, live snapshot adoption or production DI.
See [local planning boundaries](../../astronomy/satellite-planning-v1.md) and the
existing [virtual-camera evidence](../orbital-prediction-v1/virtual-camera.md).

## Consumer resource and validation evidence

The [warm resource pack](resources/README.md) retains every sample, a predeclared
protocol, exact product/work checks and reproducible source. It compares direct
engine calls with the real planning facade on the same ISS/Starlink queries;
it excludes cold source loading, HTTP/authentication, UI and capture costs.
It establishes neither statistical equivalence nor five-second cadence.

The [runtime signal boundary](runtime-signals.md) identifies protected reads,
typed outcomes, safe failure logs, cancellation/session behavior and the absence
of a new autonomous worker. The [initial candidate coverage manifest](candidate-coverage.json)
binds the exact 23-report selection at source `0c48a019`, before review
corrections. It retains the original Integration failure, the complete passing
affected replay and the unchanged Catalog/LogicHost qualification gaps. It is
not a final-source coverage pass or a production qualification claim.

Review corrections add explicit snapshot-limited `NoMatches`, reject unsupported
query time before loading orbital data, preserve singleton projected samples,
and keep stale/source/geometry/zero-EOP warnings in mandatory presentation text.
The integrated correction at `d7814be3` passed 83 focused CameraAgent and 45
presentation cases plus the real-engine UI Manual export. Broader affected
validation and independent correction review remain separately recorded in the
issue/PR ledger.

The [C1 correction coverage manifest](correction-coverage.json) records the
subsequent frozen `6186b9fd` source: full Unit selection 9,098 passed, 119 existing
qualification-limited skips and no failures. Original Integration had 842 passes
and one standalone capture/outage timeout; the unchanged complete 26-case
affected assembly then passed with the identical case IDs and coverage settings.
The other 817 passing cases remain selected. The failed run is retained;
[timeout diagnostics follow-up #1428](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1428)
records the unknown cause, rather than asserting a repair.

All 23 exact coverage inputs were consumed and all six native input guards passed.
Shared, CameraAgent and combined component floors passed. Aggregate/delivery
still failed the existing ReleaseTool file floor; LogicHost still failed its
existing aggregate floor tracked in #1420. No baseline changed. The manifest uses
native enforcer aggregates with their configured exclusions. These results
precede the separately checked one-pixel/smaller-marker revision and pending
runtime composition; they are not a final-source coverage or cadence claim.

## Runtime adoption after the #528 handoff

[runtime-adoption.json](runtime-adoption.json) binds source `2e0f67e2` to actual
CameraAgent startup, the installed singleton reader, pinned time files, real
store activation and stale-session/UI checks. The complete affected CameraAgent
Unit selection is 5,297 passed plus one existing platform skip; all 29 standalone
Integration cases pass. Actual discovery IDs equal execution IDs. The fresh
Manual export passes one actual case, verifies 57 outputs and preserves the three
canonical angular arcs byte-for-byte. These results do not replace the historical
full-suite, replay or coverage evidence above, or qualify production/cadence.
