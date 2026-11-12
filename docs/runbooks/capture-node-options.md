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
even if that graph's Storage no longer matches the active profile. Before
standard processing, CameraAgent checks the current profile. On a mismatch it
first commits the capture-time ProjectedScene product through the existing durable
node path, when the capture carries scene evidence. Image and Storage nodes do not
run. A missing scene stage remains retryable under the existing live-work limits.
A required scene node keeps its normal required-node failure behavior. An optional
scene node is not promoted to required: if its stage stays missing, corrupt or
mismatched through the retry limit, or it has another terminal/skipped outcome,
standard work expires with lane reason `storage-policy-mismatch-scene-unavailable`
and execution reason `processing.storage-policy-mismatch-scene-unavailable`.
This optional failure does not quarantine the lane or refuse the next raw capture,
including after restart. The node retains its failure evidence; no scene is invented.
The capture is not reported as an intact, scene-recoverable skip before its scene
evidence commits. Once that evidence is committed, the mismatch abandons
the remaining standard processing with lane reason `storage-policy-mismatch` and
execution reason `processing.storage-policy-mismatch`. Raw bytes and sidecars
remain retained under the existing raw retention/upload rules. Storage-policy
skips, including exhausted optional scenes, do not turn raw ingress unhealthy or
stop recovery at the first incompatible capture. Genuine raw corruption or a
required-node failure retains the normal evidence/required-lane failure rules.
Later compatible work can continue. No Storage publication runs for skipped work.
For a Named graph, processing health reports `active-storage-policy-mismatch`
while it still differs from the profile, with one error log on entry and one recovery
log when it clears. Configured-basic refreshes from the active profile in the same
health check and needs no manual reconciliation or activation; a routine profile
change in that mode raises no mismatch alarm.

Actual abandoned standard work is a separate health signal in both modes:
`StoragePolicySkippedCount` counts standard-lane rows abandoned in the past 24 hours for
`storage-policy-mismatch` or `storage-policy-mismatch-scene-unavailable`.
Processing health is Degraded with reason `storage-policy-skipped` while that
count is nonzero, including after restart and after the active graph matches.
Inspect the affected executions and explicitly replay intact retained inputs
when needed. Successful new work and replay do not erase historical skips; each
skip ages out of this health window after 24 hours without changing its evidence. It does not
stop raw acquisition or compatible processing, or classify unrelated abandoned
work as a Storage skip.

Mismatch guidance names differing fields without showing directory paths or
policy values. The fence, lifecycle/raw-ingress/index locks, retention root
comparisons, and pressure/outbox state keys share one lexical full-path identity:
trailing separators, dot segments and equivalent relative spellings map to the
same root. This does not rewrite configured paths or change the storage layout.
Artifact-policy StepId selectors compare ignoring case. On Linux,
backslash is a filename character, not a directory separator. Policy order is
preserved: equally specific overlapping policies use the first match. Storage-policy
comparison is order-sensitive: a reordered policy array is a real policy conflict,
including when its individual rules are unchanged. Reconcile it with the active
profile before preview, save or selection; no order-insensitive normalization applies.

For a planned Storage change:

1. Pause acquisition and let in-flight standard processing finish before changing
   the profile. Drain pending standard work too if those captures must complete
   under the old policy, even for a retention-only edit in the same directory.
   Already running processing is not transactionally migrated: switching before
   it finishes can still publish to an old root that the new profile will not drain.
2. Change Storage through the local capture-profile configuration owner. Reconcile
   a graph draft with that policy, then save, validate and activate it. If retained
   artifact-policy collections differ, refresh **Operations → Pipeline → Graphs**
   and start from the current configured-basic revision. Reading the registry
   refreshes that source from the active profile while acquisition stays stopped;
   it preserves the active Named graph. No capture or restart is needed. This
   scalar panel does not edit those collections.
   In configured-basic mode, the profile refresh supplies and selects the matching
   revision automatically; manual graph reconciliation/activation is only needed
   when a Named graph remains active.
3. Resume acquisition after the matching graph is active. Pending work bound to
   an incompatible old graph is skipped; activation resumes compatible/new work
   and does not silently replay or rewrite old captures. Only matching-policy
   graphs can be selected by an explicit activation or rollback command.

Find skipped captures in **Operations → Pipeline → Executions**
(`/operations/pipeline/executions`). Open an expired live execution and check
`processing.storage-policy-mismatch`; its capture/artifact IDs identify the
retained source. The `processing.storage-policy-mismatch-scene-unavailable`
variant means an optional scene could not commit. A replay graph requiring that
scene cannot replay this capture; archived replay cannot reconstruct the missing
scene from a transient stage or current settings. From that capture's archive page, use **Submit replay**, choose
the reconciled validated/active revision, review the frozen inputs and confirm.
Replay uses the committed capture-time scene product, including after restart;
it does not recreate a scene from current settings or rewrite the original raw
manifest, graph binding or expired execution. Replay results do not publish or
upload automatically. Recovery requires the raw and scene products still to be
retained; normal retention and live retry/deadline limits continue to apply.

Retries of an acknowledged create with the same request/key return the recorded
revision even after a profile change or restart. Reusing that key for a different
request conflicts; a new key does not bypass current Storage-policy validation.

Other graph settings can still be edited and activated while preserving Storage.
This panel does not update the profile or teach retention/upload workers to read
graph revisions. Explicit archived replay uses the capture's retained camera/rig
configuration with the validated/active graph revision named in the replay request,
including that revision's Storage options. It can differ from the live current
profile; replay does not change the active capture profile or graph selection.
The replay execution records the selected graph revision and frozen plan identities
for audit. This does not migrate Storage roots, bypass retained-input/plan eligibility
checks, or enable automatic publication/upload. No new current-profile fence applies.

Defaults match the existing runtime option classes. Unchanged saves preserve
absent defaults, source member casing, unknown fields and nested scientific
inputs. Unsupported producer versions do not borrow an editor from another
version. This delivery introduces no scientific algorithm or rendering version.

Use JSON numbers for numeric settings. Runtime binding can accept quoted numbers,
but the typed editor currently rejects those tokens until the affected fields
are explicitly corrected. It does not silently rewrite retained values.

This restriction also applies to mixed-gain RollingCombination window sizes and
age/integration limits, including raw JSON authored in LogicHost. Repeated
described option names, including differently cased spellings, are rejected;
runtime binding can instead keep the last value. Mixed-gain WindowKind retains
its existing enum binding (names or defined numeric values).

Use **Preview plan** in a CameraAgent graph draft as a preflight before saving
an imported or retained graph. Preview and save validate raw option tokens
before graph compilation or persistence and report the exact option path.
For a quoted numeric token, explicitly correct the field in the typed panel or
remove the quotes in the source JSON. For duplicate names, remove all but the
intended member in the source JSON before importing it again. Preview once more
and then save a new immutable revision. This preflight does not activate a graph,
execute a node, or rewrite the source revision. Graph compilation alone is not
this preflight: its runtime normalization may accept these source spellings.

These scalar panels preserve association/label-policy settings, astrometry
catalog/clock/prior/solver inputs, satellite catalog/work-bound/time settings and
artifact-specific storage policies. Their richer editors and the remaining
per-overlay styling remain outside this partial delivery. Refs #1386.
