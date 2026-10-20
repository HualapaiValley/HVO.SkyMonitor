# Independent actual-EOP full-pass oracle preparation

This standalone pack was produced offline on 2026-10-19/20 UTC from **already retained approved raw inputs**. It imports no HVO code and makes no network requests. No repository/Git/GitHub edits, .NET calls, builds, tests, captures or performance runs were performed. Root owns integration and qualification against the implementation; these expectations were not fitted to its output.

The generator uses Python sgp4 **2.27**, explicit WGS72/AFSPC mode **a**, Skyfield **1.55** coordinate rotation/observer routines, NumPy **2.5.4** and the copied approved `coordinate-reference-generator.py`. The script preserves split UTC/GMST82 and actual retained DUT1/polar-motion interpolation. Python datetime does not quantize requested samples: UTC is integer **100ns ticks**, including the `0.1234567s` off-phase. Propagation uses `sgp4_tsince` from exact tick differences against the decimal raw epoch, while coordinate rotation receives split day/fraction. No whole-Julian-date subtraction, default mode-i initialization, TIO s-prime, refraction, brightness or shadow model is substituted.

`cases.json` contains **21,665** independently evaluated timed states across three complete passes, plus references to exact start/peak/end anchors. No expected state is interpolated. Every state has `error:0`, finite unit direction/range/rate/speed and range >=50km. This is a bounded numerical fixture, not an all-orbit completeness or physical-accuracy claim.

| Case | Independent start / peak / end UTC | Max altitude | EOP | Samples / off-phase stride |
|---|---|---:|---|---:|
| ISS Seattle | 2026-10-20 12:58:52.2711562 / 13:04:21.3047801 / 13:09:52.5896071 | 76.38493432728447° | **Predicted**, actual retained Bulletin A P | 4,822 / 0.137s |
| Vanguard1 Sydney | 2000-06-28 11:50:22.2073744 / 12:08:33.6259318 / 12:17:25.9491347 | 88.02343916073188° | **Final**, actual retained Bulletin B | 6,854 / 0.237s |
| Molniya08195 HVO | 2006-06-25 00:51:37.2076033 / 01:53:51.9212365 / 02:53:17.0402144 | 1.2829543708809825° | **Final**, actual retained Bulletin B | 9,989 / 0.731s |

The Molniya case is a low-elevation full pass from the approved high-eccentricity 12-hour resonant orbit. It is not a high-elevation scenario. It uses newly extracted actual final EOP from the existing source; it does **not** relabel the previous zero-EOP pass references. Historical Vanguard is near its own 2000 epoch, not a current prediction or current source-status assertion. Observer heights are explicitly assumed WGS84 ellipsoidal. All cases use geometric horizon threshold zero, with no clipped window boundaries.

The ISS window intentionally covers the full pass around the familiar 13:03 virtual exposure. Its earlier approved independent event pack is retained as `inputs/pass-references-original.json`, for source context. The revised event timestamps are independently refined with exact 100ns sample times and explicit epoch-elapsed propagation; small differences from the older microsecond/JD path are visible, not adjusted to HVO.

## Frozen gates and interpretation

