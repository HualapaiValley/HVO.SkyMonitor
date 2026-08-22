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

Use wired Ethernet for sustained or full-frame CameraAgent workloads when it is
available. Wi-Fi remains supported, but SSH reachability, health checks, signal
strength, nominal association rate, and loss-free ping do not prove sufficient
payload throughput. Before relying on Wi-Fi, measure a representative direct
transfer from the CameraAgent to LogicHost and require comfortable margin under
the observed artifact-upload request deadline and expected capture cadence.

Runtime roots may contain ordinary spaces and are passed only through quoted
arguments. Control characters, repeated separators, `.` or `..` components,
backslash, comma, and double quote are rejected before deployment so every
accepted root has one unambiguous Docker bind-mount and mountinfo representation.
`moduleConfigPath` remains a generic safe absolute file path and does not inherit
those Docker-specific delimiter restrictions. Catalog versions follow the repository's existing `4.2`,
`hyg-v4.2-p3-s2-r1`, and fixture-style identifiers; colon is not a supported
catalog-version separator.
Inventory schema v7 pins the Git revision, catalog, source-revision image tag,
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

Schema v7 also declares the observatory, agent friendly names, owner automation
credential reference, workload selection, service ownership, distinct SQL
administrator/initializer/runtime users, Redis administrator/runtime identities
and key prefix, MinIO root/runtime identities and buckets, certificate paths,
KeyPerFile mappings, and resource limits. Application memory remains independent
from the SQL Server container and internal memory ceilings. `existing` service
mode never provisions those services. `deploy` requires an explicit shared-services target
and digest references for SQL Server, Redis, MinIO, the MinIO client, and optional
Mailpit. Mailpit is accepted only for isolated deployment.

The shared-services target may be co-located with LogicHost on one physical host
and Docker daemon. That exact pair may share expected hostname, machine identity,
and daemon identity only when it uses distinct SSH host and Docker context aliases,
runtime roots, names, and ports. This keeps transport selection unambiguous while
allowing an isolated central stack on one daemon. CameraAgents and every other
target combination remain identity-collision failures.

Every CameraAgent declares an owner-password secret reference and an absolute
path to a complete `CameraModuleDocument`. `up` copies that document separately,
sets `CameraAgent:ConfigFilePath` through KeyPerFile, and stages only an
`AdminPasswordFile` pointer plus the private password file. Appsettings-shaped
objects are not valid module documents and are rejected before startup.
`deployment.transient.mode` is explicit and accepts `Off` or `Hybrid`. LogicHost
and CameraAgents start `Off` while agent provisioning gates are closed and upload
is disabled. Bootstrap stages each agent's declared mode and required flag in the
same configuration generation that enables upload and removes its gate. Central
transient processing is activated only after every agent acknowledges its final
identity, upload, rig, and transient configuration. This prevents Hybrid central
processing from starting against an incomplete fleet.

For an isolated Mailpit-backed campaign, review only known generated events with
an explicit owner-only allowlist based on
`deploy/split-host/transient-confirm.allowlist.example.json`:

```bash
./scripts/deploy:environment transient-confirm \
  --inventory /absolute/path/inventory.json \
  --mode isolated \
  --run-id run-id \
  --allowlist /absolute/path/transient-confirm.allowlist.json
```

The command never enumerates the review queue. It first checks every supplied
central event ID directly against its expected synthetic agent ID, meteor/fireball
classification, active assessment, and review state. Only after all entries pass
does it append confirmed reviews with deterministic idempotency keys. It then
requires the redacted review audit projection, a sent notification record, and a
new Mailpit message correlated by recipient, timestamp, subject, and event ID.
The resulting `transient-confirm.json` contains IDs and hashes, not credentials,
reviewer identity, idempotency keys, recipient addresses, or message text. The
allowlist itself must be a regular owner-owned mode `0400` or `0600` file.
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
- for deployed shared services, enough total host memory for the declared SQL
  container limit;
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

