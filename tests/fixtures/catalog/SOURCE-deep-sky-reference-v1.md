# Deep-sky placement reference (issue #525)

`deep-sky-reference-v1.json` is an independent numerical reference for deep-sky
placement over the composed fixture `hyg-v44-openngc-subset.sqlite`
(`SOURCE-v44-openngc.md`). `DeepSkyReferenceTests` in
`HVO.SkyMonitor.Catalog.Sqlite.Tests` checks the file's SHA-256 before use. It
then compares production's centres, footprint limbs, outline rings and glyph
sizes with it.

## Generator

`docs/validation/issue-525-deep-sky-reference.py` generated the file. Its
SHA-256 is `79746dc29bfe9de623ff9ddf5e8e970e31167787899ee59fb15d0dc6f800fea3`.

- The generator shares no code with HVO.SkyMonitor.
- Its astronomy comes from ERFA through pyerfa: `pmat76` precession at TT,
  `gmst82` with UT1 equal to UTC, and `hd2ae`. It applies no nutation,
  aberration or refraction.
- Its geometry follows the published definitions it cites:
  `docs/astronomy/resolved-footprint.md`,
  `docs/astronomy/deep-sky-openngc-v1.md` and `docs/virtual-camera.md`.
- Before it generates any case, it checks its zenith camera convention against
  the ASI174 horizon cardinals documented in `docs/virtual-camera.md`.

The generator ran under the file name `deep_sky_reference.py`, so that name
appears as `provenance.generator` in the JSON. The committed file is
byte-identical to it: `provenance.generatorSha256` equals the SHA-256 above.

## Inputs

The inputs are the two fixture CSV files of `SOURCE-v44-openngc.md`. Their
SHA-256 values appear in `provenance.inputs` and in `SHA256SUMS-v44-openngc`:

| File | SHA-256 |
| --- | --- |
| `hyg-v44-openngc-subset-deep-sky-objects.csv` | `57b50b208257e3829c9ff84b5acac863846981c39a303ced65d7fb885c0527b8` |
| `hyg-v44-openngc-subset-deep-sky-outline-points.csv` | `6ff79951ca157d4134319279dd1bb51cc5c40892e0454538513b5a33c346930b` |

## Environment

The generator ran in a virtual environment holding only pyerfa and its numpy
dependency:

- **Python 3.12.3.** This is `sys.version` from
  `python -I -c 'import sys; print(sys.version)'`.
- **`numpy==2.5.3` and `pyerfa==2.0.1.5`.** This is the complete output of
  `python -I -m pip freeze`.
- **`pip==24.0`.** `python -I -m pip freeze --all` lists this one more line.
- **ERFA 2.0.1.** This is the library bundled in pyerfa, as reported by
  `erfa.version.erfa_version`. It is recorded in the JSON as
  `provenance.erfa`.

## Command

From the repository root, with the output written outside the repository:

```bash
python -I docs/validation/issue-525-deep-sky-reference.py \
  tests/fixtures/catalog/hyg-v44-openngc-subset-deep-sky-objects.csv \
  tests/fixtures/catalog/hyg-v44-openngc-subset-deep-sky-outline-points.csv \
  <output>/deep-sky-reference-v1.json
```

The generator refuses to run without `-I`. It records only the base names of its
inputs, and it hashes its own file. The recorded run wrote
`deep-sky-reference-v1.json` with SHA-256
`2ce52b9eaf706c9b8f6fa5d7cd6e25e53261df8ea15b151cd3e925f52efba035`
(203,685 bytes): 10 cases and 6,338 ring points.

The output is byte-reproducible in this environment. Two later runs of the same
generator over the same inputs, each written to a fresh directory, produced the
same SHA-256:

- one in the default environment;
- one with `OPENBLAS_NUM_THREADS=1 OMP_NUM_THREADS=1`.

The generator itself is single-threaded. In the default environment the
recorded run used 394% of one CPU and the later run 271%. With both variables
set to 1 the run used 99%. The extra CPU therefore comes from the thread pools
those variables size, not from the generator's computation.

## Cases

All cases use the HVO site (35.347° N, 113.878° W, 0 m) at
2025-01-15T08:00:00Z. Each object is drawn by one of two cameras:

- a 1000 × 1000 pinhole camera with an 8000-pixel focal length, centred on the
  object;
- a 7000 × 7000 equidistant fisheye camera with a 2000-pixel focal length and a
  3400-pixel aperture, looking at the zenith, where the object lies off axis.

| Object | Selection | Expected representation |
| --- | --- | --- |
| M31 (`NGC0224`) | default | its catalogue ellipse |
| M45 (`Mel022`) | default | its level-1 outline |
| M45 | no outline budget | its catalogue circle |
| M42 (`NGC1976`) | preferred level 3 | its two-ring level-3 outline |
| M42 | no outline budget | a glyph sized by the local plate scale |

Every case must be unclipped. The generator stops if any evaluated point falls
below the horizon or outside the sensor or aperture.

## Sampling and tolerances

Each footprint limb and outline edge is sampled by recursive bisection:

- every chord is at most 2 pixels long, and its midpoint lies within 0.002
  pixels of it;
- the quarter points of every final chord lie within 0.004 pixels of it;
- coordinates are rounded to six decimals.

`DeepSkyReferenceTests` documents the tolerances it applies. The largest model
difference comes from sidereal time: production omits the IAU 1982 quadratic
term, which is about 0.09 arcseconds at this instant.

## Licence

The reference is derived from OpenNGC v20260501 by Mattia Verga
(https://github.com/mattiaverga/OpenNGC, commit
`36cb178a0f69dba8bfc03a99c10512831edf1c6b`). OpenNGC is licensed under the
Creative Commons Attribution-ShareAlike 4.0 International license:
https://creativecommons.org/licenses/by-sa/4.0/.

- `SOURCE-v44-openngc.md` records the attribution of the input subset.
- `docs/catalog/hyg-v44-openngc-attribution.md` holds the complete upstream
  acknowledgements.
- Neither upstream project endorses HVO.SkyMonitor's changes.

`deep-sky-reference-v1.json` is distributed under CC BY-SA 4.0. This
attribution and derivation record must accompany redistribution.
