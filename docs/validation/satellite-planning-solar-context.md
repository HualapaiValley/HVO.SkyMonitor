# Satellite pass solar context

This is a bounded presentation delivery under #1393 from `development/v1` base
`2aa8171772387c0f81570ecfab027523d87c25ec`. It does not close #1393, #1395,
#529, #530 or #527. All five issue bodies and comments were read before scope
selection. Tier B; protected profile `development-v1` (Preflight and Build and
Unit). Formal Sol/Opus review is performed separately by Kepler, as requested by
Roy; no in-thread review runner or merge is authorized.

The UI composes the existing offline ephemeris and solar-altitude classifier at
the exact original pass observer and declared start/peak/end instants. The
geometric 0°/−18° display boundaries are stated beside the results. Satellite
illumination and optical visibility remain separate facts. No new propagator,
orbital data fetch, science threshold tuning, capture processing or persistence
format is introduced.

## Regression evidence

Tests were added and run before implementation: nine failures reproduced missing
solar context, while the unresolved-anchor guard passed (10 cases). The original
red TRX and log are retained under `/tmp/kepler-satellite-evidence/regression-red`
and `/tmp/kepler-satellite-evidence/regression-red.log` on `home-dev-02`.

The ten added Unit cases increase CameraAgent's inventory from 7,549 to 7,559.
Discovery also verified an existing Processing inventory discrepancy: 1,168 Unit
cases were present against 1,167 declared. This PR corrects that count without
changing Processing code or tests. The initial candidate declared 14,974 Unit
and 1,007 Integration cases. Target synchronization with #1721 independently
includes that same Processing correction and adds 15 LogicHost diagnostic Unit
cases; the synchronized total is 14,989 Unit and 1,007 Integration cases.

`SatellitePassArcTests` uses the existing retained canonical ISS and two Starlink
arcs in `satellite-planning-v1/samples/`, plus explicitly synthetic equatorial
anchor times for day, twilight and night. The tests verify original observer and
UTC selection, ephemeris identity, missing/nonfinite results, unresolved anchors,
bounded work, independent visibility toggles, unchanged geometry and byte-identical
orbital products. These are software composition checks; the existing astronomy
tests own numerical ephemeris qualification. Published exploratory satellite
scenes are not a held-out detection evaluation set.

Reproduce with SDK 10.0.402:

```bash
dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --configuration Release --filter 'FullyQualifiedName~SatellitePassArcTests|FullyQualifiedName~SatellitePassPlannerTests'
```

The PR body records final classifier output, full local Unit/Integration counts,
Release/format results and the final-head v1 CI run. No new screenshot or browser
qualification is claimed by this component-level delivery.

## Retained batch work

| Issue | Addressed here | Explicitly retained |
| --- | --- | --- |
| #1393 | Geometric Sun/day/twilight/night context at selected full-pass anchors, missing-data status and fixture regressions. | Moon/cloud context, custom-obstruction acceptance, wider layer configuration and remaining presentation acceptance. Existing capture overlays remain exact-exposure products. |
| #529 | No new producer acceptance claimed; its pinned arc products are consumed unchanged. | Qualified shadow/illumination and physical uncertainty, remaining capture/cross-host reproduction acceptance and resource qualification. Existing capture integration delivered by #1641 is retained history. |
| #530 | No measured-association implementation or acceptance claimed. | Production causal association, append-only assessments/operator corrections, independently approved held-out quality thresholds and false-match/meteor evidence. |
| #1395 | No cadence benchmark or supported-hardware claim. | Full frame-available-to-durable-publication baseline/after evidence, actual five-second arrivals, backlog/fault/contention evidence and intended-hardware/full-resolution qualification. |
| #527 | No new source or policy decision. | Multi-update/fleet volumes, population/access/redistribution decisions, supplemental-source precedence, age/maneuver/solar/physical-image and target-architecture qualification. |

Real hardware, live external feeds and measured-science acceptance stay outside
this PR. Source approval, physical on-sky visibility/sensitivity and Raspberry Pi
performance are not inferred from synthetic or recorded data. No parent is
closed by partial work.
