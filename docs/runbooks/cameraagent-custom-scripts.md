# CameraAgent custom-script adapter (#1137)

Owner-registered scripts integrate with the predefined automation wizard from
#1133 and the durable schedule contract from #1136. The merged predefined-wizard
slice is available; #1133 itself remains open with blocked follow-up work, so this
is not a claim that the whole prerequisite issue is closed. The custom step, explicit
run-now and per-running-script stop controls are implemented here. #1137 remains
open: installer/runtime prerequisite provisioning, supported production adoption,
independent review convergence and selected protected CI/finalization are not
complete. Roy has not approved a partial issue delivery. No hardware is required.

## Authorization and registration

Only the host owner, through protected startup configuration, can enable
`CameraAgent:CustomScripts`, affirm `OwnerAuthorized`, and register script content.
Both switches default to false. The ordinary authenticated owner automation
mutation policies and antiforgery checks remain in force for definitions.
The same authenticated owner policy protects script run-now and stop mutations.
All six automation mutation endpoints reject session requests unless the real
antiforgery middleware reports successful validation, before invoking a durable
command or cancelling a worker. Missing and invalid session tokens return a
content-free HTTP 400. As with other operations mutations, an authenticated
owner ReadWrite API key is accepted without a session antiforgery token;
read-only keys and non-owner credentials cannot mutate. Keys are accepted only
through the existing Authorization header policy, never through operator JSON.
These endpoints grant permission to select an existing registration; they do not
grant permission to upload content, choose an executable, bind a directory,
change the environment or request a network capability. Anonymous execution is
not permitted. Do not put credentials in content,
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
construction without rejected values. The custom wizard option is absent when
the adapter has no qualified invocation. Its second step selects an immutable
registration and shows only its ID, version digest, argument count and byte/time
limits. Content, argument values and host paths are not projected. Validation
and review precede the existing optimistic-version/idempotent save contract.
An edited definition with a retired invocation must explicitly select its current
approved replacement in that step; the old revision stays unchanged until save.

An enabled script definition has an explicit, confirmed **Run now** control. It
queues one durable invocation without moving the periodic schedule; duplicate
keys replay the original result, and another pending run-now is refused. A lost
response keeps the exact key for retry. **Run history** shows safe fixed outcome
labels, numeric exit/count metadata, and **Stop this run** for running scripts.
Stop names the exact retained run and definition revision, including after the
definition is edited or removed, and acknowledges only after process/result drain.
It does not disable the schedule, affect built-in tasks, or undo prior effects.
Queued invocations cannot be stopped by this control; stop becomes available when
execution has acquired its slot. Active runs are projected separately from the
recent-history page so newer terminal runs cannot hide the stop control.

Fixed safe errors explain unavailable registrations, stale versions, pending runs
and missing/nonlocal runs. Neither UI nor HTTP accepts script uploads, executable
paths, environment overrides or credentials.

## Execution boundary

The qualified implementation requires non-root Linux x64 with
`/usr/bin/systemd-run`, `/usr/bin/bwrap` (supporting `--disable-userns` and
`--assert-userns-disabled`), `/usr/bin/prlimit`, a **statically linked**
`/usr/bin/busybox`, and a systemd user manager with delegated cgroup v2 memory,
CPU and process controllers. The cgroup2 mount must have `nsdelegate`; availability
and every execution fail closed without it. The interpreter ELF is checked for
absence of a dynamic interpreter. The complete launch is probed before targets
are published. No weaker fallback exists. Stock runtime images without these
prerequisites remain unavailable; installer/runtime deployment provisioning is
not delivered by this PR. Every non-x64 architecture, including ARM64,
returns `script.sandbox-architecture-unqualified` before probing or launching;
there is no arm64 syscall table or support claim. ARM64 requires a separately
qualified architecture policy and real subprocess evidence before enablement.

Bubblewrap creates private user, PID, mount, network, IPC, UTS and supported
cgroup namespaces, drops capabilities and starts a new session. It disables and
asserts the disabling of further user namespaces. The only host files mounted
are the static BusyBox executable at `/bin/sh` and `/bin/sleep`, plus `/dev/null`.
Private proc is read-only. It can still expose host-wide kernel/system information
(including boot command line, aggregate CPU/memory statistics and sysctls),
its namespace init's non-secret launcher environment, and the init executable
through `/proc/1/exe` (Bubblewrap). It does not expose a general host filesystem
capability, but the boundary must not be described as hiding every host-backed
executable through proc references. This sandbox does not promise confidentiality
of that system information or provide a multi-tenant host boundary. No loader, libc, host home, configuration, archive,
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
identity; this correction is `hvo-custom-script-linux-x64-v4`, so earlier registrations
are deliberately not reinterpreted. The qualified x64 policy permits the
verified shell operations for background jobs and `wait`, glob expansion,
readability tests and `touch`, while retaining the native-code restrictions.

A dedicated launching thread stays alive until the child exits. This matters
because Bubblewrap's parent-death signal is tied to the **forking thread**, and
an idle .NET thread-pool thread may retire while the host still runs. The scope
also has `RuntimeMaxSec = TimeoutSeconds + 10`, independently bounding an orphan.

Before execing `prlimit` and Bubblewrap, a fixed trusted static BusyBox shell
closes every inherited descriptor above 2 through `/proc/self/fd`. It runs in
place and before the descriptor ceiling is lowered, so high-numbered host file,
directory and lock descriptors cannot survive. It fails closed if the descriptor
table cannot be enumerated or closed. This cleanup code never incorporates
registered text; launcher/script arguments remain separate literal argv entries.
Only the redirected standard streams cross that boundary.

