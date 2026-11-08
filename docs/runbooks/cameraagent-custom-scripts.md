# CameraAgent custom-script adapter (#1137)

This is the adapter preparation slice alongside #1133. The predefined wizard's
custom-task step, individual running-task stop controls, installer provisioning
and production adoption are not delivered here. Keep #1137 open until those
integration and qualification gates are complete. No hardware is required.

## Authorization and registration

Only the host owner, through protected startup configuration, can enable
`CameraAgent:CustomScripts`, affirm `OwnerAuthorized`, and register script content.
Both switches default to false. The ordinary authenticated owner automation
mutation policies and antiforgery checks remain in force for definitions.
The durable run-now seam is tested below, but a generic operator script run-now
endpoint/control is not delivered in this slice. They grant permission to select an existing registration; they do not
grant permission to upload content, choose an executable, bind a directory,
change the environment or request a network capability. No new anonymous or
API-key execution endpoint is introduced. Do not put credentials in content,
arguments, definition names, IDs, reasons or other operator text.

Example startup configuration (illustrative, not enabled by any shipped profile):

```json
{
  "CameraAgent": {
    "CustomScripts": {
      "Enabled": true,
      "OwnerAuthorized": true,
      "Scripts": [{
        "Id": "local-check",
        "Interpreter": "PosixShell",
        "Content": "[ \"$1\" = true ] || exit 1; printf 'check complete'",
        "Arguments": [{ "Kind": "Boolean", "Value": "true" }],
        "TimeoutSeconds": 30,
        "ScratchBytes": 1048576,
        "OutputBytes": 65536
      }]
    }
  }
}
```

The new `CustomScript` task kind deliberately revises the former closed
no-script contract. `TaskTarget` remains an opaque registered identity:
`<id>.<sha256>`. The digest includes the content, interpreter, positional typed
arguments, all execution limits and sandbox policy version. It is not shell
text or a host path. Registrations are snapshotted at startup; changing any
covered field creates a new target. Old definitions/occurrences are unavailable
until deliberately revised, rather than reinterpreted. Definition revision and
occurrence/run hashes retain that identity through the existing journal. The
SQLite schema and hashing of existing task kinds are unchanged. Older binaries
cannot read the appended enum value in retained revisions: downgrade after use
is unsupported, even if the current definition is removed.

At most 16 registrations, 48-character identifiers, 64 KiB UTF-8 content and 16
positional arguments of 256 characters are accepted. Argument kinds are `Text`,
canonical signed `WholeNumber` (Int64), and lowercase `Boolean` (`true`/`false`).
Arguments are separate argv entries; the adapter never interpolates them into
script text. Scripts are responsible for quoting their own positional data.
No arbitrary interpreter, shebang dispatch, executable selection or environment
extension is supported. Invalid, duplicate, unapproved or unsupported startup
registration publishes an unavailable descriptor with no selectable targets.

## Execution boundary

The qualified implementation requires non-root Linux x64 or arm64 with
`/usr/bin/systemd-run`, `/usr/bin/bwrap`, `/usr/bin/prlimit`, `/usr/bin/dash`,
`/usr/bin/sleep`, the standard glibc/loader paths, and a systemd user manager
with delegated cgroup v2 memory, CPU and process controllers. It probes the complete launch boundary before
publishing availability. No unsafe execution fallback exists. Stock runtime
images without these prerequisites remain unavailable; this PR changes no
installer, packages or deployment topology. The implemented arm64 path is not
physical target qualification.

Bubblewrap creates private user, PID, mount, network, IPC, UTS and supported
cgroup namespaces, drops capabilities, starts a new session and ties the sandbox
lifetime to its parent. The only host files mounted are the fixed shell, sleep,
libc, loader and `/dev/null`. Private proc is read-only. There is no host home,
configuration, archive, capture SDK/device, rig/module, raw-ingress, identity,
Data Protection, provisioning, socket, secret or protected-state mount. No
external network is shared. Host networking and firewall configuration are never
changed. The script can use shell builtins and `sleep`; other tools and language
runtimes require a separately reviewed future execution policy.

