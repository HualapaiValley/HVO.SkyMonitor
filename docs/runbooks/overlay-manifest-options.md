# Overlay manifest layer overrides

In CameraAgent **Operations → Processing graphs**, open a draft and its
**Overlay manifest** panel (`overlay-manifest`). It describes the existing
`OverlayManifest` step at `overlay-manifest-v1`. Pipeline JSON remains authoritative.

Add a **Layer override** to choose an existing case-sensitive kind, its
**Enabled by default** flag and optional **Layer order**. The native selects,
inputs and add/remove/move buttons support keyboards and narrow screens. At most
ten rows are accepted. Legacy aliases remain supported; a kind and its alias
cannot both appear. Unknown fields inside rows travel with their row when moved.

**Use producer defaults** clears the overrides. Empty overrides preserve the
producer defaults: constellations at 10, image circle at 15, annotations at 20,
cardinals at 25, cloud mask at 30, cloud labels at 40 and environment at 50.
Optional satellite tracks default to disabled at 45 when their paired products
are present. An override does not create a missing optional layer.

**Use producer order** clears an individual order to JSON null. Omitted order
and null both retain that layer's existing order. Explicit orders range from
-1024 to 1024; smaller values draw first. Moving rows preserves source ordering
but does not determine drawing order. `scene-annotation` groups the annotation,
cardinal and image-circle layers, with order offsets of 0, +1 and -1 respectively.
A specific kind or alias takes precedence over the grouped override.

**Constellation opacity** accepts integer millionths from 0 (transparent) to
1000000 (opaque), with the existing default of 800000. The materializer uses
manifest enabled flags when its own selection is empty; an explicit materializer
selection can include a layer disabled by default.

Preview, save a draft, validate it and explicitly activate it. Saving never
activates. Use the graph controls to roll back to the previous revision. Restored
edits preserve unchanged source bytes and avoid an unnecessary revision. Invalid
rows or opacity prevent preview/save. **Use producer defaults** explicitly replaces
invalid row values or a null list; duplicate option members require correction in
pipeline JSON. Review JSON before saving a replacement.

Variants, exact producer versions, dependencies and `useRichFrameFacts` remain in
pipeline JSON. Required dependencies still apply even when a layer is disabled.
This panel adds no producers, rich-facts conversion, compositor or capture and
publication authority. Retained products and old revisions remain reconstructable
from their recorded inputs. Both hosts use the shared descriptor/codec and
materialization executor; this slice adds no LogicHost editing surface.