The slice otherwise records observations without speculative CPU, memory, disk,
clock, or thermal thresholds. The SQL check enforces only the operator-declared
container requirement needed before preflight permits service mutation.

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

The no-argument command remains the serial all-contract mode. CI runs nine
isolated shards with at most eight concurrent workers; list or select them with
`./scripts/test:deploy-environment --list-shards` and
`./scripts/test:deploy-environment --shard NAME`. See the
[CI pipeline runbook](ci-pipeline.md) for coordinator failure and retained-log
behavior.

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

`images.builder` explicitly declares the buildx builder name, driver, `default`
control endpoint, and expected local Docker daemon ID, name, OS, and
architecture. The phase requires one unambiguous builder, all of its nodes to be
running, the declared control endpoint to be present, and every required
platform to be supplied by at least one node. This permits a native AMD64/ARM64
multi-node builder without weakening control-daemon correlation. The selected
builder is passed to every build and registry inspect.
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
The catalog is built once on its documented canonical `linux/amd64` builder; this
phase transfers and installs that same approved bundle on every target, including
ARM64 CameraAgents. It never copies or executes the catalog builder remotely.
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
SQL Server, Redis, and MinIO run as that same declared UID/GID and retain their
project-scoped named-volume identities, labels, and teardown checks. Each named
volume is backed by its exact run-owned bind directory beneath
`<shared runtimeRoot>/application/{sql,redis,minio}` rather than Docker's ambient
data root.
SQL Server uses the inventory's dedicated container memory limit and
`MSSQL_MEMORY_LIMIT_MB` ceiling. The production-like W2 profile uses a `4G`
container budget and `3072` MB internal ceiling, leaving process overhead while
LogicHost and CameraAgents retain the independent `2G` application limit.
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

Before replacing a CameraAgent profile, `measure` durably records schema-v2
recovery state: the exact active module-file and configuration hashes, active
schedule revision/version, and capture-control state/version. It pauses capture
before profile replacement. A successful measurement intentionally retains the
canonical profile in the paused state for dependent campaigns. On `INT`, `TERM`,
`HUP`, or an ordinary phase failure, cleanup first pauses every affected target,
then restores and verifies each original module configuration, schedule
revision, and capture-control state in that order. The ledger is published as
failed before restoration begins. Unverified restoration remains explicit and
retains cleanup material for a later retry; it is never reported as passed.
Restart recovery reconciles completed control attempts and committed capture
boundaries rather than replaying an already completed warm-up or measured set.
Supervisors must signal the deployment process group so an active transport
child is interrupted and the shell can enter its bounded restoration handler.

For a required Hybrid transient lane, queue convergence permits only the temporal
algorithm's final two healthy history captures. The retained capture identities must
equal that exact tail. The bounded operations projection exposes up to three distinct
active center-capture identities across transient lane work, worker work, and unreleased
candidate holds, so a stale third center fails closed. Raw-ingress records range from one
retained center row through its three-source causal window. Transient records range from
one worker row per center through the built-in extraction profile's maximum 32 candidates
per center. These are bounded record counts, not substitutes for record-level identities:
they are accepted only after the pending-capture projection proves the complete active
center identities and every other lane, the transient worker, capture processing, and
artifact outbox prove there is no unrelated active work. Every other lane and queue must
be empty, and the transient worker must be healthy and idle. The retained before/after
queue snapshots expose this tail and its exact
final capture sequences; measurement marks the queues converged but not fully drained.
Any unexpected pending identity, excess fan-out, pressure, lease, retry, quarantine,
terminal work, or active transient worker fails convergence.

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

