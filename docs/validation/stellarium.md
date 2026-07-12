# Stellarium Validation

Stellarium is an external geometry and visual oracle for the canonical Hualapai
ASI174MM-compatible fisheye fixture. It is not a byte-exact image oracle and is
not part of the normal .NET test gate.

## Pinned container

Run the preferred validation path from any Docker-capable host:

```bash
./scripts/validate:stellarium-container
```

`docker/stellarium/Dockerfile` pins the Ubuntu 24.04 amd64 base manifest digest
and a separate Alpine certificate-source manifest digest,
Stellarium/data, Xvfb, Xauth, Mesa llvmpipe, ImageMagick, `jq`, fontconfig, and
DejaVu fonts. The checked-in startup script is the image entrypoint. The wrapper
builds for `linux/amd64`, runs without network or capabilities, uses a read-only
root filesystem and `no-new-privileges`, and mounts only
`TestResults/stellarium` as persistent writable storage. A bounded `/tmp` tmpfs
holds transient GUI state. It uses the caller's UID/GID so diagnostics are not
owned by root.

The pinned Noble package metadata is `stellarium=23.4-2build3`, but the binary
owned by that package reports `Stellarium 24.4`. This is a reproducible Ubuntu
package inconsistency, not a `PATH` collision. Both values are asserted and
recorded rather than treated as interchangeable.

The Dockerfile resolves exact direct package versions and all transitive packages
over verified HTTPS from Ubuntu snapshot `20260701T000000Z`; both source images
are pinned by amd64 manifest digest. The sorted `packages.txt` retained by every run records the
resolved closure. Updating the snapshot, base, or a direct package is an explicit
reviewed fixture change, never a silent mirror update.

The manual/scheduled `.github/workflows/stellarium.yml` workflow runs every
Monday and through `workflow_dispatch`, then retains JSON, logs, package metadata,
and screenshots for 30 days. It is intentionally separate from required
pull-request CI.

Validate the checked-in scripts, parser, fixture, Docker contract, and synthetic
passing report without launching Stellarium:

```bash
./scripts/test:stellarium
```

## Optional host setup

For debugging on Ubuntu 24.04 only, install and run the same direct package pins:

```bash
./scripts/setup:stellarium
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
| Endpoint catalog | `0.02 deg` | Stellarium endpoint J2000 values versus independently sourced SIMBAD ICRS coordinates |
| Endpoint sensor | `6.1291` sensor px | Normalized Stellarium endpoint versus HVO projection with astronomy and screen budgets |
| Clipped boundary | `10.0` sensor px | Stellarium endpoint chord boundary versus HVO adaptive great-circle boundary |

The run also asserts viewport dimensions, projection, FOV, observer location,
UTC, mount, disk viewport, flips, and magnitude limit. Four selected
D3-Celestial HIP endpoints prove native-to-sensor endpoint positions. Orion
`27989-25336` remains inside the image circle; Virgo `65474-69701` brackets the
circle and produces a retained clipped boundary coordinate. Atmosphere/refraction,
landscape, fog, Milky Way, nebulae, labels, planets, twinkle, and luminance
adaptation are disabled by the `.ssc` script.

The exact SIMBAD identifier-query URLs, retained sexagesimal coordinate rows,
source references, retrieval date, and contract checksum are recorded in
`tests/fixtures/stellarium/SIMBAD_ENDPOINTS.md`.

Version, package closure, environment, settings, native coordinates, normalized
coordinates, assertion details, logs, and the diagnostic PNG are written under
ignored `TestResults/stellarium/`. `settings.json`, `actual.json`, and
`report.json` are the machine-readable evidence. PNGs are transient diagnostics
only and must not be committed or used for whole-image equality. HVO's ordinary
.NET conformance tests remain authoritative for rendered centroids and annotation
anchors; this external oracle independently validates their geometric inputs.

Constellation stick figures require a separate topology qualification. HVO uses
the pinned D3-Celestial topology; Stellarium may use a different sky culture and
line convention. Its figure lines are comparable only after the selected sky
culture is pinned and its endpoint graph is shown to match D3-Celestial.
Otherwise Stellarium validates endpoint positions while D3-Celestial source
checksums and topology tests validate connectivity. Every report therefore marks
Stellarium topology `not-qualified` and `usedForConnectivity: false`.
