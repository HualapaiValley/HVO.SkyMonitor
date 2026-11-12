# CameraAgent drain and scheduled restart

Issue #1711 implements [Roy's installer-owned verifier decision](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1711#issuecomment-6722560540).
Deployment.Cli owns execution and authoritative receipts. CameraAgent owns capture
admission, authenticated operator intent and a receipt projection. No browser
credential grants Docker access, installer credentials, command execution or host
power control.

## Register the capability

The capability is opt-in. An operator runs the matching installer, independently
of CameraAgent, as the instance's recorded runtime account:

```bash
hvo-sky-install cameraagent lifecycle-worker --instance-id <uuid>
```

Use the host's configured product root, or its explicitly supported
`--product-root`. The worker reads and verifies the installed manifest, result and
lifecycle credential; its HTTP destination comes from that installation. The
request cannot supply an address, executable, arguments, Docker verb or path.
Do not run it as root or inside CameraAgent's process/container. This command is
not started by installation or by the web application. Arranging a service manager
to run it is an operator deployment decision.

`--once` performs one poll and any admitted operation, then exits. With `--json`,
it reports `poll-completed`; the durable receipt supplies the actual lifecycle outcome. The worker
registers through installation-token authentication and refreshes a 30-second
in-memory lease. Without that lease, System control's drain stays disabled and
the scheduled-restart task is unavailable. A CameraAgent startup requires a fresh
worker exchange. Drain polling also renews the lease at least every ten seconds
while the transport responds. Idle heartbeats do not write durable state or evaluate schedules.

## Drain and restart contract

1. An operations-authorized owner confirms **Drain** with a nonempty reason.
   The durable request retains the actor, reason, command identity, request time
   and expected capture-control version. Only one request may be pending.
   Replaying its identity with the same payload returns its original intent;
   changing the payload conflicts.
2. The installer takes the existing deployment lock. Upgrade, backup and other
   installer mutations cannot overlap the runtime lifecycle. It durably journals
   **Draining** before pausing acquisition with the expected version. The current
   exposure must finish through durable ingress. Accepted raw, lane, processing
   and artifact-delivery pending/leased counts must all reach zero within two
   minutes using a monotonic elapsed-time budget. Current pending/leased work, including
   held delivery retries/quarantine, can time out. Fresh numerical observations remain
   usable when a historical raw reconciliation warning marks health Degraded;
   unavailable, initializing, unhealthy or stale observations never prove drain.
   The command does not purge, acknowledge or bypass accepted evidence. Explicit archived
   replay retains the existing installer checkpoint semantics: pinned queued
   replay is preserved and interrupted claims recover, rather than blocking drain.