The runtime inventory now has 111 rows. The separate definition-only contract at
`deploy/split-host/acceptance/phase14-evidence-contract.json` binds that count and
the canonical inventory digest without changing runtime ledger compatibility. It
maps 103 test-backed rows to future source-family import, the two supported
campaigns to executable manifests, two unsupported campaigns to explicit
deferment, and four separately labeled gates to exclusion. See
`docs/planning/phase14-evidence-campaign.md` for workload values, artifact
admissibility, status meanings, and follow-up ownership. Definition states never
promote `not-run` or `recorded` runtime rows to `passed`.

The deferred source contract does not treat `;case=` as a TRX data-row selector.
Several cited methods run fault cases inside one method body, so source import
requires every fragment to bind the exact inventory selector in addition to the
method-level TRX. Sanitizer and admissibility identities live in the separate
campaign-index envelope; the existing runtime artifact schema remains unchanged.

Run the focused contract test with:

```bash
./scripts/test:phase14-acceptance
./scripts/test:phase14-campaign
./scripts/test:phase14-normal-campaign
./scripts/test:phase14-source-import
```

The campaign contract uses stateful fake transport boundaries to validate
orchestration and sanitized artifact shape. It is not a substitute for the real
split-host outage execution and retained evidence required before recording the
scenario.

## Import Test-Backed Source Evidence

Run the source importer only from the exact clean reviewed evidence-harness
revision. The contract keeps product source provenance pinned to
`ee117c1e8cf3e04998825d366da663e16c2b95ed` and tree
`17a0ad69452d710c82847f9a515022de05ebfaa5`; the harness must be a
clean descendant whose complete changed-path set is allowlisted. After the
harness commit has been reviewed, set its full commit SHA and run:

```bash
./scripts/phase14:source-import \
  --output-root /absolute/private/phase14-source \
  --harness-revision <reviewed-harness-commit> \
  --catalog-root /absolute/installed/production-catalog \
  --collector-image <reviewed-collector-image>@sha256:<digest>
```

The output root must already be an owner-only mode-`0700` directory. The command
does not accept operator-supplied assemblies, TRX files, fragments, projects,
test names, selectors, product revisions, or product trees. It builds each
standard project once in nonincremental Release mode, resolves the actual MSBuild
target assembly, runs each of 24 standard methods by exact FQN with private
recorder roots, and sanitizes raw TRX immediately. It runs the fixed issue-211
five-trial W6 harness for the sole CameraAgent acceptance method using the
reviewed catalog and digest-pinned collector image. Raw harness output remains
private scratch and is deleted before publication.

Every `;case=` value must exactly equal the fragment's retained selector. A
passing method TRX cannot replace a missing fragment. When one scenario has
multiple data-row or boundary observations, their assertion and measurement
schemas must be identical; the imported evidence records their count. The
importer generates assembly provenance itself, publishes 25 method bundles and
103 runtime-compatible scenario artifacts atomically beneath
`source-import/`, and binds every bundle, assembly, sanitized TRX,
source-evidence file, and artifact by byte length where applicable and SHA-256.
Each source bundle requires `trialResults`. Standard methods retain one
`trial-results/method.trx`; the acceptance method retains all five owner-only
sanitized files as `trial-results/trial-1.trx` through `trial-results/trial-5.trx`.
Its `result.trx` remains byte-identical to trial 1. Three semantic publication
scenarios emit once per prefixed trial; the bounded expensive restart, pressure,
and shutdown scenarios remain in trial 1. Collection therefore requires exactly
18 fragments and fails closed if any required trial result or trial-prefixed
observation is missing or digest-mismatched.

An existing valid publication is revalidated and reused without rebuilding.
Tampering, stale inventory or harness identity, unsafe paths/modes/links,
selector mismatch, incomplete observations, or changed bundle bytes fail closed
without overwrite. The index status is `recorded` and campaign status remains
`not-run`; #319 does not perform #321 admissibility, publish campaign completion,
or claim `GATE-P14`.

Run the Docker-free collector/importer contract with:

```bash
./scripts/test:phase14-source-import
```

## Record Phase 14 Scenario Evidence

