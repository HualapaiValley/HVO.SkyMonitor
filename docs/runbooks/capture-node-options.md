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
| Silo publication | Select latest-frame updates and archive publication for a Storage node | Enabled Storage nodes match the active capture profile |

The graph editor shows field names, help and ranges. Invalid values disable the
current plan preview and save, including when an earlier preview was valid.
Satellite output routes also name the retained `-inputs` and `-status` products;
change the layer's input route together with the prediction route.

Preview the plan and save a new immutable revision. Validate the saved revision,
then explicitly activate it. Saving does not run a node, move artifacts, change
retention, enable a provider or start upload. The prior revision remains available
for explicit rollback. Reloading a saved draft restores its settings.

Storage nodes use `updateLatestFrame` to select the current display and
`publishArchive` to record a centered Silo archive group from selected durable
products. Silo retention is an instance policy. Preview, save, validation,
activation and rollback compare enabled Storage nodes with the active profile.

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
remain subject to Silo retention and durable holds. Storage-policy
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
work as a Storage skip. The installer option `--allow-quarantined-readiness` does
not waive this independent Storage-skip health reason, including when a quarantine
also exists; that exception remains unavailable until the 24-hour count expires.

For a planned publication change, pause acquisition and finish in-flight
standard processing. Update the capture profile, then reconcile, validate and
activate its graph before resuming. Configured-basic refreshes from the active
profile. Named graphs require explicit activation.

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
The active profile and the installed Silo retention policy are configured independently of graph drafts. Explicit archived replay uses the capture's retained camera/rig
configuration with the validated/active graph revision named in the replay request,
including that revision's Storage options. It can differ from the live current
profile; replay does not change the active capture profile or graph selection.
The replay execution records the selected graph revision and frozen plan identities
for audit. Replay uses verified retained inputs and leaves live publication selection unchanged.

Defaults match the existing runtime option classes. Unchanged saves preserve
absent defaults, source member casing, unknown fields and nested scientific
inputs. Unsupported producer versions do not borrow an editor from another
version. This delivery introduces no scientific algorithm or rendering version.

Use JSON numbers for numeric settings. Runtime binding can accept quoted numbers,
but the typed editor currently rejects those tokens until the affected fields
are explicitly corrected. It does not silently rewrite retained values.

This restriction also applies to mixed-gain RollingCombination window sizes and
age/integration limits. Repeated
described option names, including differently cased spellings, are rejected;
runtime binding can instead keep the last value. Mixed-gain WindowKind retains
its existing enum binding (names or defined numeric values).

Use **Preview plan** in a CameraAgent graph draft as a preflight before saving
an imported or retained graph. Preview and save validate raw option tokens
before graph compilation or persistence and report the exact option path.
When starting from a retained revision, unchanged quoted numbers on untouched
steps are preserved byte-for-byte while saving unrelated edits; strict
validation applies to any added, renamed, type-changed, or option-edited step.
For a quoted numeric token on a new or modified step, explicitly correct the
field in the typed panel or remove the quotes in the source JSON. For duplicate
names, remove all but the intended member in the source JSON before importing it
again. Preview once more and then save a new immutable revision. This preflight
does not activate a graph, execute a node, or rewrite the source revision. Graph
compilation alone is not this preflight: its runtime normalization may accept
these source spellings.

These scalar panels preserve association/label-policy settings, astrometry
catalog/clock/prior/solver inputs, satellite catalog/work-bound/time settings and
artifact-specific storage policies. Their richer editors and the remaining
per-overlay styling remain outside this partial delivery. Refs #1386.

## Inspect a centered pipeline

Open **Operations → Pipeline → Configured pipeline** to inspect the selected
schedule revision and graph revision. Select **Raw frame** or a fixed
measurement to see its inputs, products and consumers. Disabled branches remain
in the configured diagram. Draft validation and activation use the existing
Schedule and Named graphs controls.

Open **Running pipeline** to compare the last admitted capture with the issued
display reference. The buffer occupancy is a live observation. After restart,
the retained journal can still identify the earlier acquisition, holds and
issued display while the current buffer is unavailable. Older profiles report
that centered evidence is unavailable and link to the execution list.

The centered slot table gives expected and available actual exposure times and
the recorded registration outcome. Layer readiness counts contributors to the
displayed base. For example, acquisition 105 can show reference 103 with satellite
evidence **4/5 assessed** and source 105 **Waiting**. Expand the layer to find the
source run. If current progress cannot be verified, the row shows **Unavailable**
and the summary reports the known count. **Assessed Empty** records a completed
assessment with zero results.

The exact revision link, preview and overlay identify the immutable publication.
From **Processing executions**, open the reference capture's run to see its
retained display publications. When a late source belongs to overlapping windows,
each compatible base can gain its own revision. The source mapping in each base
identifies the coordinates used for that revision. An earlier revision link
continues to show the earlier evidence.
