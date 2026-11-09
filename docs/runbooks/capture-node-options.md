# Capture analysis, satellite and storage panels

Open **Operations → Pipeline → Graphs**, start a draft from the desired revision,
and expand the node's named settings panel. The registered analysis, satellite
and storage nodes now join the existing filter and overlay panels.

| Panel | Existing controls | Bounds and routing |
| --- | --- | --- |
| Measured stellar associations | Enabled, output variant | Variant: 1–128 characters; enabled dependents still require their producer |
| Measured astrometry | Output variant, capture stride | Variant: 1–96 characters; stride: 60–1,000,000 captures |
| Capture satellite predictions | Output variant, allow stale-element diagnostics | Variant: 1–96 characters; stale diagnostics retain their qualification |
| Predicted satellite layer | Input variant | 1–96 characters; must match the upstream prediction output variant |
| Artifact storage | Inspect and reconcile storage directory, retention, latest-frame publication and upload queue with the active capture profile | Nonblank directory; retention: 1–3650 days; all enabled Storage options must match the active profile |

The graph editor shows field names, help and ranges. Invalid values disable the
current plan preview and save, including when an earlier preview was valid.
Satellite output routes also name the retained `-inputs` and `-status` products;
change the layer's input route together with the prediction route.

Preview the plan and save a new immutable revision. Validate the saved revision,
then explicitly activate it. Saving does not run a node, move artifacts, change
retention, enable a provider or start upload. The prior revision remains available
for explicit rollback. Reloading a saved draft restores its settings.

Storage policy belongs to the **active local capture profile**. Retention sweeps
and upload draining follow that profile. A graph draft cannot independently
override its Storage directory, retention, latest-frame/upload flags or artifact
policies. Preview and save reject an enabled Storage node that differs from its
corresponding enabled profile node; validation, activation and rollback recheck
retained revisions. Newly selected live work also fails closed for an incompatible
old revision. Disabled Storage nodes do not publish and can retain their options.

Change Storage through the local capture-profile configuration owner first, then
reconcile the graph's fields with that policy. Other graph settings can still be
edited and activated while preserving Storage. This panel does not update the
profile or teach retention/upload workers to read graph revisions. Explicit
archived replay keeps its existing retained-revision path.

Defaults match the existing runtime option classes. Unchanged saves preserve
absent defaults, source member casing, unknown fields and nested scientific
inputs. Unsupported producer versions do not borrow an editor from another
version. This delivery introduces no scientific algorithm or rendering version.

Use JSON numbers for numeric settings. Runtime binding can accept quoted numbers,
but the typed editor currently rejects those tokens until the affected fields
are explicitly corrected. It does not silently rewrite retained values.

These scalar panels preserve association/label-policy settings, astrometry
catalog/clock/prior/solver inputs, satellite catalog/work-bound/time settings and
artifact-specific storage policies. Their richer editors and the remaining
per-overlay styling are tracked by #1386; this partial delivery does not close it.