After `acceptance-init`, record one strict sanitized scenario artifact without
executing or evaluating the scenario:

```bash
./scripts/deploy:environment acceptance-record \
  --inventory /absolute/path/inventory.yml \
  --mode isolated \
  --run-id observatory-preflight-01 \
  --scenario normal-flow \
  --artifact /absolute/private/path/normal-flow.json
```

The owner-only input must be a mode-`0600`, single-link regular JSON file no
larger than 256 KiB. Its exact schema binds the run, inventory, source revision
and tree, scenario classification and execution class, evidence source, and
ordered workload identities. It permits only bounded assertions, output byte
lengths/SHA-256 identities, and nonnegative measurements with allowlisted units.
It does not permit paths, authorities, logs, credentials, payloads, exception
text, or arbitrary fields.

Recording uses `not-run -> recording -> recorded`, never `passed`. It commits
the expected artifact length and SHA-256 as an intent, atomically publishes the
canonical artifact beneath the manifest-owned `acceptance-artifacts/` path, and
then commits `recorded`. Recovery accepts an interrupted intent only when the
artifact is absent or exactly matches the committed identity. A recorded
artifact is immutable; mismatched bytes, unsafe links or modes, changed context,
or replacement input fail without rewriting prior evidence. Failed execution
artifacts are retained under the same rules, and another attempt requires a new
deployment run.

`acceptance-record` does not execute a scenario, decide whether its evidence is
sufficient, mark a scenario or classification passed, or claim `GATE-P14`.
`normal-flow` requires the exact ordered W1/W2 binding.
`logichost-network-outage` requires W2 and remains distinct from the separate
`logichost-host-failure` and `network-failure` scenarios.

Execute the normative normal-flow campaign after bootstrap, smoke, and
`acceptance-init` have passed:

```bash
./scripts/deploy:environment acceptance-run \
  --inventory /absolute/path/inventory.yml \
  --mode isolated \
  --run-id observatory-preflight-01 \
  --scenario normal-flow
```

This fixed command requires one CameraAgent and executes canonical W1 followed
by canonical W2. Each workload retains a separate committed measure quartet
beneath `state/acceptance-campaign-normal-flow/<workload>` and a sanitized
supporting mirror beneath `evidence/acceptance-support`. W2 must begin its
warm-up at the exact W1 measured end sequence, so unrelated captures fail the
campaign rather than being silently included. Completed workload evidence is
validated and reused on resume; it is never rerun to make room for the next
workload.

The immutable `normal-flow` artifact binds both supporting evidence digests,
the 60 measured Raw output lengths/checksums, exact warm-up/measured counts,
the retained measured-window correctness result, drained queues, and capture
runtime timings. The current retained measure contract does not independently
expose central object/derivative details, or establish W1/W2
capture-telemetry aggregates, content-retrieval hashes, trace/log/cardinality
review, host resource peaks, or
the complete window/cloud/transient/UI path, so this command does not claim
those checks or full `GATE-P14`.

Execute the scoped normative LogicHost outage only after the same run has passed
canonical W2 measurement and `acceptance-init`:

```bash
./scripts/deploy:environment acceptance-run \
  --inventory /absolute/path/inventory.yml \
  --mode isolated \
  --run-id observatory-preflight-01 \
  --scenario logichost-network-outage
```

This campaign requires one CameraAgent. It starts from the paused, converged W2
measurement boundary, stops only the LogicHost application container, and keeps
the CameraAgent and shared services running. It executes exactly 10 additional
W2 captures at the canonical 25-second cadence, pauses acquisition, requires a
nonzero outbox backlog with no quarantine or terminal work, restores LogicHost,
and waits up to 900 seconds from campaign start for exact local/central capture,
Raw checksum, object verification, derivative lineage, and zero-queue
convergence. Recovery must complete within the declared window at more than the
configured 0.04 captures-per-second arrival rate. The sanitized artifact retains
the exact 10 Raw lengths/checksums, outage backlog count/bytes, outage and
recovery durations, and drain rate.

