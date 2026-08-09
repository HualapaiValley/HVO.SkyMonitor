# Split-Host Deployment, Bootstrap, Smoke, And Teardown

This runbook covers preflight, runtime-root preparation, immutable image
distribution, catalog installation, and controlled application startup from issue #151. Preflight validates a versioned multi-host
inventory without remote mutation. Prepare creates only declared runtime roots
and exact ownership/lock metadata. Images then builds on the control host and
either pushes and pulls registry manifests or transfers local archives through
declared Docker contexts. `catalog` installs the declared verified catalog on
every host. `up` optionally creates isolated shared services, runs controlled
database initialization, starts LogicHost, and starts CameraAgents behind the
provisioning gate with upload disabled. `bootstrap` provisions through
application APIs, `smoke` runs bounded W0 convergence checks, and `down`
implements explicit preserve or run-owned deletion.

## Inventory And Secrets

Copy `deploy/split-host/inventory.example.yml` to an ignored operator location
and fill in every target explicitly. The file is JSON-compatible YAML and is
parsed strictly with `jq`; it is never sourced. The executable contract in
`scripts/deploy/inventory.sh` is authoritative. The matching published shape
reference is `deploy/split-host/inventory.schema.json`; CI exercises its required
keys, shared-target shape, explicit-port rule, example, and runtime validator
together because the repository does not carry a pinned JSON Schema engine.

Each target declares its SSH destination, Docker context, expected hostname,
machine identity, daemon identity, architecture, absolute runtime root, and
host ports. Application targets separately declare an HTTP `internalEndpoint`
for host-bound Compose/readiness and a `publicEndpoint` used by browsers,
CameraAgents, bootstrap payloads, and OIDC. LogicHost is the public OIDC
authority. Isolated runs may use an explicit HTTP `publicEndpoint` on an
operator-selected local network; persistent production mode requires HTTPS and
rejects HTTP before any host contact. Preflight
requires the hostname observed through SSH to equal both the
declared hostname and Docker daemon `Name`; this correlates the two routes rather
than trusting unrelated inventory assertions. Docker daemon architecture values
`x86_64` and `amd64` normalize to `amd64`; `aarch64` and `arm64` normalize to
`arm64`. Other daemon values fail with a bounded `unsupported-architecture`
reason and are not copied into output or evidence.

Runtime roots may contain ordinary spaces and are passed only through quoted
arguments. Control characters, repeated separators, and `.` or `..` components
are rejected. Catalog versions follow the repository's existing `4.2`,
`hyg-v4.2-p3-s2-r1`, and fixture-style identifiers; colon is not a supported
catalog-version separator.
Inventory schema v6 pins the Git revision, catalog, source-revision image tag,
distribution mode, named control-host buildx builder, repositories, registry tag
policy, and non-secret service routes. Image platforms are derived from target
architectures rather than separately asserted. There are no ambient target or
builder defaults.

`secretSource.path` names an ignored dotenv-style file. It must be a regular,
nonsymlink, single-link file owned by the invoking user with mode `0400` or
`0600`. `requiredReferences` contains variable names only. Preflight verifies
that those assignments exist but never sources, evaluates, prints, or persists
their values. Keep the inventory owner-only too if its declared runtime roots or
hostnames are operationally sensitive, although they are intentionally treated
as non-secret evidence fields.

Schema v6 also declares the observatory, agent friendly names, owner automation
credential reference, workload selection, service ownership, distinct SQL
administrator/initializer/runtime users, Redis administrator/runtime identities
and key prefix, MinIO root/runtime identities and buckets, certificate paths,
KeyPerFile mappings, and resource limits. `existing` service mode never
provisions those services. `deploy` requires an explicit shared-services target
and digest references for SQL Server, Redis, MinIO, the MinIO client, and optional
Mailpit. Mailpit is accepted only for isolated deployment.

Every CameraAgent declares an owner-password secret reference and an absolute
path to a complete `CameraModuleDocument`. `up` copies that document separately,
sets `CameraAgent:ConfigFilePath` through KeyPerFile, and stages only an
`AdminPasswordFile` pointer plus the private password file. Appsettings-shaped
objects are not valid module documents and are rejected before startup.
Inventory selects only workload kind, deadline, and sustained-run opt-in. The
repository-owned `deploy/split-host/workloads/canonical-workloads.json` pins W0,
W1, and W2 source paths, file and canonical-configuration SHA-256 identities,
module-options hash, dimensions, format, seed, warm-up count, measured count, and
concurrency. Activation verifies that manifest and the checked-in source bytes;
an inventory-provided or arbitrary same-shape profile cannot claim canonical
identity.

