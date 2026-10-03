# CameraAgent Local Automation

The Operations workspace section `/operations/automations` presents the registered
capture schedule, the registered environmental source tasks, the registered trigger
vocabulary, and — since issue #563 — operator-defined local automations, their
next-run calendar, and their durable run history.

This runbook covers the versioned durable local contract behind that section.

## What A Definition Can Express

A definition names a registered task, its source policy and trigger, plus its identity:

| Field | Meaning |
| --- | --- |
| `taskKind` | One value of a closed registry enumeration. |
| `taskTarget` | One target the registry currently publishes for that task kind. |
| `triggerKind` | `Periodic`, `CaptureRelative`, or adapter-supported `SourceWindowClosed`. |
| `triggerInterval` | Seconds for `Periodic`, durable captures for `CaptureRelative`; exactly 1 for `SourceWindowClosed`. |
| `sourceWindow` | Required only for `SourceWindowClosed`: versioned completed civil hour or sunrise day, actual-source selection and fixed 0–24-hour processing allowance. |
| `definitionId`, `name`, `enabled` | Identity and enablement. |

There is deliberately no field that can hold a command line, a script, a path, an
executable, or a URL, and no task kind that would interpret one. The default
registered task kind is `EnvironmentalOnDemandAcquisition`, whose targets are the
configured environmental sources that declare the `OnDemand` trigger. A save that
names a target the registry does not publish is rejected with
`automation.unregisteredTarget` before anything durable changes.

Built-in producers register `ILocalAutomationWindowTaskAdapter`; their descriptors
publish actual installed targets and supported presets. `StillImageGeneration` is
a reserved typed kind, unavailable until a producer registers its adapter. A target
must identify its immutable producer preset; an adapter must reject a retained preset
it can no longer resolve, rather than reinterpret it using changed settings. Each
definition has its own revision and schedule. Hourly and daily definitions of the
same task kind coexist; a daily definition does not require hourly outputs.

Capture-relative triggers are evaluated by the automation runner from the durable
capture sequence on its own timer. They are not the environmental capture trigger
bridge: `BeforeCapture`, `AfterCapture`, `EveryNthCapture`, and `RegimeChange`
remain environmental behaviour driven by the capture lane and are not operator
definable here.

## Storage

`<raw-ingress-root>/.automation/local-automations.db`, a dedicated SQLite database
with WAL journaling, `synchronous = FULL`, and `PRAGMA user_version` pinned to the
contract's schema version. The drift guard counts schema objects, which cannot see a
column change, so any column change bumps the schema version (currently 3) and a database written by
an earlier build is refused rather than opened. Startup verifies the version, the exact schema object
count, and `PRAGMA integrity_check`, and refuses a newer, drifted, or corrupt
database rather than migrating it. Every open re-asserts that write-ahead logging is
actually in force and that neither the database nor its `-wal`, `-shm`, or `-journal`
sidecars is a symbolic link.

The store is a separate file on purpose. The raw ingress journal pins its own exact
schema version and object count and refuses a database it does not recognize, so
adding automation tables there would make an installer rollback to a baseline image
fail to start. A baseline image never opens this file; a rollback leaves it in place
and a roll-forward finds the same definitions and run history.

Tables:

- `automation_definitions` — the current definition, its version, and its revision hash.
- `automation_definition_revisions` — every recorded revision, including the removal.
- `automation_commands` — the idempotency ledger.
- `automation_runs` — the run-history journal.
- `automation_progress` — the last fired occurrence and the last observed capture sequence.

## Concurrency, Idempotency, And Retention

- **Expected version.** Every command carries `expectedVersion`: `0` to create,
  the definition's current version to update or remove. A mismatch returns
  `409 Conflict` with `automation.expectedVersionConflict` and changes nothing.
- **Idempotent retry.** Every command carries an `Idempotency-Key`. Replaying the
  key with the same payload returns `Replayed` without recording a second revision;
  replaying it with a different payload returns `409 Conflict` with
  `automation.idempotencyKeyConflict`. The ledger retains keys for seven days, so
  the retained window is the replay window.
- **Bounded retention.** At most 32 definitions, and 50 retained revisions and 200
  terminal runs per live definition, plus at most 32 queued attempts and one running
  attempt per definition. Active work is never evicted by terminal retention.
  Per-definition retention only runs from that
  definition's own write paths, and a removal frees its slot, so removed definitions
  have their own global bound: 200 retained revisions and 200 retained runs in total
  across every removed definition, pruned on each removal. Projections return at most
  50 runs across all definitions, 10 revisions per definition, and 25 calendar
  entries.

## Runner Behaviour

- Occurrences sit on fixed boundaries measured from the definition's epoch, which is
  the instant of its first save and is preserved across later revisions. The first
  occurrence is one whole interval after that epoch, never at the instant of the save.
- A run is claimed and its progress advanced in one transaction, so a process that
  stops mid-run never repeats the occurrence.
- The run key is both the occurrence identity and the task command identity, so a
  retried occurrence replays through the task's own durable command contract instead
  of acquiring twice.
- Occurrences for which no run was recorded are recorded once as a single `Missed` run
  and are never replayed as a burst of catch-up commands.
- Changing a definition's trigger kind, or re-enabling a disabled one, re-anchors its
  progress to the present rather than clearing it, so resuming schedules the next
  occurrence one whole interval away instead of reporting every boundary since the
  epoch as missed.
- A newly enabled capture-relative definition is baselined at the current durable
  capture sequence rather than firing for captures that predate it.
- A failed run is recorded as `Failed`; the next automatic occurrence has its own
  identity. An explicit retry reuses the failed/interrupted occurrence, not a new window.
