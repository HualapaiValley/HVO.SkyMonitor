# CameraAgent Orbital-Elements Refresh

Issue #528 adds CameraAgent-local acquisition of orbital elements: one bounded
CelesTrak GP request for the `active` group, strict parsing and normalization into the
`orbital-elements-v1` contract (`docs/contracts/orbital-elements-v1.md`), immutable
content-addressed publication, atomic activation, and a verified local reader.
The provider decision and its terms are recorded in
`docs/planning/orbital-data-decision-527.md`.

Refresh is disabled by default. Nothing on the frame path makes a network request:
consumers read the last activated snapshot from local storage.

Operators normally read status, refresh and resume from the Software & Catalog page
(#1392), described under "Software & Catalog And Automations" below. The endpoints,
the `orbital-elements` health entry and the metrics remain available.

## Provider Policy

The store enforces this policy before any request is made. Neither configuration nor
an operator command can bypass it.

- **One request per two hours.** The store commits the attempt row and the next
  permitted attempt (`start + 2 h`) before the request. Every attempt counts: a failed,
  timed-out, quarantined, interrupted or storage-failed attempt still holds the floor.
  The floor is fixed and is not configurable.
- **Single flight.** At most one attempt is in flight.
- **Halt on any non-200 HTTP response.** A status from 100 to 599 other than 200 records
  a provider-wide halt with its status and time. Nothing is requested again until an
  operator resumes, and resuming does not move the floor. A status outside 100–599 is
  quarantined as `orbital.invalid-http-status` instead.
- **No retries.** A timeout or transport failure is recorded and keeps the
  last-known-good snapshot. The next request waits for the next scheduled occurrence
  after the floor.
- **No redistribution.** Raw provider bytes stay on this CameraAgent, under the store
  root. #528 sends neither the feed nor its quarantine evidence to LogicHost or
  anywhere else.

Request facts:

- URI: `https://celestrak.org/NORAD/elements/gp.php?GROUP=active&FORMAT=JSON`.
- Dataset key: `celestrak:gp:active`.
- User-Agent: `HVO.SkyMonitor.CameraAgent/<version>`, with build metadata removed.
- Bounds: a client deadline of at most 30 s, at most 32 MiB on the wire and 32 MiB
  decoded. The request asks for gzip, and a response must be either gzip or
  unencoded.
- The named HttpClient has no retry or logging handlers.

## Enable And Configure

Configuration lives in the `CameraAgent:OrbitalElements` section. Every value is
validated at startup.

| Key | Default | Range | Meaning |
| --- | --- | --- | --- |
| `Enabled` | `false` | — | Allows provider attempts. While `false`, scheduled and manual occurrences skip with `orbital.disabled`. Retained snapshots stay readable either way. |
| `RequestTimeoutSeconds` | `30` | 1–30 | Client deadline for one request. |
| `MaximumWireBytes` | `33554432` | 1 B–32 MiB | Largest accepted response body on the wire. |
| `MaximumRetainedSnapshots` | `12` | 2–64 | Newest snapshots that retention always keeps. |
| `MinimumRetentionHours` | `48` | 1–720 | Snapshots younger than this are always kept. |
| `MaximumQuarantinedPayloads` | `4` | 1–16 | Newest quarantined raw bodies that are kept as evidence. |
| `StaleAfterHours` | `12` | 3–168 | Health degrades when the active snapshot was activated longer ago than this. |

The store root is `<RawIngressRoot>/.orbital-elements/`. The store is initialized
before the automation runner starts, whether or not refresh is enabled. If it cannot
be initialized, the host keeps running and retries every five minutes. Every status
request, and every health probe while refresh is enabled, also retries, so a repaired
root recovers without a restart.

### Create The Reserved Definition

Refresh is scheduled by exactly one local automation definition. It is not a separate
timer. Create it from `/operations/automations`, where the registry publishes the
`OrbitalElementsRefresh` task kind, or with
`POST /api/v1/operations/automations/definitions`. That route needs
`CameraAgent.Operations.Mutate.V1`, antiforgery and an `Idempotency-Key` header:

```json
{
  "definitionId": "orbital-elements-refresh",
  "name": "Orbital elements refresh",
  "enabled": true,
  "taskKind": "OrbitalElementsRefresh",
  "taskTarget": "celestrak:gp:active",
  "triggerKind": "Periodic",
  "triggerInterval": 21600,
  "expectedVersion": 0,
  "reason": "Enable local orbital elements"
}
```

| Rejection | Cause |
| --- | --- |
| `orbital.reserved-definition-id` | The definition ID is not exactly `orbital-elements-refresh`. |
| `automation.unregisteredTarget` | The target is not `celestrak:gp:active`. |
| `automation.unregisteredCombination` | The trigger is not `Periodic`, or a source window was supplied. |
| `orbital.interval-below-provider-floor` | `triggerInterval` is below 7200 s. |
| `orbital.interval-out-of-range` | `triggerInterval` is above 86400 s. |
| `orbital.store-unavailable` | The orbital store is not available. |

You can save the definition while `Enabled` is `false`, so that it is ready in advance.

The first occurrence is not immediate. The definition's epoch is the save time plus a
per-installation phase offset in [0, 30 min), persisted in the store, so that
independent CameraAgents do not reach the provider on the same boundary. Like every
periodic definition, the first occurrence falls one whole interval after the epoch. To
fetch the first snapshot sooner, send a refresh request (below). No floor exists before
the first attempt, so the request is accepted straight away.

Disabling or removing the definition stops scheduling. It does not delete snapshots or
history.

## Status, Refresh And Resume

| Method | Route | Policy |
| --- | --- | --- |
| `GET` | `/api/v1/operations/orbital-elements/` | `CameraAgent.Operations.Read.V1` |
| `POST` | `/api/v1/operations/orbital-elements/refresh` | `CameraAgent.Operations.Mutate.V1`, antiforgery |
| `POST` | `/api/v1/operations/orbital-elements/resume` | `CameraAgent.Operations.Mutate.V1`, antiforgery |

Rules that apply to every command:

- Each command needs exactly one non-empty `Idempotency-Key` header. A missing or
  repeated header returns 400.
- The actor is the authenticated owner identity.
- Request bodies reject unknown members.
- Nothing in either command can supply a URL, dataset, path or schedule.

**Status.** `GET` returns the `hvo-orbital-refresh-status-v1` read model:

- `enabled`;
- the active `currentManifestSha256` and `currentPayloadSha256`;
- `lastAttemptUtc`, `lastRetrievalSuccessUtc` and `lastActivationUtc`;
- `nextDueUtc`, the next scheduled occurrence, present only while refresh and the
  definition are both enabled;
- `nextPermittedAttemptUtc`, the floor;
- the `automation` reference: definition ID, revision hash and last run key;
- `providerHalt`: since, HTTP status and reason;
- `candidateQuarantine`: observed time, raw SHA and reason;
- one `reason` and one `recoveryAction`.

An unavailable store is still reported with 200, as a state whose `recoveryAction` is
`RepairStorage`. The endpoint returns 503 only if the read itself fails.

The `reason` and `recoveryAction` come from the first matching state, in this order:

| `reason` | `recoveryAction` | State |
| --- | --- | --- |
| `orbital.store-unavailable`, `orbital.storage-*` | `RepairStorage` | The store cannot be used. |
| `orbital.disabled` | `Configure` | `Enabled` is `false`. |
| `orbital.definition-missing` / `orbital.definition-disabled` | `Configure` | The reserved definition is absent or disabled. |
| `orbital.provider-halted` | `ResumeProvider` | A provider halt is in force. |
| The parser, normalizer or response reason, otherwise `orbital.candidate-quarantined` | `InspectQuarantine` | The last attempt was quarantined. |
| `orbital.attempt-unsettled` | `RepairStorage` | An attempt is still marked in flight with no occurrence running. |
| The storage fault code, otherwise `orbital.storage-failed` | `RepairStorage` | The last attempt could not publish durably. |
| `orbital.not-yet-permitted` | `WaitForPermittedAttempt` | The last attempt failed and the floor has not elapsed. |
| `orbital.no-active-snapshot` / `orbital.ready` | `None` | Waiting for the first snapshot, or current. |

**Refresh.** The optional body is `{ "reason": "..." }`, with a non-empty trimmed
reason of at most 512 characters. A refresh is never executed inside the request. The
checks run in this order:

1. request validation;
2. `Enabled`;
3. store availability;
4. the definition exists and is enabled;
5. the halt;
6. the floor.

When all six pass, the refresh is queued as one run-now occurrence of the reserved
definition. The automation runner picks it up on its next poll. The store then
re-applies the halt and the floor when it admits the attempt, so a race between the
request and execution can never send an early request.

- A run-now occurrence that waits more than 15 minutes before it runs, for example
  because the runner is disabled, is skipped with `orbital.manual-request-expired`.
- Occurrences keep fixed boundaries. If a manual refresh moves the floor past the next
  boundary, that boundary waits for the floor when it is short by at most 120 s.
  Otherwise it skips with `orbital.floor-not-elapsed`, and the boundary after it runs.

**Resume.** The body is
`{ "expectedHaltSinceUtc": "<providerHalt.sinceUtc>", "reason": "..." }`. Supplying
the expected halt time means a resume can never clear a halt that it did not see.
Resume:

- is allowed while refresh is disabled, so you can clear a halt before re-enabling;
- never moves the floor;
- does not make a request itself.

The next permitted occurrence makes the next request.

| Command result | HTTP | Reason codes |
| --- | --- | --- |
| Accepted (refresh queued) | 202 with `Location: /api/v1/operations/orbital-elements` | `orbital.refresh-queued` |
| Applied | 200 | `orbital.resumed` |
| Replayed (same key, same payload) | 200 | `orbital.refresh-replayed`, `orbital.resume-replayed` |
| Invalid | 400 | `orbital.refresh-invalid-idempotency-key`, `-actor`, `-reason`; `orbital.resume-invalid-halt-time`, `-idempotency-key`, `-actor`, `-reason` |
| Conflict | 409 | `orbital.disabled`, `orbital.definition-missing`, `orbital.definition-disabled`, `orbital.provider-halted`, `orbital.not-yet-permitted`, `orbital.no-halt`, `orbital.halt-mismatch`, `orbital.idempotency-conflict`, `automation.run-now-pending` |
| Unavailable | 503 | `orbital.store-unavailable` or the storage fault code |

Every rejection is a problem response. It carries only `reasonCode` and, when the
floor blocked a refresh, `nextPermittedAttemptUtc`. A successful result also returns
the read model as it stands after the command. An unexpected failure returns 503 with
a fixed title and no reason code.

### Run Outcomes

Each occurrence appears in the automation run history with a `{reason code}: {text}`
detail. The detail never contains a path, header value or secret.

| Run outcome | Attempt outcome | Reason codes |
| --- | --- | --- |
| Skipped | none (no request) | `orbital.disabled`, `orbital.manual-request-expired`, `orbital.provider-halted`, `orbital.floor-not-elapsed`, `orbital.attempt-in-flight` |
| Succeeded | `Activated` / `UnchangedPayload` | `orbital.activated`, `orbital.unchanged-payload` |
| Failed | `ProviderHalted` | `orbital.provider-http-<status>` |
| Failed | `TransportFailed` | `orbital.request-timeout`, `orbital.http-client-timeout`, `orbital.transport-*`, `orbital.attempt-cancelled` |
| Failed | `Quarantined` | response checks (`orbital.media-type`, `orbital.content-encoding`, `orbital.gzip-invalid`, `orbital.length-mismatch`, `orbital.wire-bound`, `orbital.raw-bound`, `orbital.empty-body`, `orbital.invalid-http-status`) and parser or normalizer reasons (`orbital.json-*`, `orbital.missing-member`, `orbital.record-contract`, `orbital.snapshot-contract`, …) |
| Failed | `StorageFailed` | `orbital.storage-no-space`, `-database`, `-permission`, `-containment`, `-integrity`, `-io` |
| Failed | none (not admitted) | a storage fault code or `orbital.storage-integrity` at admission, `orbital.internal-error` |

`UnchangedPayload` means the normalized payload matched the active one. A new manifest
is still activated, reusing the payload file. Restart reconciliation records
`Interrupted` (`orbital.attempt-interrupted`).

## Software & Catalog And Automations

`/operations/software` has an **Orbital-element snapshot** panel. It reads the refresh
status above, the reserved definition and the verified manifest of the active snapshot.
It never loads the payload, and it never shows a path, an ETag or Last-Modified value,
a credential or a provider body. Diagnostics show only whether the ETag and
Last-Modified were recorded. Reason codes appear as operator text; an unknown code is
shown as the code itself. The page does not poll: use **Reload status**.

What the panel shows:

- One state: Fresh, Stale, Refresh overdue, No data, Disabled, Halted, Quarantined,
  Needs repair or Unavailable. The page rail repeats it.
- Coverage relative to the requested population, its known exclusions, record and
  object counts, and the epoch range.
- Element age and retrieval time, kept separate:
  - **Stale** means even the newest element epoch is older than 48 h, however recently
    it was downloaded. Stale elements stay in use until a refresh replaces them.
  - **Refresh overdue** means refresh is on and there has been no successful retrieval
    within `StaleAfterHours`.
- Validity reads "Unknown": the source supplies no validity window.
- Provider and dataset, terms, fetch and activation times, the schedule, the last
  attempt, the next permitted attempt and the next due time.
- Any provider halt (time, HTTP status and reason) and any quarantined response.
  A missing or invalid HTTP status reads "No valid HTTP status recorded". The
  last-known-good snapshot stays visible beside a quarantine.
- If the store is unavailable, or no snapshot is installed, the panel says so. It never
  reads either case as an empty sky.

Commands need `CameraAgent.Operations.Mutate.V1`. A read-only operator sees the same
facts with the commands disabled.

- **Refresh now** sends the refresh command with a fixed reason. The button is
  disabled, with the cause, whenever the command would be refused: refresh is off, the
  definition is missing or disabled, a halt is in force, or the floor has not elapsed
  (the next permitted time is shown).
- **Resume provider** appears only during a halt. It opens a dialog that shows the halt
  and asks for a single-line reason of 1 to 256 characters. It sends the halt time the
  page read, so a halt that has since changed is never cleared. Resume never moves the
  floor.
- Each command intent uses one idempotency key. If the outcome is unknown (503), the
  button changes to a retry that sends the identical request with the same key, so the
  command is never applied twice. After any other outcome, the next click is a new
  intent with a new key. A refresh, and a resume that succeeds or conflicts, re-reads
  the status. An invalid resume reason keeps the dialog open.

The **Schedule** and **Run history** links open
`/operations/automations?definition=orbital-elements-refresh` and
`/operations/automations?view=runs`. The `?definition=` link highlights that
definition's card and scrolls to it once. If the definition does not exist, the page
says so, and it ignores an identifier that is not valid.

In Automations, choosing the `OrbitalElementsRefresh` task fixes the identifier to
`orbital-elements-refresh`, the target to `celestrak:gp:active` and the trigger to
Periodic. It defaults the interval to 21600 s. The server enforces the 7200–86400 s
range, and its rejections in the table above appear as operator text.

## Health And Metrics

`/health` includes the `orbital-elements` check, tagged `dependency`. The CameraAgent
health UI lists it as "Orbital elements", scope Local, linked to
`/operations/software`. Its data holds only these allowlisted keys: `Availability`,
`Reason`, `RecoveryAction`, `Halted`, `HaltHttpStatus`, `ActiveRecordCount`,
`ActiveAgeSeconds` and `LastAttemptOutcome`. No path or provider body ever appears.

| State | When |
| --- | --- |
| Healthy | Refresh is disabled (no I/O is performed), the active snapshot is current, or the first snapshot is not yet due. |
| Unhealthy | The store is unavailable or failed integrity verification (`RecoveryAction` `RepairStorage`). |
| Degraded | The first match, in this order: a provider halt; a quarantine that has not yet been replaced by an activation; an unsettled attempt; a storage-failed attempt; `orbital.snapshot-stale` (activation older than `StaleAfterHours`); then, with no active snapshot, `orbital.first-snapshot-overdue` (an attempt already happened, or more than 5 minutes have passed since the first due time) or a missing or disabled definition. |

Degraded always means the last-known-good snapshot, if there is one, is still
readable.

The meter `HVO.SkyMonitor.CameraAgent.OrbitalElements` is registered with the
CameraAgent MeterProvider and exported through the Prometheus scraping endpoint:

| Instrument | Type | Unit | Tags |
| --- | --- | --- | --- |
| `skymonitor.cameraagent.orbital_elements.attempts` | counter | `{attempt}` | `outcome` (durable attempt outcome) |
| `skymonitor.cameraagent.orbital_elements.fetch_duration` | histogram | `ms` | `disposition` |
| `skymonitor.cameraagent.orbital_elements.raw_bytes` | histogram | `By` | — |
| `skymonitor.cameraagent.orbital_elements.active_records` | gauge | `{record}` | — |
| `skymonitor.cameraagent.orbital_elements.active_age` | gauge | `s` | — |
| `skymonitor.cameraagent.orbital_elements.halted` | gauge | `{halt}` | — |

Log event IDs for orbital refresh fall in 7700–7769. Logs carry reason codes and SHA
identities, never paths.

## Storage Layout, Retention And Quarantine

```text
<RawIngressRoot>/.orbital-elements/
  orbital-elements.db      schema 1, STRICT tables, WAL; the ledger and provider state
  orbital-elements.lock    exclusive root lock (a second owner gets orbital.storage-locked)
  raw/<RawSha256>.json            decoded provider response bytes
  payloads/<PayloadSha256>.json   normalized orbital-elements-v1 payload
  manifests/<ManifestSha256>.json snapshot manifest
  quarantine/<RawSha256>.json     rejected raw bodies kept as evidence
  staging/                        publication intents during a write
```

The database tables are as follows:

- `orbital_installation`, which is immutable. It holds the phase-offset seed.
- `orbital_provider_state`. Its revision and sequences are monotonic, and it holds the
  halt, the floor and the quarantine.
- `orbital_attempts`, which is append-only. A terminal row is immutable.
- `orbital_snapshots`. Rows are tombstoned and never deleted.
- `orbital_activation_history`.
- `orbital_snapshot_references`.
- `orbital_commands`, which is append-only. It is the idempotency ledger.

Triggers abort deletes from every table except `orbital_snapshot_references`. They
also abort any decrease of a provider-state counter.

Files are content-addressed, so a payload or raw body shared by several manifests is
stored once. In the synthetic active-group evidence (16,754 records), one changed
snapshot used about 28.6 MB: a 21.4 MB payload, 7.1 MB raw and a 1.7 KB manifest. The
raw size matches the 7.06 MB measured for the real feed in the #527 decision.

**Retention** runs best-effort after every successful activation. A snapshot is
retired only when it falls outside both the newest `MaximumRetainedSnapshots` and
`MinimumRetentionHours`. The active, previous-active, referenced and currently-loading
snapshots are never retired. The tombstone commits before any file is deleted, and a
payload or raw file is deleted only once no published manifest still needs it.

To size the disk, plan for roughly the larger of:

- `MaximumRetainedSnapshots`;
- the number of activations within `MinimumRetentionHours`, which is about 24 at a
  2-hour interval with the defaults;

and multiply by about 28.6 MB. Add up to `MaximumQuarantinedPayloads` raw bodies of
at most 32 MiB each.

**Quarantine inspection.** When the status shows `candidateQuarantine`:

1. Read its `reason`, the precise parser, normalizer or response code, and its
   `rawSha256`.
2. If a raw SHA is present, `quarantine/<rawSha256>.json` holds the decoded response
   bytes exactly as received.
3. Inspect a copy. Do not edit, rename or delete files in the store.
4. Keep the copy local, because the provider terms forbid redistribution.

Response-level rejections and an invalid HTTP status have no raw body. The newest
`MaximumQuarantinedPayloads` bodies are kept and older ones are pruned automatically.
The quarantine state clears at the next successful activation. If the cause is a
provider format change, it needs a parser change under the contract; no configuration
setting accepts it.

## Crash Recovery

On every start, before the automation runner starts, the store:

- marks any `InFlight` attempt as `Interrupted` (`orbital.attempt-interrupted`). It
  keeps its start time, so the floor still applies;
- settles staging intents and deletes partially written staging files;
- removes publisher temporaries;
- prunes quarantine bodies that no retained attempt names;
- leaves anything ambiguous where it is and logs it. That covers a directory or link
  where a file is expected, and a name that is not a SHA-256.

Activation is one SQLite transaction. After a crash, either the previous manifest or
the new one is active, never a mixture. The automation runner claims an occurrence and
advances its progress in one transaction, so a stopped process never repeats it.

If the process stays up but cannot record an attempt's terminal outcome (for example,
the halt write fails), the attempt stays `InFlight`. A halt is never downgraded to
another outcome. Later attempts refuse with `orbital.attempt-in-flight`. Status reports
`orbital.attempt-unsettled` and `RepairStorage`. Fix the storage fault and restart; the
restart settles the attempt as `Interrupted`.