## Run Preflight

Use an explicit inventory and mode:

```bash
./scripts/deploy:environment preflight \
  --inventory /absolute/path/inventory.yml \
  --mode persistent \
  --run-id observatory-preflight-01
```

`--mode` is `isolated` or `persistent`. Isolated mode permits explicit HTTP
authorities for portable local-network validation. Persistent mode rejects
fixture catalogs and requires HTTPS public authorities for LogicHost and every
CameraAgent.
The shipped Compose files remain HTTP-only. Before persistent preflight, provision
TLS termination/reverse proxies at every public authority, configure each
target's exact proxy IP in `trustedProxyAddresses`, forward only
`X-Forwarded-For`, `X-Forwarded-Host`, and `X-Forwarded-Proto`, and make the
LogicHost public OIDC discovery endpoint reachable from every CameraAgent host.
The application trusts one forwarded hop only and ignores unlisted proxies.
Optional `--state-root` and
`--evidence-root` must be distinct canonical absolute paths beneath safe,
owner-controlled ancestors. Their defaults are separate mode-`0700`
directories under `TestResults/deploy/<run-id>/`; files are mode `0600`.

The command clears deployment, smoke-test, Docker, and SSH target-selection
variables before reading the inventory. Before any remote call it acquires a
local cooperative run lock and atomically publishes a schema-versioned running
manifest. Output is limited to `stage`, `check`, `status`, and bounded `reason`
fields. Raw SSH, Docker, curl, and TCP diagnostics are discarded.

## Read-Only Checks

For every explicitly selected target, preflight checks:

- batch SSH reachability and the declared stable machine identity;
- Docker context existence and daemon reachability, correlated hostname,
  identity, Linux OS, architecture, and server version;
- Docker Compose plugin availability and version through that same context;
- CPU count, total memory, filesystem free space, and host clock observation;
- thermal and throttle reporting, accurately marked `unsupported` when absent;
- lexical and component-by-component runtime-root safety without creating it;
- listening-port and Docker container conflicts;
- each declared service route from its declared source targets;
- in persistent mode, the pre-provisioned LogicHost public authority from every
  CameraAgent target. Isolated mode records this check as deferred because the
  authority is created by `up`, which performs the required cross-host readiness
  check before bootstrap.

Remote TCP checks require Bash and `timeout`; HTTP checks require Bash and curl.
Missing required tools are reported as bounded `tool-unavailable` failures,
distinct from an endpoint that was actually probed and found `unreachable`.

The slice records observations but intentionally defines no speculative CPU,
memory, disk, clock, or thermal thresholds. Policy thresholds require later
operational requirements and evidence.

Every existing runtime-root component is checked without following symbolic
links. An existing root owned by the SSH user and not group/world writable is
`ready`. A missing root beneath either an SSH-user-owned or root-owned
non-writable ancestor is `needs_prepare`, not a failure. Other owners, writable
nearest ancestors, symlinks, and non-directory components fail. A later explicit
prepare phase must create and assign the declared root; preflight never does so.

## Resume And Evidence

Rerunning the same run ID resumes only when mode, canonical inventory SHA-256,
source revision, target identities, and completed check outcomes still match.
Changed inventory, revision, target identity, or prior check outcome is rejected.
Interrupted temporary manifest files are never accepted as state; publication is
atomic and a rerun reconstructs an incomplete phase. Inventory, existing-manifest,
and resume-contract validation occur before evidence changes. A completed passed
manifest must also match its sanitized evidence checks and target identities.
Mismatch is rejected as `completed-check-or-target-mismatch` without rewriting
either artifact, so a rejected invocation preserves prior valid evidence. Once a
rerun is accepted, its manifest is committed as `running` before the prior
evidence entry is removed safely. Success publishes passed evidence and a failed
remote phase publishes failed evidence, so stale passed evidence is not retained.

Publication commits matching evidence before committing a passed manifest. A
crash between those atomic writes leaves the manifest `running`, so the staged
passed evidence is not authoritative. The next invocation validates the running
manifest, removes the staged evidence, reruns all remote checks, and publishes a
new matching passed evidence/manifest pair. Failed publication follows the same
evidence-first order before committing a failed manifest.