3. A standalone **Drain** has finish semantics, not checkpoint semantics. After
   its pause, all four accepted queue families must finish: raw ingress, capture
   lanes, processing and artifact delivery (outbox) must each report zero pending
   and zero leased work, from fresh observations, within the two-minute monotonic
   bound of step 2. A standalone Drain never resumes capture and never purges
   accepted work. It ends with one of these terminal codes.

   Once its pause has been issued, capture stays paused until an explicit,
   authorized resume:
   - **Succeeded** `lifecycle.drained-paused`: every family reached zero and no
     cancellation was recorded by the post-drain boundary.
   - **Failed** `drain.timeout`: the two-minute bound elapsed before every family
     reached zero. The bound takes precedence over a cancellation recorded
     during the drain.
   - **Failed** `drain.failed-check-capture`: the drain call failed or returned
     without proving the boundary, for example unobserved queues or remaining
     work.
   - **Cancelled** `drain.cancelled-paused`: an owner cancelled after the
     installer claimed the request. Once claimed, a Drain still pauses and
     drains. The installer reads the cancellation when the drain ends, at the
     same post-drain boundary where a Restart checks it before restarting. The
     receipt records who cancelled, when and why. Cancellation does not restore
     the prior running state.

   Before any pause, capture is left as it was:
   - **Cancelled** `lifecycle.unclaimed-cancelled`: the owner cancelled the
     request before the installer claimed it. If the installer's claim raced that
     cancellation, its own journal records `lifecycle.claim-refused` for the same
     request, and Recent control receipts keeps the CameraAgent record.
   - **Failed** `lifecycle.capture-conflict`: capture was not initialized, its
     capture-control version no longer matched the request, or it was neither
     Running nor Paused.
   - **Failed** `lifecycle.installer-busy`: another installer deployment
     operation held the deployment lock when the worker would have claimed it.

   On either side of the pause, check capture state before resuming:
   - **Failed** `lifecycle.timeout` or `lifecycle.failed-check-capture`: an
     exchange with CameraAgent timed out or failed, or the installer could not
     write its journal. After a proven drain this is the post-drain boundary
     read or the journal write that records the drained boundary; neither is
     reported as Succeeded or as a drain failure.
   - **Cancelled** `lifecycle.cancelled-check-capture`: the installer process was
     stopped during the operation. This is not an owner cancellation.
   - **Failed** `lifecycle.installer-interrupted` or
     `lifecycle.projection-orphaned`: an interrupted worker or a projection with
     no installer journal was settled; see the recovery sections below.

   The installer journals the drained boundary before publishing it to
   CameraAgent, and adopts it only once that write succeeds, so CameraAgent
   never holds evidence the journal lacks. An installer interrupted there, or
   one that stops because its journal stays unwritable, is settled by a
   restarted worker as `lifecycle.installer-interrupted` once the journal is
   writable, instead of having its receipt refused.
   A cancellation recorded after the post-drain boundary is too late to change
   the outcome: the receipt stays `lifecycle.drained-paused`, and Recent control
   receipts shows the late cancellation beside it. Pause itself remains the
   existing admission-only operation: it does not wait for every accepted
   processing/delivery queue to empty.
4. The registered **Scheduled CameraAgent Restart** task delegates the same
   sequence to the installer. Its reserved definition is `cameraagent-restart`,
   its target is `installed-cameraagent`, and its periodic interval is 3,600 to
   86,400 seconds. Run now is unavailable. A scheduled occurrence inside a
   capture window is recorded as skipped; it never forces that window closed.
   Before draining and again before requesting restart, the schedule must be
   demonstrably closed for the remaining lifecycle budget: four minutes plus the
   one-second supervised-stop delay before drain, then two minutes plus that delay
   immediately before restart. The drain budget is not charged twice. Manual pause, unknown solar
   boundaries and unavailable scheduling do not count as a closed window.
   Current force-open/force-closed overrides participate in that decision.
5. The installer journals **Restarting**, including the verification deadline,
   before calling the fixed installer-only supervised process-restart mechanism.
   CameraAgent stops only itself; its existing supervisor starts it again.
   A lost acknowledgement is ambiguous and leads to verification, never a second
   restart request. Once an accepted delayed stop is armed, an independent process-local
   capture/resume fence remains until that original process exits. Cancellation or a
   terminal failure receipt cannot reopen capture while its stop is still pending.
6. The independent installer journals **Verifying** and allows two monotonic minutes for a
   different CameraAgent process identity, initialized capture control, the same
   durable pause version, a nondecreasing durable capture sequence, and the
   drained queue boundary. Only then may it restore the prior running state,
   fenced by the exact pause version and latest accepted capture-command identity.
   An originally paused agent stays paused.
   Capture admission stays fenced from Draining until terminal receipt publication,
   including while a verified resume awaits acknowledgement. If that acknowledgement
   is lost, terminal failure publication first restores the pause; replaying the
   receipt cannot undo a later explicit owner resume. Local resume is fenced while
   a lifecycle request is active, including after
   CameraAgent restarts. Terminal settlement releases the intent fence, but cannot
   release an armed-stop fence in the original process. The next process reloads the
   durable pause and terminal evidence before an explicit authorized resume.

