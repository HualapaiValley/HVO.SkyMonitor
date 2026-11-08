# Predefined automation wizard — first slice (#1133)

The 2026-11-08 operator assignment authorizes this bounded slice of the held
wizard issue. It references #1133 without completing the broader acceptance.

## Presets and date ownership

Use installed registry targets only. Still-image and time-lapse adapters offer
`CompletedCivilHour` and `SunriseDay` when configured and available. The operator
selects actual-source or dark-night selection; the task adapter validates that
selection against its pinned target (for example, keograms require all actual
sources and star trails require dark-night sources). Pinned producer settings
remain in host configuration; this slice does not invent editable algorithm
parameters or silently alter a target's recipe.

Source windows and triggers remain separate: the source preset resolves exact
`[start,end)` instants, while `SourceWindowClosed` runs after end plus an explicit
0–86,400 second settle allowance. A completed civil hour can be partial at the
sunrise boundary. The daily preset uses computed sunrise to next sunrise, owned
by the starting sunrise's local date. Existing noon-based archive history is
not reinterpreted. Repeated civil hours retain different UTC instants and offsets;
gaps contain no invented hour. A solar no-event result has no substitute boundary.

The preview is a read-only planning calculation for the next future occurrence
if enabled, as of the displayed time. It is not a reservation, coverage report or
publication guarantee. Saving later can select a later next occurrence. Existing
due/missed work remains governed by the durable runner. Edits create revisions;
old occurrences and retries retain their original windows and site identity.

## Execution-engine decision

| Option | Required schedules and offline recovery | Footprint and lifecycle | Decision |
| --- | --- | --- | --- |
| Existing .NET runner and SQLite | Already supports periodic, capture-relative and sunrise/hour window-close triggers, durable claims, interrupted/missed runs and retained retries offline | Existing host cancellation, bounded worker and dependencies; SDK 10.0.402, no added packages | Retain for this slice |
| Quartz.NET with RAM store | Calendar/cron triggers would help future rules; sunrise needs one-shot replanning and capture-relative still needs a separate signal. SQLite reconciliation and claims remain necessary after restart | Adds a scheduler lifecycle and dependency while RAM triggers are only derived state | Reassess for concrete additional schedule requirements; no version adopted or qualified |
| Hangfire | Persistent job orchestration would duplicate the delivered occurrence/claim authority unless explicitly integrated; sunrise still needs the shared planner | Adds storage/provider, dependency and operational requirements | No adoption or qualified version in this slice |

The comparison uses the delivered contracts and the framework capabilities
discussed on #1133; it makes no package-version qualification claim. No framework
migration or separately deployed worker is approved here. Existing task adapter
interfaces remain host-local execution seams. SQLite remains the sole durable
definition, claim and publication authority, independent of acquisition cadence.
Any future external worker must accept retained occurrences through that authority.
Custom script/plugin isolation, hard process timeouts and authorization belong to
#1137.

## Acceptance boundary and evidence

This slice delivers registered task selection, source/settle validation, exact
future window/finality preview, and create/edit/pause with existing immutable
revision history. It retains truthful unsupported states and authenticated owner
mutations. Regression evidence covers wizard navigation, independent same-kind
definitions, preview authorization and failure handling, DST fold/gap offsets,
solar no-event, and persistence/restart/edit provenance through the real SQLite
store. Existing runner tests retain immutable retry, overlap and interruption
qualification.

Remaining #1133 work includes richer per-task parameters, additional approved
calendar/solar trigger presets, run-history product links and supported UI
run/cancel/backfill controls, plus broader performance and operator qualification.
Script acceptance belongs to #1137. Protected `development/v1` CI and final
base synchronization wait for the repository finalization reservation; this draft
does not authorize merge or release adoption.