For `bootstrap`, `smoke`, `measure`, `acceptance-init`, and `down`, recovery validates the
phase-specific next-generation ledger before reconstructing any mirror. A
malformed candidate leaves the prior manifest, evidence, and digest commit
unchanged. A manifest, evidence, or commit without its authoritative phase
ledger is rejected as an orphan companion rather than adopted or deleted.

The private manifest contains declared non-secret runtime roots, target
identities, bounded checks, source dirty disposition, and timestamps. The
separate evidence file contains the same sanitized preflight facts. Neither file
contains the secret-source path, secret values, connection strings, or raw
command output.

Preflight uses a local cooperative lock. It intentionally creates no remote lock
files because this phase is read-only. Therefore it cannot exclude an unrelated
operator mutating a target concurrently; later mutation slices must acquire
per-target durable cooperative locks before changing deployment state.

## Contract Test

```bash
./scripts/test:deploy-environment
```

The test uses fake `ssh`, `scp`, and `docker` through `PATH`. Fake SSH executes the exact
supplied remote Bash scripts against controlled host commands, while TCP and HTTP
checks use real Bash `/dev/tcp` and curl behavior against a temporary listener
hosted by the pinned .NET SDK already installed by the devcontainer and CI. It
asserts routing, host correlation, path and lock safety, missing-tool behavior,
redaction, failed-evidence replacement, resume behavior, atomic publication
recovery, and absence of mutation commands.

It also exercises fixture-catalog transfer and installation, private KeyPerFile
staging, initializer/runtime credential separation, provisioning-gated agent
startup, Compose ordering, readiness, sanitized evidence, and idempotent
catalog/`up` resume without contacting deployment hosts.

## Prepare Runtime Roots

After the exact run has a matching passed preflight, prepare its declared roots:

```bash
./scripts/deploy:environment prepare \
  --inventory /absolute/path/inventory.yml \
  --mode persistent \
  --run-id observatory-preflight-01
```

`prepare` requires the same inventory hash, mode, source revision, worktree
disposition, completed checks, and target identities recorded by preflight. It
never skips or synthesizes preflight. Each target explicitly declares
`runtimeOwner`, which must resolve to the effective SSH UID. This slice has no
root helper, password, interactive privilege, or ownership-escalation path.
The immediate parent of a missing runtime root must already be owned and writable
by that SSH user while remaining non-writable to group and world.
The example assumes packaging has provisioned `/srv/hvo/skymonitor` to user
`hvo`; its LogicHost and CameraAgent roots are distinct children of that parent.

For each target, prepare validates every existing component again and acquires a
nonblocking sibling `.hvo-deploy-prepare-<digest>.lock` before root mutation. The
lock metadata is bound to the expected ownership marker and remains held through
that target's preparation. Symlinks, special files, writable ancestors, foreign
non-root ownership, changed host identity, unsafe roots, lock mismatch, and lock
contention fail with bounded reasons.

Consequently, preflight may validly report `needs_prepare` beneath a trusted
root-owned ancestor such as `/srv`, but prepare will refuse until an operator or
platform package creates an owner-controlled child parent out of band. Supplying
that production directory/package ownership is a later packaging prerequisite,
not a privileged operation implemented by this slice.

The final created target entries are the declared runtime root, its private
`.hvo-deploy/` control directory, the `ownership` marker, the cooperative lock
beside the root, and the lock's `.state` provenance sidecar. Root/control mode is
`0700`; marker, lock, and state sidecar mode is `0600`. Atomic publication may
temporarily create `<lock>.state.tmp.<marker-digest>` and
`.hvo-deploy/.ownership.tmp.<marker-digest>`; exact interrupted temporaries are
validated and completed on resume. Prepare does not invoke Docker, Compose,
service endpoints, catalogs, bootstrap, or deletion commands.

In `isolated` mode, an existing root must carry the exact run/inventory/target
marker. An empty root left after an interrupted exact run is recoverable only
through its matching lock metadata. Other unmarked or cross-run roots are
refused. In `persistent` mode, the marker is stable for installation ID, target,
root, and owner. A first run may initialize only an absent or empty root;
nonempty unmarked roots and markers from another installation are refused.
Existing application state is never reset or deleted.