For scheduled restart failures or cancellation **before a stop was issued**, the
installer restores the prior running state before publishing the terminal receipt,
when the same process and its exact completed pause command still own the boundary.
It uses the stored command identity, actor, expected version and result version,
and requires that pause to remain the latest accepted command. A later accepted
owner pause, including a successful no-op with the same capture version, withdraws
recovery authority. The conditional resume checks this ordering in the same SQLite
write transaction as the state change, so a pause accepted after the installer's
snapshot is preserved too. Ordinary pause/resume versions and no-op semantics are
unchanged. A definite refusal of the restart mechanism
also permits this recovery; an ambiguous restart acknowledgement does not, and
neither does a refusal the installer could not write to its journal. An
originally paused host stays paused. If the recovery resume is unconfirmed,
publication restores paused admission and records `restart.resume-unconfirmed`.
A failed Restarting publication before the stop call records a separate durable
`restartNotIssued` proof before attempting restore. Durable restart intent alone
is not that proof: after a crash, an unmarked intent remains ambiguous and never
authorizes another stop or an automatic pre-stop restore. Standalone Drain always
leaves capture paused. No recovery deletes accepted work.

## Receipts and failure recovery

The installer writes its authoritative, atomically published and synchronized
journal to `<product-root>/operations/control-worker-<uuid>.json`, outside the
CameraAgent state mounts. It retains up to 100 receipts and never discards
unpublished evidence to make room. The journal includes ordered phases, observed UTC times (which may reflect clock corrections),
request actor/reason, original/verified process identity, pause version, durable
capture sequence, deadline and terminal code. The file is owner-only. Installer
credentials never appear in requests or receipts.

CameraAgent persists immutable intent, cancellation intent and bounded receipt
projections under the raw-ingress root's `.lifecycle-control/state.json`.
**Recent control receipts** displays progress and terminal outcomes; the page
refreshes active progress every two seconds. Its cancellation command requests
settlement at a bounded boundary, with a required cancellation reason. It records who cancelled, when and the reason in
Recent control receipts. An unclaimed Queued request is cancelled locally without
waiting for a worker; a concurrent late installer claim is refused before any pause.
An unclaimed request does not fence ordinary resume. Claimed requests require
installer settlement; for a claimed standalone Drain that boundary is the end
of the drain (step 3 of the contract above). Cancellation cannot undo an already accepted delayed stop.
Closing the confirmation dialog sends no request. Storage faults return Unavailable
without faulting the Blazor circuit. If local cancellation succeeds but the separate
SQLite automation-history write is busy or fails, the UI reports Unavailable while
retaining the cancellation. History is reconciled from the terminal receipt after
the database becomes available; cancellation does not need to be repeated.

If CameraAgent never returns, the installer writes **Failed** with
`restart.verification-timeout` locally even though it cannot deliver that receipt
to the offline UI. The last acknowledged pause remains recorded; reachability
is not proof of capture admission state. Inspect the installer journal, recover
the supervisor/process, then read capture state before explicitly resuming.
When CameraAgent returns, the worker republishes retained terminal evidence.
Pending control evidence does not prevent the offline recovery route. Docker must
confirm an absent or stopped container; for a running container with cached
`unhealthy` status, the installer also probes the fixed container-local `/alive`
endpoint now. Any current HTTP reply, including an error response, means reachable
and keeps ordinary settlement gating. Only an explicit connection failure or a
bounded no-response timeout admits the offline exception. Starting/missing health,
probe execution errors and Docker inspection errors do not authorize recovery.
The control-journal exception applies to `uninstall`, `restore`, `reinstall`, and
`rollback`; it does not change their other prerequisites. Reinstall requires a
preserved uninstalled instance, restore requires an eligible target and verified
backup, and rollback still requires its original-image and authenticated
pre-mutation checks. For an original host that cannot provide those checks, use
preserve-by-default uninstall, restore if needed, then reinstall.

A retained nonterminal control projection can refuse recovery's final resume with
409 even after the recovered image and paused boundary have committed. In that
case leave both journals intact and complete these steps:

