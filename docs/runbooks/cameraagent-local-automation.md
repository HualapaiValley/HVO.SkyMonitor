# CameraAgent Local Automation

The Operations workspace section `/operations/automations` presents the registered
capture schedule, the registered environmental source tasks, the registered trigger
vocabulary, and — since issue #563 — operator-defined local automations, their
next-run calendar, and their durable run history.

This runbook covers the versioned durable local contract behind that section.

## What A Definition Can Express

A definition names exactly four things plus its identity:

| Field | Meaning |
| --- | --- |
| `taskKind` | One value of a closed registry enumeration. |
| `taskTarget` | One target the registry currently publishes for that task kind. |
| `triggerKind` | `Periodic` or `CaptureRelative`. |
| `triggerInterval` | Seconds for `Periodic`, durable captures for `CaptureRelative`. |
| `definitionId`, `name`, `enabled` | Identity and enablement. |

There is deliberately no field that can hold a command line, a script, a path, an
executable, or a URL, and no task kind that would interpret one. Two task kinds are
registered:

| Task kind | Targets | Triggers |
| --- | --- | --- |
| `EnvironmentalOnDemandAcquisition` | The configured environmental sources that declare the `OnDemand` trigger. | `Periodic`, `CaptureRelative` |
| `NightlyProductGeneration` | `keogram` and `star-trail`, only while `CameraAgent:NightlyProducts:Enabled` is `true`. | `Periodic` |

A save that names a target the registry does not publish is rejected with
`automation.unregisteredTarget` before anything durable changes. Rolling back to a
build that predates a task kind is not transparent. That build cannot read a stored
definition of the newer kind, so its automation store fails closed. Remove those
definitions before such a rollback.

Capture-relative triggers are evaluated by the automation runner from the durable
capture sequence on its own timer. They are not the environmental capture trigger
bridge: `BeforeCapture`, `AfterCapture`, `EveryNthCapture`, and `RegimeChange`
remain environmental behaviour driven by the capture lane and are not operator
definable here.

## Storage

`<raw-ingress-root>/.automation/local-automations.db`, a dedicated SQLite database
with WAL journaling, `synchronous = FULL`, and `PRAGMA user_version` pinned to the
contract's schema version. The drift guard counts schema objects, which cannot see a
column change, so any column change bumps the schema version and a database written by
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
  retained runs per live definition. Per-definition retention only runs from that
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
- A failed run is recorded as `Failed`; the definition stays enabled and retries at
  its next occurrence.
- The runner issues the same on-demand acquisition an operator can issue by hand. It
  never admits an exposure, never changes acquisition cadence, and never occupies the
  live processing slot.

## Nightly Products

A `NightlyProductGeneration` definition schedules keogram or star-trail generation
from published Preview frames. Use one definition per target, with a `Periodic`
interval of about 15 minutes so that hourly segments appear soon after they settle.
Each run does the following:

- **Windows.** It evaluates the previous and the current observing day. An observing
  day runs from local noon to local noon in the observatory time zone, so a day has 23
  or 25 segments across a daylight-saving change. A segment window is evaluated once
  it closed at least `SettleSeconds` ago. The night product is composed once the whole
  observing day has settled, at the next run after the noon rollover.
- **Sources.** It reads the published, `Available` Preview outputs of the
  `SourceNodeId` node, chosen by each raw capture's own exposure start. An output that
  only an unpublished execution recorded is never a source. A capture that has several outputs contributes only its latest. A window
  with more than 4096 candidates is `Rejected` with `nightly.window-source-bound`
  rather than truncated.
- **Admission.** A frame is admitted only if it was captured through the current rig
  profile and the Sun was at or below the kind's altitude limit. The Sun's altitude is
  evaluated at the frame's own exposure start and capture location. The default limits
  are 0° for keograms and −18° for star trails. A frame without a usable location is
  excluded, not assumed. Every exclusion is counted per reason code on the window.