Private `prepare-ledger.json` records each target's declared root, marker digest,
newly-created entries versus reused/resumed state, and phase status. Before use,
the complete ledger shape, revision, run, mode, inventory hash, unique inventory
targets, roots, marker identities, dispositions, booleans, and statuses are
validated. Creation flags are persisted in the mode-`0600` lock state sidecar
before each corresponding remote mutation together with the creating run ID, so
same-run recovery retains exact provenance. A later run for the same persistent
installation validates the complete prior root and records it as
`preexisting-or-resumed` with all creation flags false while retaining the
original creating run identity. After every successful target, the running manifest and sanitized
evidence are refreshed from the ledger; a later failure therefore lists all and
only completed target mutations. On resume, every completed target still runs a
non-mutating locked remote validation of SSH identity, root/control/marker,
ownership, modes, marker digest, lock, and state sidecar. Valid targets retain
their original ledger provenance and are not mutated again; drift fails before
later targets continue.

Completed validation is strictly read-only: it never initializes, chmods, or
repairs an empty or mismatched lock/state file. Remote validation returns the
state sidecar's creating run and effective creation flags. Those values must
exactly match the ledger booleans and disposition before any running manifest or
prepare evidence is published; a type-valid but false provenance claim is
rejected without changing prior artifacts.
Sanitized `prepare.json` omits runtime roots and machine identities. Evidence is
published before the passed `prepare-manifest.json` commit. An accepted rerun
commits its running manifest before safely clearing stale prepare evidence;
rejected validation leaves prior evidence untouched. Interruptions after root,
marker, ledger, running-manifest, or evidence publication resume idempotently.
Prepare failure never changes the valid preflight manifest or evidence.

## Build And Distribute Images

After the exact run has matching passed preflight and prepare state, run:

```bash
./scripts/deploy:environment images \
  --inventory /absolute/path/inventory.yml \
  --mode persistent \
  --run-id observatory-preflight-01
```

The image phase requires the same run ID, mode, canonical inventory SHA-256,
source revision, clean worktree, target set, passed preflight, and passed prepare
evidence. `images` rejects dirty source even when preflight declared
`allow-dirty`. It records the committed Git tree ID and checks clean HEAD and
that tree before and after every component build and again throughout resume and
distribution. Builds label the revision, tree ID, commit timestamp, source
repository/tag, component, and pinned .NET SDK version. The source tag must be
exactly `rev-<source.revision>`; `latest`, embedded tags or digests, URL schemes,
credentials, and ports in repository values are rejected.

`images.builder` explicitly declares the buildx builder name, driver, sole
`default` local endpoint, and expected local Docker daemon ID, name, OS, and architecture. The
phase checks the builder is running, local to that endpoint, and supports every
required platform, then passes `--builder` to every build and registry inspect.
Ambient `DOCKER_CONTEXT` is cleared and the selected/default builder is never
trusted. The phase does not create, alter, stop, or remove builders. LogicHost is
built for `linux/amd64`; CameraAgent platforms are the unique `amd64` and/or
`arm64` architectures declared by its targets.

In `registry` mode, `artifactRoot` is `null` and
`registryImmutableTags` must be `true`. This is an operator assertion that the
external registry rejects tag replacement. It is a required registry-side
policy prerequisite: client precheck and postvalidation cannot make a tag push
atomic or prevent a registry that permits overwrites from racing the client.
Docker must already be authenticated; the command has no credential flags and
does not read secret values.

Before a push, the phase durably records `push-intended` with the exact component,
tag, platform set, revision/tree-bound labels, timestamp, and SDK. If the tag is
already present, including after an abrupt post-push exit, it is adopted only
after the canonical parent digest, exact non-attestation platform descriptors and
child digests, optional well-formed attestation descriptors, and every child
image config label all match the intent. A mismatched existing tag is never
overwritten. A missing tag is pushed once and subjected to the same checks before
the intent becomes `built`. Targets pull only the resulting
`repository@sha256:<parent-manifest>` reference.

In `archive` mode, `artifactRoot` is an explicit canonical absolute directory
outside the repository, state root, and evidence root. It and each archive are
owner-only. One single-platform Docker archive is built for each required
component/architecture. Before publication, the phase validates the archive
config name and digest, Linux architecture, provenance labels, and then names the
file `<component>-<architecture>-sha256-<archive-sha256>.tar`. Existing unsafe,
linked, mismatched, or duplicate outputs are rejected. Archives are transferred
by `docker --context ... image load --input`, which uses the inventory-selected
SSH-backed Docker context without creating a target-side staging path.

