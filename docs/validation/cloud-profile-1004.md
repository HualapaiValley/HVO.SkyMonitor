# CameraAgent cloud profile evidence (#1004)

Date: 2026-11-08 UTC. SDK 10.0.402, Linux x64, Release; tier B,
`profile=development-v1`. Base `16545df686f3abe6df357545278bbc61dc29ea4b`.
This is software/profile integration evidence. Physical ASI174 reference and
fractional-accuracy qualification remain on #1004; the PR uses `Refs #1004`.

## Regression-first findings

Before their fixes, the shipped-sample acceptance failed because `CloudAssessment`
was absent, and the installer-profile acceptance failed for the same reason.
The quantified-only publication test failed with expected `Skipped`, actual
`Produced`: an unavailable assessment could otherwise enable a measured-layer
control. The durable virtual cases then exposed missing linear calibration in
the sample's retained input descriptor, and rejection of clouds with an explicit
native-depth full-frame identity readout. Each failure was reproduced before its
correction. Original red TRX/log files remain under `/tmp/hvo-1004-evidence` on the
implementation host.

The implementation reuses the delivered analyzer, mask/label producer, gallery
reader and optional-branch worker. It adds explicit sample ASI174 input levels
(12-bit native codes, 16-bit container, black 64, white 4095), the optional durable
cloud branch in both ordinary profile entry points, and an opt-in quantified-only
layer policy. The VirtualSky guard admits clouds with full-frame native-depth
Mono16 identity readout; crop, binning, depth reduction, byte-order conversion and
stored-code conversion remain refused. No pixel algorithm was changed.

## Virtual inputs and durable results

The acceptance captures independent raw reference/current artifacts through
VirtualSky and real SQLite/file ingress, executes the shipped processing graph,
and reads the retained assessment and layered presentation through the gallery
services. The fixture uses an in-memory catalog, fixed celestial time, explicit
background/vignetting and the virtual ASI174 sensor response. Scenario parameters
go only to the camera renderer. The analyzer receives ordinary image artifacts,
linear levels, compatibility identities and explicit missing environment evidence.

Nine cases cover missing reference, clear, partial, overcast, incompatible
setpoints and corrupt reference; clear/partial/overcast also run at full
1936×1216 geometry. Reduced cases use 484×304. They verify current/reference
checksums, ordered source-artifact lineage, canonical metadata checksum/identity,
null coverage and reason codes when unavailable, retained grouped mask/labels
only when quantified, and raw plus preview survival on optional failure.
No first frame becomes a reference automatically.

The focused run passed 13 cases (12 new cases plus the expanded existing
missing-reference regression). Five additional module cases pin the refused
readout conversions. The new installer profile test runs in the deployment CLI
Unit suite; the total change adds 18 Unit cases across the two projects.

| Full-frame result | Coverage millionths | Confidence millionths | Assessment bytes | Standard processing ms |
| --- | ---: | ---: | ---: | ---: |
| Clear | 0 | 473791 | 38792 | 479.2 |
| Partial | 821230 | 317163 | 38482 | 415.8 |
| Overcast | 1000000 | 440418 | 38497 | 379.0 |

All three are `Quantified` with degraded quality and
`cloud.environment-missing`; missing precipitation/solar context was not invented.
The missing/incompatible cases report null coverage and respectively
`cloud.missing-clear-reference` / `cloud.incompatible-clear-reference`.
A malformed manifest terminates only the optional cloud branch.

These are single-run bounded observations, not a comparative benchmark or a
physical accuracy certificate. Assessment payloads are asserted below the
existing 1 MiB cap. The partial scenario has a coverage parameter of 0.5; its
82.123% sample-weighted tile classification is explicitly **not** a passed
fractional-accuracy gate. Default threshold, spatial tile aggregation, reference
support and sensor noise matter. The tests establish partial classification and
reconstructable evidence, not agreement with a hidden simulator percentage.

## Presentation and authorization

[Retained acceptance image](cloud-profile-1004.png) combines the actual full-frame
partial-case base and grouped SVG with the bUnit-rendered Current Sky controls,
using shipped Bootstrap/theme/component CSS. Viewport-height rules are fixed to
the screenshot's 800-pixel reference height for static rendering. It is a
component/service fixture, not an authenticated live-host screenshot.

The dashboard reference is
[`dashboard.html`](../prototypes/pipeline-operations/dashboard.html): measured
cloud assessment stays in the Diagnostics group, separate from expected scene
geometry and predicted tracks. The retained controls expose the existing mask
and assessment groups; without retained cloud products the measured-cloud
placeholder remains disabled. Two new bUnit cases verify those states. Other
science controls shown disabled in this fixture are outside #1004's scope.

The full CameraAgent integration suite owns authenticated Current Sky/gallery and
presentation endpoint access. Its final result, complete relevant Unit evidence,
Release build, formatting, classifier selection and required Development v1 CI
are recorded on the PR ledger. No installed instance, live profile or network
configuration is changed by this evidence.