1. Let the already-running `lifecycle-worker` poll, or explicitly run it with
   `--once` if no worker is running. Once the recovery command releases
   `deployment.lock`, the worker may settle retained receipts during a **Running /
   Committed** reinstall or rollback with a verified paused boundary. It cannot
   start new runtime-control work while that deployment is incomplete. Before
   commit, settlement is also allowed for incomplete reinstall or rollback when
   an authenticated, initialized CameraAgent reports a nonempty process identity
   different from the receipt's recorded original identity. This includes a
   reinstall whose manifest still says Uninstalled and rollback's Restoring
   phase after exact rollback also fails. A missing original identity, the same
   process, or an uninitialized process does not grant this exception.
2. Check that the retained control receipt is terminal and published. The existing
   conditional settlement preserves request/audit history and does not replay a
   stop. A returned process has a new identity, so the old process's pre-stop
   resume authority does not transfer. Capture remains paused until the recovery
   command resumes it. Terminal-but-unpublished receipts use the worker's existing
   publication path, which does not need the deployment lock.
3. Rerun the **same** reinstall or rollback command with `--resume`. Its committed
   retry verifies the recovered instance, resumes through the normal native
   authority and completes the deployment journal without another image start.
   For a precommit failure, correct the reported underlying failure first, then
   use the same command with `--resume`; its existing phase checks determine
   which deployment steps must be retried.

If reinstall cannot reach commit and the worker cannot prove a different
initialized process (or the installed tooling predates this recovery path),
an operator can stop **only the affected instance's container**, then retry the
same reinstall command with `--resume`. Determine its name from the instance ID
(`hvo-skymonitor-` followed by the instance GUID without hyphens):

```bash
docker stop hvo-skymonitor-<instance-guid-without-hyphens>
# Repeat the original reinstall invocation, retaining its options, with --resume.
```

Stopping the container prevents capture; it does not clear either journal.
The offline gate can then admit the reinstall retry. Once the replacement
process returns, run the worker as above and retry `--resume` if the retained
projection still refuses the final resume. Do not use this workaround to skip
rollback's original-image/authenticated preconditions; if those cannot be met,
use the preserve-by-default uninstall/restore/reinstall route described above.

Do not edit or delete either journal. Definite quarantined refusals still require
reconciliation; they never qualify for the offline exception.

An installer-worker interruption is settled as terminal failure
(`lifecycle.installer-interrupted`) once any eligible pre-stop restore is proven
or definitely refused. Transport failure, unavailable capture initialization, or an
outstanding `PauseRequested` leaves the journal nonterminal so a later poll can
retry after the pause settles and confirm the same idempotent, version-fenced
resume; an ambiguous restart is not replayed. Startup
and later polls use the same owned-pause settlement. A busy deployment lock leaves
a journal-owned interrupted operation pending and retries on a later poll; it is
not classified as an orphan or terminalized without attempting safe restore.
Interrupted installer deployment work must be recovered before starting another
runtime lifecycle. The recovery exceptions above permit retained-receipt
settlement only, not another lifecycle request. Failures and cancellation preserve accepted capture/artifact
records, pause intent and the journal. Corrupt, unsupported or linked state is
refused rather than silently reset.

The automation run remains **Running** across CameraAgent restart until its
matched installer terminal receipt completes it. Only this registered lifecycle
task delegates run completion when its exact run has durable installer intent. A crash
between claiming a run and submitting that intent is recovered as Interrupted; an
unrelated retained request cannot keep it Running. Ordinary automation restart recovery and claimant
ownership are unchanged. Startup, runner sweeps and operator history reads reconcile a retained
terminal receipt with its still-Running reserved restart run, including a crash between
local unclaimed cancellation and SQLite history completion. The indexed lookup is
bounded to that definition's retained history and never reissues an effect. Neither
reconciliation nor installer publication rewrites an already terminal run.
Retaining a new task kind is a forward-only definition change, consistent with
the other appended registered task kinds; older binaries cannot read it.

