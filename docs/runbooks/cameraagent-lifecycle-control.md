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
worker exchange. Idle heartbeats do not write durable state or evaluate schedules.

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
   minutes using a monotonic elapsed-time budget. A required quarantined or offline-delivery backlog can time out;
   this command does not purge, acknowledge or bypass it. Explicit archived
   replay retains the existing installer checkpoint semantics: pinned queued
   replay is preserved and interrupted claims recover, rather than blocking drain.
3. A standalone drain ends **Succeeded**, with capture paused until an explicit
   resume. Pause itself remains the existing admission-only operation: it does
   not wait for every accepted processing/delivery queue to empty.
4. The registered **Scheduled CameraAgent Restart** task delegates the same
   sequence to the installer. Its reserved definition is `cameraagent-restart`,
   its target is `installed-cameraagent`, and its periodic interval is 3,600 to
   86,400 seconds. Run now is unavailable. A scheduled occurrence inside a
   capture window is recorded as skipped; it never forces that window closed.
   Before draining and again before requesting restart, the schedule must be
   demonstrably closed for at least four minutes. Manual pause, unknown solar
   boundaries and unavailable scheduling do not count as a closed window.
   Current force-open/force-closed overrides participate in that decision.
5. The installer journals **Restarting**, including the verification deadline,
   before calling the fixed installer-only supervised process-restart mechanism.
   CameraAgent stops only itself; its existing supervisor starts it again.
   A lost acknowledgement is ambiguous and leads to verification, never a second
   restart request.
6. The independent installer journals **Verifying** and allows two monotonic minutes for a
   different CameraAgent process identity, initialized capture control, the same
   durable pause version, a nondecreasing durable capture sequence, and the
   drained queue boundary. Only then may it restore the prior running state,
   fenced by the exact pause version. An originally paused agent stays paused.
   Capture admission stays fenced from Draining until terminal receipt publication,
   including while a verified resume awaits acknowledgement. If that acknowledgement
   is lost, terminal failure publication first restores the pause; replaying the
   receipt cannot undo a later explicit owner resume. Local resume is fenced while
   a lifecycle request is active, including after
   CameraAgent restarts. Terminal failure/cancellation releases that fence for
   an explicit authorized resume; it never automatically resumes capture.

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
settlement at a bounded boundary; cancellation does not undo an already issued
restart or automatically resume capture. Closing the confirmation dialog sends
no request.

If CameraAgent never returns, the installer writes **Failed** with
`restart.verification-timeout` locally even though it cannot deliver that receipt
to the offline UI. The last acknowledged pause remains recorded; reachability
is not proof of capture admission state. Inspect the installer journal, recover
the supervisor/process, then read capture state before explicitly resuming.
When CameraAgent returns, the worker republishes retained terminal evidence.

An installer-worker interruption is recovered as terminal failure
(`lifecycle.installer-interrupted`); an ambiguous restart is not replayed.
Interrupted installer deployment work must be recovered before starting another
runtime lifecycle. Failures and cancellation preserve accepted capture/artifact
records, pause intent and the journal. Corrupt, unsupported or linked state is
refused rather than silently reset.

The automation run remains **Running** across CameraAgent restart until its
matched installer terminal receipt completes it. Only this registered lifecycle
task delegates run completion when its exact run has durable installer intent. A crash
between claiming a run and submitting that intent is recovered as Interrupted; an
unrelated retained request cannot keep it Running. Ordinary automation restart recovery and claimant
ownership are unchanged. The installer never rewrites an already terminal run.
Retaining a new task kind is a forward-only definition change, consistent with
the other appended registered task kinds; older binaries cannot read it.

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
