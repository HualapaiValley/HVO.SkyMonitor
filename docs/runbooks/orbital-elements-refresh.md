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

## Credentials

None are needed, and none are accepted. CelesTrak GP is a public HTTPS source.

- The request carries only the fixed User-Agent, `Accept: application/json` and
  `Accept-Encoding: gzip`. It never sends a cookie, an `Authorization` header or a
  conditional header.
- The named HttpClient disables cookies, pre-authentication and handler credentials.
  It refuses to start with any default request header configured.
- `CameraAgent:OrbitalElements` has only the seven keys listed below. No key takes a
  URL, token, account or password. An unrecognized key in the section is ignored, not
  used.
- Outbound proxy selection follows the host's standard proxy environment. The orbital
  configuration has no proxy or credential setting of its own.

Authenticated providers (Space-Track, SupGP) are deferred by the #527 decision. They
need their own terms and credential handling before they can be enabled.

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
  operator resumes, and resuming does not move the floor.
- **Halt when no valid HTTP status is recorded.** Two cases also record a provider-wide
  halt, with no valid HTTP status recorded:
  - a status line outside 100–599 (`orbital.invalid-http-status`);
  - an attempt that may have reached the provider but whose outcome was never durably
    recorded (`orbital.provider-outcome-unknown`). See
    [Crash Recovery](#crash-recovery).

  The halt's HTTP status is then absent. It is never shown as 0, 200, 403 or 429. The
  same resume rules apply.
- **No retries.** A timeout or transport failure is recorded and keeps the
  last-known-good snapshot. The next request waits for the next scheduled occurrence
  after the floor.
- **No conditional requests.** Each request is a full fetch. The client sends no
  `If-None-Match` or `If-Modified-Since`, so a 304 is an ordinary non-200 halt.
- **One egress address, one budget.** CelesTrak sees one client per egress address.
  Every CameraAgent behind the same NAT gateway, proxy or shared public IP looks like
  the same client and counts against the same allowance. Each CameraAgent's floor and
  phase offset protect only its own requests. For a shared-egress site:
  - enable refresh on as few CameraAgents as the deployment needs;
  - keep the interval well above the two-hour floor;
  - if a 403 or 429 halts one CameraAgent, treat the shared address as refused and
    check the others before resuming any of them.

  #527 requires coordinating shared NAT and cache deployments. A single approved
  cache or distributor is future topology; #528 provides none.
- **No redistribution.** Raw and quarantined provider bytes stay on this CameraAgent's
  host, under the store root. #528 never sends the feed or its quarantine evidence to
  LogicHost or any other service. Local `cameraagent backup` archives do contain them,
  because the store, `raw/` and `quarantine/` included, lies inside the instance a
  backup copies. Keep those archives owner-only, and do not redistribute them or share
  them with third parties. See [Backup And Restore](#backup-and-restore).

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
| `MaximumWireBytes` | `33554432` | 1 MiB–32 MiB | Largest accepted response body on the wire. |
| `MaximumRetainedSnapshots` | `12` | 2–64 | Newest snapshots that retention always keeps. |
| `MinimumRetentionHours` | `48` | 1–720 | Snapshots younger than this are always kept. |
| `MaximumQuarantinedPayloads` | `4` | 1–16 | Newest quarantined raw bodies that are kept as evidence. |
| `StaleAfterHours` | `12` | 3–168 | The refresh-overdue bound. With an active snapshot whose elements are not stale, health degrades with `orbital.refresh-overdue` when no valid provider retrieval (an HTTP 200 the store recorded) has succeeded within this many hours. It does not measure element age: `orbital.snapshot-stale` follows the newest element epoch against a fixed 48-hour envelope, so re-downloading an unchanged old payload clears refresh-overdue but never makes stale elements fresh. |

The store root is `<RawIngressRoot>/.orbital-elements/`. The store is initialized
before the automation runner starts, whether or not refresh is enabled. If it cannot
be initialized, the host keeps running and retries every five minutes. Every status
request, and every health probe while refresh is enabled, may also retry after the shared
initialization backoff (1, 2, 4, 8, 16, then 30 seconds). A repaired root recovers without a restart.

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

- A browser session must send a valid antiforgery token. Without one the command
  returns 400 with `orbital.antiforgery-invalid`, before the owner, key and body checks
  and before anything durable happens. An owner API key with read-write access needs
  no token, because a browser never adds an `Authorization` header to a cross-site
  request. A read-only key is refused by the mutate policy with 403.
- The actor is the authenticated owner identity.
- Each command needs exactly one non-empty `Idempotency-Key` header. A missing or
  repeated header returns 400.
- Each command needs a JSON body. A request with no body is refused with 400 by request
  binding before any orbital check runs. Request bodies reject unknown members.
- Nothing in either command can supply a URL, dataset, path or schedule.

**Status.** `GET` returns the `hvo-orbital-refresh-status-v1` read model:

- `enabled`;
- the active `currentManifestSha256` and `currentPayloadSha256`;
- `lastAttemptUtc`, `lastRetrievalSuccessUtc` and `lastActivationUtc`;
- `nextDueUtc`, the next scheduled occurrence, present only while refresh and the
  definition are both enabled;
- `nextPermittedAttemptUtc`, the floor;
- the `automation` reference: definition ID, revision hash and last run key;
- `providerHalt`: since, HTTP status and reason. The HTTP status is null when no valid
  HTTP status was recorded; the reason then begins with
  `orbital.provider-outcome-unknown` or `orbital.invalid-http-status`;
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

`orbital.ready` describes acquisition only. Element freshness is a health fact
(`orbital.snapshot-stale`) and a metric (`newest_element_age`), not a status reason.

**Refresh.** The body `{ "reason": "..." }` is required, and so is its reason: a
non-empty trimmed reason of at most 512 characters. An absent, blank or over-long
reason returns 400 with `orbital.refresh-invalid-reason`. A refresh is never executed
inside the request. The checks run in this order:

1. request validation;
2. the idempotency key. If the automation store already recorded this key for the
   reserved definition, the same key and payload replay (200,
   `orbital.refresh-replayed`) and a different payload conflicts (409,
   `automation.idempotencyKeyConflict`). Either answer comes before any policy check,
   so a retry still gets its original answer after the queued run has executed and
   moved the floor, or after a halt. A key recorded against an earlier definition
   version conflicts, and a key can be matched only while the reserved definition
   exists;
3. `Enabled`;
4. store availability;
5. the definition exists and is enabled;
6. the halt;
7. the floor.

When all seven pass, the refresh is queued as one run-now occurrence of the reserved
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

- answers a recorded idempotency key first: the same key and payload replay
  (`orbital.resume-replayed`) even after the halt has cleared, and a different payload
  conflicts (`orbital.idempotency-conflict`), before the halt is checked;
- is allowed while refresh is disabled, so you can clear a halt before re-enabling;
- clears a halt with no valid HTTP status recorded in the same way as any other halt;
- never moves the floor;
- does not make a request itself.

The next permitted occurrence makes the next request.

| Command result | HTTP | Reason codes |
| --- | --- | --- |
| Accepted (refresh queued) | 202 with `Location: /api/v1/operations/orbital-elements` | `orbital.refresh-queued` |
| Applied | 200 | `orbital.resumed` |
| Replayed (same key, same payload) | 200 | `orbital.refresh-replayed`, `orbital.resume-replayed` |
| Invalid | 400 | `orbital.antiforgery-invalid`; `orbital.refresh-invalid-idempotency-key`, `-actor`, `-reason`; `orbital.resume-invalid-halt-time`, `-idempotency-key`, `-actor`, `-reason` |
| Conflict | 409 | `orbital.disabled`, `orbital.definition-missing`, `orbital.definition-disabled`, `orbital.provider-halted`, `orbital.not-yet-permitted`, `orbital.no-halt`, `orbital.halt-mismatch`, `orbital.idempotency-conflict` (resume), `automation.idempotencyKeyConflict` (refresh), `automation.run-now-pending` |
| Unavailable | 503 | `orbital.store-unavailable`, the storage fault code, or `automation.run-now-unavailable` |

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
| Failed | `ProviderHalted` | `orbital.provider-http-<status>`; `orbital.invalid-http-status` (status line outside 100–599, halted with no valid HTTP status recorded) |
| Failed | `TransportFailed` | `orbital.request-timeout`, `orbital.http-client-timeout`, `orbital.transport-*` |
| Failed | `Quarantined` | response checks (`orbital.media-type`, `orbital.content-encoding`, `orbital.gzip-invalid`, `orbital.length-mismatch`, `orbital.wire-bound`, `orbital.raw-bound`, `orbital.empty-body`) and parser or normalizer reasons (`orbital.json-*`, `orbital.missing-member`, `orbital.record-contract`, `orbital.snapshot-contract`, …) |
| Failed | `StorageFailed` | `orbital.storage-no-space`, `-database`, `-permission`, `-containment`, `-integrity`, `-io` |
| Failed | none (not admitted) | a storage fault code or `orbital.storage-integrity` at admission, `orbital.internal-error` |

`UnchangedPayload` means the normalized payload matched the active one. A new manifest
is still activated, reusing the payload file. It advances `lastRetrievalSuccessUtc`
but not `lastActivationUtc`, which moves only when the active payload changes.

An attempt that stops before completing settles as `Interrupted`:

- `orbital.provider-outcome-unknown`: no provider outcome was recorded, so the request
  may have been refused. A provider-wide halt with no valid HTTP status is recorded,
  unless a halt is already in force. This covers cancellation during the fetch (for
  example a graceful shutdown) and restart reconciliation;
- `orbital.attempt-cancelled`: cancelled after a valid HTTP 200 was recorded. No halt;
- `orbital.attempt-interrupted`: restart reconciliation after a valid HTTP 200 was
  recorded. No halt.

A valid HTTP 200, one that passed the response checks, is recorded durably and
advances `lastRetrievalSuccessUtc` before the body is parsed. A valid 200 whose content
is later quarantined or fails storage still counts as a retrieval. A 200 rejected by
the response checks does not.

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
- Element age and retrieval time, kept separate. Both use the health check's own rules
  (`OrbitalElementsHealthEvaluator.IsElementStale` and `IsRefreshOverdue`, see "Health
  And Metrics"), so the page and `/health` never disagree about the same facts:
  - **Stale** means even the newest element epoch in the active snapshot's verified
    manifest is more than 48 h old, however recently it was downloaded. Stale elements
    stay in use until a refresh replaces them. If the manifest cannot be read, the
    element age is unknown and the elements read as stale, fail safe.
  - **Refresh overdue** means an active snapshot is installed, refresh is on, and no
    valid provider retrieval (an HTTP 200 the store recorded) has succeeded within
    `StaleAfterHours`. With no recorded retrieval at all it is overdue: the activation
    time is not used in its place.
- Validity reads "Unknown": the source supplies no validity window.
- Provider and dataset, terms, fetch and activation times, the schedule, the last
  attempt, the next permitted attempt and the next due time.
- Any provider halt (time, HTTP status and reason) and any quarantined response. A halt
  with no valid HTTP status recorded (`orbital.provider-outcome-unknown` or
  `orbital.invalid-http-status`) reads "No valid HTTP status recorded" in the panel,
  the resume dialog, the data notice and the page rail. No status number is ever shown
  for it. The last-known-good snapshot stays visible beside a quarantine.
- If the store is unavailable, or no snapshot is installed, the panel says so. It never
  reads either case as an empty sky.

The panel embeds the compact orbital data notice. It lists the requested population and
its coverage, the newest element age, and then each of these that applies: stale
elements, refresh overdue, and a provider halt with its status. These are separate
facts, so stale and overdue still show while refresh is halted, quarantined or needs
repair. A `StorageFailed` last attempt can carry its specific storage reason, such as
`orbital.storage-no-space`, rather than the generic `orbital.storage-failed`. The panel
and notice translate that reason and the `RepairStorage` recommendation into operator
text, without inventing an HTTP status or a provider halt. The installed snapshot, if
any, remains visible, and the failed attempt still holds the provider floor.

The Observatory & location page shows the same notice, read-only, directly above the
satellite pass planner. It describes the snapshot installed now; a completed pass search
keeps the snapshot it was computed from.

The page and the health check agree on every fact, but they summarize differently, by
design:

- Health reports only its first degrading reason (see the order under "Health And
  Metrics"). The panel shows one state word but lists every freshness fact.
- The state word puts an unavailable store first, then a halt. Quarantined and Needs
  repair follow the status `recoveryAction`, so a definition that needs configuring is
  reported before them. A quarantine kept after a later attempt that did not
  quarantine still degrades health, but the state word does not show it; the panel's
  quarantine section does.
- With refresh off, health is Healthy, but the panel still reads Stale for stale
  elements. It never reads Refresh overdue while refresh is off.
- With no active snapshot, the state word is No data (or Disabled). **Exact
  diagnostics** shows health's own state and reason, for example
  `orbital.first-snapshot-overdue`.
- If the manifest cannot be read, the panel reads Stale with the element age unknown.
  Health uses the epoch the store recorded at activation, so it can still read the
  elements as fresh.

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
`/operations/software` (see "Software & Catalog And Automations" above). No path or
provider body ever appears in its data, which holds only these allowlisted keys:

| Key | Meaning |
| --- | --- |
| `Availability` | `Available`, `Disabled`, or the storage failure state. |
| `Reason` | The degrading reason when there is one, otherwise the status `reason`. |
| `RecoveryAction` | The status `recoveryAction`. |
| `Halted` | `true` while a provider halt is in force. |
| `HaltHttpStatus` | The halt's HTTP status. Omitted when no valid HTTP status was recorded; a missing status is never shown as a number. |
| `ActiveRecordCount` | Records in the active snapshot. |
| `ActiveObjectCount` | Distinct objects in the active snapshot. |
| `NewestElementAgeSeconds` | Seconds since the newest element epoch in the active snapshot. This is what element freshness follows. |
| `OldestElementAgeSeconds` | Seconds since the oldest element epoch in the active snapshot. |
| `ActivationAgeSeconds` | Seconds since the active payload last changed. An unchanged re-download does not reset it. |
| `RetrievalAgeSeconds` | Seconds since the last valid provider retrieval (HTTP 200). |
| `LastAttemptOutcome` | The durable outcome of the last attempt. |

A disabled or unavailable store reports only the first three keys. Every other key is
present only when its value is known. Ages are whole seconds and never negative, so an
epoch ahead of the clock reads as 0.

| State | When |
| --- | --- |
| Healthy | Refresh is disabled (no I/O is performed); the active snapshot's elements are fresh and a valid retrieval succeeded within `StaleAfterHours`; or the first snapshot is not yet due. |
| Unhealthy | The store is unavailable or failed integrity verification (`RecoveryAction` `RepairStorage`). |
| Degraded | The first match, in this order: a provider halt (`orbital.provider-halted`); a quarantined last attempt (its reason); a quarantine that has not yet been replaced by an activation; an unsettled attempt; a storage-failed attempt; `orbital.snapshot-stale` (the active snapshot has no newest element epoch, or it is more than 48 hours old); `orbital.refresh-overdue` (no valid provider retrieval within `StaleAfterHours`); then, with no active snapshot, a missing or disabled definition, or `orbital.first-snapshot-overdue` (an attempt already happened, or more than 5 minutes have passed since the first due time). |

The 48-hour element envelope is the #527 selection policy's stale age, read from
`OrbitalSelectionPolicy`, and it is not configurable. An age exactly at either
threshold is neither stale nor overdue. Re-downloading an unchanged old payload
clears `orbital.refresh-overdue` but never makes stale elements fresh.
`OrbitalElementsHealthEvaluator.IsElementStale` and `IsRefreshOverdue` expose the two
rules, so the operator UI applies the same comparisons.

Health, status and the gauges report element staleness at that one envelope,
`OrbitalSelectionPolicy.StaleAbsoluteAgeHours`. While refresh is enabled, health stays
Degraded from then on, however old the elements become: `orbital.snapshot-stale`,
unless an earlier reason in the order above applies. Status reports acquisition, not
freshness, and the age gauges report the age itself. There is no separate expired
health state. Expiry is a per-record selection outcome,
`OrbitalElementFreshness.Expired`, that prediction consumers apply through the #527
selection policy (`OrbitalRecordSelector.Select` in
`src/HVO.SkyMonitor.Astronomy/Orbital/OrbitalRecordSelection.cs`). For each record of
the requested object, the selector measures the requested interval's start and end
against the record's epoch, in hours, positive when the interval is after the epoch.
It classifies the record by the first rule that matches:

1. an end offset beyond `MaximumEpochOffsetHours` (168 hours) is `Expired`;
2. a start offset below `MinimumEpochOffsetHours` (−24 hours, so an epoch more than a
   day after the interval starts) is `FutureBeyondPolicy`;
3. a start or end offset larger than `StaleAbsoluteAgeHours` (48 hours) in absolute
   value is `Stale`;
4. anything else is `Fresh`.

A record outside the −24 to 168 hour window is never selectable
(`OutsidePolicyWindow`), whatever `AllowStaleDiagnostics` says. A `Stale` record inside
the window is withheld (`StaleWithheld`) unless the policy sets
`AllowStaleDiagnostics`. Then it is eligible, but the prediction products accept that
policy, and a stale record's lineage, only for diagnostic use.

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
| `skymonitor.cameraagent.orbital_elements.active_objects` | gauge | `{object}` | — |
| `skymonitor.cameraagent.orbital_elements.newest_element_age` | gauge | `s` | — |
| `skymonitor.cameraagent.orbital_elements.oldest_element_age` | gauge | `s` | — |
| `skymonitor.cameraagent.orbital_elements.activation_age` | gauge | `s` | — |
| `skymonitor.cameraagent.orbital_elements.halted` | gauge | `{halt}` | — |

The gauges read a cached provider-state snapshot, so a scrape performs no I/O. The age
gauges are computed at scrape time from the cached epochs and times. Only the refresh
job publishes that snapshot, from a durable read serialized with its single-flight
lock:

- at the end of every occurrence, including skipped ones;
- once the store first becomes available at startup, after restart reconciliation:
  straight away, or at the five-minute retry that finds a repaired store. A durable
  halt, or one that reconciliation has just recorded, therefore reads `halted` 1 after
  a restart, before any attempt;
- after an applied operator resume, so `halted` drops to 0 then rather than at the next
  attempt. A replayed resume does not publish.

Status reads and health evaluation never publish. Nothing is emitted before the first
publication. A store that later becomes unusable leaves the last published values in
place. An occurrence cancelled by shutdown does not publish; the next start does.

Logs carry reason codes, SHA identities, attempt IDs, store-relative directory names
and counts in their structured fields, never an absolute path or a provider body. The
event IDs are:

| Event IDs | Source |
| --- | --- |
| 7700–7717 | The store. 7715 is an unknown provider outcome halted fail-safe, 7716 a halt for a status line outside 100–599, and 7717 the resume of a halt with no valid HTTP status recorded. |
| 7720–7725 | The snapshot reader and best-effort preparation. |
| 7730–7731 | Store availability. |
| 7740–7751 | The refresh job. |
| 7755–7757 | Operator commands. |

Some events also carry the exception object: 7706, 7708, 7709, 7710 (store), 7720
(reader), 7730 (availability), and 7743, 7744, 7745, 7747, 7750 (job). So do two
warnings that have no event ID. The operator endpoints log them under the category
`HVO.SkyMonitor.CameraAgent.Endpoints.CameraAgentOrbitalOperationsEndpoints` when a
request fails unexpectedly:

- `CameraAgent orbital elements status read failed.`, when a status read throws;
- `CameraAgent orbital elements command failed.`, when a refresh or resume throws.

The response never carries the exception. Its body is the sanitized 503 problem, with
the fixed title `Orbital elements status is unavailable.` or `The orbital elements
command could not be completed.` and no reason code. An exception's message and stack
trace come from the runtime or SQLite. An I/O exception message can name an absolute
file path under the store root. Treat these log entries as deployment-local, and
redact them before sharing them outside the site.

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

Response-level rejections have no raw body. A status line outside 100–599 is a halt,
not a quarantine. The newest `MaximumQuarantinedPayloads` bodies are kept and older
ones are pruned automatically. The quarantine state clears at the next successful
activation. If the cause is a provider format change, it needs a parser change under
the contract; no configuration setting accepts it.

## Crash Recovery

On every start, before the automation runner starts, the store:

- marks any `InFlight` attempt as `Interrupted`. It keeps its start time, so the floor
  still applies:
  - with a recorded valid HTTP 200, the reason is `orbital.attempt-interrupted`, and
    nothing else changes;
  - with no recorded provider outcome, the request may have reached the provider and
    been refused. The reason is `orbital.provider-outcome-unknown`, and the store
    records a provider-wide halt with no valid HTTP status (event 7715), unless a halt
    is already in force. An existing halt is never replaced;
- settles staging intents and deletes partially written staging files;
- removes publisher temporaries;
- prunes quarantine bodies that no retained attempt names;
- leaves anything ambiguous where it is and logs it. That covers a directory or link
  where a file is expected, and a name that is not a SHA-256.

Activation is one SQLite transaction. After a crash, either the previous manifest or
the new one is active, never a mixture. The automation runner claims an occurrence and
advances its progress in one transaction, so a stopped process never repeats it.

If the process stays up but cannot record an attempt's terminal outcome (for example,
the halt write fails), the attempt stays `InFlight` (events 7747 and 7748). A halt is
never downgraded to another outcome. Later attempts refuse with
`orbital.attempt-in-flight`. Status reports `orbital.attempt-unsettled` and
`RepairStorage`. Fix the storage fault and restart. Unless a valid HTTP 200 was
recorded first, the restart settles the attempt as `orbital.provider-outcome-unknown`
and holds refresh with a halt that has no valid HTTP status recorded. The status that
could not be written is not recorded anywhere. Event 7747 names the settlement that
failed (`Halt`, `Quarantine`, `Transport`, `Interrupted` or `Storage`); `Settlement=Halt`
means the provider answered with a halt status or a status line outside 100–599, and
`Settlement=Storage` means the storage-failure fallback for an unsettled storage fault
(such as a retrieval record that could not be written) could not be written either.

The unknown-outcome halt is deliberately conservative. It also holds refresh in cases
where the provider did answer but nothing durable recorded it:

- a crash before a response-rejected quarantine (wrong media type, encoding or length)
  commits, because a rejected response never records a valid HTTP 200;
- a crash before a transport-failure record commits;
- a failure to write both the pending settlement and its fallback, which leaves the
  attempt `InFlight` until restart;
- cancellation during the fetch, including a graceful shutdown.

No automatic retry follows the floor in any of these cases, and a refresh request
cannot bypass the hold; it is refused with `orbital.provider-halted`. To recover from
`orbital.provider-outcome-unknown`:

1. Optionally set `Enabled` to `false` while you investigate. The halt alone already
   stops every request.
2. Read events 7715, 7745, 7747 and 7748 and the last run detail to see what the
   attempt was doing.
3. Confirm the provider is not refusing this site before resuming. Do not send test
   requests from this site's egress address to find out. A refusal of a shared egress
   address applies to every CameraAgent behind it.
4. Resume with `expectedHaltSinceUtc` from `providerHalt.sinceUtc`. The two-hour floor
   still applies after the resume.

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

## Removing Orbital Data

Use this to remove orbital data from a CameraAgent, for example when you retire
acquisition or decommission the device. Everything #528 stores, provider bytes
included, lives under the store root `<RawIngressRoot>/.orbital-elements/`. Raw and
quarantined provider bytes are never sent to LogicHost or any other service, so there
is no central copy to remove. Local `cameraagent backup` archives are the exception:
every archive taken while the store existed contains it, `raw/` and `quarantine/`
included (see [Backup And Restore](#backup-and-restore)). If your purpose requires the
orbital data to be gone, also delete those archives, or move them to owner-controlled
storage as in step 4, and record that in the deployment ledger.

1. While the CameraAgent is running, read `GET /api/v1/operations/orbital-elements/`
   and record these fields in the deployment ledger: `lastAttemptUtc`,
   `nextPermittedAttemptUtc` (the floor), `providerHalt` (since, HTTP status or "no
   valid HTTP status recorded", and reason), and `currentManifestSha256`. This is the
   floor and halt evidence. A new store starts without it.
2. Set `CameraAgent:OrbitalElements:Enabled` to `false`, and disable or remove the
   `orbital-elements-refresh` definition. Do this before the store is removed, so no
   occurrence can be admitted against an empty store.
3. Stop the CameraAgent.
4. Then either:
   - archive the whole `.orbital-elements/` directory, including the `-wal` and `-shm`
     sidecars, to owner-controlled storage, and record its checksum and the
     disposition in the deployment ledger; or
   - delete the whole directory, and record the deletion and the step 1 evidence in
     the deployment ledger.

   Remove the directory as a unit. Do not delete individual files, rows or
   subdirectories from a store you intend to keep. The triggers and the
   content-addressed layout depend on the store staying whole.
5. Start the CameraAgent. If `.orbital-elements/` is absent, a new, empty store is
   created. Consumers then read `NoActiveSnapshot`, and health reports no snapshot
   (healthy while refresh stays disabled).

An archive of the store, like a `cameraagent backup` archive, holds `raw/` and
`quarantine/`, which are CelesTrak's response bytes exactly as received, and
`payloads/`, which is derived from them. The #527 decision infers no redistribution
licence for the feed:

- Keep the archive on owner-controlled storage.
- Do not publish it, attach it to an issue or support ticket, upload it to LogicHost or
  another site, or include it in a release or installer.
- Share checksums, manifest identities and counts instead of bytes.

To restart acquisition later on a new store, follow the checks in
[Integrity Failure And Store Replacement](#integrity-failure-and-store-replacement):
at least two hours since the recorded `lastAttemptUtc`, and the cause of any recorded
halt resolved, before setting `Enabled` to `true`.

## Backup And Restore

`cameraagent backup` copies every folder and regular file of the instance root, and
the store lies inside it: `<RawIngressRoot>` is the container's `/app/data/raw`, bound
from the instance's `state/raw`. The store is not one of the SQLite stores a backup
snapshots, so it is copied as files while the instance is stopped (a backup stops a
running instance first): the database with any `-wal` and `-shm` sidecars, `raw/`,
`quarantine/`, `payloads/` and `manifests/`.

- Backup archives are the service account's alone on the host
  (`<product-root>/backups`, mode 0700, files 0600). Copy one only to owner-controlled
  storage.
- Do not redistribute an archive or share it with a third party. It holds CelesTrak's
  response bytes, as described in [Removing Orbital Data](#removing-orbital-data).

`cameraagent reset-state` deletes the whole raw tree, the store included. The checks
in [Integrity Failure And Store Replacement](#integrity-failure-and-store-replacement)
then apply before refresh is enabled again.

**Restoring an older archive rewinds the provider state.** `cameraagent restore` puts
the instance root back exactly as it was archived, so the orbital store comes back as
of the backup, with its floor, halt and attempt history. A halt, floor or attempt
recorded after the backup is lost, and nothing carries it forward. An archive taken
before the store existed restores no store at all, so the instance starts with an
empty one, with no floor and no halt. This is unlike an installer upgrade or rollback:
their snapshots, and the automatic recovery of a failed candidate, never touch
`.orbital-elements/` (see [Rollback](#rollback)). A restore of an older backup does
rewind it, so the replacement checks apply: at least two hours since the last attempt,
and any halt resolved. Before you restore an archive older than the latest halt or
attempt:

1. While the instance still runs, record the status fields listed in step 1 of
   [Removing Orbital Data](#removing-orbital-data).
2. Make sure refresh is disabled (`CameraAgent:OrbitalElements:Enabled=false`) in the
   restored instance before it runs. A restore also brings back the archive's
   configuration, including the operator settings file, so a setting changed on the
   live instance before the restore does not survive it. A restore leaves the instance
   uninstalled, so set the value after the restore and before `cameraagent reinstall`
   starts it.
3. Re-enable refresh only after you confirm that the provider is not refusing this
   site, without sending test requests to find out, and that at least the two-hour
   floor has elapsed since the last request actually made, from step 1 or the logs.
   Any halt the restore dropped must be resolved first.

## Rollback

**Installer snapshots never touch the orbital store.** A migrating upgrade's database
snapshot, the automatic recovery of a failed candidate, and
`hvo-sky-install cameraagent rollback` restore only the stores in the installer's
snapshot set. The orbital store is declared in the image's state contract
(`orbitalElements`, writes 1, reads 1 to 1) but deliberately left out of that set
(`docs/hvo-install-contract.md` section 6). The floor, halt and history recorded
before a rollback therefore survive it, and the release rolled back to cannot request
inside the floor or after a refusal. `cameraagent restore` of an older backup is
different: it does rewind the store (see [Backup And Restore](#backup-and-restore)),
so the replacement checks apply before refresh is enabled again: at least two hours
since the last attempt, and any halt resolved.

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

### Warm preparation and bounded execution (Refs #1425)

The initializer prepares persisted active state in the shared reader before the runner starts, even with refresh
disabled. After durable activation and retention, the job prepares the new manifest identity through the same
pinned, verified materialization path. This policy always prepares, including on hosts with no reader consumers;
it holds at most two materialized snapshots. The job's candidate and prepared copy can overlap in memory.
Preparation failure never changes durable activation or the prior cache: reader events 7724/7725 report it, and the
successful automation outcome includes `orbital.preparation-unavailable` when job-side preparation fails.

A consumer arriving between activation and preparation completion can still pay the cold load or wait for its load
lease. Reads after preparation use the cache. A prepared-only Astronomy contract is a possible coordinated follow-up;
this change preserves the existing consumer contract and typed unavailable reasons.

Failed initialization has one shared backoff window across reader, operator and initializer entry points: 1, 2, 4,
8, 16 and then 30 seconds, capped at 30 seconds, using the injected clock. Reads during the window return
`StorageUnavailable` without initialization I/O. Success resets the window; caller cancellation never creates one.
Monotonic elapsed time bounds a wall-clock correction, and observing a backwards clock expires the window.
The initializer's existing five-minute recovery probe cadence remains in force when no other caller probes.

Only the orbital adapter opts into a single bounded worker outside the sequential automation sweep. The runner
reserves capacity before claiming periodic or run-now work; when occupied, the occurrence stays due or queued.
Dispatch never executes even a synchronous adapter inline. The original occurrence and run key remain authoritative,
and the automation journal stays `Running` until the real terminal result is committed. Run-now keeps priority and
its existing fifteen-minute age check. Host shutdown cancels and drains work while holding execution authority;
restart records interruption and never automatically replays provider work. Floor, halt, precision wait, request
deadline and single-flight policy remain in the existing refresh job. See the Refs #1425 measurements in
[performance validation](../planning/performance-validation.md) for the transferred job cost and memory evidence.