Ordinary command failure attempts both an authenticated safety pause and
LogicHost restoration. An abrupt process or control-host loss can leave the
exact-count window contaminated; such a runtime fails closed as
`fresh-run-required` rather than fabricating recovery or replaying captures. The
campaign exercises the normative “LogicHost or network outage” row only. It does
not satisfy the separate generic `network-failure` or `logichost-host-failure`
scenario.

## Import LogicHost Dependency Evidence

Use the bounded component importer only for the fixed `logichost-dependencies`
family and only from the clean exact inventory revision and tree:

```bash
dotnet build tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj \
  --configuration Release -warnaserror
./scripts/deploy:environment acceptance-component \
  --inventory /absolute/path/inventory.yml \
  --mode isolated \
  --run-id observatory-preflight-01 \
  --component logichost-dependencies
```

The command accepts no scenario, artifact, workload, alternate component, test
name, or source-evidence option. It runs exactly the Manual
`LogicHostDependencyOutageAcceptanceTests.Issue107_DependenciesDegradeWithoutFabricatedDataAndRecoverWithinBound`
test against SQL Server, Redis, MinIO, and SMTP Testcontainers. The test emits
strict v2 evidence bound by `EvidenceSourceIdentity` to the requested current
revision, clean tree, Release test/LogicHost/TestSupport assemblies, and explicit
healthy-operation, outage-operation, and recovered-operation assertions. The
evidence contains no payloads, file paths, service addresses, response bodies,
exception text, or credentials.

Before recording anything, the importer requires exactly one passing TRX for
that fully qualified test and exactly four evidence entries. It sanitizes only
`minio-failure`, `sql-failure`, `redis-failure`, and `smtp-failure` into the
existing acceptance artifact schema. Source evidence, TRX, sanitized artifacts,
and their lengths/SHA-256 values are committed as one owner-only, atomically
renamed bundle beneath
`state/acceptance-component-logichost-dependencies/bundle`. The bundle manifest
is itself digest-bound by `bundle-commit.json`.

Recording occurs only after the complete bundle validates, in fixed MinIO, SQL
Server, Redis, SMTP order. An interrupted invocation can reuse a valid committed
bundle without rerunning the test; unsafe, malformed, context-mismatched, or
digest-tampered bundles fail closed and are never rebuilt in place. Source HEAD,
tree, and cleanliness are checked before execution, before bundle publication,
before recording, and after recording. The command records immutable artifacts
using `not-run -> recording -> recorded`; it does not promote a scenario or
classification to `passed` and does not claim `GATE-P14` or split-host dependency
fault coverage.

Run the importer contract without service containers with:

