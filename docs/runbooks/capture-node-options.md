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
retained revisions. These four HTTP commands return 409 ProblemDetails with the
profile-policy guidance when they reject a mismatch. Disabled Storage nodes do
not publish and can retain their options.

Raw acquisition and startup recovery retain the original immutable graph binding
even if that graph's Storage no longer matches the active profile. Before any
standard processing node runs, CameraAgent checks the current profile. A mismatch
abandons standard processing with lane reason `storage-policy-mismatch` and
execution reason `processing.storage-policy-mismatch`. Raw bytes and sidecars
remain retained under the existing raw retention/upload rules; this condition does
not turn raw ingress unhealthy or stop recovery at the first incompatible capture.
Later compatible work can continue. No Storage publication runs for skipped work.

For a planned Storage change:

1. Pause acquisition and let in-flight standard processing finish before changing
   the profile. Already running processing is not transactionally migrated.
2. Change Storage through the local capture-profile configuration owner. Reconcile
   a graph draft with that policy, then save, validate and activate it. If retained
   artifact-policy collections differ, start from the current configured-basic
   revision; this scalar panel does not edit those collections.
3. Resume acquisition after the matching graph is active. Pending work bound to
   an incompatible old graph is skipped; activation resumes compatible/new work
   and does not silently replay or rewrite old captures. Only matching-policy
   graphs can be selected by an explicit activation or rollback command.

Other graph settings can still be edited and activated while preserving Storage.
This panel does not update the profile or teach retention/upload workers to read
graph revisions. Explicit archived replay keeps its existing retained-revision
path and does not gain a current-profile Storage check in this delivery.

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