Immediately before and after every target pull, load, or image validation, the
phase re-inspects the declared Docker context and daemon. ID, name, Linux OS, and
architecture must still match both inventory and passed preflight, including on
completed-target resume. Image inspection must match component, revision, tree,
commit timestamp, SDK, source reference, immutable parent manifest digest or
image ID, and target architecture. Distribution is deliberately sequential, so
concurrency is bounded at one target. No image is built on a deployment target
and no container is created or started.

Private `images-ledger.json` records staged build and target progress;
`images-manifest.json` is the commit authority. Sanitized `images.json` omits
local archive paths. A passed ledger with a running manifest is staged, not
completed, whether an abrupt exit occurs before evidence publication or between
evidence and manifest publication. Its evidence may therefore be absent, stale
running progress, or matching passed evidence. Resume strictly validates the
ledger and any existing evidence entry, republishes running state, safely clears
stale evidence, reuses or reruns staged work, re-correlates every completed
target, and only then publishes matching passed ledger/evidence and commits a
passed manifest. Registry pull and archive load are idempotent if interrupted
before progress publication. All artifacts are owner-only and atomic; bounded
errors and evidence contain no raw Docker/registry output, secret-source path,
credentials, or secret values.

The Dockerfiles pin the build SDK to `10.0.100`, but their Microsoft SDK/runtime
and Debian package inputs are still referenced by upstream tags and package
indexes. Those upstream inputs can change until reviewed base-image digests and
package snapshots are adopted. The produced application manifests, archives,
config IDs, and deployed references are nevertheless recorded and verified by
digest; this runbook does not claim the current builds are bit-reproducible.

## Install Catalogs

After `images` passes for the same run, install the inventory catalog:

```bash
./scripts/deploy:environment catalog --inventory /absolute/path/inventory.yml \
  --mode isolated --run-id observatory-preflight-01
```

Before any SSH, transfer, or installation, the control host requires the bundle
to contain exactly the files allowed for its fixture or production package. It
also requires manifest package kind/version plus database SHA-256, byte length,
and row count to match inventory. The same database identity is verified again
after install.

The control host validates SQLite integrity, byte length, row count, and SHA-256
before transfer. Production bundles additionally pass the canonical HYG manifest
validator. Every target is identity-correlated around mutation. The remote
installer preserves the canonical `current`/`previous` behavior and verifies the
active database. Resume accepts only an exact private ledger and re-verifies
completed targets rather than trusting a prior installed flag.
The remote bundle directory is created explicitly before `scp`; a fresh prepared
host is part of the contract test. Failed catalog runs atomically publish failed
ledger, manifest, and evidence with all completed targets retained for resume.

Fixture catalogs require `allowFixture: true` and isolated mode. Persistent mode
requires a production catalog and HTTPS application authorities.

## Start Services And Hosts

```bash
./scripts/deploy:environment up --inventory /absolute/path/inventory.yml \
  --mode isolated --run-id observatory-preflight-01
```

For `services.mode: deploy`, the command starts digest-pinned SQL Server, Redis,
MinIO, and optionally Mailpit on the shared target. It creates the run database,
distinct migration/runtime SQL users and roles, a prefix-scoped Redis ACL user,
two buckets, and a bucket-scoped MinIO user. Administrator credentials stay in
the shared-service configuration and are never mounted in application
containers. MinIO client credentials are JSON-escaped into an owner-only client
configuration; the provisioning container runs as the declared SSH UID/GID and
atomically publishes its generated application credentials with mode `0600`.
For `existing`, endpoint routes are checked without service mutation;
controlled initialization and LogicHost dependency health then exercise the
configured application identities.

Before reading any image reference, `up` reruns the full images-ledger validator
and requires byte-equivalent passed ledger/manifest plus matching sanitized
evidence. Mutable, malformed, incomplete, or cross-run references are rejected
before startup state changes. Every Compose `up`/`run` and provisioning operation
is bracketed by fresh SSH/Docker daemon identity correlation.

In isolated mode every declared project, database, Redis prefix, and bucket name
must contain the exact run ID; startup fails before mutation otherwise. Persistent
mode uses the stable declared names without appending a run-specific suffix.

LogicHost initialization mounts only `initializer-secrets`; runtime mounts only
`runtime-secrets`. The one-shot command is
`--host-mode=database-initialize`. After it succeeds, deployed-service mode
applies runtime grants against the final schema before starting LogicHost.
Production OpenIddict signing and encryption PFX files are mounted read-only and
loaded from configured absolute paths. Both hosts mount the complete catalog
installation root at `/app/catalog` and receive the inventory package kind.
Internal HTTP `/alive`, `/health`, and `/metrics` must pass before agents start;
OIDC discovery is checked through the declared public authority from every
CameraAgent host. Isolated mode explicitly stages `Deployment:Mode=isolated`,
which permits HTTP token transport for the local-network campaign. Persistent
mode never stages that exception and reaches the HTTPS authority through the
declared TLS proxy.

