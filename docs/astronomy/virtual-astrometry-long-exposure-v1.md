# Virtual all-sky astrometry qualification — long exposures (v1)

Issue [#1168](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1168), under epic
[#520](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/520), adds the exposure axis to the virtual
astrometry envelope. The #1106 envelope ([v1](virtual-astrometry-qualification-v1.md)) and the #1126
projection-family envelope ([v2](virtual-astrometry-qualification-v2.md)) were measured on 1 s frames. This report
runs the same #1102, #1103, #1104 and #1105 harnesses, with the trail-aware v2 measurer, on 20 s and 60 s VirtualSky
sensor-pixel frames, using the [#1168 manifest](../validation/issue-1168-qualification-manifest.json), its
[runner](../validation/issue-1168-qualification.sh) and its [metrics](../validation/issue-1168-metrics.sh). It is
the single statement of which exposures the virtual milestone supports. v2 remains the 1 s projection-family
envelope.

**Status: declared at `27c611dc`, not yet measured.** The matrix, refusals, withheld cells, tolerances, floor rule
and continuity rules below were committed in the manifest before any continuity, tuning or held-out run. Results
and the [supported envelope](#supported-envelope) are added after measurement.

This is a virtual qualification only. It makes no claim about physical accuracy, and no profile here is a physical
lens certification. No product code changed: `src/` is identical to `development/v1` at `0639e27d`.

## Reference time: the exposure midpoint

A long exposure's stars trail across the sensor. Every frame here is rendered at its exposure midpoint, and solved,
scored and mapped there:

- The renderer's scene time is `start + exposure / 2`.
- `AstrometricFrameContext.MidpointUtc` equals the rendered scene time exactly. Every case records
  `midpointDeltaTicks`, which must be 0.
- `CameraFrame` timestamps are the exposure **start**, not the midpoint.

**Consumer obligation.** A consumer of this envelope, first #1169 (registered stacking), must bind the solve and
every cross-frame mapping to the exposure midpoint, not to the frame timestamp. The pixel harness's negative
control binds start-UTC instead. It reports the resulting pose error and start-time sky displacement against the
predicted half-exposure sidereal motion, so the size of the mistake is on record. The negative control is a
diagnostic, not a pass condition; its numbers are under [results](#negative-control-start-utc-binding).

## Frozen matrix

Every cell uses gain 150, a PSF of σ 1 px with a 4 px radius, and the shared HYG 4.2 snapshot
`hyg-v4.2-p3-s2-r1` (119,625 rows, database SHA-256 `b51d18b7…b0f9e2`). The solver catalog is unchanged at
magnitude 5 (1,637 entries) and owned by #1167. The `product-depth` variant deepens only the render; no
deeper-catalog claim is made.

Families:
- `equidistant`: the v1 fisheye, mono (ASI174) and CFA (`cfa-native`, ASI178MC).
- `rectilinear`: the v2 Kowa LM6HC 6 mm substitution, mono.

Every other v2 family stays qualified at 1 s only. Long exposure is not qualified for them.

| Harness | Cells |
| --- | --- |
| Pixels, blind/warm/readouts (#1102) | equidistant and rectilinear at 1, 20 and 60 s with no variant; equidistant at 20 and 60 s under each variant |
| Measured stars (#1103) | equidistant at 1, 20 and 60 s; rectilinear at 1 and 20 s |
| Session optical calibration (#1104) | as measured stars |
| Astrometric uncertainty (#1105) | as measured stars |
| Configured-resolution resources | equidistant 1, 20, 60, 60, 20, 1 s, then rectilinear 1, 20, 20, 1 s, in one session |
| Renderer capacity | the rectilinear boundary and every 60 s refusal, once |

Each runs as a tuning pack, then once as a held-out pack, except resources and renderer capacity, which run only in
the held-out pack. Pixels and resources run the v2 measurer.

### Variants

Each variant other than `none` renders only `mono-native` and `cfa-native`, on equidistant.

| Variant | Render depth | Max results | Background (e⁻/s) | Clouds | Expected outcome |
| --- | --- | --- | --- | --- | --- |
| `none` | mag 5 | 2,000 | 2 | — | accepted |
| `product-depth` | mag 6.5 | 32,768 | 2 | — | accepted or fail-closed |
| `bright-background` | mag 5 | 2,000 | 20 | — | accepted or fail-closed |
| `clouds-partial` | mag 5 | 2,000 | 2 | 40% coverage, opacity ≤ 0.9 | accepted or fail-closed |
| `overcast` | mag 5 | 2,000 | 2 | full coverage, opacity 1 | rejected |

"Fail-closed" means every case either accepts and passes or rejects; nothing is accepted wrongly. An accepted
assessment with association precision below 1, pose error above 0.06° or focal relative error above 0.001 is a
failure in every variant.

### Declared refusals and withheld cells

Each refusal is listed in the manifest with its exact exception type, message and stage. The harnesses catch only
that refusal, in that cell. Any other exception, or a refusal in an unlisted cell, fails the run.

| Cells | Disposition | Stage | Exception |
| --- | --- | --- | --- |
| Every mono view of a cloud variant | unsupported-render-refused | initialize | `NotSupportedException`: the native mono readout does not support clouds |
| Rectilinear full-frame views (`mono-native`, `mono-bin2`, `mono-mirror`, `mono-roll`) at 60 s | unsupported-render-refused | capture | `InvalidOperationException`: `stellar-exposure-temporal-budget-exceeded` |
| Measured stars, calibration and uncertainty for rectilinear at 60 s | withheld-render-refused | — | Not run: these harnesses render the refused full-frame views |

## Renderer capacity

The renderer integrates each star's trail in temporal slots:

- Each slot moves the star at most 0.15 px: the configured bound, which the σ 1 px PSF does not tighten.
- A source needs `ceil(exposureSeconds × speedBound / 0.15)` slots, where `speedBound` is its fastest sensor-plane
  motion.
- More than 64 slots refuses the capture.

On the rectilinear 6 mm `mono-native` view, the fastest source moves 0.16882 px/s. That view renders at 56 s
(64 slots) and refuses at 57 s (65 slots); 60 s would need 68. The derived maximum exposure is 56.86 s. The
renderer-capacity run measures this boundary, and the follow-up tracks raising it. The rectilinear `mono-roi` and
`mono-roi-bin2` views are not refused at 60 s, and the renderer-capacity run renders them.

## Tolerances

Every #1106/#1126 tolerance is unchanged. Nothing is weakened. The manifest names these additions:

- Same-frame and cross-frame round trips within 1e-6 px. Points that leave the horizon or aperture are counted as
  excluded, not failed.
- Truth is the midpoint of each star's rendered trail. A trail truncated by the horizon, the aperture or the frame
  is excluded from scoring and counted.
- Saturated detections are scored unless excluded as `saturated-excessive`. Every exclusion reason is counted per
  cell.
- An unmodeled-source association is a failure. That is an association whose source lies more than 1.5 px from
  its catalog star's trail and nearer another rendered star, for example a fainter `product-depth` star matched to a
  catalog star.
- Measured stars: the v1 recall, centroid-regression and time-ratio comparisons gate only 1 s. At 20 s and 60 s,
  v1 is reported, not compared. `no-candidate` is a reported missed-star category, not a failure.
- Trail metadata, for truth trails of at least 3 px:
  - length error p95 within max(0.5 px, 15%);
  - angle error p95 within 10°.

  This gates only the trail-metadata envelope row, never solving.
- Resources: capture p95 within the #522 renderer budgets:

  | Exposure | Mono | CFA |
  | --- | --- | --- |
  | 1 s | 10 s | 20 s |
  | 20 s, 60 s | 10 s | 40 s |

  Warm solve stays within 500 ms.

### Uncertainty floors

The #1105 floors are declared for 1 s only. The 20 s and 60 s floors are derived on the tuning pack by the #1105
rule, then committed in one commit before held-out:

- Each tuning frame reports its required floor: the floor that brings χ² per degree of freedom to 1.
- Within each format group (mono, mono 2×2, CFA), take the median across every tuned family at that exposure.
- Round half-up to 0.001 px. Null floors are excluded and counted.

The tuning runs at 20 s and 60 s estimate at the provisional 1 s floors only to report each frame's required
floor. Their coverage outcome is not a result. A held-out coverage miss is recorded, and covariance for that
exposure is withheld; it is never retuned.

## Continuity with #1126

With every selector unset, the harnesses reproduce the #1126 1 s v1 equidistant matrix. The continuity run builds
base `0639e27d` and this head in Release. It then runs every #1126 manifest entry on base and then on head, in one
heavy-lock session on hvo-dev-02.

Every report leaf that differs must be classified:

| Class | Meaning |
| --- | --- |
| identity | The revision fields. |
| run-varying | Timing and resource fields. |
| declared-changed | An exact base and head value named in the manifest, such as the schema versions. |
| declared-added | A new field, such as `exposureSeconds`, `cases`, `cells` or `negativeControl`. |
| scorer-by-design | Under a scorer prefix: the scorers became trail-aware by design. |

Any unclassified leaf fails continuity. So does any difference in solver, measurement, mapping, uncertainty,
identity hashes or failures.

## Reproduction

On an idle host with the verified snapshot installed:

```bash
export HVO_ASTROMETRY_CATALOG_ROOT=<snapshot root> HVO_HEAVY_LOCK=/tmp/hvo-520-heavy.lock
docs/validation/issue-1168-qualification.sh continuity <new pack directory>
docs/validation/issue-1168-qualification.sh tuning <new pack directory>
docs/validation/issue-1168-metrics.sh floors <tuning pack>
docs/validation/issue-1168-qualification.sh final <new pack directory>
docs/validation/issue-1168-metrics.sh summary <pack>
```

The runner refuses a dirty tree and runs one process per declared cell. It records the host, the TRX counters,
and the harness source blobs. Every pack is sealed with `SHA256SUMS`.

## Results

Pending measurement.

### Negative control: start-UTC binding

Pending measurement.

## Supported envelope

Pending measurement.

## Not claimed

- Physical accuracy, or any physical lens.
- A deeper solver catalog. #1167 owns solver catalog depth; #1168 owns render depth and exposure.
- Long exposure for the equisolid, stereographic, orthographic and rectilinear 8 mm families.
- Gains other than 150.
