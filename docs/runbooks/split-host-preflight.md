# Split-Host Preflight Runbook

This runbook covers the frozen first deployment slice from issue #151. It
validates a versioned multi-host inventory and records sanitized evidence. It
does not build or load images, create deployment paths, start Compose, bootstrap
applications, mutate shared services, stop applications, or tear anything down.

## Inventory And Secrets

Copy `deploy/split-host/inventory.example.yml` to an ignored operator location
and fill in every target explicitly. The file is JSON-compatible YAML and is
parsed strictly with `jq`; it is never sourced. The executable contract in
`scripts/deploy/inventory.sh` is authoritative. The matching published shape
reference is `deploy/split-host/inventory.schema.json`; CI exercises its required
keys, shared-target shape, explicit-port rule, example, and runtime validator
together because the repository does not carry a pinned JSON Schema engine.

Each target declares its SSH destination, Docker context, expected hostname,
machine identity, daemon identity, architecture, absolute runtime root, endpoint,
and ports. Preflight requires the hostname observed through SSH to equal both the
declared hostname and Docker daemon `Name`; this correlates the two routes rather
than trusting unrelated inventory assertions.

Runtime roots may contain ordinary spaces and are passed only through quoted
arguments. Control characters, repeated separators, and `.` or `..` components
are rejected. Catalog versions follow the repository's existing `4.2`,
`hyg-v4.2-p3-s2-r1`, and fixture-style identifiers; colon is not a supported
catalog-version separator.
The inventory also pins the Git revision, catalog, image digests, and non-secret
service routes. There are no ambient target defaults.

`secretSource.path` names an ignored dotenv-style file. It must be a regular,
nonsymlink, single-link file owned by the invoking user with mode `0400` or
`0600`. `requiredReferences` contains variable names only. Preflight verifies
that those assignments exist but never sources, evaluates, prints, or persists
their values. Keep the inventory owner-only too if its declared runtime roots or
hostnames are operationally sensitive, although they are intentionally treated
as non-secret evidence fields.

## Run Preflight

Use an explicit inventory and mode:

```bash
./scripts/deploy:environment preflight \
  --inventory /absolute/path/inventory.yml \
  --mode persistent \
  --run-id observatory-preflight-01
```

`--mode` is `isolated` or `persistent`. Persistent mode rejects fixture
catalogs and requires HTTPS public authorities for LogicHost, every CameraAgent,
and the optional shared-services target. Optional `--state-root` and
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

The test uses fake `ssh` and `docker` through `PATH`. Fake SSH executes the exact
supplied remote Bash scripts against controlled host commands, while TCP and HTTP
checks use real Bash `/dev/tcp` and curl behavior against a temporary listener
hosted by the pinned .NET SDK already installed by the devcontainer and CI. It
asserts routing, host correlation, path and lock safety, missing-tool behavior,
redaction, failed-evidence replacement, resume behavior, atomic publication
recovery, and absence of mutation commands.
