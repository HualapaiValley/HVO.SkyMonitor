# Existing legacy Annotation styling

In CameraAgent, use **Current Sky → Edit legacy Annotation styling**, or open
**Operations → Pipeline → Named graphs**, select a retained revision and choose
**Draft a new revision from this one**. The `Annotation` node has a **Legacy
annotation** panel. The deep link explains when the selected graph has no
supported node; it does not insert, replace or upgrade one.

Edit the existing controls, **Preview plan**, then **Save immutable draft**.
Saving leaves the active revision unchanged. Validate the saved revision and
explicitly activate it to affect subsequent captures. To restore prior styling,
explicitly roll back to the earlier validated revision. Retained images and
archived replay using that revision keep its former styling and identity.

## Runtime effectiveness inventory

The shared Processing descriptor binds only registered alias `Annotation`
(case insensitive), exact implementation **`projected-annotation-v4`**. The
operator's `RecipeVersion` string (default `projected-scene-annotation-v3`) is a
retained artifact label, not the implementation version. Neither it nor the
algorithm identities change when opening the panel.

| Controls / JSON members | Code defaults | Bounds and effect |
| --- | --- | --- |
| `enabled` | `true` | Runs the node; explicit v2 graphs use the existing top-level node enabled field. Legacy v1 keeps its options-level behavior. |
| `markRadius`, `markerValue` | `6`, `144` | Radius 0–32 px; intensity 0–255. Dotted point markers use both; zero radius draws one pixel. Resolved footprints retain their existing outline/point selection rules and use marker intensity. |
| `drawLabels`, `labelScale`, `maximumLabelMagnitude` | `true`, `1`, `2.5` | Scale 1–8; magnitude −30–30. Existing named stellar label/marker selection uses the magnitude ceiling; solar-system selection and measured-label qualification stay unchanged. Label intensity stays fixed. |
| `drawConstellationLines` | `true` | Selects segments from the existing scene and `ConstellationIds`. |
| `constellationLineValue` | `160` | 0–255; **Mono8 previews only**. It has no RGB effect. |
| `constellationLineRed`, `constellationLineGreen`, `constellationLineBlue` (one color control) | `96`, `160`, `255` | Each channel 0–255; **RGB previews only**. These channels have no Mono8 effect. |
| `constellationLineThickness`, `constellationLineOpacity` | `1`, `0.8` | Thickness 1–8 px; opacity 0–1, on both Mono8 and RGB previews. |
| `drawImageCircle`, `imageCircleValue` | `false`, `96` | Existing projection border visibility and intensity 0–255; requires projection landmarks. |
| `drawCardinalDirections`, `cardinalValue`, `cardinalScale` | `false`, `255`, `2` | Existing N/E/S/W visibility, intensity 0–255 and bitmap scale 1–8; requires projection landmarks. |
| `drawMetadataCorners`, `metadataValue`, `metadataScale` | `false`, `255`, `1` | Existing corner lines' visibility, intensity 0–255 and bitmap scale 1–4. |
| `metadataInset`, `metadataLineSpacing` | `4`, `2` | Inset 0–64 px and additional line spacing 0–16 px. Existing token content and environment association remain authoritative. |

This inventory follows `AnnotationCaptureProcessingStep.ProcessAsync`,
`AnnotationRecipe.CreateAnnotationOptions` and `AnnotationRenderer`:

- The step passes marker, label, projection-border, cardinal and constellation
  styling to the shared recipe. Magnitude/constellation selection happens in
  the step. Metadata styling travels with the existing `MetadataCornerOverlay`.
- Both constellation intensity and RGB channels reach the recipe and renderer,
  but apply to different preview formats. Mono8 constellation lines use
  `ConstellationLineValue`; RGB lines use `ConstellationLineRed/Green/Blue`,
  defaulting to **96/160/255**, with the configured thickness and opacity.
  The panel labels both format limits: changing intensity cannot recolor RGB
  pixels, and changing RGB channels cannot recolor Mono8 pixels. Such ineffective
  cross-format choices are not offered as general intensity/color controls.
- Marker, circle, cardinal and metadata intensities are
  grayscale, applied to all RGB channels. Visibility controls need their
  corresponding scene, projection or metadata content to draw anything.
- `DrawLabels` does not license unmeasured stellar labels. Existing
  `ExpectedPositionDiagnostics` and measured-association rules remain in force;
  curated deep-sky labels retain their separate existing rules.

Pipeline JSON stays authoritative. The shared descriptor/codec changes only
edited scalar tokens, preserving member case, whitespace, numeric spelling,
unknown fields, protected collections and identity settings. Missing options
display code defaults without writing them. A restored value keeps its source
bytes; unchanged drafts cannot be saved as new revisions. Explicit nulls are
preserved untouched; null for a described scalar is invalid and is reported
rather than silently treated as its default.

`TopLeftTokens`, `TopRightTokens`, `BottomLeftTokens`, `BottomRightTokens`,
`EnvironmentalKinds`, `ConstellationIds`, `RecipeVersion`, `OutputVariant`,
`RequireProjectedSceneDependency` and `ExpectedPositionDiagnostics` remain
preserved JSON settings. No templates, fonts, glyphs, rich text, new renderer,
qualification change or automatic conversion to
layered presentation/Frame Facts is included. The same shared recipe remains
available to CameraAgent and LogicHost; LogicHost does not gain edge metadata
collection or a new configuration surface. #1386 remains a partial delivery.