The launch environment is cleared; only the non-secret `XDG_RUNTIME_DIR` user-bus
locator is supplied to the scope launcher. Bubblewrap clears that locator too.
The script receives only `PATH=/bin`, `HOME=/work`, `TMPDIR=/work` and `LC_ALL=C`.
The working directory is a private size-limited `/work` tmpfs; `/tmp` points to
it. The remaining filesystem is read-only. Scratch capacity is shared across
files and defaults to 1 MiB (4 KiB–8 MiB allowed). Scratch disappears with the
namespace. There is **no output artifact publication contract in this slice**:
no script file is imported into products or served as a download, and there is
no capture/object-store/publication authority. Future publication requires its
own confinement, validation, identity and authorization design.

Runtime is 1–300 seconds (30 default). Combined stdout/stderr production is
limited to 256 bytes–1 MiB (64 KiB default); crossing it terminates execution.
Two fixed 4 KiB buffers drain/discard the streams. Raw output retention is
**zero bytes**, including launch failures, so diagnostic output cannot become
operator history/log text. Only fixed outcome labels, exit code and byte counts
are returned. No exception message or script/argument text is logged.

Every launch first enters its own transient systemd user scope: 64 MiB aggregate
memory (including charged kernel/scratch metadata), zero swap, 16 processes/threads
including launchers, and 50% of one CPU. These cgroup budgets apply only to that
script tree and cannot be raised from inside the sandbox. The host verifies the exact scope membership and kernel memory, swap, process
and CPU limit files before sending any script content to stdin. A failed scope/budget
setup cannot fall back to an unbudgeted process. The scope is collected after its
tree exits; no persistent service configuration is changed. `prlimit` additionally
sets zero core bytes, 32 MiB address space per process, 64 descriptors, file size
no larger than scratch capacity, and CPU seconds no larger than wall timeout.
Deployment qualification still owns host capacity and supported service-manager
topology; this slice does not certify hostile-code multi-tenant hosting.

## Durable execution policy and outcomes

Only fixed-period schedules (60–86,400 seconds) and the durable runner
run-now integration seam are supported; no source-window or capture-relative script execution.
The existing sole SQLite execution authority claims an immutable occurrence
before launch. Scripts use the bounded off-sweep worker, do not occupy capture
or live processing slots, and a second direct invocation is refused as busy.
The worker retains execution authority through cancellation and actual drain.
Other tasks and acquisition keep their own failure boundaries.

The journal records definition revision/target digest, occurrence/run identity,
scheduled/start/end instants and terminal outcome. Detail contains
`script.completed`, `script.timeout`, `script.output-limit` or
`script.launch-failed`, exit status and stdout/stderr counts. Nonzero exit,
timeout, capacity/launch failure and output excess are failures. Host shutdown
cancellation kills the process tree, waits for process exit and pipe drain,
then propagates cancellation for the runner's interrupted/failed policy.

There is no automatic retry of arbitrary side effects or replay of missed work.
Missed periods are recorded under the existing bounded missed-occurrence policy;
failed or interrupted occurrences are not automatically rerun after restart.
A new scheduled occurrence or explicit run-now is a new attempt with a new
identity. Any explicit retained-occurrence retry supported by the owning durable
contract remains an operator choice and may repeat effects. Exactly-once external
side effects are not promised. Shutdown/timeout/cancellation cannot undo effects
that happened before termination.

## Acceptance and qualification

- Script identity, typed arguments, fixed interpreter and execution policy:
  implemented through immutable owner registrations and opaque targets.
- Authorization, environment/filesystem/network/secret and publication policy:
  implemented for the restrictive no-publication sandbox above.
- Runtime, concurrency, scratch/output retention, identities and tree drain:
  implemented; real subprocess and SQLite regressions qualify this host.
- Retry/missed policy and failure isolation: reuse the delivered durable runner;
  no exactly-once promise or automatic script retry.
- Custom wizard step and run-now/stop/history UI: pending #1133 integration,
  an authenticated generic script run-now control and a separately qualified
  running-task stop command; no wizard files changed here.
- Unit/Integration, independent deep review and selected CI: record actual
  results in the issue/PR ledger. Protected development-v1 Preflight + Build and
  Unit require the finalization reservation and a converged, synchronized head.
  Local passes alone do not complete this acceptance item.