Before a deployment transition can start or resume, the installer checks the
selected instance's runtime-control journal under the deployment lock. Active,
unpublished, or quarantined control evidence refuses ordinary deployment before it
can write a new deployment journal. The offline recovery exception above applies
only to the four named recovery commands and never to quarantined refusals.
While CameraAgent is reachable, run the explicit `lifecycle-worker` to settle
interrupted runtime work first; retain quarantined evidence for offline
reconciliation rather than deleting or editing either journal. A worker
interrupted before the stop retries transport failures without making its
receipt terminal, and reuses the original version-fenced resume command. Later
owner commands remain authoritative. Drain polling renews the worker lease at
least every ten seconds while the transport responds. A heartbeat transport outage
does not abandon the authoritative drain; its existing budget and outcome still apply.
A transient failure to
pause after an unconfirmed resume returns a retryable 503; a concurrent owner
pause can satisfy that settlement boundary. The same lifecycle fence also refuses
new manual focus previews, calibration acquisition, activation and rollback during
Draining, Restarting and Verifying. Explicit requests use the existing capture-control
conflict response (409); scheduled capture continues waiting for admission release.
If a lifecycle claim races calibration planning, refusal retires only the unstarted
plan without consuming an attempt; its terminal idempotency history remains, and
a new request can proceed after lifecycle release. Interrupted attempts remain recoverable.
The command gate lets an already admitted preview or calibration source/master
generation finish before a pause completes, without adding cancellation. Calibration
profile publication and SQLite finalization follow that generation boundary and are
not independently tracked by drain. After terminal settlement, ordinary pause/resume
semantics apply again; an armed stop still fences its original process until restart.
Refs #1746

## Expired workers, refused publication and damaged projections

System control identifies an expired worker lease while a request is active, and
separately identifies a pending stop in the current process. An owner may cancel an
unclaimed Queued request and use ordinary resume. For claimed orphaned work, restore
the independent worker using the same installation and retained journal. It records
`lifecycle.installer-interrupted`, never reissues an ambiguous stop, and restores
only a provably owned pre-restart pause. If a restored active CameraAgent projection
has no installer journal authority, the worker settles it as
`lifecycle.projection-orphaned` without replaying its effects. Inspect the receipt
and actual capture state before an explicit resume; an armed stop keeps its original
process fenced even after settlement.

A definite 4xx publication refusal is durably recorded in the installer journal's
`refused` entries with operation ID, status and UTC time. It emits
`publication=quarantined` and continues heartbeating and processing other requests.
The refused receipt is not endlessly replayed or silently discarded. The expected
unclaimed cancellation/claim race is acknowledged without quarantine only when
CameraAgent proves the same request already has an audited terminal local
cancellation and the worker's receipt has no effect metadata. The canonical
cancellation/audit remains unchanged, and the journal entry becomes acknowledged
and eligible for ordinary retention. At the
100-receipt bound, retained refused/unpublished evidence prevents new work until
operator reconciliation. Preserve the journal and compare the refused receipt with
the matching CameraAgent request/backup; repair the consistent installation state
or archive the complete journal as retained evidence during offline maintenance
before creating a new journal. Never edit a receipt to make a conflict disappear.

Corrupt, unsupported, oversized or linked CameraAgent projection files remain a
fail-closed startup condition. Recovery is an **offline operator maintenance** task:
ensure no original process or delayed stop remains, retain copies/checksums of
`.lifecycle-control/state.json`, the authoritative installer journal, automation
SQLite and capture-control journal, then restore a mutually consistent known-good
backup. If no such backup exists, preserve the damaged projection separately and
reconstruct its receipts from the authoritative installer evidence before restarting
CameraAgent; do not delete pause intent, automation history or accepted frames to
make startup pass. Validate the repaired JSON/schema and capture state with the
virtual/fake recovery tests before authorizing normal operation. This runbook does
not grant the web owner a host-power or reset capability.

## Validation and deployment boundary

Regression tests use simulated process lifetimes, fake HTTP transport/windows,
temporary SQLite/file state and virtual sources. They exercise success,
never-returning CameraAgent, hanging drain, ambiguous acknowledgement,
cancellation, unsafe windows, identity/version/continuity conflicts, authorization,
durable replay, admission fencing, automation history and UI confirmation.

Changing the installer-only lifecycle routes requires deployment-risk review.
Local and development-v1 CI evidence is software integration evidence; it does
not qualify a production supervisor, physical camera or deployment. Starting the
worker or changing service-manager configuration requires the operator's normal
deployment authorization.
