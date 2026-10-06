# Resolved footprint — v1

Issue #518 gives the Sun and Moon a resolved angular footprint. The renderer, the projected scene and the
annotations all draw that footprint instead of a point marker. The same contract is the seam that #525 uses for
extended deep-sky objects. #525 adds catalogue extents and a `DeepSkyObject` producer. It does not add a second
footprint shape.

**Not included:**
- deep-sky catalogue extents and their producer, which belong to #525
- lunar or solar surface texture, libration, limb darkening and the terminator as projected geometry. The phase
  shading stays inside the renderer, and the footprint carries only its outline.
- refraction away from the Bennett standard atmosphere, and pressure/temperature inputs
- physical lens qualification of the disc size, which stays with the physical-qualification epic #1166

## Ownership

- **`HVO.SkyMonitor.Astronomy`** owns the whole contract:
  - `ResolvedFootprintExtent`, `ProjectedResolvedFootprint` (`resolved-footprint-v1`), `ResolvedSourceFrame` and
    `ResolvedFootprintSampler` (`resolved-footprint-limb-adaptive-v1`), all in `ResolvedFootprint.cs`
  - `VisibleScene.WithResolvedBodies`, which replaces the geocentric Sun/Moon objects with topocentric ones and
    attaches their footprints
  - the footprint transforms in `VisibleSceneReadoutTransform` (binning) and `ProjectedSceneImageTransform`
    (crop, bin, mirror, rotation)
  - `projected-scene-v2` serialization and validation in `ProjectedSceneContracts.cs`
- **`HVO.SkyMonitor.AgentCore`** owns only the schema-version constants on `SceneProvenance`:
  `ResolvedFootprintProjectedSceneSchemaVersion` and `IsRetainedProjectedSceneSchemaVersion`.
- The hosts consume these contracts. They contain no footprint geometry of their own.

## Extent

`ResolvedFootprintExtent` describes the footprint on the sky, before any projection.

| Field | Unit / frame | Meaning |
| --- | --- | --- |
| `shape` | `Circle` or `Ellipse` | A `Circle` must have equal axes and a position angle of 0. |
| `semiMajorAxisDegrees` | degrees of arc | Half the major axis, in (0, 90]. |
| `semiMinorAxisDegrees` | degrees of arc | Half the minor axis, in (0, major]. |
| `positionAngleDegrees` | degrees, [0, 180) | Direction of the major axis, measured from celestial north (of date) through east. |
| `semiMajorAxisUncertaintyDegrees` | degrees, 1σ, or `null` | `null` means the source publishes no uncertainty. It never means zero. |
| `semiMinorAxisUncertaintyDegrees` | degrees, 1σ, or `null` | as above |
| `positionAngleUncertaintyDegrees` | degrees, 1σ, or `null` | as above |
| `source` | text | The provenance of the size, for example `asin(nasa-volumetric-mean-radius/topocentric-distance)-v1`. |

The Sun and Moon use `Circle` with the topocentric angular radius from `SolarDiskEphemeris`. Their uncertainties are
`null`. #525 sets the uncertainties from its catalogue when the catalogue publishes them, and leaves them `null`
when it does not.

## Frame and sampling

- `ResolvedSourceFrame.Create(geometricCenter, latitude)` builds an orthonormal tangent basis at the **geometric**
  (unrefracted, topocentric) centre:
  - local up and local east (toward increasing azimuth)
  - celestial north, which is the projection of the celestial pole onto the tangent plane, and
    celestial east = north × centre
- Celestial north falls back to local up at the pole, where the position angle is undefined.
- The limb is the ellipse `a·cos t · major + b·sin t · minor`, with `major = N·cos PA + E·sin PA`. It is mapped to the
  sphere with the exponential map, so the outline is exact for any extent up to 90°.
- Each limb direction goes through the scene pipeline, **one sample at a time**: geometric horizon test, refraction,
  projection, lens distortion. The outline is then clipped to the aperture or sensor. Near the horizon, refraction
  therefore lifts the lower limb more than the upper limb, and the disc flattens as it would in the sky.
- Sampling starts from 64 equal parameter steps. It subdivides any chord whose midpoint deviates more than
  0.25 px from the straight chord, or whose midpoint fails to project, up to depth 7. It then bisects the
  horizon or aperture boundary for 32 iterations.
- A footprint with no visible part is omitted. A footprint can therefore exist while its centre is off-frame or below
  the horizon. In that case `centerPixel` is `null`.

## Projected footprint

`ProjectedResolvedFootprint` is the per-frame result. Pixel values are in the coordinate space of the document that
carries the footprint, after its emitted-image transform.

- **Identity:** `id` (`solar-system:Sun`, `solar-system:Moon`, or a #525 deep-sky id), `displayName`, `sourceKind`,
  `contractVersion` and `samplingAlgorithmVersion`.
- **Extent:** `extent`, as described above.
- **Centres:**
  - `geometricCenter`: topocentric alt/az, without refraction.
  - `apparentCenter`: the same azimuth, with altitude = `AtmosphericRefraction.Apply(geometric)`.
  - `refractionModel`: `bennett-1982-standard-atmosphere-v1` when the scene enables refraction, otherwise `null`.
- **`centerPixel`:** the projected apparent centre. It is `null` if the centre is outside the crop, or is below the
  geometric horizon under `GeometricHorizon`.
- **Outline:**
  - `parts`: polylines of points.
  - `closed = true` only when the footprint is a single complete loop.
  - `clipped`: true unless the footprint is one closed part.
  - `bounds`: the axis-aligned bounding box of every point.
- **`appearance`:** present only for `SolarSystemBody`. It holds the ephemeris algorithm, the topocentric distance
  in km, the illuminated fraction, the bright-limb angle (degrees in the local horizontal frame, measured from local up toward increasing azimuth;
  this is not a celestial position angle) and the visual magnitude.

Structural limits per footprint: at most 256 parts and 16,384 points. Per scene: at most 64 footprints.

## Projected-scene schema

- A projected scene is `projected-scene-v2` **only when it carries at least one footprint**. Every other scene is
  still `projected-scene-v1`, byte for byte, and its identity is unchanged.
  - The v1 golden identity test pins this.
  - `resolvedFootprints` is omitted from v1 JSON and required, and non-empty, in v2.
- Readers accept both versions through `ProjectedSceneV1.IsSupportedSchemaVersion` and
  `SceneProvenance.IsRetainedProjectedSceneSchemaVersion`. Producers never choose the version themselves. It
  follows from the content.
- Validation recomputes the following from the scene's own projection, refraction and image transform:
  - every centre and its pixel
  - every point's membership of the output and the source aperture
  - `clipped` and `bounds`
  - agreement with any object that has the same id
- Validation rejects a footprint that does not agree.

## Topocentric Sun and Moon

`SolarDiskEphemeris.Get` returns the topocentric, unrefracted direction. `WithResolvedBodies` uses that direction for
the object and the footprint. The object's of-date and J2000 equatorial coordinates are derived from it, so they are
topocentric too. The Moon's diurnal parallax, up to about 1°, is therefore present in every pixel product.
Planets other than the Sun and Moon keep the geocentric `astronomy-engine-2.1.19-eqj-v1` objects.

## Consumer guidance for #525

- Produce `ResolvedFootprintExtent` values from the catalogue and call `ResolvedFootprintSampler.Sample` with
  `ResolvedFootprintSourceKind.DeepSkyObject` and a `null` appearance.
- Use a stable, ordinal-sortable id with a non-`solar-system:` prefix.
- Annotation layers draw `parts` as they are given. They must not re-project the extent.