Each CameraAgent receives separate Identity, Data Protection, provisioning, raw,
and archive roots, but no central SQL/Redis/MinIO credentials. Fresh durable
capture state is paused and upload is disabled; central integration remains
enabled so the supported bootstrap workflow can reach LogicHost. `/alive` and
`/health` prove provisioning-gated startup only; they do not claim bootstrap or
fleet readiness. Private ledgers and evidence contain resource names, image
and catalog identities, and bounded status, but no secret values or connection
strings.
Failed startup atomically publishes the exact completed service, initializer,
runtime-role, and target progress. Resume validates committed or failed
ledger/manifest/evidence equality, rechecks completed state, and converges without
duplicating target entries.

## Bootstrap Agents

```bash
./scripts/deploy:environment bootstrap --inventory /absolute/path/inventory.yml \
  --mode isolated --run-id observatory-preflight-01
```

The phase automates the existing `/Account/Login` antiforgery form and normal
owner cookie. Curl submits the form without pinning POST across the redirect,
follows the resulting GET, and proves the cookie against an owner-only endpoint before obtaining an owner-authorized antiforgery header for the
identity/bootstrap mutations. There is no anonymous password API. It asks the
application to create/read its device identity, selects the exact declared
observatory through LogicHost's owner API, verifies the device, requests a
short-lived envelope, and imports it through `DeviceBootstrapWorkflow`. Passwords,
API keys, verification codes, envelopes, device keys, and registration tokens
remain in mode-`0600` request/response files and never enter process arguments,
ledgers, evidence, or status output. The remote plaintext owner password is
deleted immediately after login; cookie and header files are deleted on every
phase exit.

The one-time envelope response is retained only under the owner-private local
state root while its exact authoritative registration remains Pending and its
expiry is still in the future. It is deleted locally and remotely after import
is confirmed. Continuity returns deterministic registration history: one Active
registration is authoritative; otherwise the newest Pending registration is;
multiple Active registrations are an incident. If LogicHost is Active while the
edge remains unprovisioned, automation rotates the registration only when there
are no central frames, artifacts, or fleet records. Any central evidence fails
closed for incident recovery. A successful Active-to-Pending rotation always
discards the retained old envelope and requests and stores a newly issued one
before retrying local import. Revoked, expired, mismatched, ambiguous, and all
unexpected HTTP states fail closed. The phase then writes final `AgentId=DeviceId`, disables the provisioning
gate, enables upload, recreates the CameraAgent, and waits for the first fleet
acknowledgement whose local and central sequence and central receive timestamp
are strictly newer than the pre-recreation values. Durable raw-capture, artifact-outbox, and fleet metadata are
exposed only through the owner-authorized continuity projection; counters are
never edited or repaired. Persistent mode additionally requires the durable raw
ingress database and a nonzero local capture sequence at least as large as the
central maximum. If central artifacts exist, the local artifact-outbox database,
record/audit progress, and device-bound acknowledged artifact capture sequence
must not be missing, reset, or behind the central artifact window. If central
fleet history exists, the local fleet database must retain the exact agent
instance identity, a maximum sequence at least as large as central, and a next
sequence strictly larger than central. These checks occur before registration,
configuration, or gate mutation. Missing, reset, stale, or rebound state fails
closed. Readiness
also requires the central current rig-profile version and immutable hash to equal
the edge expectation, so a restart cannot silently acknowledge a different rig
configuration.

## Run W0 Smoke

```bash
./scripts/deploy:environment smoke --inventory /absolute/path/inventory.yml \
  --mode isolated --run-id observatory-preflight-01
```