The launch environment is cleared; only the non-secret `XDG_RUNTIME_DIR` user-bus
locator is supplied to the scope launcher. Bubblewrap clears that locator too.
The script receives only `PATH=/bin`, `HOME=/work`, `TMPDIR=/work` and `LC_ALL=C`.
Registered content is a literal `sh -c` argument, with separately supplied positional
arguments. The host's process table can expose that content and those arguments
to host users allowed to read `/proc/<pid>/cmdline`, which can include
unprivileged users on default proc configurations. Hosts using a piped core
handler such as systemd-coredump can additionally journal `COREDUMP_CMDLINE` or
`COREDUMP_CONTAINER_CMDLINE` after a script crash, even with `RLIMIT_CORE=0`.
The content-free application/scope lifecycle logging guarantee does not conceal
argv or cover host crash-reporting services; that channel is unqualified here.
Never place secrets in content or arguments. The adapter never interpolates
them into a host shell or writes them to application logs. After verifying
the cgroup, the host sends only a finite binary seccomp policy to Bubblewrap's
stdin. Bubblewrap must consume/install it before exec; the script itself receives
`/dev/null` as stdin, so `read` cannot consume later script lines.
The transient scope has a random content-free unit name and the fixed description
`HVO bounded custom script`; systemd's default description derived from the command
line is never used. Scope metadata and its lifecycle journal messages therefore
contain no registered content or arguments. Qualification inspects the actual
scope and only that unit's journal, without publishing raw script output.
The working directory is a private size-limited `/work` tmpfs; `/tmp` points to
it. The remaining filesystem is read-only. Scratch capacity is shared across
files and defaults to 1 MiB (4 KiB–8 MiB allowed, and at least one host memory
page). Capacity rounds down to whole host pages so tmpfs cannot round above the
registered byte bound; sub-page limits fail closed instead of using an unlimited
zero-sized tmpfs. Scratch disappears with the
namespace. There is **no output artifact publication contract in this slice**:
no script file is imported into products or served as a download, and there is
no capture/object-store/publication authority. Future publication requires its
own confinement, validation, identity and authorization design.

Runtime is 1–300 seconds (30 default). Combined stdout/stderr production is
limited to 256 bytes–1 MiB (64 KiB default); crossing it terminates execution.
Two fixed 4 KiB buffers drain/discard the streams. Raw output retention is
**zero bytes**, including launch failures, so diagnostic output cannot become
operator history/log text. Only fixed outcome labels, exit code and byte counts
are returned. The adapter logs no exception message or script/argument text;
the host process/crash-reporting exposure above remains separate.

Every launch first enters its own transient systemd user scope: 64 MiB aggregate
memory (including charged kernel/scratch metadata), zero swap, 16 processes/threads
including launchers, and 50% of one CPU. These cgroup budgets apply only to that
script tree and cannot be raised from inside the sandbox. The host verifies the exact scope membership and kernel memory, swap, process
and CPU limit files before releasing the seccomp/exec barrier. A failed scope/budget
setup cannot fall back to an unbudgeted process. The scope is collected after its
tree exits; no persistent service configuration is changed. `prlimit` additionally
sets zero core bytes, 32 MiB address space per process, 64 descriptors, file size
no larger than the registered scratch bound, and CPU seconds no larger than wall timeout.
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
If an already claimed script is cancelled before launch, the runner durably
records `script.cancelled-before-start`, no exit code, zero stream counts and the
actual cancellation-completion instant with the same bounded completion token.
A restart does not replace that instant with its own reconciliation time.
The same bounded cancellation-independent write preserves an already completed
script result if cancellation arrives between execution and journal settlement.

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
The full-CI `--provision` path still needs its first real disposable-runner
execution; a local verify-only pass does not establish that workflow result.

Native fixtures are freestanding, bounded C helpers compiled under the test's
scratch root. They prove memfd rejection, namespace/mount refusal, socket refusal,
non-executable mappings and preload rejection through the actual script boundary.
The fixture source is retained; no host escape, unbounded resource load or hardware
is required. Tests also qualify delayed cancellation recovery and continued built-in
refresh progress during repeated real script timeouts.
Descriptor regressions deliberately inherit non-CLOEXEC host file/directory fds
above the script's descriptor ceiling and verify refusal without touching host
state outside their synthetic fixture. Cleanup/start assertions observe only
descendants of the current testhost and their exact script cgroups, so unrelated
sleep processes and other owners' script scopes cannot affect the assertions.

## Acceptance and qualification

- Script identity, typed arguments, fixed interpreter and execution policy:
  implemented through immutable owner registrations and opaque targets.
- Authorization, environment/filesystem/network/secret and publication policy:
  implemented for the restrictive no-publication sandbox above.
- Runtime, concurrency, scratch/output retention, identities and tree drain:
  implemented; real subprocess and SQLite regressions qualify this host.
- Retry/missed policy and failure isolation: reuse the delivered durable runner;
  no exactly-once promise or automatic script retry.
- Custom wizard step, confirmed run-now and running-script stop/history UI:
  implemented with safe registration metadata, durable replay, exact-run stop,
  retained active visibility and fixed meaningful errors. Local behavioral proof
  is recorded at the candidate SHA in the PR requirement/evidence map.
- Unit/Integration, independent deep review and selected CI: record actual
  results in the issue/PR ledger. Protected development-v1 Preflight + Build and
  Unit require the finalization reservation and a converged, synchronized head.
  Local passes alone do not complete this acceptance item.
