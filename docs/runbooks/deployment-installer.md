# CameraAgent Deployment Installer

The `hvo-skymonitor` self-contained Linux CLI installs one local, standalone
VirtualSky CameraAgent without a repository checkout or a target-host .NET
runtime. Docker Engine and the Compose plugin are the only application-runtime
prerequisites. Catalog releases can be resolved from signed local metadata,
HTTPS mirrors, or signed release indexes; every source uses the same pinned
signature, length, checksum, archive, and internal catalog validation.

## Inputs

Supply an immutable CameraAgent repository digest or an already loaded image ID,
plus the verified production catalog bundle. An offline image archive additionally
requires its independently trusted lowercase SHA-256. Never use a mutable image
tag or pass a password on the command line.

The compatibility form below consumes the internally verified local catalog
directory produced by the deterministic catalog tooling:

```bash
hvo-skymonitor cameraagent install \
  --friendly-name "North All-Sky Camera" \
  --owner-email admin@home.lan \
  --bind-address 127.0.0.1 \
  --port 5130 \
  --catalog-bundle /srv/hvo/hyg-v4.2-p3-s2-r1.bundle \
  --image-ref sha256:<loaded-image-id> \
  --latitude 35.2 \
  --longitude -114.1 \
  --elevation 800 \
  --time-zone America/Phoenix
```

For a signed offline catalog release, place `catalog-manifest.json`,
`catalog-manifest.json.sig`, and the referenced bundle together and use:

```bash
hvo-skymonitor cameraagent install \
  --channel local \
  --catalog-manifest /srv/hvo/catalog-release/catalog-manifest.json \
  --no-download \
  --friendly-name "North All-Sky Camera" \
  --owner-email admin@home.lan \
  --image-ref sha256:<loaded-image-id>
```

For an exact online release, use an immutable versioned manifest URL. To resolve
a documented channel default or `--catalog-version`, use `--catalog-index` and a
signed immutable index snapshot. A non-GitHub mirror also requires
`--asset-base-url`; mirrored bytes retain the release tag, manifest hash, asset
hash, and signing identity. Channels are `stable`, `nightly`, `prerelease`, and
`local`; `local` is the compatibility default.

`--no-download` permits local inputs and fully verified cache hits but performs
no HTTP request and no Docker registry pull. The user cache is
`$XDG_CACHE_HOME/hvo/skymonitor/distribution`, or
`~/.cache/hvo/skymonitor/distribution` when `XDG_CACHE_HOME` is unset. Cache
hits are rehashed, partial transfers use signed lengths and HTTP ranges, invalid
complete or partial bytes are removed, and signed-index sequence/hash state
prevents rollback. Network requests are HTTPS-only with bounded redirects,
timeouts, retries, response sizes, and disk-space checks. Optional public GitHub
authentication uses `HVO_GITHUB_TOKEN`; credentials are sent only to the
original `github.com` request and never retained as evidence.

Signed-index rollback state is durable rather than cache data. It is stored
under `$XDG_STATE_HOME/hvo/skymonitor/distribution`, or
`~/.local/state/hvo/skymonitor/distribution` when `XDG_STATE_HOME` is unset, and
is not removed by ordinary cache cleanup.

Use `--image-archive /srv/hvo/cameraagent.tar` together with
`--image-archive-sha256 <sha256>` for an offline image load. Use
`--password-file <owner-only-path>` for an operator-supplied temporary password;
otherwise the installer generates one. `--json` selects the versioned structured
result. `--dry-run` validates the catalog, Docker daemon, loaded image, generated
configuration, and rendered Compose model using private temporary storage without
creating the product root, loading/pulling an image, or starting a container.

Missing required values are prompted only on an interactive terminal. A strict
JSON config may be supplied with `--config`; value options cannot be mixed with
it. Flags such as `--dry-run`, `--json`, `--no-download`, and the explicit non-loopback HTTP
acknowledgement may still be applied.

## Signed Image Release