```bash
./scripts/test:phase14-component
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

Exactly one policy is required. CameraAgents are paused and reach a safe queue
boundary before they stop, LogicHost stops second, and
deployed shared services last; existing shared services are left running.
The pause command carries a stable run-and-target-scoped `Idempotency-Key`; only
an exact paused state and integer control version in the HTTP success response
are accepted, and an arbitrary conflict is not treated as an idempotent replay.
Teardown binds that receipt to the durable paused control projection, the inventory's
exact Off or Hybrid mode, and exactly one durable capture sequence for the provisioned
device. The device identity, nonnegative end sequence (including zero), and normalized
active-configuration SHA-256 must remain unchanged. Two consecutive
fresh observations must satisfy the shared queue convergence contract before
Compose stop: Off requires an empty drain and a Disabled idle transient worker;
Hybrid is selected from the required transient runtime lane and permits only its
healthy idle zero-boundary state or bounded final one-or-two-capture temporal
tail. Every summary and continuity request is cache-busted; each accepted summary must
be newer than the preceding accepted root timestamp, bound to the current validated
device configuration and expected transient mode, and identical in its canonical
safety-value fingerprint. Raw ingress and capture lanes must be fresh and observed after
the pause. Capture processing and artifact outbox must be fresh but may predate the pause
because they refresh asynchronously or may be disabled. Capture control needs a valid
observation timestamp and the exact receipt state/version, but an already-paused durable
state need not refresh periodically. Hybrid requires a fresh transient worker; Off accepts
an idle Disabled worker with a valid observation timestamp even when its initial state is
stale. Any unsafe or non-monotonic summary resets confirmation. A newer safe summary with
a changed fingerprint begins a new candidate pair, so A,B,B converges while continuously
changing snapshots do not. Identity, sequence, or active configuration drift fails closed.
A final pause uses a new idempotency key and request after those two observations and must
return the same control version. One final cache-busted summary/continuity pair must then
be newer, queue-safe, mode/hash stable, and fingerprint-identical before Compose stop.
Supported orchestration assumes no malicious external
mutation in the remaining non-atomic script-to-Compose boundary; the final reassertion
narrows that boundary without a new application endpoint.
Deletion is accepted only in isolated `services.mode: deploy`, only when every
runtime root was created by the confirmed run, and only after all guards pass.
Before parsing, the prepare ledger, manifest, and evidence must each be an
owner-UID, mode-`0600`, single-link regular non-symlink, and the passed private
manifest must equal the ledger. Deletion removes explicit Compose services, all
four exact project default networks, the three exact project-named volumes, and
marker-validated runtime roots plus their matching prepare lock/state pairs. The
three volume objects are bind-backed by the
run-owned shared-service directories; removing a volume object does not delete
an arbitrary host path, and the later marker-validated runtime-root deletion
removes the backing data. Mailpit is included through the `test-smtp`
profile. Every mutation is journaled with an atomic intent and completion; resume
accepts absence only after a committed ownership-validated intent/completion.

A volume whose run-ID label matches but whose inventory label is a different
valid SHA-256 is never removed or relabeled. Down records the exact volume as a
completed `retain-volume-label-drift` action, emits
`inventory-label-drift-retained`, and fails before runtime-root deletion. That
retained action authorizes only exact Docker absence on a later retry, allowing
operator recovery to converge without treating a foreign, malformed, unlabeled,
or unreachable volume as absent. If the exact expected labels are restored, the
normal validated deletion path remains available. Wrong run identity, malformed
labels, daemon errors, and unproven initial absence remain hard failures.

Runtime-root deletion has no sudo, host privilege escalation, generic privileged
helper, or arbitrary image path. The effective SSH UID must own the exact root,
its `.hvo-deploy` control directory, and the single-link regular `ownership`
marker. Root and control mode must remain `0700`; marker mode must remain `0600`
and its two-line bytes, including exactly one final newline and no appended data,
must match the prepare-ledger digest exactly. Inspection uses no symlink
following. When the unprivileged ownership traversal succeeds, it allows only
the runtime UID or UID 0 and rejects every reported foreign UID before mutation.
If that traversal cannot enter a mode-`0700` UID-0 directory or otherwise fails,
the tree is classified as mixed and delegated without host mutation; the failure
never authorizes unprivileged deletion. The helper then performs the complete
ownership traversal and rejects every foreign UID or metadata error before
mutation. A mountpoint at or below
the runtime root is also rejected before mutation, including a same-device bind
mount. Each validation pass scans `/proc/self/mountinfo` once, encodes the actual
root into its escaped path representation for comparison, and fails closed on
scan errors; `-xdev` is
not treated as mountpoint proof. The component-by-component
ancestor checks and exact marker checks still fail closed for symlinks, special
files, hard links, wrong content, and mode or owner drift.

An entirely runtime-owned root is cleared by the unprivileged SSH user. Runtime-
owned directories are made owner-writable without following links, all content
except `.hvo-deploy/ownership` is removed, and the marker is revalidated before
the marker, control directory, and root are removed last. If inspection finds a
legitimate UID-0 descendant or cannot fully inspect an inaccessible descendant,
deletion uses only the already identity-correlated
target Docker daemon. The helper image is parsed without sourcing from the
owner-only up-rendered environment: `CAMERAAGENT_IMAGE` for a CameraAgent,
`LOGICHOST_IMAGE` for LogicHost, or `REDIS_IMAGE` for shared services. It must be
one exact locally present immutable reference: either a registry
`repository@sha256:<64-lowercase-hex>` reference or an archive-loaded local
`sha256:<64-lowercase-hex>` image ID. Tags, options, whitespace, duplicate
assignments, and malformed digests are rejected.

The mixed-UID helper runs through a 3600-second execution bound with
`--pull never`, PID limit 64, no network, a read-only helper
root filesystem, root user, dropped capabilities except the reviewed filesystem
override/owner capabilities, and no-new-privileges. Its `/bin/sh` script uses
POSIX/BusyBox-compatible syntax so both the Debian ASP.NET application images and
the Alpine Redis image can execute it. Its only host mount is the
exact validated runtime root at fixed `/runtime-root`. Inventory and transport
reject backslash, comma, and double quote roots before the host mountinfo scan or
Docker mount construction. The helper
independently revalidates root/control/
marker ownership and modes, exact marker content and link count, descendant UIDs,
and nested mountpoints. After making validated directories writable, depth-first
directory removal is checked again through exact tree emptiness, including nested
and top-level UID-0 directories. It removes neither marker nor root. Docker/SSH identity
is re-correlated immediately before and after this helper, after which the SSH
runtime owner revalidates and removes marker, control, and root. Helper denial,
failure, or interruption after mixed content clearing leaves the exact marker
and durable delete intent in place, so a corrected rerun resumes safely through
the same validation. There is no generic privilege escalation path.

Each helper run receives a fresh owner-only mode-`0700` local directory containing
a fixed, initially nonexistent cidfile path. Docker creates that file under
`umask 077`; only an owner-owned, single-link, mode-`0600` regular file containing
exactly 64 lowercase hexadecimal bytes with no newline is authoritative. Success
requires exact Docker not-found proof that `--rm` removed that full-ID helper
before host finalization. On timeout or failure, only that valid full ID authorizes
`docker --context <exact-target> container rm -f <exact-id>`, followed by the same
exact not-found proof. Empty or malformed cidfiles authorize no container removal;
the cidfile and its private directory are removed on every handled return path.

This is the same owner-controlled marker trust model used by prepare. Immediate
component and marker revalidation prevents accidental deletion and deletion of a
foreign path, but it does not claim inode binding or resistance to a malicious
runtime account that can race its owner-controlled parent between checks. Such an
account is inside the deployment trust boundary; operators must protect that
identity and parent from hostile concurrent mutation.

After an exact run-created runtime root is absent, down journals a separate
deletion for its deterministic sibling prepare lock and `.state` sidecar. It
derives one exact lock name from the prepare-ledger target/root and never uses a
wildcard. The runtime SSH owner must still control the non-group/world-writable
parent. Under a nonblocking flock, cleanup requires owner-UID, single-link,
mode-`0600` regular non-symlinks, exact lock bytes, and exact state bytes binding
the marker digest, creating run, and all ledger creation flags. It removes state
before lock and verifies both absent. A durable lock-delete intent permits retry
when interruption left the exact lock but already removed its state, or when both
are absent; no intent permits partial absence. Completed actions re-probe the
root, lock, and sidecar as absent, so recreation fails closed. Prepare itself
never repairs or adopts stale cross-run provenance.

The initial SSH filesystem inspection, final owner cleanup, and prepare-lock
cleanup are bounded to 3600 seconds with a 30-second kill grace and SSH
keepalives every 10 seconds with three missed replies permitted. Timeout or
transport loss fails the durable
delete intent and preserves the ownership marker unless final cleanup had already
completed and returned success.

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
