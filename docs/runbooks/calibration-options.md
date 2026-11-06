# Calibration options

From CameraAgent **Operations → Pipeline → Graphs**, create a draft from a retained
revision and open **Calibration**. The direct link
`/operations/pipeline/graphs/new?section=calibration#calibration` starts from the
active graph. Opening it never adds or enables a node. A new calibration node
needs an explicit raw dependency.

The shared Processing catalog describes registered alias `Calibration` at both
existing step versions: `linear-normalization-none-v1` for `None`, and
`reference-calibration-linear16-v2` for `SyntheticReferences` or
`CalibrationLibrary`. Both hosts resolve these same descriptors. No recipe,
version, settings store, or automatic conversion is introduced.

| Control | Default | Existing meaning and limits |
| --- | --- | --- |
| Enabled | `true` | Edits the v2 node's enabled field. Enabled dependent nodes require an enabled producer. |
| Calibration strategy | `None` | Exactly `None`, `SyntheticReferences`, or `CalibrationLibrary`, including case. |
| Calibration output variant | `none` | Graph identifier, 1–128 characters, trimmed, without control characters. Update variant-specific dependent inputs separately. |

`None` normalizes raw samples without reference correction. `SyntheticReferences`
requires the retained `SyntheticCalibration` model and the capture's matching
model identity. `CalibrationLibrary` selects existing active compatible masters;
missing or inactive references skip, and incompatible or corrupt references fail
closed under the existing calibration rules. Both reference strategies require
linear 16-bit input; the existing layout and conditions checks still apply. The panel does not select different
masters or change the active library.

The nested model remains opaque and is never edited, created, or removed by this
panel. The existing server validation rejects `SyntheticReferences` without a
model and rejects a model with either other strategy. Therefore, a graph carrying
a synthetic model cannot switch away from `SyntheticReferences` through this
scalar-only panel. Start from a suitable retained graph instead. Invalid previews
cannot save a previously valid preview.

`CalibrationPasses` and `MaxCalibrationSeconds` are compatibility properties, not
runtime-effective controls, so they are not shown. Their existing validation
ranges (1–10 and 1–600 respectively) still apply to retained JSON. Unknown members,
model contents, source spelling/formatting and output routing are preserved unless
the corresponding scalar is explicitly edited. Returning an absent option to its
default does not materialize it; unchanged drafts cannot be saved as new revisions.

Use **Preview plan → Save immutable draft → Validate → Activate**. Saving alone
never changes live capture. **Rollback** restores an eligible retained revision
with the current registry version. Old graph/product identities and raw/master
bytes remain retained for reconstruction. Master acquisition, model editing,
physical qualification, and actual-rig activation are outside this slice.

This is the bounded #1566 delivery, Refs #1386; #1386 stays open.
