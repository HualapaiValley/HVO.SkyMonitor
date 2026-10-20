# Independent dense sampling references (#529)

The generator and calibration/held-out split were frozen before the integrated
sampler was run against this pack. It calls pinned Python-sgp4 2.27 WGS72/mode a,
Skyfield 1.55, NumPy 2.5.4 and jplephem 2.24; it never imports HVO or downloads
data. The coordinate oracle and retained source/license manifest remain in
[coordinate-reference.md](coordinate-reference.md).

There are 36 cases and 9,360 rows: the six retained GP records with nonzero
retained EOP; each at epoch offsets -23.9, 0, 47.9 and 167.9 hours with explicitly
zero-EOP diagnostics; and six synthetic numerical stress records. Durations
are 0.3, 5, 20 and 100 seconds. Sites include Seattle, HVO, the antimeridian,
negative height and the North Pole. Synthetic cases exercise eccentricity
0..0.89999, mean motion 0.50001..15.5 rev/day, inclinations 0..180 degrees and
BSTAR +/-0.00999. They assert no actual physical object or orbit accuracy.

Each case retains 257 off-node phases `(i+0.371)/257`, endpoints and midpoint.
UTC is quantized to Python's microsecond representation and retained verbatim.
Odd/even case assignments to calibration/held-out are deterministic and are
not changed in response to HVO results. The output SHA256 is
`8B9B12052455E06947AE44226151329D0480A895102CBF67025BF32210F22EBC`.
[sampling-source-manifest.json](sampling-source-manifest.json) binds the exact
generator, source facts, EOP/leap inputs and output bytes. Library licenses are
the retained MIT notices in this directory. Reproduce offline with:

```bash
python docs/validation/orbital-prediction-v1/sampling-reference-generator.py
```

The engine support profile is deliberately narrower than source validation:
mean motion 0.5..18 rev/day, eccentricity <=0.9, absolute BSTAR <=0.01,
nominal WGS72 Kepler perigee >=100 km, observer ellipsoidal height -1..10 km,
and evaluated observer range >=50 km. Existing policy bounds epoch offsets
to -24..168 hours. Unsupported records produce explicit failures; they are
never clamped or silently omitted. These limits describe numerical support,
not physical validity or a proof over a continuous rectangular parameter set.

Qualification must compare the final engine's returned chords to these frozen
directions through all five existing optical families and the remaining
crop/bin/mirror/rotation transform. The predeclared per-query chord tolerance
is unchanged; a separate 1e-5-pixel reference allowance covers independent
propagator/coordinate residual and oracle UTC quantization in the broad optical
qualification chart. Narrow-field and clipping adversaries are separate
sampler-kernel tests. An empirical remainder estimate and dense qualification
are not a formal all-time SGP4 enclosure or orbit-confidence bound.

The original 36-case file and its hash remain unchanged. Two supplemental held-out
ISS passes at Seattle and near zenith at HVO add 520 rows in
`fast-directions.json`, SHA256
`397235B677EDED5BD08D6EA9C85E5E47184D727D758B2A642C8B513B1252BDFA`.
The supplemental generator/output pins are recorded in the same manifest.

The integrated engine passed 38 cases and 9,880 independent phases for each
of the five optical families (49,400 comparisons). All queries completed;
maximum time step was at most the separately requested 0.37 seconds and the
requested chord tolerance was 0.01 pixels. Crop, bin2, both mirrors and 270-degree
rotation were applied through the existing transform exactly once.

| Projection | Worst measured off-phase chord error (pixels) |
| --- | ---: |
| Perspective | 0.000031362 |
| Equidistant | 0.000070898 |
| Equisolid | 0.000077822 |
| Orthographic | 0.000157408 |
| Stereographic | 0.000056172 |

Every comparison met the returned error estimate plus the predeclared
1e-5-pixel reference allowance; the largest ratio to that total was 0.713.
These are numerical sampling/coordinate results, not physical orbit accuracy.
The integrated focused run passed 53 tests, including independent-instance and
concurrent determinism, model/time mismatch, missing objects, actual initializer
work, finalization deadline, pass events and sampling adversaries. The affected
Release test/tool builds subsequently passed with zero warnings/errors after
ordinary analyzer corrections. An initial chart setup used an invalid circular
perspective/anisotropic fisheye combination; fixing chart configuration did not
change reference inputs, production projection or numerical tolerances.

Full population resource evidence, canonical camera captures and independent
review remain required before merge.