- The runner issues the same on-demand acquisition an operator can issue by hand. It
  never admits an exposure, never changes acquisition cadence, and never occupies the
  live processing slot.

## Restart Recovery

`InitializeAsync` runs from the runner's `StartAsync`, so a store that fails any of
those checks fails host startup rather than letting the host serve traffic and stop
later. Before opening SQLite it acquires an exclusive lifetime file handle at
`.automation/local-automations.lock`. A competing process fails startup without
settling or changing the owner's rows. Execution retains that authority through actual
drain, including tasks that ignore cancellation. Every admitted journal operation also
retains the handle through its transaction and projection. Disposal rejects new admissions
and waits for admitted commands and tasks to drain; a running task may finish its own
durable completion while draining. A new sole owner settles remaining `Running`
rows as `Interrupted`; it never automatically reruns them. Progress already advanced with
the claim, so the cadence continues at the next occurrence rather than repeating the
interrupted one. If a completion ever finds its run no longer claimed, that is logged
as a warning (event 7408) rather than passing silently.

## Source Windows And Finality

The source policy uses #1135's configured-site sunrise calendar. A named daily report
date covers that date's sunrise through the following date's sunrise. Windows retain
exact UTC `[start,end)`, report date, full site snapshot, time-zone rule digest, both
solar algorithm identities, definition/revision identity and canonical occurrence/run
identity. The occurrence is committed before adapter execution. Retry reads the retained
value and does not consult current coordinates, time-zone rules or wall time to rebuild it.

Completed civil hours partition the entire sunrise period. Enumerate site-local
top-of-hour boundaries: omit invalid DST boundaries and retain both UTC instants of
ambiguous boundaries. Repeated hours have distinct identities. Sunrise clips the first
and last hour. Non-hour offset changes may produce shorter or longer elapsed spans;
every actual instant still belongs to exactly one window. No sunrise means explicit
unavailability; no noon or fixed-time replacement is generated.

Final generation waits until `endUtc + processingSettleAllowance`. Daily finality
also retains the shared sunrise finality contract. The adapter receives the same fixed
period; `AllActualSources` accepts only actual observations within it and
`DarkNightActualSources` additionally requires geometric solar altitude at each
source's actual timestamp to be at most −18 degrees. Missing leading/trailing/interior
coverage remains missing. Selection cannot shorten the planned chart, synthesize
pixels or interpolate trails. The product adapter owns actual output/provenance checks.

The preview and runner share one planner. `definitions[].nextOccurrence`, calendar
entries' `occurrence`, and `windowUnavailableReasonCode` expose exact upcoming spans
and eligibility. A late automatic sweep runs only the latest eligible window, records
the number missed within a bounded seven-day UTC lookback of final-run eligibility,
and identifies earlier coverage as unresolved. The date search includes periods that
start before that UTC bound but close and settle inside it, using each bound's actual
site-local offset. It does not launch a catch-up burst.

Explicit backfill names a report date and, for an hour, its exact UTC start. It requires
finality, current enablement/version and a window ending within seven days. Explicit
retry accepts only a retained `Failed` or `Interrupted` attempt and caps each occurrence
at three attempts. Both commands require an operator reason and retain actor, request
key/time and payload digest. Queued rows have no start time. Disabling pauses queued work;
removal cancels queued work and is rejected while an attempt is running. Retries retain
the original definition/window while requiring the operator's current definition version.
Adapter command identity stays the original run key; journal retries use `:retry2` or
`:retry3`. Prepared work does not rewind automatic progress. Restart retains queued work.

The existing bounded BackgroundService/SQLite journal provides the calendar scheduling
and sole durable authority. No scheduler framework, sidecar or SQL/Redis dependency is
needed. A future worker must execute through this authority and its immutable occurrence;
opening a second competing journal owner is unsupported. Installed-instance migration
remains separately held; older automation schemas fail closed and are never upgraded here.

## Configuration

`CameraAgent:Automation:Enabled` (default `true`) and
`CameraAgent:Automation:PollIntervalSeconds` (default `30`, range 5-3600). Disabling
the runner leaves definitions and history readable and mutable; it only stops
occurrences from firing.

`Enabled` is **not** an escape hatch past store verification. The store is initialized
and verified from the runner's `StartAsync` before the flag is read, so a drifted or
corrupt store fails host startup whether or not the runner is enabled. That is
deliberate: the store is durable operator state, and a CameraAgent that cannot read it
must say so rather than start and silently present nothing.

## Endpoints

| Method | Route | Policy |
| --- | --- | --- |
| `GET` | `/api/v1/operations/automations` | `CameraAgent.Operations.Read.V1` |
| `POST` | `/api/v1/operations/automations/definitions` | `CameraAgent.Operations.Mutate.V1`, antiforgery |
| `POST` | `/api/v1/operations/automations/definitions/{definitionId}/removal` | `CameraAgent.Operations.Mutate.V1`, antiforgery |
| `POST` | `/api/v1/operations/automations/definitions/{definitionId}/backfill` | `CameraAgent.Operations.Mutate.V1`, antiforgery |
| `POST` | `/api/v1/operations/automations/definitions/{definitionId}/retry` | `CameraAgent.Operations.Mutate.V1`, antiforgery |

All mutations require the `Idempotency-Key` header and a body `expectedVersion`, and
record the authenticated owner identity as the actor. A caller-supplied actor is
ignored.

## Recovery

Deleting `local-automations.db` loses only the local automation definitions and their
run journal. Capture, calibration, and deployment-location evidence are unaffected and
the store is recreated empty on the next start. A database whose `user_version` or
schema object count does not match the contract makes startup fail closed; archive it
and complete an explicit state-disposition procedure rather than editing it in place.
