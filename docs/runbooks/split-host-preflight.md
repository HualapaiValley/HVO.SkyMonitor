# Split-Host Deployment Through Controlled Startup

This runbook covers preflight, runtime-root preparation, immutable image
distribution, catalog installation, and controlled application startup from issue #151. Preflight validates a versioned multi-host
inventory without remote mutation. Prepare creates only declared runtime roots
and exact ownership/lock metadata. Images then builds on the control host and
either pushes and pulls registry manifests or transfers local archives through
declared Docker contexts. `catalog` installs the declared verified catalog on
every host. `up` optionally creates isolated shared services, runs controlled
database initialization, starts LogicHost, and starts CameraAgents paused with
upload disabled. Bootstrap, workloads, stop, and deletion remain out of scope.

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
authority, so its `publicEndpoint` must use HTTPS in both isolated and persistent
modes; inventory validation rejects HTTP before any host contact. Preflight
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
Inventory schema v5 pins the Git revision, catalog, source-revision image tag,
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

Schema v5 also declares service ownership, distinct SQL
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

## Run Preflight

Use an explicit inventory and mode:

```bash
./scripts/deploy:environment preflight \
  --inventory /absolute/path/inventory.yml \
  --mode persistent \
  --run-id observatory-preflight-01
```

`--mode` is `isolated` or `persistent`. LogicHost requires an HTTPS public
authority in both modes. Persistent mode additionally rejects fixture catalogs
and requires HTTPS public authorities for every CameraAgent.
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
- the LogicHost public authority from every CameraAgent target.

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
OIDC discovery is checked only through the public HTTPS authority from every
CameraAgent host through the declared TLS proxy.

Each CameraAgent receives separate Identity, Data Protection, provisioning, raw,
and archive roots, but no central SQL/Redis/MinIO credentials. Fresh durable
capture state is paused and upload/central integration are disabled. `/alive`
and `/health` prove provisioning-gated startup only; they do not claim bootstrap
or fleet readiness. Private ledgers and evidence contain resource names, image
and catalog identities, and bounded status, but no secret values or connection
strings.
Failed startup atomically publishes the exact completed service, initializer,
runtime-role, and target progress. Resume validates committed or failed
ledger/manifest/evidence equality, rechecks completed state, and converges without
duplicating target entries.