## Disk Full

If publication cannot complete durably:

- The attempt records `StorageFailed`. The code is `orbital.storage-no-space` for a
  filesystem fault, or `orbital.storage-database` for a SQLite failure.
- Partial files are removed.
- The last-known-good snapshot stays active and readable.
- The floor still applies, because the provider request was made.
- Status and health report the code with `RepairStorage`.

Free space under the raw ingress root. No retry happens before the next permitted
scheduled occurrence, or a refresh request once the floor has elapsed. The next
successful activation clears the state and runs retention. Retention does not run
after a failed attempt, so it cannot be relied on to free space after a failure. If
writing a quarantine body fails, that is logged and the attempt is still recorded as
`Quarantined`, without its body.

## Integrity Failure And Store Replacement

The store refuses to start in these cases:

- a database written by a newer CameraAgent;
- an unknown schema version;
- unversioned schema objects;
- schema drift;
- a failed `PRAGMA integrity_check`.

Each one reports `orbital.storage-integrity`. The host keeps running, refresh fails
closed and health is Unhealthy. Do not edit the database.

To replace the store:

1. Stop the CameraAgent.
2. Archive the whole `.orbital-elements/` directory, including the `-wal` and `-shm`
   sidecars, to owner-controlled storage. Record its checksum and the disposition in
   the deployment ledger.