`smoke` accepts only inventory workload `W0`. It renders the pinned profile with
the registered device ID, stages it through the private configuration path,
recreates the CameraAgent, and gates health before the measured window. The
profile is exactly 64 x 48 Mono16 with seed 2025 and a stable source/config hash.
Each agent receives its own `durationSeconds` deadline and W0 observes one deterministic capture advance; it requires
all host health and metrics endpoints, exact configured identity, a local and
central capture-sequence advance, zero pending/quarantined capture, processing,
lane, and artifact queues, enabled central integration, and a current fleet
acknowledgement. Artifacts created in the sequence window must have Available
objects with verification timestamps, exact SHA-256 source relations, configured
recipe identities, lineage counts, and a positive central completed-derivative
delta. Derivative provenance is validated separately by resolving every ordered
source identity and checksum in the central window. Raw output must be 64 x 48
Mono16, 6,144 bytes, and match the activated profile. Evidence
records these sanitized facts rather than checksum/recipe booleans. For one W0
Raw artifact per agent, `smoke` selects an explicit intersection of central-window
Raw artifacts and device-bound locally acknowledged Raw artifacts. Preview UUID
ordering and central-only derivatives cannot influence the checksum proof. It proves that local durable-outbox,
central metadata, private retrieved bytes, and response-declared SHA-256 are equal; the
temporary retrieval is deleted after hashing. It also records only bounded
metric totals and series counts, a bounded application-log line count, and one
owner-projected capture-pipeline trace/span identity whose capture sequence and
Raw artifact identity exactly match the checksum proof. The endpoint request's own
Activity is never evidence. The runtime retains at most 64 correlated entries.
Missing signals,
excessive metric cardinality or output size, and credential/payload-like content
fail the phase; raw metrics, logs, traces, paths, and payloads never enter the
ledger or evidence.

`measure --workload W1|W2` activates the selected repository-pinned canonical profile and
executes its declared warm-up plus measured capture count. With explicit
`sustainedArmOptIn: true`, use:

```bash
./scripts/deploy:environment measure --inventory /absolute/path/inventory.yml \
  --mode isolated --run-id observatory-preflight-01 --workload W1
```

The W1 profile is `virtual-asi174.full.json` at 1936 x 1216 Mono16; W2 is
`virtual-asi178mc.full.json` at 3096 x 2080 BayerRggb16. `measure` records source
and rendered config hashes, options identity, seed, dimensions, format,
concurrency, and exact warm-up and measured counts. Warm-up waits for the exact
capture-sequence boundary; overshoot fails. The phase then resets the bounded
capture telemetry and timing windows through the owner-mutating deployment API,
requires an empty fresh baseline, and executes the exact measured boundary.
Evidence records `warmupCompleted` and `measuredCompleted` separately and retains
only measured-window before/after durable continuity, complete queue/backlog,
capture telemetry, timing, and Docker CPU/RSS/block/network-I/O snapshots. Missing fields, wrong
types, target-shape drift, or failure to reach the declared operation count by
`durationSeconds` fails the phase. Evidence is marked `executionMode: canonical`
and `canonicalWorkloadConfigured: true`. Normal contract tests use a one-operation
representative hook; Tier M candidate evidence uses the inventory's canonical 5
warm-up plus 30 measured operations fixed by the canonical manifest and the trial/regression rules in
`docs/planning/performance-validation.md`.

`bootstrap`, `smoke`, `measure`, `acceptance-init`, and `down` publish an authoritative private
ledger with a monotonically increasing publication generation. A digest commit
identifies the generation for which ledger, manifest, and evidence are all
equal. If the process exits after any individual rename, the next invocation
accepts only the immediately newer ledger generation and reconstructs stale or
missing mirrors before resuming. A fully committed generation with a changed
ledger, manifest, or evidence fails as tampering rather than being repaired. The
phase validator runs before mirror reconstruction, and orphan phase companions
without the ledger fail closed.

## Initialize Phase 14 Acceptance

After `smoke` passes, use a clean worktree to initialize the versioned Phase 14
campaign index without executing or claiming any scenario:

```bash
./scripts/deploy:environment acceptance-init --inventory /absolute/path/inventory.yml \
  --mode isolated --run-id observatory-preflight-01
```

