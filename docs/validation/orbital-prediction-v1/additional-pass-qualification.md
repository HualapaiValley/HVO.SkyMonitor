# Additional pass qualification (#529)

The [independent source pack](additional-passes/README.md) adds retained
Vallado verification cases 08195 (Molniya, 12-hour resonance) and 28623
(high-eccentricity transfer orbit). Both fit the declared numerical support
profile. Each covers its own historical element epoch minus 12 hours through
plus 48 hours, at HVO 35.347°,−113.878°, 0 m. These are explicitly degraded
nominal-zero-EOP diagnostics with WGS72/AFSPC mode `a`, WGS84 assumed ellipsoidal
observer height, no refraction and actual retained leap-table bytes.

The generator imports no HVO code, performs no network operations, and retains
its exact inputs, software versions, terms, output hashes and numerical
limitations. Python's default TLE mode `i` is independently checked and then
explicitly reinitialized as mode `a`. Nonzero DOT/DDOT uses the approved direct
coefficient convention. Negative offsets are actually propagated. Original
ISS/LES5 oracles and all predeclared tolerances remain unchanged.

Two new public-engine Unit cases each compare both 0° and 10° thresholds,
including every crossing, pass boundary and highest peak. A pass may contain
multiple local maxima; the expected peak is independently selected from all
maxima and clipped window endpoints. Every emitted canonical product is
rehashed. Source fixture hashes are asserted before use.

| Case / threshold | Complete passes | Crossings | Actual core evaluations |
| --- | ---: | ---: | ---: |
|08195 /0°|6|11|22,211|
|08195 /10°|3|5|21,131|
|28623 /0°|5|9|30,479|
|28623 /10°|5|9|31,404|

| Case | Worst crossing residual | Worst highest-peak time residual | Worst peak altitude residual |
| --- | ---: | ---: | ---: |
|08195|0.0026448s|0.0030399s|8.94e-13degrees|
|28623|0.0039024s|0.0008037s|1.72e-11degrees|

All 34 new crossings and 19 pass peaks meet the unchanged 0.02-second agreement
gate and 1e-6-degree peak-altitude gate. Independent reference brackets are
at most 0.001 seconds, and a second finer shifted scan agrees on event counts.
The two tests pass without a production algorithm or tolerance change.
Fixture preparation is independent evidence work, not the PR's independent
review. This finite numerical qualification does not establish a global
completeness proof, physical orbit accuracy or clock quality.

Reproduce the public-engine checks after building the Release test project:

```bash
dotnet test tests/HVO.SkyMonitor.Astronomy.Tests/HVO.SkyMonitor.Astronomy.Tests.csproj --no-build --configuration Release --filter 'FullyQualifiedName~OrbitalAdditionalPassTests'
```

For independent regeneration, install the pinned Python requirements from the
source-pack README and run `additional_pass_generator.py` from its directory.
Its retained input files are beside it; it does not require this host's original
artifact paths. The manifest's absolute software paths record the original
generation environment rather than portable dependencies. Regenerated fixtures
must retain the committed hashes before replacing the tested copies in
`tests/fixtures/orbital-prediction-v1/passes/`.