3. Start the CameraAgent.

A replacement store starts with no floor and no halt. Before enabling refresh on it,
confirm two things from the archived ledger or the logs:

- at least two hours have passed since the last provider request;
- the condition behind any recorded halt is resolved.

Never delete or replace the store to clear a halt or the floor. Doing so would permit
a request that the provider policy forbids.

## Rollback

`OrbitalElementsRefresh` is a value appended to the closed `LocalAutomationTaskKind`
enumeration. It is stored by name in `<RawIngressRoot>/.automation/local-automations.db`.

A CameraAgent build from before #528 cannot parse that name, and it fails closed:

- Reading the definitions throws.
- Every run occurrence embeds its full definition, and the operator projection reads
  recent runs across all definitions, so the automations view fails too.
- The runner's sweep fails, which stops every local automation, not only orbital
  refresh.
- Removing the definition before rolling back is not enough, because the retained
  revisions and runs still embed the task kind.

Supported paths:

1. **Upgrade forward (preferred).** Fix the problem in a newer build, which reads all
   of the existing state.
2. **Documented archive and disposition** (when a rollback cannot be avoided):
   1. Stop the CameraAgent.
   2. Archive `<RawIngressRoot>/.automation/`, including the `-wal`/`-shm` sidecars,
      to owner-controlled storage.
   3. Record its checksum and the disposition in the deployment ledger.
   4. Move the directory aside.
   5. Start the older build. It recreates an empty automation store, so local
      automation definitions are absent until you restore or recreate them.
   6. Leave `.orbital-elements/` in place. An older build never opens it, so the floor,
      halt and history survive for the roll-forward.
   7. On roll-forward, choose explicitly between the archived automation store and the
      one created during the rollback. Never merge them by editing.

