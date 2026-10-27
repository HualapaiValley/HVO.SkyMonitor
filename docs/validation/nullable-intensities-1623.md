# Existing scene and environment intensities (#1623)

In the processing graph editor, open **Scene overlay** for Marker intensity,
Label intensity, Image circle intensity and Cardinal intensity, or
**Environment layer** for Environment intensity. Each accepts a whole number
from 0 to 255. Zero explicitly draws black; it does not mean unset. Blank input
or **Use default palette** clears an override. The byte-backed overrides reject
numeric `-0`, matching the producer binder; explicitly entering `0` repairs that
invalid source spelling while preserving every unrelated byte. The producer's current grayscale
palette returns: marker 144, label 255, image circle 96, cardinal 255 and
environment text 255. Setting an already-white text override to 255 therefore
retains its existing rendered bytes and product identity.

Preview and save an immutable draft, validate it, then activate it explicitly
from Named graphs. Rollback selects the earlier revision. Saving alone does not
activate a graph or change retained products. Untouched absent members stay
absent; untouched explicit nulls and all unrelated JSON retain their source
bytes. Editing and restoring a value before saving is a no-op.

The shared descriptors pin ScenePresentationLayer's existing v12 and v13
producer versions and EnvironmentPresentationLayer's existing
`metadata-corner-presentation-v6-original-colours` version. No new RGB fields,
producer versions, renderer identities, palettes, captions or measured/expected
qualification rules are introduced. Rich Frame Facts and other style families
remain under #1386 and their existing issues.

## Regression evidence

- `ProcessingNodeOptionsTests.Intensities`: exact-version nullable bounds,
  absent/null/zero/255 spelling and byte preservation, wrong types/nonfinite
  input/duplicates, shared graph validation and round-trip identities on both
  host compilation lanes.
- `CaptureScalarOptionsEditorTests.Intensities`: every control sets zero/255 or
  clears a present value, preserves no-ops and unrelated bytes, and rejects
  invalid input even after a valid preview. Labels, help/status associations
  and explicit-save behavior are checked. Existing denied-access tests cover
  both panels; service tests reject invalid and unauthorized raw edits before
  compilation or storage. Signed-zero regressions exercise the real pipeline
  factory and the UI repair action for every intensity field.
- `ProcessingGraphOperationsTests.RetainedLayerOptions`: registered versions,
  runtime defaults/bounds, unchanged effective plan, validation before
  activation, immutable retained revisions and rollback for both producers.
- `PresentationMetadataFactsBuilderTests.Intensities` and `.LayerOptions`:
  edited form JSON reaches real scene/environment capture producers. Zero and
  255 reach the selected primitives, other layers and captions remain intact,
  and clear/old-revision reconstruction reproduces palette bytes and identities.
  Environment observations retain missing/stale statuses and source lineage.
- `HostRecipeContractConformanceTests.Scene`: retained scene reconstruction
  through LogicHost and CameraAgent adapters, shared nullable scene/environment
  options, deterministic materialization and clear-to-palette reconstruction.

## Browser evidence

The [scene panel](scene-intensity-1623.png) and
[environment panel](environment-intensity-1623.png) show actual bUnit-rendered
components with shipped Bootstrap, theme and scoped CSS. T3 preview checks at
1280×800 and 390×844 found no horizontal overflow or duplicate IDs. Keyboard
Tab moves from an intensity field to its palette action. These are disposable
component fixtures; service/coordinator/producer tests provide execution evidence.
Mobile screenshots and rendered HTML are retained with the local issue evidence.

Tier B; protected profile `development-v1`. The PR ledger records classifier
selection, exact candidate and passing local results. Heavy builds/tests are
serialized under the host gate lock with node reuse/shared compilation disabled;
Unit tests unset the production catalog and use the invalid Docker endpoint.
No deployment or main/release qualification is requested.
