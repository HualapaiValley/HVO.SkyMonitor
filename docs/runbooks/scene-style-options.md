# Versioned scene overlay style

Open **Named graphs → Edit → Scene overlay** (the existing
`/operations/pipeline/graphs/new?section=scene-overlay#scene-overlay` deep link).
For an existing `ScenePresentationLayer`, select **Review whole-draft scene
style upgrade**, inspect the before/after graph and apply it to the draft.
Every legacy scene node, including disabled nodes, becomes
`SceneStylePresentationLayerV1`. Manifest and materializer consumers explicitly
accept the new retained payload together. IDs, variants, dependencies,
publication controls and unrelated JSON remain attached to the draft.

The upgrade preserves constellation controls and maps non-null grayscale
intensities to equal RGB channels. Absent/null intensities select the previous
palette: marker 144/144/144, label and cardinal 255/255/255, image circle
96/96/96. Labels/cardinals default to MonoBold; point markers default to Circle.
The v12 portable upgrade retains its strict expected-label setting when absent.
The installed legacy alias continues to bind its existing v13 producer.

Choose marker, label, cardinal and image-circle RGB colours, independent label
and cardinal packaged faces, and Circle or Crosshair point markers. The pinned
faces are DejaVu Sans Mono regular/bold and IBM Plex Sans regular. Face and
weight are a closed choice; Plex bold, downloaded fonts, arbitrary Unicode
markers and unrecognized values fail validation. Font assets and checksums are
unchanged. Existing scales, label budgets, magnitude selection, layer visibility
and decluttering still apply. Larger glyph bounds may admit fewer labels.

**Preview plan**, then **Save immutable draft**. Validate the saved revision
and activate it from Named graphs. Validation is required before activation;
stale registry versions and denied edits cannot activate a graph. Rollback
selects the actual previous revision. Saving or previewing alone changes no
active graph. Reading, validating or saving an unchanged old draft never
selects the styled producer.

Point glyphs retain their existing projected coordinates and cannot turn
expected positions into measurements. Catalog/expected scientific captions,
dashed predicted geometry, missing facts and source pixels retain their
existing rules. Deep-sky qualification captions keep their fixed palette and
fonts even when object label colours/fonts are edited. Satellites, Frame Facts,
time-lapses and catalog query selection do not adopt these settings.

The producer is `projected-scene-presentation-v14-explicit-style`; its exact
shared descriptor is `scene-style-presentation-options-v1`. New layers use
`presentation-layer-payload-v5-scene-style` and
`typed-presentation-compositor-v8-scene-style`, retaining the style identity,
pinned font-set identity through that hash, scene identity and actual source
lineage. Readers that do not support the new payload reject it. CameraAgent
and LogicHost use shared payload readers, raster materialization and grouped
SVG glyph paths; retained layers do not consult the currently active style.
Old v12/v13 graph JSON and v1–v4 retained payloads retain their previous
identities and rendering.

Packed Mono8 composition uses the red channel, matching the existing colour
contract. Display composition promotes a monochrome base to RGB so chosen
colours survive. RGB input remains RGB. Layer selection and opacity continue
through the existing manifest/materializer controls.