The repository manifest at
`../../deploy/split-host/acceptance/phase14-scenarios.json` enumerates normal
flow, all twelve Phase 14 project-plan fault-row identities, and explicit
executable commit/publication boundaries for raw ingress, capture lanes,
processing, calibration, transient candidate/runtime journals, outbox
transitions, central ingest/object publication, jobs, and windows. Raw ingress
`ValidationCompleted` and transient-candidate `BeforeReservationValidation` and
`AfterReservationValidation` are also tracked explicitly even though they are
validation hooks rather than durable commits. The capture-lane `BeforeHandler`
and `AfterHandler` execution hooks are not labeled commit boundaries; the
separate lease-crash scenarios cover them. Processing `BeforeNodeExecution` is
likewise an execution hook used by pressure/shutdown campaigns, not a publication
boundary. CameraAgent host, LogicHost host, network, SQL,
Redis, MinIO, and SMTP failures are distinct scenarios. `executionClass`
distinguishes existing component automation from boundaries requiring the real
campaign; external, soak, Stellarium, and future-hardware remain separate gates.
Every test evidence source uses an existing public MSTest fully-qualified method
name and an optional `;case=<selector>`; the focused contract audits every FQN
against source and compares the manifest boundary sets with the relevant fault
enums. The owner-only
`acceptance-index.json` binds the run, inventory, source revision/tree, sanitized
target identities, canonical workload identities, scenario status, and expected
artifact metadata. Initialization first parses the inventory once, canonicalizes
it, and requires its SHA-256 to equal the invocation's inventory hash. Smoke
validation and topology projection use only that immutable JSON snapshot; the
live inventory file is never reread. Initialization requires smoke to be a fully
committed passed phase: owner-only regular ledger, manifest, evidence, and commit
files; exact smoke ledger shape and run/mode/inventory/revision/workload/target
identity; an exact generation/digest commit; and canonical ledger/manifest/
evidence equality. This check is read-only and never repairs or rewrites smoke.
Initialization reads the campaign, workload manifest, and
workload sources as blobs from the exact inventory revision rather than from the
working tree. It verifies clean HEAD, source tree, and status immediately before
and after recovery/publication; initialization and every resume reject drift
before reporting success. Every scenario and classification starts `not-run`.
Machine output reports `stage=acceptance-init`; its `passed` status means only
that campaign-index initialization passed and never means `GATE-P14` passed.
Artifact bytes are not copied, and byte length/SHA-256 remain null until a later
execution slice records a sanitized artifact. Resume rejects contract drift,
changed source/topology/workload identities, unsafe files, or committed mirror
tampering without changing prior evidence. Contract tests cover interruptions
after each ledger, manifest, evidence, and commit rename plus symlink, hardlink,
orphan, and tamper rejection for the four phase files. They also reject
mirror-only, tampered, and unsafe smoke quartets and verify inventory hash drift
cannot create acceptance files or alter topology projected from a prior snapshot.

Run the focused contract test with:

```bash
./scripts/test:phase14-acceptance
```

## Stop Or Delete

Preserve all state:

```bash
./scripts/deploy:environment down --inventory /absolute/path/inventory.yml \
  --mode persistent --run-id observatory-preflight-01 --preserve-state
```

Delete a fully isolated, orchestrator-owned stack:

```bash
./scripts/deploy:environment down --inventory /absolute/path/inventory.yml \
  --mode isolated --run-id observatory-preflight-01 \
  --delete-state --confirm observatory-preflight-01
```

Exactly one policy is required. CameraAgents are paused and drained before they
stop, LogicHost stops second, and
deployed shared services last; existing shared services are left running.
The pause command carries a stable run-and-target-scoped `Idempotency-Key`; only
an HTTP success response is accepted, and an arbitrary conflict is not treated
as an idempotent replay. Deletion is accepted only in isolated `services.mode: deploy`, only when every
runtime root was created by the confirmed run, and only after all guards pass.
Before parsing, the prepare ledger, manifest, and evidence must each be an
owner-UID, mode-`0600`, single-link regular non-symlink, and the passed private
manifest must equal the ledger. Deletion removes explicit Compose services, all
four exact project default networks, the three exact project-named volumes, and
marker-validated runtime roots. Mailpit is included through the `test-smtp`
profile. Every mutation is journaled with an atomic intent and completion; resume
accepts absence only after a committed ownership-validated intent/completion.
SSH host and Docker daemon identity are re-correlated immediately before and
after each mutation. Every removable network and volume carries the exact
run-ID and inventory-SHA labels written at creation; network identity additionally
requires the exact Compose project and `default` network labels. Completed
actions re-probe container stopped/absent state and require networks, volumes,
and runtime roots to remain absent. Recreated or relabeled resources therefore
retry through a prior intent or fail instead of producing a false passed phase.
Docker absence is recognized only from the exact object-type not-found response;
daemon, transport, authorization, and malformed-output failures are errors, not
absence proof. Completed-resource verification applies the same distinction and
re-correlates the target before and after every check.
It never invokes generic `compose down -v`, a
Redis flush, production bucket/database deletion, or deletion of reused roots.
