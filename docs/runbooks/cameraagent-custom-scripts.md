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
registration publishes an unavailable descriptor with no selectable targets. Availability uses
fixed content-free codes (`script.disabled`, `script.owner-authorization-required`,
`script.binding-invalid`, `script.registration-invalid`, `script.registration-duplicate`,
and specific `script.sandbox-*` prerequisite/probe codes), logged once at adapter
construction without rejected values. The existing generic editor continues to
list this registered kind as disabled when unavailable, and exposes opaque digest
targets only when enabled. That limited setup is accepted for this adapter slice;
#1133 owns the later guided task presentation. No wizard files are changed here.

## Execution boundary

The qualified implementation requires non-root Linux x64 or arm64 with
`/usr/bin/systemd-run`, `/usr/bin/bwrap` (supporting `--disable-userns` and
`--assert-userns-disabled`), `/usr/bin/prlimit`, a **statically linked**
`/usr/bin/busybox`, and a systemd user manager with delegated cgroup v2 memory,
CPU and process controllers. The cgroup2 mount must have `nsdelegate`; availability
and every execution fail closed without it. The interpreter ELF is checked for
absence of a dynamic interpreter. The complete launch is probed before targets
are published. No weaker fallback exists. Stock runtime images without these
prerequisites remain unavailable; installer/runtime deployment provisioning is
outside this PR. ARM64 is not physical-target qualification.

Bubblewrap creates private user, PID, mount, network, IPC, UTS and supported
cgroup namespaces, drops capabilities and starts a new session. It disables and
asserts the disabling of further user namespaces. The only host files mounted
are the static BusyBox executable at `/bin/sh` and `/bin/sleep`, plus `/dev/null`.
Private proc is read-only. No loader, libc, host home, configuration, archive,
SDK/device, rig/module, raw-ingress, identity, provisioning, socket or secret is
mounted. Host networking/firewall configuration is unchanged.

This is a kernel-enforced syscall boundary, not a claim that shell text cannot
materialize native code. BusyBox applets and any native ELF emitted into scratch
inherit a checked-architecture seccomp allowlist. Unknown syscalls, namespace
creation, mounts, sockets (including non-IP families), memfd/anonymous temporary
files, SysV IPC, io_uring, keys, tracing and executable mmap/mprotect are denied.
There is no dynamic loader or LD_PRELOAD injection path. Ordinary named files,
including O_TMPFILE, remain confined to the size-limited `/work` mount; proc and
the rest of the root are read-only. The policy version is part of each target
identity; this correction is `hvo-custom-script-linux-v2`, so v1 registrations
are deliberately not reinterpreted.

A dedicated launching thread stays alive until the child exits. This matters
because Bubblewrap's parent-death signal is tied to the **forking thread**, and
an idle .NET thread-pool thread may retire while the host still runs. The scope
also has `RuntimeMaxSec = TimeoutSeconds + 10`, independently bounding an orphan.

The launch environment is cleared; only the non-secret `XDG_RUNTIME_DIR` user-bus
locator is supplied to the scope launcher. Bubblewrap clears that locator too.
The script receives only `PATH=/bin`, `HOME=/work`, `TMPDIR=/work` and `LC_ALL=C`.
Registered content is a literal `sh -c` argument, with separately supplied positional
arguments. It is never interpolated into a host shell or logged. After verifying
the cgroup, the host sends only a finite binary seccomp policy to Bubblewrap's
stdin. Bubblewrap must consume/install it before exec; the script itself receives
`/dev/null` as stdin, so `read` cannot consume later script lines.
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
and CPU limit files before releasing the seccomp/exec barrier. A failed scope/budget
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
Admission rotates after the last dispatched off-sweep definition. Even when scripts
are due on every poll (including runtime/interval/poll mismatches), later reserved
refresh tasks progress after capacity returns; a lexical-first script cannot
monopolize every new slot. Other tasks and acquisition keep their failure boundaries.

The journal records definition revision/target digest, occurrence/run identity,
scheduled/start/end instants and terminal outcome. Detail contains
`script.completed`, `script.timeout`, `script.output-limit` or
`script.launch-failed`, `script.isolation-unavailable` or `script.cancelled`, exit
status and stdout/stderr counts. `script.process-failed` identifies a nonzero script or
post-launch setup exit without pretending the script completed successfully; raw diagnostic output remains deliberately discarded. Nonzero exit,
timeout, capacity/launch failure and output excess are failures. Host shutdown
cancellation kills the process tree, waits for process exit and pipe drain,
then records an `Interrupted` terminal result with the real exit code, stream
counts and injected-clock completion time. The runner retains execution authority
through a five-second cancellation-independent journal write before propagating
cancellation. A delayed restart preserves that result. Abrupt host death still
uses the existing restart reconciliation when no orderly result is available.

There is no automatic retry of arbitrary side effects or replay of missed work.
Missed periods are recorded under the existing bounded missed-occurrence policy;
failed or interrupted occurrences are not automatically rerun after restart.
A new scheduled occurrence or explicit run-now is a new attempt with a new
identity. Any explicit retained-occurrence retry supported by the owning durable
contract remains an operator choice and may repeat effects. Exactly-once external
side effects are not promised. Shutdown/timeout/cancellation cannot undo effects
that happened before termination.

## Required qualification

On Linux the sandbox Integration setup **fails** if the complete probe cannot run;
it never converts missing enforcement into an inconclusive pass. The main and
CameraAgent full-qualification Integration lanes provision `bubblewrap`,
`busybox-static` and `gcc`, start the disposable runner's user manager, and verify
prerequisites through `scripts/ci:script-sandbox --provision`. This changes only
test-runner setup, not installer/runtime topology. The positive Unit selection
remains Docker-free; unavailable hosts are qualified as unavailable in Unit tests.
Non-Linux Integration execution explicitly reports Linux-only qualification.
The development-v1 protected profile still runs only Preflight and Build and Unit;
local Linux Integration evidence remains separately required for this adapter.

Native fixtures are freestanding, bounded C helpers compiled under the test's
scratch root. They prove memfd rejection, namespace/mount refusal, socket refusal,
non-executable mappings and preload rejection through the actual script boundary.
The fixture source is retained; no host escape, unbounded resource load or hardware
is required. Tests also qualify delayed cancellation recovery and continued built-in
refresh progress during repeated real script timeouts.

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
