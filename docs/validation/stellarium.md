# Stellarium Validation

Stellarium is an external geometry and visual oracle for the canonical Hualapai
ASI174MM-compatible fisheye fixture. It is not a byte-exact image oracle and is
not part of the normal .NET test gate.

## Ubuntu 24.04 setup

Install the pinned packages idempotently:

```bash
./scripts/setup:stellarium
```

The setup requires Ubuntu 24.04 and installs `stellarium=23.4-2build3`,
`stellarium-data=23.4-2build3`, Xvfb, Xauth, Mesa llvmpipe, ImageMagick, and
`jq`. It stops with an actionable error if the pinned packages are unavailable
from the configured apt sources.

Validation invokes `/usr/bin/stellarium` directly and requires its reported
version to be exactly `Stellarium 23.4`. This prevents a different executable
earlier on `PATH` from invalidating an otherwise pinned package run.

## Run

```bash
./scripts/validate:stellarium
```

Each run uses a fresh temporary Stellarium user directory, UTC, a fixed
`1024x1024` Xvfb screen, software OpenGL through Mesa llvmpipe, and the checked-in
`scripts/stellarium/hualapai-fisheye.ssc`. A 45-second process timeout prevents a
startup or scripting failure from hanging automation.

The canonical inputs and independently calculated expected star positions are
in `tests/fixtures/stellarium/hualapai-fisheye-v1.json`. The astronomy fixture
uses published Hipparcos J2000 positions, IAU 1982 GMST, and geometric spherical
equatorial-to-horizontal equations. It deliberately omits precession, nutation,
annual aberration, proper motion, parallax, and refraction; those differences
belong only to the separately stated astronomy-model tolerance.

## Assertions

The validator records Stellarium's native screen coordinates and normalizes
them to continuous ASI174 sensor coordinates exactly as follows:

```text
x_sensor = 968 + (x_native - 512) * (595.84 / 512)
y_top = 1024 - y_native
y_sensor = 608 + (y_top - 512) * (595.84 / 512)
595.84 = 0.98 * min(1936, 1216) / 2
```

`core.getScreenXYFromAltAzi` reports native Y upward from the bottom edge;
continuous sensor coordinates report Y downward from the top edge. The explicit
`y_top` conversion reconciles those coordinate origins without enabling a
Stellarium display flip.

Tolerance categories are not combined:

| Category | Limit | Purpose |
| --- | ---: | --- |
| Analytic | `1.5` sensor px | Zenith and cardinal equidistant-projection landmarks |
| Astronomy model | `0.75 deg` (`4.9653` sensor px) | Stellarium date model versus the intentionally coarse independent J2000 fixture |
| Screen rounding | `1.0` native px | Stellarium horizontal coordinates versus its rendered fisheye screen coordinates |

The run also asserts viewport dimensions, projection, FOV, observer location,
UTC, mount, disk viewport, flips, and magnitude limit. Atmosphere/refraction,
landscape, fog, Milky Way, nebulae, labels, planets, twinkle, and luminance
adaptation are disabled by the `.ssc` script.

Version, environment, settings, native coordinates, normalized coordinates,
assertion details, logs, and the diagnostic PNG are written under ignored
`TestResults/stellarium/`. PNGs are transient diagnostics only and must not be
committed or used for whole-image equality.
