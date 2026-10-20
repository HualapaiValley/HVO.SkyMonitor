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
