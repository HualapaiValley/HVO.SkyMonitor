# Additional independent future-pass oracles

Two approved retained Vallado verification cases add high-eccentricity/resonant numerical regimes to the existing ISS/LES5 pass coverage. This is external evidence preparation, not a PR review, production source adoption, physical prediction or #529 completion claim. No repository/GitHub edits, HTTP requests, EOP acquisition, HVO engine calls, tests, builds, captures or benchmarks were performed.

## Fixed workload and gates

- Observer: HVO latitude35.347°, longitude−113.878°, height0m assumed WGS84 ellipsoidal.
- Each window: exact retained element epoch−12h through epoch+48h,60hours. All input ages remain within−24..+48h freshness.
- GravityWGS72; AFSPC operation mode`a`.
- Explicit **nominal zero EOP diagnostic**: UT1=UTC, xp=yp=0. Split UTC Julian day/fraction; GMST82, zero TIOs-prime, no polar derivative, no DUT1-slope/LOD rate correction; no refraction.
- Existing independent IERS leap-file SHA256 `DB5A895F16853B03BFC865E8D68F9FC8710EF1740E3400C701CD46A5BBBC3433`. Actual file entries/expiry are parsed: both windows lie within coverage1972-01-01..2027-06-28exclusive and cross no leap offset change.
- Thresholds0° and10°. Independent numerical crossing/extremum bracket width≤**0.001s**, fixed before generation. Requested HVO agreement≤**0.02s**; do not relax existing or new oracle tolerances.
- No additional near-tangent case was added: existing independent tangent fixtures remain unchanged, while these cases add distinct orbit and global-peak regimes.

| Case | Epoch UTC | n(rev/day) | e | BSTAR | Evaluator-style nominal perigee(km) | Seed(s) |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
|08195 Molniya2-14,12h resonant|2006-06-25T07:58:18.143616Z|2.00491383|0.6877146|0.00011873|1918.2680824160434|30.000|
|28623 H-2R/B,GTO/high-e,deep-space low perigee|2006-06-26T19:27:32.414976Z|3.79477162|0.6249053|0.0009639|134.43598224783364|22.795|

Both meet the present static n/e/BSTAR/nominal-perigee profile; all generated points also have observer range≥50km. No case substitution was needed. The TLE comment's28623 perigee135.75km uses the SGP4-adjusted orbit; the134.435982km value above deliberately uses the current evaluator's398600.8/6378.135 nominal Kepler formula. Later states across both60h windows propagated without error, including **tsince−720minutes**.

`08195-passes.json` and `28623-passes.json` retain exact TLE lines, decimal-derived epochs, normalized numerical fields, initializer conventions, endpoint altitude/azimuth/range, all seed-discovered/refined extrema, all rise/set sign brackets at each threshold, above-threshold local maxima and assembled passes. Each pass selects the highest local maximum or clipped window boundary. Both windows end above both thresholds, so final passes are correctly window-clipped and their endpoint peak may not be an interior maximum.

| Case | Threshold | Crossings | Passes | Above-threshold local maxima |
| --- | ---: | ---: | ---: | ---: |
|08195|0°|11|6|7|
|08195|10°|5|3|4|
|28623|0°|9|5|4|
|28623|10°|9|5|4|

Molniya has multiple local maxima within some connected passes; this independently exercises selection of the **global highest peak**, rather than assuming one local maximum per pass. Endpoint altitudes:08195−66.4805493615°→+45.6199319796°;28623−47.8654273431°→+12.8687991183°.

## Independent method and numerical qualification

`additional_pass_generator.py` imports no HVO. Python-sgp42.27 `Satrec.twoline2rv(...,WGS72)` is used to independently decode/check the retained TLE; the API's actual default mode`i` is asserted, then a **new** Satrec is explicitly initialized with`sgp4init(WGS72,'a',...)`. Direct element fields and GP/TLE DOT/DDOT are preserved.08195 DOT=9.9e−7,DDOT=0;28623 DOT=0.00637644,DDOT=6.9054e−7. The conversion is2π/1440² and2π/1440³ respectively, with **no extra factors2or6**. Converted values are cross-checked against the independent TLE decoder.

SGP4 is evaluated with `sgp4_tsince` using UTC elapsed time from the exact decimal TLE epoch; negative offsets are actually evaluated. Coordinate rotation receives a separately split UTC day/fraction. The copied pinned Skyfield1.55 helper supplies GMST82 TEME→ITRF and WGS84 topocentric geometry under declared zero EOP. NumPy2.5.4/JPLEphem2.24 versions and source/binary hashes are retained.

A nominal Kepler perigee angular-rate estimate chooses a seed with≤2° nominal phase advance and≤30s duration. Seed-discovered maxima/minima are refined by local ternary search to≤1ms; refined extrema are inserted before threshold sign tests. Crossings use sign-preserving bisection to≤1ms and retain both endpoint altitudes/UTCs. A second half-step grid, shifted by a quarter original step, independently repeats event discovery/refinement. All crossing/peak counts agree. Largest peak-time disagreement is0.000283s(Molniya) and0.000610s(GTO); crossing disagreements are≤6e−11s. Maximum actual retained bracket widths are0.000916s and0.000803s.

This is **empirical numerical qualification for these two immutable fixtures**, not a rigorous whole-cell enclosure or arbitrary pass-search completeness proof. The Kepler seed estimate is not asserted to bound SGP4/topocentric motion. Local extremum brackets assume the local unimodality seen at the stated grids. The copied existing oracle source and all original expectations/tolerances remain unchanged. Root must still compare actual engine products against these fixed new records; standalone input generation is not an engine pass.

For root integration, initialize `OrbitalTimeData.CreateNominalZeroDiagnostic(leapBytes)`, bind its actual model facts and request Diagnostic with explicitly configured/assumed geometry. Preserve degraded zero-EOP semantics and unknown illumination. Compare all threshold-specific rise/set events and per-pass highest peaks, respecting clipped-window boundaries. Supplemental `extrema`/`peaks` lists make the multi-local-maximum case auditable.

## Reproduction and attribution

From this directory, run offline in the already retained environment:

```bash
/home/roys/.codex/artifacts/satellite-research-2026-10-19/venv/bin/python additional_pass_generator.py
```

Portable requirements are`sgp4==2.27 skyfield==1.55 numpy==2.5.4 jplephem==2.24`. All raw fixture/helper inputs are local files beside the generator; no path fetches network resources or imports HVO. The generator writes the two case JSON files and `summary.json`; the accompanying source/hash manifest identifies the exact retained output/source set. Preserve the existing oracle files when integrating; these are additions only.

Vallado/AIAA2006-6753 verification data is retained through aholinch/sgp4 revision552cb1489a52c3023ae70cb6c7e239e84c5950fe under its included Unlicense notice. The original raw TLE source SHA isD246D1D9D768ACE445A38A965713FA9BA52D80FD8A41A0502FF83D7ACFFE2881; approved retained CRLF→LF/trailing-space-trim derivative SHA is76727A72FC0D0A1AF115E03AF487C33213BEC8842AE6C32B5F0829454C8A45D7. Python-sgp4/Skyfield/JPLEphem are MIT; NumPy's distributed notice is retained. The raw IERS leap artifact explicitly declares public domain. Exact original source URLs and hashes are in`manifest.json`. No production acquisition, status inference, physical-clock accuracy or institutional source-adoption approval is implied.

Actual helper session metadata reverified: providerOpenAI, modelgpt-6.1-sol, effortxhigh. Account label is not exposed in session metadata.
