# Frame Facts corner templates

In CameraAgent, **Current Sky → Edit Frame Facts corner templates** opens a draft
of the active graph at its Frame Facts section. Alternatively, open **Operations
→ Pipeline → Named graphs**, choose a retained revision, then **Draft a new
revision from this one**. The source revision remains immutable.

## Convert existing presets explicitly

A legacy `EnvironmentPresentationLayer` node keeps its existing presets until
you select **Review whole-draft Frame Facts conversion**. Inspect both complete
pipeline definitions, then choose **Apply conversion to draft** or **Cancel
conversion**. The proposal must compile before it can be accepted.

Conversion changes every legacy preset node to the opt-in
`FrameFactsPresentationLayerV1` alias and enables `useRichFrameFacts` on the
existing manifest/materializer nodes together. It preserves node IDs, order,
dependencies, publication policy, variant names, selected environment kinds,
unknown option members and their spelling/content. Omitted legacy environment
selection, inset and variant defaults are frozen explicitly where the rich
version has different defaults. Legacy automatic typography becomes explicit
16-pixel packaged Mono typography; inspect the new appearance. A legacy
selection with more than seven environment kinds cannot become a single
8-line corner preset; conversion rejects the whole proposal. Reduce that
selection explicitly before conversion or author a rich node separately.

No saved revision, current selection or historical product changes when you
review or accept the proposal. Conversion is a draft operation; saving,
validation, activation and rollback remain separate.

## Edit any corner

Each of **Top left**, **Top right**, **Bottom left** and **Bottom right** accepts
literal text and any token from **Supported tokens and formats**. The catalogue
shows the retained source, unit, provenance, default format and every allowed
scalar format. Status companions support only `text`. Token names and formats are case sensitive. For example:

```text
**CAPTURE {{capture.sequence}}**
UTC {{time.captureUtc|time}}
__GAIN__ {{acquisition.gain|fixed1}}
```

Use `**bold**`, `*italic*`, `__underline__`, backslash escapes and actual newlines.
Markers can nest. HTML, scripts, expressions and custom format strings are
rejected. `spaceWeather.*` remains reserved. The [grammar contract](../contracts/frame-facts-template-v1.md)
contains the exact syntax, closed formats and diagnostic codes.

Choose a pinned **MonoRegular**, **MonoBold** or **PlexRegular** font, size in
thousandths of an image pixel (4,000–64,000), and RGB colour with the picker or
hex input. Plex regular cannot render bold emphasis; choose a Mono face when
using bold. The dark corner panel remains. Set the presentation width/height to
the actual image dimensions; inset is 0–64 pixels and extra line spacing is
0–16 pixels. Text must fit its own image quadrant. No clipping, shrinking or
wrapping hides an oversized template.

Per corner, the bounds are 4,096 UTF-16 source units, eight lines and 64 Unicode
characters per rendered line. Empty source hides a corner; actual blank lines
remain. Errors show the affected option/corner and stable codes/offsets. Invalid
options cannot be saved through the UI service.

**Placeholder** renders `UNAVAILABLE` for missing, stale, conflicting or
unsupported scalar facts. **DropLine** removes a line with an unavailable
scalar unless it also contains a `.status` token; the complete line remains in
that case so the reason is visible. No current weather, rig, clock or time-zone
lookup supplies absent retained evidence. Environmental tokens require their
kind to have been selected for retention in the pipeline's `environmentalKinds`;
conversion preserves that selection. A missing kind remains unavailable.

## Preview frozen facts, then save and select

**Render frozen preview** uses the shared evaluator, pinned font layout and
raster compositor on a plain background, with a text alternative for each
corner. It is an overlay preview at the configured image dimensions, not a new
capture or a live image. Before a facts file is loaded, every scalar is explicitly
`MISSING`.

Optionally load a retained `presentation-metadata-facts-v3` JSON product or a
`frame-facts-snapshot-v1` JSON snapshot (at most 1 MiB). Obtain retained products
from the existing artifact download/read path. The catalogue and content
identity are validated. Preview becomes available after the file finishes validation. The displayed
snapshot identity stays fixed while you edit. Invalid files clear the old preview and return to missing evidence;
legacy facts v2 have no rich snapshot and are rejected with that explanation.
The input exists only in the current form; it is not saved in the pipeline or a
separate settings store. Editing a rendering option clears the former preview.

1. Give the draft a new revision label and select **Preview plan**. This compiles
   the entire graph against the installed node contracts without changing state.
2. Select **Save immutable draft**, then **Open the saved draft**. Opening and
   previewing an unchanged draft, including restoring all original values, keeps
   save disabled. A successful save cannot be submitted again until you edit.
3. In **Named graphs**, **Validate**, then **Activate** the saved revision and
   confirm. A capture already accepted retains its selected graph.
4. Use **Review rollback** on the validated configured basic graph or a previously
   activated revision to restore its original
   settings for subsequent captures. Retained captures, facts, presentation
   layers and source definitions keep their original bytes and identities.

Operations authorization is checked by the existing read/mutation services.
An unauthorized caller cannot preview or save a graph through the service.
This feature adds no camera, deployment, promotion or other filter-family work.