- **Composition.** A window is composed in ordered parts of at most
  `MaximumSegmentSources` frames.
  - A keogram samples the true north–zenith–south meridian. A gap longer than
    `KeogramMaximumGapSeconds` is drawn as patterned columns.
  - A keogram night is assembled from its segments, byte for byte the same as composing
    every frame directly.
  - A star-trail night is reduced through ordered rollups of at most
    `MaximumSegmentSources` products each.
  - A run stops after `MaximumSegmentsPerRun` recipe executions and reports the rest as
    pending. Product identity is the recipe output identity, so the next run continues
    without recomputing anything already published.
- **Publication.** Products are immutable. The database is
  `<raw-ingress-root>/.nightly-products/nightly-products.db`. Each product also has
  three files under
  `<raw-ingress-root>/nightly-products/<yyyy>/<MM>/<dd>/<target>/<product-id>`:
  - `.bin`, the packed payload;
  - `.jpg`, the rendition;
  - `.provenance.json`, the ordered lineage, the recipe identity and the checksums.

  The files are written and synchronized before the row commits. Database triggers
  refuse to update or delete products and their lineage. A window re-evaluated over
  different sources records new current products without removing the earlier ones.
  The run detail reports windows evaluated, products published, pending, rejected, and
  failed. A failed window does not stop the remaining windows.

Nightly products are not pruned by capture retention. Video slots are not produced
here; time-lapse generation is tracked by #1130.

## Restart Recovery

`InitializeAsync` runs from the runner's `StartAsync`, so a store that fails any of
those checks fails host startup rather than letting the host serve traffic and stop
later. It settles every run still marked `Running` as `Interrupted` with its completion
time, because the process that claimed it is gone. A restarted process and a
competing second instance are indistinguishable at that point, so liveness is
asserted at completion instead: a run stays authoritative for the instance that
claimed it, and that instance records its real outcome even if another settled the
row meanwhile. Progress already advanced with
the claim, so the cadence continues at the next occurrence rather than repeating the
interrupted one. If a completion ever finds its run no longer claimed, that is logged
as a warning (event 7408) rather than passing silently.

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

`CameraAgent:NightlyProducts` bounds nightly generation:

| Setting | Default | Range |
| --- | --- | --- |
| `Enabled` | `false` | |
| `SourceNodeId` | none; required when enabled | the Preview-producing pipeline node |
| `SegmentMinutes` | `60` | 15–240 |
| `SettleSeconds` | `300` | 0–3600 |
| `MaximumSegmentSources` | `32` | 1–512 |
| `MaximumSegmentsPerRun` | `32` | 1–256 |
| `StarTrailMaximumSolarAltitudeDegrees` | `-18` | −90–0 |
| `KeogramMaximumSolarAltitudeDegrees` | `0` | −90–90 |
| `KeogramMaximumGapSeconds` | `300` | 1–86400 |
| `KeogramMaximumGapColumnCount` | `64` | 1–65536, at most `KeogramMaximumColumnCount` |
| `KeogramMaximumColumnCount` | `16384` | 2–65536 |
| `RenditionJpegQuality` | `90` | 1–100 |

`MaximumSegmentSources` bounds a run's working set, because every source of one part
is resident at once.

## Endpoints

| Method | Route | Policy |
| --- | --- | --- |
| `GET` | `/api/v1/operations/automations` | `CameraAgent.Operations.Read.V1` |
| `POST` | `/api/v1/operations/automations/definitions` | `CameraAgent.Operations.Mutate.V1`, antiforgery |
| `POST` | `/api/v1/operations/automations/definitions/{definitionId}/removal` | `CameraAgent.Operations.Mutate.V1`, antiforgery |

Both mutations require the `Idempotency-Key` header and a body `expectedVersion`, and
record the authenticated owner identity as the actor. A caller-supplied actor is
ignored.

## Recovery

Deleting `local-automations.db` loses only the local automation definitions and their
run journal. Capture, calibration, and deployment-location evidence are unaffected and
the store is recreated empty on the next start. A database whose `user_version` or
schema object count does not match the contract makes startup fail closed; archive it
and complete an explicit state-disposition procedure rather than editing it in place.
