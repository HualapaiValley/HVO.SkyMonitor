# Deep-sky objects — OpenNGC v1

Issue #525 places OpenNGC deep-sky objects in the projected scene and offers an
opt-in presentation layer that draws them. They come from the composed catalog
[HYG 4.4 + OpenNGC](../catalog/hyg-v44-openngc.md). Each object carries its
catalogue identity and, where the catalogue has one, its extent: a sourced
outline, an ellipse drawn through the [resolved footprint](resolved-footprint.md)
contract, or a class symbol.

Every drawn deep-sky primitive is **expected catalogue geometry**. It shows where
the catalogue says an object is, never that the image shows it. Deep-sky
objects are never star, reference or astrometry candidates. They never enter
the measured label path that #526 gates on detections.

**Not included:**
- Sharpless 2 objects, which the composed catalog drops
- survey or reference imagery
- the search experience, which belongs to #524. #525 adds only the
  `CelestialObjectSearch` seam that resolves deep-sky aliases.
- deep-sky content on the raster annotation product. The annotation recipe,
  `ProcessingAnnotationInput` and the raster versions are unchanged.
- deep-sky content from the physical stager, which stays on scene schema v1
  until physical qualification (#1166)

## Measurement status

M1/AC8 acceptance remains pending. The corrected harness and its prospective
query, CPU, full-resolution resource and cache interpretation are described in
[the measurement contract](../validation/issue-525-measurement.md). Smoke proves
functional output and bounds; it does not replace the allocated ABBA campaign
or the other explicit acceptance evidence.

## Ownership

- **`HVO.SkyMonitor.Astronomy`** owns the catalogue and scene contracts:
  - `IDeepSkyCatalog`, `DeepSkyObject`, `DeepSkyOutline` and the
    `DeepSkyObjectTypes`, `DeepSkyAliasKinds` and `DeepSkyTombstoneReasons`
    vocabularies, in `DeepSkyCatalog.cs`
  - `ProjectedSceneDeepSkySelection`, `ProjectedDeepSkyObject`,
    `ProjectedDeepSkyOutline`, `ProjectedDeepSky` and the projector, in
    `DeepSkyScene.cs`
  - `VisibleScene.WithDeepSky`, the deep-sky transforms in
    `VisibleSceneReadoutTransform` and `ProjectedSceneImageTransform`, and
    `projected-scene-v3` serialization and validation in
    `ProjectedSceneContracts.cs`
- **`HVO.SkyMonitor.Catalog.Sqlite`** reads the schema-4 deep-sky tables into
  `IDeepSkyCatalog`.
- **`HVO.SkyMonitor.Processing`** owns `PresentationDeepSkyLayerProducer`, the
  layer that draws a scene's deep-sky collection.
- The hosts consume these contracts. Neither host contains deep-sky geometry of
  its own. Ellipses are sampled by `ResolvedFootprintSampler`, and outlines by
  the same projector, so there is no second projection path.

## Scene schema

A scene with a deep-sky collection is serialized as `projected-scene-v3`. Every
other scene keeps the schema it had before: `projected-scene-v1` without
resolved footprints, `projected-scene-v2` with them. Their bytes are unchanged.

`projected-scene-v3` adds two members:

- `selection.deepSky`, a `ProjectedSceneDeepSkySelection` that records the
  bounds the collection was placed under
- `deepSky`, a `ProjectedDeepSky` collection (`projected-deep-sky-v1`) with the
  OpenNGC source name, version, commit, URL and license, the count of omitted
  candidates, the placed objects in ID order and their outlines in ID order

Deep-sky ellipses are ordinary `resolved-footprint-v1` entries in the scene's
`resolvedFootprints`, with source kind `DeepSkyObject` and the object's scene
ID. Scene IDs take the prefix `deep-sky:`, so they never collide with star or
solar-system IDs.

**Upgrade order.** A reader that knows only v1 and v2 refuses a v3 scene at
`$schemaVersion`; it does not misread it. Deploy LogicHost before any CameraAgent
emits v3 scenes. A retained v1 or v2 capture regenerates byte-identical scene,
identity and provenance bytes after the upgrade, because the deep-sky members
are written only when a collection is present.

## Selection

Deep-sky placement is off unless the virtual camera module configures it
(`"DeepSky": { "Enabled": true }` under the module options). Each bound defaults
to `ProjectedSceneDeepSkySelection.Default`:

| Bound | Default | Accepted range |
| --- | --- | --- |
| `maximumObjects` | 300 | 1–2,000 |
| `maximumFootprints` | 64, the shared resolved-footprint budget | 0–64 |
| `maximumOutlines` | 64 | 0–256 |
| `preferredOutlineLevel` | 1, the widest | 1–3 |
| `minimumGlyphPixels` | 8 | greater than 0, up to 64 |

The footprint budget is shared with the Sun and Moon: deep-sky ellipses use only
what the scene's other footprints leave.

## Placement

The projector considers every catalogue object except a stellar (`*` or `**`)
row without a Messier number. Stellar rows are identities only, so M40 is the
one double star a scene draws.

An object's pixel follows the resolved-footprint centre rule. It is present only
where the scene would admit a point in that direction. An object whose centre
is outside the field is still considered when its major axis or outline can
reach into the field.

Candidates are ordered by `deep-sky-priority-v1-featured-major-axis-pixels-magnitude-id`:

1. featured objects first, those with a Messier or Caldwell number or a common
   name
2. then the larger projected major axis, unknown last
3. then the brighter V magnitude, falling back to B, unknown last
4. then the ordinal ID

Representations are assigned in that order, and the scene budgets are spent in
the same order:

| Representation | When |
| --- | --- |
| `Outline` | The object is resolvable and has a sourced outline. The available level closest to `preferredOutlineLevel` is used, the wider on a tie. |
| `Footprint` | The object is resolvable, has no drawn outline and has an oriented extent: both axes and a position angle, or equal axes. |
| `SizedGlyph` | A class symbol drawn at the projected major-axis size. |
| `MinimumGlyph` | A fixed-size class symbol for an object below `minimumGlyphPixels`. |
| `UnknownExtentGlyph` | A fixed-size class symbol for an object with no catalogue extent. |
| `StellarGlyph` | A star or double-star symbol for a stellar row placed for its Messier identity. |

An object is resolvable when it is not stellar and its projected major axis is
unknown or at least `minimumGlyphPixels`. An object with only a major axis, or
with two unequal axes and no position angle, cannot be oriented, so it gets a
size-qualified glyph rather than a guessed ellipse. A catalogue position angle
is rotated from J2000 north to north of date before sampling.

When a budget runs out, the object falls back to the next representation and
records the budget in `degradation`: `OutlineLimit` (the outline count, one
outline's 256 parts or 16,384 points, or the scene's 16,384-point total) or
`FootprintLimit`. An object off the field with no glyph pixel is dropped. Once
`maximumObjects` objects are placed, the remaining candidates are counted in
`omittedCandidateCount`.

## Outlines

`ProjectedDeepSkyOutline` (`deep-sky-outline-v1`) is one object's sourced
outline at one contour level. Each ring is sampled along great circles by
`deep-sky-outline-great-circle-adaptive-v1` through the resolved-footprint
projector. It is `clipped` unless every ring survives as one closed part. The
readout and image transforms (bin, crop, mirror, rotation) carry outlines with
the scene and mark one clipped when a transform cuts it.

## Presentation layer

`PresentationDeepSkyLayerProducer` (`deep-sky-presentation-layer-v1`) draws the
collection as a separate overlay layer:

- layer kind `deep-sky`, z-order 12, `enabledByDefault` false
- layer options record the basis `catalog-expected-geometry` and the legend
  "catalog position — not a detection"
- every primitive is dashed (6 px on, 4 px off) and drawn in one colour that no
  measured layer uses. Unknown-extent glyphs use a shorter dash.
- the layer reads only canonical scene facts, never base pixels, so the edge and
  central hosts draw the same bytes from the same scene

The layer spends at most an eighth of the compositor's geometry work and the
compositor's per-layer primitive budget, so turning it on cannot starve the
existing layers. Objects are drawn in scene priority order. Each draws the
outline or footprint the scene assigned it. When that geometry would exceed the
layer budget, the object falls back to its class glyph. It is omitted only when
the glyph does not fit either. Together with the scene's own budgets, the
degradation order is outline, then footprint, then glyph.

Labels have their own reserved share of the budget. By default, only featured
objects are labelled: up to 24 labels of up to 24 characters, using the
display name. Other NGC and IC designations are labelled only when
`DeepSky.CatalogLabels` is set. M102 is never a display name, so it is never
labelled or drawn.

### Configuration

The layer is configured on three capture processing steps and is absent unless
configured:

- `ScenePresentationLayer`: a `DeepSky` section adds a fifth output, the
  deep-sky layer. `OutputVariant` names it, and `CatalogLabels` and
  `MaximumLabels` (default 24) control labels
- `OverlayManifest` and `PresentationMaterializer`: `DeepSkyVariant` names that
  output, so the manifest records the layer and the materializer can select it

A definition without these options keeps its existing outputs and plan identity
byte for byte: each option is omitted from the effective options while unset.
Once the `DeepSky` section is present, its members take their defaults, so an
explicit default and an omitted one have the same plan identity. The scene-consuming layer steps keep declaring the
`projected-scene-v1`/`projected-scene-v2` dependency set
(`LayeredPresentationCaptureProcessing.PlanIdentitySceneSchemaVersions`). A v3
scene still satisfies that dependency at run time, because it satisfies the
declared family root.

Because the layer starts disabled, a viewer shows it only when the operator
turns it on. The CameraAgent Current Sky page lists it as "Deep-sky (catalog
positions)" with its legend. It reports when a capture retained no deep-sky
layer.