Never delete history, edit rows or lower counters in either store. The triggers
refuse most of these changes, and working around them would break the idempotency,
floor and audit guarantees. The installer's general upgrade and rollback contract is
in `docs/hvo-install-contract.md`.

## Consumer Handoff (#529, #1393)

- **Registration.** `IOrbitalSnapshotReader` (Astronomy contract) is implemented by
  `OrbitalSnapshotReader`. It is registered as a singleton with `configured: true` by
  the internal `AddCameraAgentOrbitalElements` extension, which
  `AddCameraAgentInfrastructure` calls. Resolve the interface; do not construct the
  store or the reader. Every consumer shares its cache of two materialized snapshots.
- **No network on reads.** The reader never touches an HttpClient or the provider
  client.
- **Read cost.** A warm read returns the cached instance with no I/O. A cold
  materialization of the active group re-verifies the bytes through
  `OrbitalSnapshot.Parse`. On the reference workstation that took about 0.95 s and
  allocated about 2.1 GB. Keep the result and its identity rather than re-reading on a
  latency-sensitive path. The measurements are in
  `docs/planning/performance-validation.md`.
- **No data and offline behaviour.**
  - `ReadCurrentAsync` returns `NoActiveSnapshot` before the first activation.
  - While refresh is halted, disabled, offline, quarantined or storage-failed, it keeps
    returning the last-known-good active snapshot. Refresh state never invalidates a
    retained snapshot; judge scientific age from record epochs (Astronomy), not from
    refresh status.
  - An unusable store (lock, permission, database, I/O or disposal) returns
    `StorageUnavailable`.
  - Missing, mismatched or unparseable bytes for a known manifest return
    `IntegrityFailure`.
  - `ReadExactAsync` takes the uppercase manifest SHA. It returns `InvalidIdentity`
    before any I/O for a malformed key, `UnknownIdentity` for an unknown key, and
    `Retired` after retention. It never substitutes the current snapshot.
  - Cancellation throws `OperationCanceledException`.
  - `NotConfigured` is not produced by this composition.
- **Identities.**
  - Snapshot identity: `Manifest.ManifestSha256`. This is the `ReadExactAsync` key and
    the status `currentManifestSha256`.
  - Payload identity: `Manifest.NormalizedPayloadSha256`, shown as the status
    `currentPayloadSha256`.
  - Raw provider response: `Manifest.Sources[].RawSha256`.
  - Each record also carries its own `RecordSha256`.
- **Time inputs.** #528 does not supply Earth-orientation parameters or leap-second
  inputs. The host must resolve approved local EOP and leap-second inputs separately.
  #528 adds no implicit zero-EOP or default-clock behaviour.
- **Scope.** #528 registers no prediction engine or consumer.