Root supplied the binding gates from [#529 comment6279921029](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/529#issuecomment-6279921029): requested arc target **0.001 degrees**, independent direction allowance **1e-6 degrees**, and exact-UTC anchor direction allowance **1e-8 degrees**. Independent crossing/peak brackets must be <=**0.001 seconds** and existing engine event agreement remains **20ms**. No gate or tolerance was relaxed here.

All primary brackets in this pack are <0.0001s. A primary bounded scan and a half-step, quarter-phase-shifted scan independently locate crossings and refine every observed local maximum in the selected pass. The global peak is the maximum among those refined candidates and endpoints. Search windows are 14min/1h/2.5h, not another full 60-hour campaign. Independent phase-scan peak differences are 0.0000111s (ISS), 0.0000118s (Vanguard) and 0.0001036s (flat Molniya); all are retained. This supports these selected fixtures numerically, not a proof that arbitrary maxima cannot lie between scan probes.

For exact-anchor tests, construct a synthetic canonical parent pass from these **independent** boundary/peak UTCs and evaluate its arc at those same UTCs. A synthetic parent is a fixture input, not an HVO search result. For normal engine search -> arc tests, compare only fixed oracle sample UTCs inside the common interval. **Do not compare directions at an engine endpoint and a different independently searched endpoint under the 1e-8-degree same-UTC allowance**; use the separate 20ms event gate. Direction errors should use unit-vector angular separation (stable atan2 of cross norm and dot), not scalar azimuth subtraction near wrap or zenith. Direction sampling estimates and physical orbit uncertainty remain separate.

## Portable data shape

Top-level `cases.json`: `schema`, `softwareVersions`, `maximumOutputStates`, `totalOutputStates`, `cases`.

Each case carries `name`, `facts` (uppercase GP numerical keys), `observer:[latitude,longitude,height]`, `windowStartUtc`, `windowEndUtc`, `thresholdDegrees`, `eopFile`, `eopSha256`, `eopStatus`, `eopCoverage`, `leapFile`, `leapSha256`, `leapCoverage`, model/convention and gate facts, `pass`, `anchors`, `samples`, `sampling`, `independentSearch`, and `initializer`. TLE cases additionally carry exact `sourceTle` lines and their raw-file hash. Derived `facts.EPOCH` has **no trailing Z**, matching the existing `OrbitalPredictionTestData.Record` helper's convention; all actual window/pass/state timestamps have explicit seven-fractional-digit Z UTC.

`pass` keys: `startUtc`, `endUtc`, `peakUtc`, `maximumElevationDegrees`, `startWindowClipped:false`, `endWindowClipped:false`.

`anchors` keys: `start`, `peak`, `end`, each a full state. `samples` use the existing dense fixture naming: `utc`, `error:0`, `altitude`, `azimuth`, `unitEnu:[east,north,up]`, `rangeKilometers`, `rangeRateKilometersPerSecond`, `angularSpeedDegreesPerSecond`. Anchors are included in the sorted unique sample array. Off-phase cadence and exact UTC precision are declared in `sampling`.

`facts` for TLE cases are independently decoded fixed-width numerical values, retaining original GP/TLE DOT/DDOT units and explicit a-mode reinitialization. The raw text, original line-ending version, source URI/revision/terms, software hashes and previous approval provenance are retained. There is no HVO production-normalizer call or private production DTO.

## Time and source provenance

See `sources.json`, `software-hashes.json` and `artifact-manifest.json` for exact bytes/hashes and licenses. No acquisition timestamp is invented.

The common source is already retained USNO/IERS `finals2000A.all`, 3,769,212 bytes, SHA256 `BFA2066AAB0400B075357F573AF6081E0068A518993609EEE0C5CA991BAB0845`, retrieved 2026-10-19T17:40:05.574120Z. Only bounded raw subsets are copied:

- `inputs/predicted-2026.finals2000A`: existing unchanged subset MJD61331..61338, **Predicted**, SHA256 `A11AD9627063DC10FA59FE25B9F9C07F0BACF81EA65F9405F40E4055DAA42E8B`; covers 2026-10-18..25 UTC. Real retained predicted EOP is different from zero assumed EOP and from final observations.
- `inputs/final-2000.finals2000A`: existing unchanged MJD51721..51725, **Final**, SHA256 `F13535B5EC4F6B4013C610AB7C6B41ED2C4A03B5B030C08FD171D975B6C47FA2`; covers 2000-06-26..30 UTC.
- `inputs/final-2006.finals2000A`: newly extracted MJD53910..53914, **Final**, SHA256 `68317FDD7BD2191EBC8EE86219F314D48415BE76C7ACA575659426A4B109C573`; covers 2006-06-24..28 UTC. Transformation is inclusive MJD selection, trailing ASCII-space trim and LF normalization, preserving numeric columns. All five rows have Bulletin B final DUT1/xp/yp. This is the sole new raw subset; there was no new fetch.

The common 5,065-byte leap table is unchanged, SHA256 `DB5A895F16853B03BFC865E8D68F9FC8710EF1740E3400C701CD46A5BBBC3433`, coverage1972-01-01 through expiration2027-06-28 exclusive. It declares public domain. The generator checks both epoch and search intervals and rejects any intervening leap; all three cases pass those input checks.

IERS scientific data attribution is retained; the existing source record explicitly does not infer a bespoke institutional redistribution grant or production-source approval. This pack extends already approved bounded local fixtures only. CelesTrak source snapshot and approved factual subset identities remain in the retained source manifest. Historical raw TLE data retains the Unlicense and exact upstream revision. The original TLE HTTP retrieval time is unknown and remains null. Python sgp4 and Skyfield MIT, NumPy distributed license and JPLEphem notice files are under `notices/`. No new source/software adoption or hardware accuracy approval is inferred.

## Reproduction

Use an environment already containing the exact versions in `requirements.txt`; this pack itself performs no installation/download. The original local environment is `/home/roys/.codex/artifacts/satellite-research-2026-10-19/venv/bin/python`.

On a **copy** of the pack, run `python generate.py` to regenerate `cases.json` and `software-hashes.json` from its portable raw inputs. Generation replaces those files, so preserve the immutable handoff originals first. Run `python generate.py --verify` to check every file declared in `artifact-manifest.json` without regenerating expectations. Source/binary hashes in `software-hashes.json` describe the actual original environment; a different binary build must be recorded rather than impersonating that hash. Cases contain no wall-clock timestamps, so the same binaries/inputs reproduce stable expected bytes.

Root should copy only the needed source/fixture supplement into its issue branch, record independent qualification results without changing these expectations, and keep current #529 component evidence distinct from the remaining producer/UI/live-integration/physical acceptance. No #1393 completion, measured #530 acceptance or full #1395 cadence is claimed by this pack.