> `linux/arm64` installation is qualified natively: the `open(2)` flag defect
> ([#603](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/603)) is fixed,
> the advisory arm64 workflow runs this CLI's Unit suite, and the complete signed
> installer lifecycle has run on aarch64 under
> [#651](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/651). The campaign
> uses locally signed published-format candidates. An archive downloaded from an
> actual production release remains untested because no production release exists;
> preserve that publication/download caveat until the first release smoke.

`--image-ref` and `--image-archive` name an image the operator has already
established. `--image-manifest`, `--image-index`, and `--image-version` instead
consume the signed CameraAgent image train described in
[release-distribution.md](release-distribution.md), and the two forms are
mutually exclusive: a signed release resolves the image, so an installation
never both trusts a release and accepts an operator-supplied image.

An air-gapped installation receives the release directory on local media and
needs neither registry access nor a source checkout:

```bash
hvo-skymonitor cameraagent install \
  --channel local \
  --image-manifest /media/hvo/image-v1.4.0/image-manifest.json \
  --catalog-manifest /media/hvo/catalog-release/catalog-manifest.json \
  --no-download \
  --friendly-name "North All-Sky Camera" \
  --owner-email admin@home.lan \
  --latitude 35.2 --longitude -114.1 --elevation 800 --time-zone America/Phoenix
```

Online, use an immutable versioned manifest URL, or `--image-index` with
`--image-version` to resolve a documented default from a signed index snapshot.
A non-GitHub mirror also requires `--asset-base-url`; that option is shared with
the catalog train, so a single invocation that names both a mirrored image
release and a catalog release resolves both from the same base. Signed indexes
are rollback-protected per train, so an older image index is refused just as an
older catalog index is.

The installation retains the verified archive under
`<instance-root>/state/deployment/image-archive.tar` so a resumed or repeated
operation does not re-acquire it. It is the size of the published image and is
included in instance backups; size the instance filesystem accordingly.

The installation resolves the release, selects the platform matching this host,
and verifies the offline archive against its signed length and checksum **before
the Docker daemon is contacted**. An unsupported architecture, a release that
does not publish this host's architecture, a tampered archive, a mismatched
signature, or a rolled-back index therefore fails while nothing has been loaded,
started, or written to instance state. After Docker resolves the image, the
labels it actually carries are compared against the signed compatibility record,
and every contradicted boundary is reported in one message.

The selected release is retained beside the other deployment evidence:

```text
<instance-root>/state/deployment/image-distribution.json
```

It records the release train, tag, version, manifest checksum, signing key,
asset name and checksum, resolved source URI, verification result, the platform
and immutable image ID that were installed, the source revision and tree, the
SBOM, provenance, and vulnerability-scan asset names, the per-platform component
inventory asset published for the installed architecture (absent for a release
published before inventories existed), and the exact compatibility boundaries
the release declared. An operator can correlate a running container with its
release, and name the component inventory to triage against, without network
access. The record is schema version 2; a version-1 record written by an earlier
installer has no inventory member and remains valid.

The record follows the image the instance actually runs. It is written only once
the image has been prepared and accepted, a refused install or upgrade leaves
none behind, and a rollback withdraws whatever the superseded upgrade recorded.
Its `manifestDigest` is the release's multi-architecture identity; for a release
published without a registry push that value is a computed index digest and is
not resolvable with `docker pull`.

An upgrade accepts the same options and writes the same evidence before the
operation begins. It selects the platform from the architecture recorded for
the instance's Docker daemon at installation, not from the machine running the
CLI, so a remote or cross-architecture daemon is served the archive it can run;
a release that does not publish that architecture is refused, naming what it
publishes, before anything is downloaded:

```bash
hvo-skymonitor cameraagent upgrade \
  --instance-id <uuid> \
  --channel local \
  --image-manifest /media/hvo/image-v1.5.0/image-manifest.json \
  --no-download
```

The generated CameraAgent rig and pipeline are immutable instance configuration.
The installer-virtualsky-v3 profile changes only the generated **new-install**
20-second ASI174 combined-preview display stretch (white percentile 0.9997,
asinh strength 8 instead of 0.9999 and 4); raw and combined Mono16 samples are
unchanged. Upgrading the image on an existing v2 instance does **not** replace
its rig or pipeline and therefore does not apply the new stretch to its captures.
The scene-layer producer version changes independently: new processing with the
new image draws star names at 14 source pixels on a 1936x1216 frame while
retaining the same cardinal/corner text geometry, even on an existing instance;
retained layers and previews are immutable and do not change retroactively.
To adopt both installer defaults, provision a new isolated instance through a
clean install and migrate operational state only under the supported lifecycle;
do not edit an installed profile in place or treat an image upgrade as a clean
install. These are display choices, not a physical visibility model.

`cameraagent preflight` accepts the same release selectors and resolves them the same way, so a planned upgrade
can be evaluated against persisted state first without acquiring, loading, or starting anything; see
[State Compatibility Boundary](#state-compatibility-boundary).

`--migration-backward-compatible` remains a separate operator assertion about the
candidate's state migration and is required only when the candidate declares one.
Do not add it to a routine upgrade; doing so defeats the gate it exists for.

Rollback continues to use the retained previous image identity and never
consults a release train.

### Proving the signed lifecycle in the installer campaign

`scripts/test:deployment-installer` proves the signed lifecycle against real
containers when `HVO_INSTALLER_SIGNED_RELEASE_CAMPAIGN=1` is set. The scenario
builds two release candidates with `scripts/release:cameraagent-image` from two
committed revisions in throwaway Git worktrees, so each signed manifest
describes exactly the tree it was built from, then drives one instance through
every transition the retained release record has to follow:

| Transition | Command | `image-distribution.json` |
| --- | --- | --- |
| Install from the superseded release | `cameraagent install --image-manifest <a>` | present, naming that release and the running image |
| Refused by the production trust root | the unmodified CLI, same upgrade | unchanged |
| Preflight the candidate release | `cameraagent preflight --image-manifest <b>` | unchanged; nothing is acquired or started |
| Candidate verification fails after mutation | `cameraagent upgrade --image-manifest <b>` through the campaign-only verifier fault | byte-identical; still names the restored superseded release and image |
| Resume the same upgrade to the candidate release | `cameraagent upgrade --image-manifest <b> --resume` | present, naming the new release and the new image |
| Upgrade to an operator-supplied image no signed release named | `cameraagent upgrade --image-ref <a-image>` | withdrawn (absent) |
| Upgrade back to the candidate release | `cameraagent upgrade --image-manifest <b>` | present again, naming the candidate release and image |
| Refused: signed by a key the trust root does not hold | `cameraagent upgrade --image-manifest <untrusted>` | unchanged; still names the running release |
| Refused: declares a key identity the trust root does not carry | `cameraagent upgrade --image-manifest <declared-key>` | unchanged |
| Refused: the trusted key's signature over a different release | `cameraagent upgrade --image-manifest <forged>` | unchanged |
| Refused: names an archive that is not the one it signed | `cameraagent upgrade --image-manifest <mismatched>` | unchanged |
| Rollback to the retained previous image | `cameraagent rollback` | absent |
| Refused: release contradicts the image labels | `cameraagent upgrade --image-manifest <contradicting>` | still absent |
| Refused: resuming the refused upgrade | `cameraagent upgrade --image-manifest <contradicting> --resume` | still absent; the journal is byte-identical |
| State-compatibility preflight on what the sequence left behind | `cameraagent preflight` | still absent |
| Uninstall without `--resume` after the refusal | `cameraagent uninstall` | still absent; the container is gone |

Each refusal targets its own gate. The production-trust-root refusal and the
four derived-release refusals share the acquirer's single diagnostic, because
the CLI deliberately does not surface the inner verification cause; what
separates the derived releases is that each varies exactly one trust or
integrity condition against a release the instance has already accepted, and
that each is required to leave every durable deployment record — the release
record, the instance manifest, the installation result and state, the retained
preflight report, the staged image archive, and the lifecycle journal —
byte-identical. Only the label-agreement refusal has a diagnostic of its own,
and it is the only refusal that runs after the lifecycle journal has been
opened. The campaign asserts that the journal records it as a terminal
refusal: `status` is `Failed`, `failureCode` is `lifecycle-refused`, the
`failureMessage` carries the gate's diagnostic, and `mutationStarted` is
`false`. That record blocks nothing: resuming the refused command is refused
because no incomplete operation exists, and the closing uninstall runs without
`--resume` (the `LifecycleContractTests` prove the same for a rollback and a
different upgrade). Every transition reads the running image back through
`status`, whose `status` outcome (rather than `drifted`) means the container
really carries the recorded image. Every step retains, under
`TestResults/issue-598/<run>/`, the release record when it exists and an
explicit `image-distribution.absent` marker when it does not, the instance
manifest, the installation result and state, the retained preflight report, the
lifecycle journal and its candidate diagnostics once a lifecycle operation has
created them, a state inventory with modes, deployment and Compose checksums
(the staged image archive appears there by SHA-256 rather than as a copied
file), the container log, the container's real image and health, and the exact
CLI reports the assertions read.

Every trust or integrity refusal in the scenario happens before the upgrade
mutates anything. The separate post-mutation failure runs only after the signed
candidate has passed acquisition, image-label agreement, state compatibility,
capture drain, Compose stop, backup, Compose-up, application health, and installation-identity
verification. A campaign-local Docker shim then records the real healthy,
unprivileged candidate inspection and reports an impossible privileged runtime
to the candidate-container verifier exactly once. The existing lifecycle catch
restores the prior image, byte-identical Compose files, manifest, and result,
resumes capture, and leaves `image-distribution.json` present and byte-identical
naming that restored signed release. Every recovery command uses unmodified
Docker results, and neither production code nor either signed candidate is
changed to create the fault.

The campaign proves that the retained record follows the image the instance runs
across every transition above. The record is settled as soon as an upgrade or
rollback commit is durable and again on `--resume`, so a signed upgrade whose
final resume acknowledgement is lost still records its release when completed
with `--resume` (proved by `LifecycleContractTests`, because the campaign cannot
lose a real acknowledgement on demand), and an `--image-ref` upgrade of an
instance installed from a signed release withdraws the superseded record
(transition `03d` above, against a real container).

The refusals bracket the trust decision from several sides. The release
contradicting the image labels is genuinely signed and is refused by the
label-agreement gate after acquisition and before any mutation. Four more are
refused during acquisition:

- a manifest signed by a key the trust root does not hold, and release B's
  manifest presented with the trusted key's real signature over release A. Both
  fail signature verification: a signature the trusted key cannot verify at all,
  and a signature it verifies but not over these bytes. The forged case is the
  one that isolates signature verification, because nothing else can refuse it.
  The untrusted case also declares the untrusted key's identity, so it would
  still be refused by the declared-key comparison if verification were removed.
- a manifest declaring a key identity the trust root does not carry, signed by
  the key it does. `VerifyManifest` verifies the signature before comparing
  `signing.keyId`, so this is the only way to reach that comparison — the release
  tool refuses to sign a manifest whose declared key is not the signing key, so
  the campaign produces this signature in detached mode.
- a manifest naming the other candidate's archive under this release's signed
  length and checksum: the only case that fails on the acquired bytes rather than
  on the metadata.

None of the four derived releases or the label-agreement refusal writes,
rewrites, or resurrects the retained release record. The four derived releases
must leave every durable deployment record and the lifecycle journal
byte-identical; the label-agreement refusal legitimately journals its refused
operation as terminal and must leave every other durable record byte-identical.

Each refused release is a manifest and signature in its own directory that names
the real published archives through `--asset-base-url`, so nothing copies or
links them, each published candidate still contains exactly the files its own
`SHA256SUMS` describes — which the run verifies at the end — and the installer's
refusal to read a hard-linked input still applies to every file it opens. The
retained evidence keeps each refused manifest, the exact signature it was
presented with, and the public keys that verify them, under
`refused-releases/`.

The contradicted boundary is the signed compatibility record — the campaign
changes the release's `minimumCompatibleRevision` — because the manifest's own
consistency rules already bind the release identity, the repository, the source
revision and tree, the evidence assets, and each platform's archive to one
another. The compatibility record is the one claim about the image that the
manifest cannot check against itself, which is precisely why the installation
compares it against the labels the image actually carries.

### The ephemeral key: what the campaign does and does not establish

The production signing key is a Key Vault key that only the release workflow's
federated identity may sign with. No identity available to this campaign can
sign with or export it, so a production-signed release can be produced only by a
real publication run, which this scenario excludes. The campaign therefore signs
both candidates with an ephemeral P-256 key and publishes a campaign-only CLI,
built from the same committed revision, whose embedded trust root is that
ephemeral public key. The key is generated fresh for each run into the
disposable workspace; a durable `HVO_INSTALLER_SIGNED_WORKSPACE` retains it
alongside the candidates it signed, so reruns against that workspace reuse it.
Either way it is never a production key. Nothing else is changed: no
verification step is removed, relaxed, or bypassed.

The substitution is proved to redirect trust rather than to disable it. An
unmodified CLI, published from the same archived candidate source so that it
differs from the campaign CLI only in the embedded trusted key, is run against
the same signed release and must refuse it; the campaign CLI must refuse the
same release under a key it does not hold, must refuse the trusted key's real
signature over other bytes, and must refuse a release whose archive is not the
one it signed. A campaign whose trust root had simply been switched off would
pass none of them.

What the campaign establishes: the whole signed image lifecycle — manifest and
signature verification, asset length and checksum verification, platform
selection, the agreement between the signed compatibility record and the labels
the image actually carries, automatic exact restore after a post-mutation
candidate-verification failure, the terminal journal a pre-mutation refusal
leaves behind, and the retained release record across install, upgrade,
rollback, refusal, and the closing uninstall — against real multi-architecture release
candidates, a real Docker daemon, and a real running CameraAgent.

The manifest's declared-key-identity comparison is included: reaching it needs a
signature the trust root can verify over a manifest that declares a different
key, which the campaign produces with a detached signature.

What it does not establish: that the committed production public key matches the
Key Vault private key, that the workflow's federated identity can sign, that the
signatures Key Vault produces verify against the committed trust root, that key
custody and rotation behave as documented, or anything about the registry push
and the published index. Those remain the first real publishing run's evidence,
as [release-distribution.md](release-distribution.md) records.

## Local Replay Runner

Archived replay remains in the CameraAgent process by default. Select the
optional long-lived local process boundary only during installation:

```bash
hvo-skymonitor cameraagent install \
  --replay-profile local-runner \
  <other-required-inputs>
```

`--replay-profile in-process` is the explicit form of the default. The selected
profile is part of the retained installation request and rendered Compose model;
changing it requires a new converged installation rather than an ad hoc Compose
edit. The LocalRunner topology uses the distinct `cameraagent-compose-v3`
contract; legacy and current in-process installations remain on
`cameraagent-compose-v2`. The split-host development Compose scripts remain
in-process only.

The local-runner profile starts the self-contained replay executable from the
same immutable CameraAgent image. CameraAgent still owns SQLite replay leases,
frozen input selection, graph orchestration, output publication, and stale-lease
fencing. Only one built-in recipe invocation and its declared immutable payloads
cross the owner-only Unix socket at a time. Live processing never uses the
runner.

The installer generates a 256-bit authentication key under `config/secrets`,
mounts only that secret and the shared socket directory into the runner, disables
its network, drops all capabilities, enables `no-new-privileges`, and does not
mount CameraAgent raw, archive, catalog, identity, provisioning, or SQLite state.
The protocol additionally authenticates each replay job against its execution,
graph, node, durable-attempt, lease, and deadline metadata and verifies input and
output checksums.

Runner absence, saturation, authentication/capability mismatch, heartbeat loss,
or disconnect never falls back to in-process execution after the local-runner
profile has been selected. The replay returns to pending with reason
`processing.replay-runner-unavailable` without consuming a replay attempt.
Malformed output is a retryable execution failure and consumes the normal
bounded attempt budget, but output checksums, semantic role, ordered recipe lineage,
recipe identity, and lease fencing prevent it from being committed. Already
committed nodes remain fenced and reusable. Confirm that the
`replay-runner` Compose service is running, that both services share
`/run/hvo-replay`, and that the owner-only authentication-key file exists. A
healthy executable reports protocol, recipe, architecture, and warmup evidence
without opening the service socket:

```bash
docker compose exec replay-runner \
  /app/replay-runner/HVO.SkyMonitor.CameraAgent.ReplayRunner --capabilities
```

The container health check authenticates the running socket with the same
owner-only key. Run the same probe manually with:

```bash
docker compose exec replay-runner \
  /app/replay-runner/HVO.SkyMonitor.CameraAgent.ReplayRunner --probe
```

Loopback TCP exists for explicitly secured non-Compose environments, but Unix
sockets are the deployment default. TCP rejects non-loopback endpoints and both
transports require a 32-to-4096-byte authorization key when the external profile
is active.

LocalRunner performance evidence is intentionally split by concern. The manual
`LocalReplayRunnerPerformanceTests.W1W2AndW6SizedPreviewInProcessAndLocalRunnerEvidence`
campaign isolates process-boundary overhead, durable backlog/drain, runner outage,
and live-preemption behavior using a single Preview recipe at W1, W2, and W6 frame
dimensions. It is supplemental evidence, not the canonical W6 workload. Run
`scripts/test:cameraagent-standalone-211` separately for the full 14-node
`cameraagent.standalone-w6.json` graph, production catalog, calibration and
environment prerequisites, rendered outputs, isolation, recovery, and canonical
W6 performance disposition defined in
[`performance-validation.md`](../planning/performance-validation.md).

## Persistent State

Production installation always uses `/var/lib/hvo/skymonitor` and the canonical
layout in [product-instance-layout.md](product-instance-layout.md). The
`--product-root` override is rejected unless the invocation was admitted for a
test root. Admission is decided exactly once, when the command line is parsed:
the process environment is read there and nowhere else, and only
`HVO_INSTALLER_ALLOW_TEST_ROOT=1` admits. The decision then travels on the
request itself, so a later change to the environment cannot revoke an admission
already granted or grant one already refused, and an installer configuration
file cannot reach the decision at all because the flag is never deserialized.
Tests that need a temporary root set that admission on the request they build
rather than mutating the environment of the process they share.

The installer records owner-only files beneath the UUID instance root:

```text
instance-manifest.json
application-identity.json
config/camera-module.json
config/compose/{compose.yml,instance.env}
config/secrets/*
config/owner-bootstrap/temporary-password
state/deployment/{installation-state.json,installation-result.json,state-preflight.json}
operations/cameraagent-<uuid>.owner-recovery.json
operations/state-reset-<operation-uuid>.evidence.json
operations/owner-recovery/<operation-uuid>/temporary-password
```

The manifest binds installation, instance, application, runtime UID/GID, exact
catalog, image, Docker daemon, Compose template/model, configuration, rig,
schedule, deployment-location snapshot, and installation-verification token
hash identities. Password and verification-token content is never written to
the manifest, result, phase state, Compose environment, logs, or terminal output.
Signed installs additionally retain the release train/version/tag, exact
manifest and asset length/hash, signing key ID, source and resolved public URI,
verification result/time, and provenance identity without authentication data.

The installer creates the product root through one narrow `sudo`-executed
internal preparation command when needed, then performs catalog, configuration,
Docker, Compose, and HTTP work as the invoking Docker-capable runtime user. Do
not invoke the whole installer through `sudo`.

## Bootstrap And Recovery

The first startup mounts the temporary password file only long enough for the
application to seed and authenticate the configured owner. The installer then
removes `LocalIdentity__AdminPasswordFile` authority, recreates the exact
Compose service, and requires the durable `owner-password-change-required`
state. The operator completes password replacement through CameraAgent.

If the owner loses the durable password after initial setup, recover access from
the CameraAgent host as the Docker-capable deployment runtime user, never as
root:

```bash
hvo-skymonitor cameraagent recover-owner --instance-id <uuid> --generate-password
```

Alternatively, provide an absolute owner-only mode `0400` or `0600` file with
`--password-file`. Password input is never accepted on the command line. The
command validates the installed manifest/result, runtime UID/GID, product and
instance locks, and the retained lifecycle-control credential. It contacts the
installed CameraAgent directly through `<state-root>/identity/owner.sock`, which
the existing Identity state mount exposes inside the container as
`/app/App_Data/owner.sock`; redirects and system proxies are disabled. The CLI
requires the socket directory to be mode `0700` and the socket to be a
single-link mode `0600` Unix socket owned by the exact installation runtime
UID/GID. The recovery endpoints return `404` on every TCP listener, including
loopback, bridge, published, and reverse-proxy requests. The command never sends
the lifecycle credential or temporary password to a public listener and never
edits Identity SQLite directly.

Recovery socket activation requires Linux, explicit
`LocalIdentity:AllowMissingAdminPassword=true`, removed runtime password
authority, and a configured lifecycle-control credential. Generated Compose
already mounts the Identity state directory, so an existing installation gains
the socket when upgraded to a supporting CameraAgent image without a Compose
enable flag or host-port restriction. Lifecycle stop, uninstall, and rollback
remove only a socket whose type, ownership, mode, and link count still match the
installed runtime identity. After an ungraceful stop, CameraAgent startup locks
and authenticates the owner-only parent directory, refuses a concurrent startup,
active listener, or unexpected node, and rechecks an inactive runtime-owned
socket immediately before removing it and binding again. The owner-only parent
remains the access boundary if termination occurs in the brief interval before
the new socket is restricted to mode `0600`.

Before disclosing either recovery secret, the CLI sends a random nonce and
requires the Unix-socket listener to return an operation-bound HMAC-SHA256 proof
using the retained lifecycle credential. It then obtains a five-minute Data
Protection challenge bound to the operation, configured owner, and current
security stamp. The challenge, password, and resulting bootstrap state use
bounded `application/octet-stream` messages rather than JSON. Completion
requires exactly one configured site owner, removed bootstrap password
authority, and an unchanged challenge. It resets that owner only, restores the
mandatory temporary-password gate, rotates the security stamp to revoke
existing cookies and interactive authorization, and records the operation
idempotently in existing Identity tables. Capture and durable storage continue
throughout the Identity-only transaction. An older or disabled CameraAgent
fails closed as unsupported.

If acknowledgement is interrupted or the outcome is ambiguous, rerun the exact
command with `--resume`; the same operation ID and staged password are reused.
A definitive pre-completion rejection closes that attempt and requires a fresh
command without `--resume`. Human output prints only the operation ID, resulting
state, and owner-only password-file path; `--json` omits the path. Sign in with
that temporary password, replace it immediately, then securely remove the
recovery password file. Never retain its content in shell history, logs,
tickets, or evidence.

The lifecycle-control credential is site-owner-equivalent recovery authority.
Keep its retained file and mirror owner-only, preserve its manifest hash
binding, and rotate the whole installed instance through an approved lifecycle
when compromise is suspected. Device verification material and
`device-secrets.dat` are not owner-recovery proof.

Each mutation publishes a durable phase. A failed run retains redacted state and
diagnostics. Resume only an explicitly known instance:

```bash
hvo-skymonitor cameraagent install --resume --instance-id <uuid> <same-inputs>
```

Immutable input drift fails closed before production configuration or container
mutation. A completed rerun returns the retained result when healthy; if the
container is stopped, it revalidates the catalog, image, configuration, owner,
deployment location, and Compose identities and converges the same instance back
to healthy without regenerating identity or credentials.

## State Compatibility Boundary

The CameraAgent image declares the durable state contract it reads and writes rather than a generic
compatibility promise:

```text
io.hvo.skymonitor.state-compatibility=cameraagent-state-v2
io.hvo.skymonitor.minimum-compatible-revision=70ecdd3a0d02a5288aaa6438e3a5cfc8e395545f
io.hvo.skymonitor.identity-migration=20260827053715_InitialIdentity
io.hvo.skymonitor.raw-ingress-schema=13
io.hvo.skymonitor.catalog-manifest-version=2
```

**CameraAgent state produced before `70ecdd3` is an incompatible source for a direct in-place upgrade.** Those
revisions wrote catalog manifest version 1, Identity migration `20251125021552_CreateLocalIdentity`, and
raw-ingress schema 11. The current image requires manifest version 2, migration
`20260827053715_InitialIdentity`, and schema 13. There is no supported automatic migration from
schema 12 or earlier: a populated schema-12 journal is rejected by preflight before drain, backup,
stop, or Compose mutation, and runtime startup also refuses it without changing its contents.
Upgrading such an instance requires the CameraAgent-only reset below or an equivalent explicit
state-disposition procedure. The superseded `backward-compatible` label value remains readable only so an
already installed image stays inspectable; it is never accepted as an upgrade candidate declaration.

Deployment preflight compares the declared boundaries against the persisted state before Compose starts the
container, so an incompatible instance fails once with the complete boundary list instead of through container
restart loops. Install, upgrade, and rollback all run it before any backup, drain, stop, or `docker compose`
invocation and before the container starts. The candidate image is inspected first because its
labels are the expected values being compared, and on install the catalog bundle and Compose files
are written first because the preflight reads the selected catalog and the bind sources they
establish. Install and upgrade retain the report at `state/deployment/state-preflight.json`. Run
it on demand without starting, loading, pulling, or mutating anything:

```bash
hvo-skymonitor cameraagent preflight --instance-id <uuid> --json
hvo-skymonitor cameraagent preflight --instance-id <uuid> \
  --image-ref <repository@sha256:digest>
hvo-skymonitor cameraagent preflight --instance-id <uuid> \
  --image-manifest /media/hvo/image-v1.5.0/image-manifest.json
hvo-skymonitor cameraagent preflight --instance-id <uuid> \
  --channel stable --no-download \
  --image-index https://mirror.example/indexes/image-stable-index.json \
  --image-version 1.5.0 --asset-base-url https://mirror.example/releases
```

A `--json` install or lifecycle invocation reserves standard error for its single error object, so the
rendered report is written there only in human form. Read the complete report from
`state/deployment/state-preflight.json`, or from `cameraagent preflight --json` on standard output; a
`--dry-run` deliberately retains nothing, and its error message still enumerates every blocking code.

Without a candidate the installed image's declaration is evaluated; naming one evaluates that candidate's
declaration and requires the current contract, matching an in-place upgrade. `--image-ref` names an image this
host already holds and is read from the local Docker image store. `--image-manifest`, `--image-index`, and
`--image-version` instead name a signed image release from the train above and are resolved exactly as an
upgrade resolves one: the same signature and index verification, the same rollback protection, the same
offline-archive identification, and the same platform selection, which follows the architecture recorded for the
instance's Docker daemon rather than the machine running the CLI, so the report describes exactly the platform the
upgrade acquires. `--asset-base-url`, `--channel`, and `--no-download` apply to them as they do to an
upgrade and are rejected without `--image-manifest` or `--image-index`. A local-media manifest is read from disk
whether or not `--no-download` is given, so the flag matters only for an `https://` manifest or index. A signed release and `--image-ref` are mutually
exclusive here for the same reason install and upgrade refuse the combination.

A signed-release preflight is strictly read-only. It evaluates the release's own signed compatibility record,
so it neither acquires the offline archive nor contacts Docker at all and it reports on a release this host has
not received yet. It writes nothing into the instance, the release media, or the distribution download cache,
and it never advances the signed-index rollback state an acquisition would commit. A persisted database whose
journal carries unreplayed recovery state without a wal-index is read through a private temporary copy, so
replay never touches the instance. A database a writer currently holds is instead read in place, which
registers a reader in the wal-index that already exists, exactly as the instance's own readers do: no file is
created and no durable state changes. That is preferred over copying a database under a live writer, since such
a copy can tear and report a busy instance as unreadable. Unlike install and upgrade the on-demand command retains no `state-preflight.json`. The
report names the resolved release tag beside the immutable image ID it selected; install and upgrade record the
same tag in the report they retain, which is the only release evidence a refused operation leaves behind.

A signed-release preflight also evaluates the upgrade's contract-identity gate from the signed declaration,
because every value that gate compares is in the release manifest and the instance manifest. The candidate's
component, configuration contract, and catalog contract must match this instance, and a LocalRunner instance
additionally requires the release to declare the local replay-runner contract; an in-process instance imposes no
runner requirement. Each mismatch is a blocking finding under the `contract-identity` boundary with its own code
(`contract-component`, `contract-configuration`, `contract-catalog`, `contract-replay-runner`), naming the
declared and required identities. The release's platform is selected for the instance's recorded daemon
architecture before this evaluation, so an architecture mismatch is reported as a resolution error rather than a
finding. A release built for another configuration or catalog contract, or one omitting the runner contract a
LocalRunner instance needs, therefore no longer preflights clean only to be refused after acquisition.

**A compatible signed-release preflight is still not a promise that the upgrade will proceed.** It compares
persisted state and the declared contract identities against this instance. Gates that need the image itself run
only during the upgrade: the labels the loaded image actually carries are compared against the signed record, and
a candidate declaring a state migration still requires `--migration-backward-compatible`. When either signed
identity for the selected platform (`offlineArchiveImageId` or `manifestDigest`) equals the installed
`manifest.image.imageId`, preflight remains compatible but includes the non-blocking
`candidate-image-already-active` advisory. The advisory tells the operator that no upgrade action is needed; an
attempted upgrade to that same image is still refused outright.

Rollback state is retained per operating-system user
(`$XDG_STATE_HOME/hvo/skymonitor/distribution`, else `~/.local/state/...`), so run the preflight as the same
user that will run the upgrade for its rollback protection to consult the same floor.

The command exits `0` when compatible and `1` with error code `state-incompatible` otherwise. Each finding
names its boundary code, path, observed
value, expected value, and remediation. The checked boundaries are the selected catalog manifest version and
catalog identity, the Identity migration lineage recorded in `__EFMigrationsHistory`, the raw-ingress
`PRAGMA user_version`, the ownership and mode of every writable Compose bind source, and, for a signed release,
the contract identities described above.

An in-place upgrade requires a candidate that declares `cameraagent-state-v2`. Installing an image, and
rolling back to one, are not state migrations and therefore also accept the superseded declaration. Any
boundary label a candidate omits, whichever contract it declares, is skipped rather than assumed met, and the
report records an advisory `candidate-boundaries-undeclared` finding naming exactly which labels were
omitted; an image predating the correction declares none of them. The catalog manifest version is still
compared against the version the runtime resolver enforces even when the image declares none. Rolling a state
boundary backwards is not supported: if an instance has run a newer state contract, restore it through the
reset procedure below rather than by rolling back to a pre-`70ecdd3` image.

The installer and every lifecycle mutation pre-create all writable bind sources
(`state/identity`, `state/data-protection`, `state/provisioning`, `state/raw`, `state/archive`, and
`state/replay-runner`) with the configured runtime UID/GID and mode `0700` before Compose runs. Docker would
otherwise create a missing nested bind source as `root:root` mode `0755`, and the capability-dropped container
cannot restrict it. A bind source owned by another identity fails closed with the exact path and both
identities rather than being silently adopted.

## CameraAgent State Reset

Reset deletes CameraAgent-owned local runtime state only. It never touches deployment configuration, secrets,
credentials, installation identity, shared catalogs, another instance, or a shared service. It requires an
explicit confirmation that repeats the instance UUID, and it refuses to run until the instance has completed a
preserve-by-default uninstall and its container no longer exists.

Destructive paths, all beneath `<instance-root>/state`:

```text
state/identity          # local Identity SQLite database and owner recovery socket
state/data-protection   # Data Protection key ring
state/provisioning      # device identity and device secrets
state/raw               # raw-ingress journal, frames, index, quarantine, outboxes
state/archive           # archived artifacts
state/replay-runner     # local replay runner socket directory
```

Preserved: `instance-manifest.json`, `application-identity.json`, the whole `config/` tree including
`config/secrets`, `config/owner-bootstrap`, `config/lifecycle-control`, `config/installation-verification`,
and `config/compose`, plus `<product-root>/catalogs`, `<product-root>/operations`, and instance backups.

```bash
hvo-skymonitor cameraagent uninstall --instance-id <uuid>
hvo-skymonitor cameraagent reset-state --instance-id <uuid> \
  --confirm-instance-id <uuid> --dry-run
hvo-skymonitor cameraagent reset-state --instance-id <uuid> \
  --confirm-instance-id <uuid>
hvo-skymonitor cameraagent install --instance-id <uuid> <same immutable inputs>
```

`--dry-run` records the same authenticated deletion inventory and prints the exact destructive and preserved
paths without deleting anything. Both forms write owner-only evidence to
`<product-root>/operations/state-reset-<operation-uuid>.evidence.json` containing the operation ID, request
hash, host identity, instance manifest, destructive roots, preserved paths, and the complete per-entry
ownership/mode/inode inventory captured before deletion. Deletion re-authenticates every entry's type,
ownership, mode, link count, and filesystem before unlinking it and rejects symbolic links, foreign
filesystems, and hard links.

After deletion each state directory is recreated empty with the runtime UID/GID and mode `0700`, the retained
completed installation result is withdrawn, and the retained installation phase returns to `Preflight` so the
final `install` rerun re-seeds the owner from the preserved temporary password and converges the same
instance identity back to healthy. Take an instance backup first if the deleted capture history matters; see
[product-instance-layout.md](product-instance-layout.md).

## CameraAgent Lifecycle

Run lifecycle commands as the Docker-capable deployment user, never as root.
Image and catalog transitions authenticate through the lifecycle-control credential
provisioned during installation and bound to the instance manifest. Candidate
health, installation identity, owner identity, and runtime ownership are verified
without accepting an operator password file. Image references must be immutable digests:

```bash
hvo-skymonitor status --instance-id <uuid> --json
hvo-skymonitor cameraagent upgrade --instance-id <uuid> \
  --image-ref <repository@sha256:digest> --migration-backward-compatible
hvo-skymonitor cameraagent rollback --instance-id <uuid>
hvo-skymonitor cameraagent uninstall --instance-id <uuid>
hvo-skymonitor cameraagent reinstall --instance-id <uuid>
```

`--migration-backward-compatible` is the operator's explicit acknowledgement that no transactional restore
path is being reserved. It is necessary but not sufficient: the candidate must also declare the current
`cameraagent-state-v2` contract, and the persisted state must satisfy every declared boundary described in
[State Compatibility Boundary](#state-compatibility-boundary). A rollback target installed before that label
correction is accepted as a known contract because no state migration occurs on the way back.

Each transition pins the Docker endpoint and daemon identity, validates the
rendered Compose model, records pre-mutation continuity, creates and validates a
consistent backup, and journals mutation intent before pause or stop. Candidate
Compose, manifest, and result identities commit while capture remains paused;
resume is the final idempotent action. If final acknowledgement is lost, rerun
the exact command with `--resume`. A failed candidate restores the exact prior
Compose, image, and identity records before capture resumes. Noncurrent manifests
and results are rejected before lifecycle state mutation; invalid candidate images
or rollback models are rejected before runtime mutation.

### Restore Only After an Interrupted Upgrade

Ordinary `upgrade --resume` retries the upgrade, including backup and candidate
startup. **Do not use it merely to restore service after a failed backup.** For
an operator-image upgrade whose exact original runtime is already healthy, the
bounded recovery-only form is:

```bash
hvo-skymonitor cameraagent upgrade \
  --instance-id <original-instance-uuid> \
  --image-ref <original-request-candidate-reference> \
  <same-original-request-options> \
  --resume --restore-only --operation-id <interrupted-operation-uuid>
```

Retain the original image reference, archive checksum, `--no-download`, and
compatibility acknowledgement exactly as supplied to that upgrade. The command
checks the original request hash and operation UUID; it does not acquire or
inspect the candidate image. This first recovery slice supports operator-image
upgrades only, not signed-release selectors, rollback, or catalog operations.

The original manifest/result and operation snapshots must agree exactly, including
the prior rollback history. Owner-only bounded snapshot reads, canonical configuration
hash, rendered original Compose hash, daemon identity, root ownership/inode checks,
container image/user/mount/port/security checks, protected schema-1 bound application
identity, original image schema boundaries and candidate request/platform correlation,
protected installation identity/owner verification, and both retained tokens remain
required. Admission must match the recorded paused version and capture sequence.
Changed state, candidate-running, absent/unhealthy original runtime, foreign admission,
expired/rejected credentials, and committed or partially committed candidates fail
closed. No backup archive, including a leftover partial archive, authorizes recovery.

This form never pauses capture, stops/recreates/restarts a container, writes Compose,
changes identity, or resets state. An approved Docker runtime memory override remains
untouched; it is not a change to the authenticated Compose file. It does not provide a
cold-start or candidate-to-old-image restoration procedure.

Before resuming admission the journal retains a command UUID. Retries reuse that
UUID and the original pause version, so a lost acknowledgement cannot create a new
command or override a later operator pause. The authenticated executed receipt is
validated separately from current admission: if a lost acknowledgement was followed
by an operator pause, replay proves the earlier Running result while leaving that
newer pause intact, including after restart. This settles recovery instead of leaving
an interrupted operation permanently blocking further lifecycle work. Normal success reports
`restored-previous-healthy-admission-resumed`; the original upgrade remains terminal
`Failed` / `Restored`, with `mutationStarted=false` and the original failure retained.
Older interrupted journals lacking a failure detail explicitly record that it was
not retained, rather than inventing a cause. Superseded success reports
`restored-previous-healthy-resume-superseded-current-admission-preserved`; the journal
retains the executed resume receipt separately from the current post-recovery boundary.
Both are validated on read, including nonnegative counters, initialization, versions,
capture sequence and receipt timing. The identical complete invariant is checked
before terminal publication against one captured post-resume boundary. Inconsistent
timestamps (including a future receipt or a backward clock) leave the operation
`Restoring` with its mutation flag and command UUID retained for a later valid replay;
they never publish terminal success. Repeating a successful restore-only request
verifies the current result without another resume command. It never reports the
upgrade completed, and ordinary `--resume` cannot restart a settled recovery.

Issue [#1044](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1044) remains
open for size-aware backup deadlines, capacity, progress, cancellation/partial cleanup,
and resource-qualified cold startup. Do not manually edit the lifecycle journal or
reduce an approved memory override to work around those remaining limits.

The protected installation-verification GET used by both the pre-mutation owner
state read and full installation identity verification has one finite **120-second
request deadline**, including response-body receipt. Each call makes one request;
this is not an unlimited retry or a change to the separate health/startup, login,
or drain limits. An elapsed request deadline reports
`CameraAgent installation verification request timed out.` without transport
details or credentials. Caller cancellation remains cancellation, not a timeout.
Before mutation, a timeout follows the existing refusal policy without pause,
stop, backup, or image replacement; caller cancellation leaves an unmutated
operation resumable. Do not manually repair the journal or reset the instance.
Issue [#1041](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1041) records
an old-image response of about 35 seconds, beyond the former 15-second client
limit; this CLI compatibility allowance does not fix or attribute the endpoint's
underlying repeated work.

Capture-admission initialization is a CameraAgent startup responsibility that
runs after configuration initialization and before the processing and capture
workers; it does not depend on the camera capture worker reaching its loop. The
lifecycle client treats uninitialized `Initializing` or `Unavailable` snapshots
as transient startup observations. An authenticated pause or resume request made
before that initialization completes returns `503 Service Unavailable` with a
one-second `Retry-After` header and does not try to initialize capture admission
on the request thread. The installer retries that response, `408`, `429`, and
non-`500` 5xx responses with one idempotent command ID inside the existing drain
deadline, honoring `Retry-After` when present. A `500` or a non-transient status
remains terminal. Once capture admission is initialized, a
`Running` or terminal `Unavailable` snapshot fails a drain confirmation after
the first state read instead of consuming the full drain deadline.

Uninstall removes only the selected Compose runtime and preserves config,
secrets, state, evidence, rollback identities, and shared catalogs. Purge is a
separate operation and requires `--confirm-instance-id <same-uuid>` after
uninstall. Purge quarantines the inode-authenticated instance tree and resumes
deletion from that tombstone after interruption; it never republishes a partially
deleted tree.

Catalog versions are explicit per instance:

```bash
hvo-skymonitor catalog install --catalog-bundle /owner-private/catalog.bundle
hvo-skymonitor catalog select --instance-id <uuid> --catalog-version <version>
hvo-skymonitor catalog rollback --instance-id <uuid>
hvo-skymonitor catalog gc --catalog-version <version>
```

Mutating garbage collection requires one explicit version. It fails closed on
unknown instance roots, malformed selection pointers, active Docker mounts,
manifests, backups, operations, rollback slots, or historical reconstruction
references. Interrupted deletion continues from an authenticated tombstone and
never restores a partial immutable catalog. Resume requires the same explicit
version and `--resume`; request hash, operation ID, host, daemon, root inode, and
the original tree inventory must still match. Missing original entries are
accepted after partial deletion, while additions and replacements fail closed.

LogicHost lifecycle, remote orchestration, and physical-camera discovery remain
future lifecycle scope.

## Harness Inputs For A Published Image

Both container harnesses can exercise a published image instead of building one,
so a signed release can be proved against the bytes an operator receives:

```bash
# Install, health, replay-runner health, stop-and-reinstall idempotence, preflight,
# bind-source ownership and mode, uninstall, and the explicit reset, against a
# published archive rather than a locally built image. The authenticated owner
# recovery, upgrade, and rollback contract belongs to the baseline-revision run
# and is not exercised here.
case "$(uname -m)" in
  x86_64) release_architecture=amd64 ;;
  aarch64|arm64) release_architecture=arm64 ;;
  *) printf 'Unsupported machine architecture: %s\n' "$(uname -m)" >&2; exit 2 ;;
esac
HVO_PRODUCTION_CATALOG_BUNDLE=<bundle> \
HVO_INSTALLER_RELEASE_ARCHIVE=<candidate>/cameraagent-image-v<version>-linux-${release_architecture}.tar \
  ./scripts/test:deployment-installer

# Two-agent standalone smoke against a published image. Diagnostic mode only:
# this run records the local worktree revision, so it cannot produce citable
# evidence for an image built elsewhere.
HVO_CAMERAAGENT_SMOKE_IMAGE=<tag, repository digest, or image ID> \
  ./scripts/test:cameraagent-dual-standalone-smoke
```

Neither variable changes anything when unset; both harnesses build their own
image as before. `HVO_INSTALLER_BASELINE_REVISION` takes precedence, because a
baseline upgrade contract needs two images built from two revisions.

The signed-release scenario is a separate opt-in section of the same campaign
and adds its own instance rather than replacing the operator-supplied image
path:

```bash
HVO_PRODUCTION_CATALOG_BUNDLE=<bundle> \
HVO_INSTALLER_SIGNED_RELEASE_CAMPAIGN=1 \
  ./scripts/test:deployment-installer
```

| Variable | Meaning |
| --- | --- |
| `HVO_INSTALLER_SIGNED_RELEASE_CAMPAIGN` | `1` runs the signed install/upgrade/post-mutation automatic-restore/rollback/refusal scenario |
| `HVO_INSTALLER_SIGNED_BASE_REVISION` | Revision of the superseded release. Default `HEAD~1` |
| `HVO_INSTALLER_SIGNED_CANDIDATE_REVISION` | Revision of the upgrade candidate, which also builds the campaign CLI. Default `HEAD`. The base must be a distinct ancestor of it, and both must own the release train |
| `HVO_INSTALLER_SIGNED_ARM64_BUILDER` | `linux/arm64` builder for the release candidates. Falls back to `HVO_RELEASE_ARM64_BUILDER`, then to `hvo-edge-01-arm64` |
| `HVO_INSTALLER_SIGNED_VERSION_A` / `_B` | Candidate versions. Default `0.0.0-598a` and `0.0.0-598b` |
| `HVO_INSTALLER_SIGNED_WORKSPACE` | Durable owner-private directory for the signing key and two candidates, created if absent and set to mode `0700`. A retained exact-candidate workspace is cryptographically reverified and reused without recopying or rebuilding; leaving it unset creates a fresh disposable workspace |
| `HVO_INSTALLER_SIGNED_EVIDENCE_ROOT` | Retained evidence directory. Default `TestResults/issue-598/<timestamp>` |

The harness derives its native platform once from `uname -m`: `x86_64` selects
`linux/amd64` and the `linux-x64` deployment CLI, while `aarch64` or `arm64`
selects `linux/arm64` and the `linux-arm64` CLI. Any other architecture is
refused. The Docker server must be local Linux of the same normalized
architecture, and every archive loaded by the harness must report that native
platform. An emulated image or an ARM host driving an x64 Docker server is not
native qualification evidence.

Every one of those preconditions — a clean worktree, `openssl`, two distinct
ancestor revisions that both own the release train, and two distinct versions —
is checked before any scenario runs, so a misconfigured invocation fails in
seconds rather than after the rest of the campaign.

The scenario requires a clean worktree because its candidates and CLI are bound
to committed revisions. On first use it builds two multi-architecture candidates
and scans four archives; later exact-candidate runs authenticate and reuse the
retained packages. A complete first run that also exercises
`HVO_INSTALLER_BASELINE_REVISION` took 35 minutes on an amd64 host with a native
`linux/arm64` builder over the network, of which about 25 minutes were the two
candidate builds. Hold the shared Docker window for the whole run. It never
pushes to a registry, and CI never sets
`HVO_INSTALLER_SIGNED_RELEASE_CAMPAIGN`, so this scenario is an operator-run gate
rather than a CI-gated one.

### Reusing signed candidates for native ARM64 qualification

Candidate creation and native execution can use different hosts without
weakening identity. First run the corrected campaign on an x64 host with an
external, owner-private `HVO_INSTALLER_SIGNED_WORKSPACE`. That run builds and
verifies both multi-architecture candidates. Copy the complete workspace — the
two release directories, campaign signing key and public key, and untrusted test
key pair — to an owner-private directory on the ARM64 host. Do not copy only the
ARM64 archives: manifest verification, refusal derivations, and final
`SHA256SUMS` checks require the complete candidates.

On the ARM64 host, use a clean checkout that contains the same two exact Git
objects and set the same base/candidate revisions and versions:

```bash
umask 077
chmod 700 <external-signed-workspace>
chmod 600 <external-signed-workspace>/*.pem

HVO_PRODUCTION_CATALOG_BUNDLE=<exact-production-bundle> \
HVO_INSTALLER_SIGNED_RELEASE_CAMPAIGN=1 \
HVO_INSTALLER_SIGNED_BASE_REVISION=<release-a-sha> \
HVO_INSTALLER_SIGNED_CANDIDATE_REVISION=<release-b-sha> \
HVO_INSTALLER_SIGNED_VERSION_A=<version-a> \
HVO_INSTALLER_SIGNED_VERSION_B=<version-b> \
HVO_INSTALLER_SIGNED_WORKSPACE=<external-signed-workspace> \
HVO_INSTALLER_SIGNED_EVIDENCE_ROOT=<external-evidence-root> \
  ./scripts/test:deployment-installer
```

Before reuse, the production verifier authenticates each retained signed manifest,
every declared asset, and the independently signed checksum list, then binds the
release and image revision, tree, version, and tag to the requested Git objects.
The complete verification repeats after the lifecycle. A campaign retains 16
transition directories: `01`,
`02`, `03a` through `03e`, `04` through `07`, `08`, `09`, `09b`, `10`, and
`11`. Every applicable `image-distribution.json` must name `linux/arm64`, the
ARM64 archive and component inventory, and the expected immutable A/B identity.
Retain `summary.txt`, the manifests, signed checksums, refusal artifacts,
container evidence, host and Docker inventories, and before/after cleanup
inventories. Keep the owner-private exact-candidate cache through review and any
bounded rerun so the 1.3 GB package need not be recopied. For exact-candidate
reuse, keep the ephemeral `signing.pem` at mode `600` in that owner-private
workspace for as long as the cached candidates remain reusable; it is never a
production key. To retire the private keys instead, securely delete them and
delete the workspace's `release-*` directories (or the whole workspace) before
the next campaign. A later run generates a new key and correctly refuses retained
candidates signed by the retired key rather than rebuilding them. Immutable public
candidates and public-key evidence may be retained separately as evidence, not as
a reusable workspace after key retirement. Never use a production CameraAgent
host for this campaign.

### First published ARM64 release smoke

The first real image release, and each materially changed release process, must
run the published archive path on a qualified native ARM64 host. This is not a
release-production command: it downloads immutable public assets after the
release exists, verifies them with the production trust root, and exercises the
archive an operator receives.

```bash
set -euo pipefail
umask 077
release_tag=image-v<version>
release_root="$(mktemp -d /srv/hvo/image-release-smoke.XXXXXX)"
evidence_root=/srv/hvo/evidence/<issue-or-release>/<timestamp>
mkdir -m 700 "$evidence_root"

test "$(gh release view "$release_tag" --repo RoySalisbury/HVO.SkyMonitor \
  --json isDraft --jq .isDraft)" = false
gh release view "$release_tag" --repo RoySalisbury/HVO.SkyMonitor \
  --json tagName,targetCommitish,url >"$evidence_root/release.json"
gh release download "$release_tag" --repo RoySalisbury/HVO.SkyMonitor \
  --dir "$release_root"

dotnet restore tools/HVO.SkyMonitor.Deployment.ReleaseTool/HVO.SkyMonitor.Deployment.ReleaseTool.csproj
dotnet build tools/HVO.SkyMonitor.Deployment.ReleaseTool/HVO.SkyMonitor.Deployment.ReleaseTool.csproj \
  --no-restore --configuration Release -warnaserror
release_tool=(dotnet run --project tools/HVO.SkyMonitor.Deployment.ReleaseTool/HVO.SkyMonitor.Deployment.ReleaseTool.csproj \
  --no-build --configuration Release --)
"${release_tool[@]}" verify \
  --manifest "$release_root/image-manifest.json" \
  --signature "$release_root/image-manifest.json.sig" \
  --asset-root "$release_root"
"${release_tool[@]}" verify-signature \
  --input "$release_root/SHA256SUMS" \
  --signature "$release_root/SHA256SUMS.sig"
(cd "$release_root" && sha256sum --check SHA256SUMS)

test "$(jq -er '[.images[0].platforms[] | select(.operatingSystem == "linux" and .architecture == "arm64")] | length' \
  "$release_root/image-manifest.json")" = 1
archive_name="$(jq -er '.images[0].platforms[] | select(.operatingSystem == "linux" and .architecture == "arm64") | .offlineArchiveAsset' \
  "$release_root/image-manifest.json")"
test "$archive_name" = "cameraagent-image-v<version>-linux-arm64.tar"
test -f "$release_root/$archive_name"

{
  uname -a
  docker info --format 'server={{.ServerVersion}} os={{.OSType}} architecture={{.Architecture}} id={{.ID}} root={{.DockerRootDir}}'
  jq -er '.images[0].sourceRevision' "$release_root/image-manifest.json"
  sha256sum "$release_root/$archive_name"
} >"$evidence_root/host-release-inventory.txt"

HVO_PRODUCTION_CATALOG_BUNDLE=<exact-production-bundle> \
HVO_INSTALLER_RELEASE_ARCHIVE="$release_root/$archive_name" \
  ./scripts/test:deployment-installer 2>&1 | tee "$evidence_root/deployment-installer.log"
```

Require an `aarch64`/`arm64` host, a local Linux ARM64 Docker daemon on qualified
non-production storage, exit zero, a healthy unprivileged CameraAgent with
capture advancing, and clean before/after container, network, image, process,
and worktree inventories. Record the public release URL/tag, manifest source
revision, archive checksum, catalog identity, host/daemon/storage inventory, and
cleanup result. Until this post-release run exists, native signed-campaign
qualification does not by itself prove a production-published ARM64 archive.

## Build Evidence

Build the two supported self-contained binaries with:

```bash
dotnet publish src/HVO.SkyMonitor.Deployment.Cli/HVO.SkyMonitor.Deployment.Cli.csproj \
  --configuration Release --runtime linux-x64
dotnet publish src/HVO.SkyMonitor.Deployment.Cli/HVO.SkyMonitor.Deployment.Cli.csproj \
  --configuration Release --runtime linux-arm64
dotnet publish src/HVO.SkyMonitor.CameraAgent.ReplayRunner/HVO.SkyMonitor.CameraAgent.ReplayRunner.csproj \
  --configuration Release --runtime linux-x64
dotnet publish src/HVO.SkyMonitor.CameraAgent.ReplayRunner/HVO.SkyMonitor.CameraAgent.ReplayRunner.csproj \
  --configuration Release --runtime linux-arm64
```

Run the x64 output with `--capabilities`; require protocol version `1` and
completed runtime/native-library warmup. On an ARM64 builder, the existing
`scripts/test:cameraagent-arm64-ci` gate performs the equivalent native publish,
capability, image, and constrained-container checks.

The campaign additionally proves the state preflight, the operator-confirmed CameraAgent-only reset, and the
reset-then-reinstall convergence against a real image and a real product root.

Run the disposable installer contract against the current image with
`./scripts/test:deployment-installer`. To validate an owner-recovery image
transition, use a clean committed worktree with an ancestor pre-support commit
available locally:

```bash
HVO_INSTALLER_BASELINE_REVISION=<pre-support-commit> \
  ./scripts/test:deployment-installer
```

The extended path builds the baseline image and candidate image/CLI from exact
Git archives, completes the initial password replacement to establish an
owner-ready baseline, verifies a completed installer rerun preserves that state,
proves unsupported recovery retains resumable state, performs an image-only
upgrade, authenticates the mounted owner-only socket, and resumes the same
recovery operation without restarting the CameraAgent process. It rejects the
prior durable password and authenticated session, verifies the
replacement-password gate, hard-restarts and replays recovery idempotently,
verifies capture control remains running, and rolls back while preserving
installation, configuration, catalog, and recovered Identity state. The exact
production catalog bundle remains required through
`HVO_PRODUCTION_CATALOG_BUNDLE`.

The `Signed Distribution Release` workflow publishes independent installer and
catalog tags, signed manifests and indexes, checksums, SBOMs, provenance,
licenses, and attribution. The optional bootstrap script downloads and verifies
only the selected self-contained CLI archive:

```bash
./scripts/install-hvo-skymonitor.sh 1.0.0 /opt/hvo-installer-1.0.0
```

Resolve a signed installer-index default or exact indexed version with:

```bash
./scripts/install-hvo-skymonitor.sh \
  --index https://github.com/RoySalisbury/HVO.SkyMonitor/releases/download/installer-index-3/installer-release-index.json \
  --version 1.0.0 \
  /opt/hvo-installer-1.0.0
```

The optional third direct-mode argument and `--asset-base` support HTTPS mirrors
or absolute local release directories. `HVO_INSTALLER_NO_DOWNLOAD=1` permits
only local or cached bytes. `HVO_INSTALLER_CACHE` selects an owner-only bounded
2 GiB cache; mirror redirects are refused and GitHub redirects are restricted
to approved release CDN hosts. The script retains the signed manifest, signature,
and sanitized `installer-distribution.txt` beside the verified executable; it
does not download or execute a helper verifier.
