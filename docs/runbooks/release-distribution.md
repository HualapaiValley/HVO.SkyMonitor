# Signed Distribution Releases

## Boundary And Identity

GitHub Releases are the initial source of truth for self-contained installer
archives, production catalog bundles, signed manifests and indexes, checksums,
SBOMs, provenance, licenses, and attribution. GHCR remains the source for
CameraAgent and LogicHost images, which are selected only by immutable digest.
The CameraAgent image train publishes the multi-architecture image itself: the
signed release names the immutable registry digest and carries a loadable OCI
archive per supported architecture, so an air-gapped installation never needs
registry access. Installer, catalog, and image trains have independent versions,
tags, index sequences, retention, and rollback histories:

```text
installer-v<version>
catalog-<package-version>
image-v<version>
installer-index-<sequence>
catalog-index-<sequence>
image-index-<sequence>
```

`scripts/ci:release-tag` owns this naming. The release workflow, the release
script, and this runbook resolve tags and manifest file names through it rather
than repeating the patterns.

Published tags and asset names are immutable identities. Never use `latest`, a
branch/raw URL, Git LFS, or a mutable container tag. Never delete an asset named
by a supported installation, backup, rollback record, signed index, or
historical reconstruction policy.

## Trust Root

Metadata is signed as exact UTF-8 bytes with ECDSA P-256/SHA-256 and fixed
64-byte IEEE P1363 signatures. The production public-key identity is:

```text
p256-sha256:64aa88e5fd4750839ac5f30fdd4e32c2652eaff9994173ae4b47c799ac215aeb
```

The private key is the pinned version
`a86dff95a75848c1ae013e841e704052` of
`hvo-skymonitor-release-signing-p256` in `hvo-central-kv`. GitHub obtains a
short-lived Azure token through environment-scoped OIDC. The principal can sign
with this key but cannot create, delete, rotate, export, or administer keys.
Owner-only recovery files and the encrypted Key Vault backup are outside Git.

Key rotation requires an installer release authenticated by the current key
that introduces the approved successor trust root before metadata requires the
successor. A suspected compromise pauses publication, disables the federated
credential and key version, preserves all release evidence, and starts a
documented trust-root replacement incident. Do not silently replace the
committed public key.

## Publication

Dispatch `.github/workflows/release.yml` from `main` with the train, exact
version, exact 40-character main commit, next index sequence, and `PUBLISH`
confirmation. The source commit must already have a completed exact-SHA
main-push CI workflow whose `Required CI` job succeeded.

The workflow:

1. Builds and tests without signing or release-write permission.
2. Produces deterministic x64/ARM64 installer archives or the exact production
   catalog through the existing pinned catalog path.
3. Uses the `production-release` environment and Azure OIDC to sign the
   manifest, checksum list, and next append-only index snapshot.
4. Immediately verifies every Key Vault signature against the embedded key.
5. Uses a separate release-write job to refuse tag/asset collisions, upload
   drafts once, and re-download and byte-compare every draft asset.
6. Publishes and anonymously verifies the package through versioned public URLs,
   including signature, checksum, architecture, and catalog-internal validation.
7. Publishes the signed default-bearing index only after package verification,
   then anonymously verifies the index signature and schema.

An interrupted retry may continue only an existing draft targeting the exact
source SHA whose complete asset set is byte-identical. A different byte under an
existing tag or asset name is an identity collision and must not be overwritten.
Never use `--clobber`. A published partial outcome remains immutable; diagnose
and complete only the missing independent index publication rather than
replacing release bytes.

The environment currently cannot require reviewers or a wait timer because of
the repository billing plan. Manual dispatch, protected-main/green-head checks,
least privilege, exact key-version pinning, and split signing/publication jobs
are mandatory compensating controls.

## Local Verification

Verify a downloaded release directory with the same implementation used by
publication and acquisition:

```bash
dotnet run --project tools/HVO.SkyMonitor.Deployment.ReleaseTool \
  -- verify \
  --manifest /srv/hvo/release/catalog-manifest.json \
  --signature /srv/hvo/release/catalog-manifest.json.sig \
  --asset-root /srv/hvo/release
```

Catalog verification checks the signed outer archive and then the exact inner
manifest, package/catalog/schema/preprocessing identities, database
length/checksum/row count, license, attribution, and topology contract. The
bootstrap script verifies the signed installer manifest and selected
architecture archive before extracting the CLI.

## Mirrors And Cache

A mirror must preserve exact metadata and asset bytes. Configure an immutable
signed index or manifest plus `--asset-base-url`; mirror transport adds no trust.
The installer records original and resolved URIs but never credentials. HTTPS
scheme, redirects, timeout, retry, size, disk, cache, resume, and cancellation
bounds apply equally to GitHub and mirrors. Cache hits are always reverified,
and retained signed-index sequence/hash state rejects rollback.

Do not make an observatory MinIO deployment a bootstrap dependency. Future
S3/MinIO/CDN mirrors remain interchangeable only because release identity is
location-independent.

## CameraAgent Image Train

The image train publishes one signed release per CameraAgent image:

```text
image-manifest.json                              signed release manifest
image-manifest.json.sig                          detached P-256 signature
cameraagent-image-v<version>-linux-amd64.tar     loadable OCI archive
cameraagent-image-v<version>-linux-arm64.tar     loadable OCI archive
image-sbom.spdx.json                             SPDX 2.3 file manifest of the published archives
image-components-linux-amd64.spdx.json           SPDX 2.3 component inventory of the amd64 image
image-components-linux-arm64.spdx.json           SPDX 2.3 component inventory of the arm64 image
image-provenance.json                            source, Dockerfile, platform, and label provenance
image-vulnerability-scan.json                    scanner identity, coverage, and severity summary
THIRD-PARTY-NOTICES.md                           notices
SHA256SUMS + SHA256SUMS.sig                      checksum list and signature
```

The signed manifest records the repository, the multi-architecture manifest
digest, and, for each published platform, its OCI manifest digest, its offline
archive asset, and the immutable image ID that archive loads. It also restates
the image's compatibility labels — state contract, minimum compatible revision,
identity migration, raw-ingress schema, catalog manifest version, configuration
contract, catalog contract, and replay-runner contract. An installation compares
those signed values against the labels the image actually carries and refuses to
proceed on any disagreement, so the release metadata is a claim that the running
bytes must satisfy rather than a description that is trusted on its own.

Every platform identity written into the manifest is derived from the archive
bytes: the release tool parses the OCI layout, verifies that each metadata blob
hashes to the name it is stored under, and reads the platform, digests, and
labels out of the image configuration. It never records a caller-supplied
assertion about the image, and `hvo-release verify` re-derives the same facts
from the published archives.

### Component inventory

`image-sbom.spdx.json` remains a file-level manifest: it names the published
archives and their SHA-256 values. The component-level answer to "does this image
contain package X at version Y" comes from the per-platform inventories, one for
each published architecture.

Each inventory is an SPDX 2.3 document listing the operating-system packages and
.NET libraries in that architecture's image. It is rendered from the **same**
scan pass that produced `image-vulnerability-scan.json`: the scanner's JSON
report already records every installed package rather than only the vulnerable
ones, so `trivy convert --format spdx-json` re-renders that one pass instead of
unpacking and scanning the image again. The scanner also records the image it
scanned in the inventory's container package annotations, so the release tool
binds each inventory to the image ID it derived from that platform's archive
bytes, refuses an inventory that names another image or carries no usable
component list, and re-derives the same binding during `hvo-release verify`.
An inventory built for one architecture therefore cannot be signed or verified as
another's.

A published image release must carry an inventory for every architecture it
ships. Only a version ending in `-dryrun`, which the tool never lets reach
publication, may omit them. Omission is what the unscanned dry run
(`--scanner none --unscanned-dry-run`) produces, because no scan runs and there
is nothing to render an inventory from; that candidate publishes the
release-manifest version 1 shape. A `-dryrun` version scanned with Trivy still
carries inventories and a version 2 manifest.

The file-level `image-sbom.spdx.json` describes the two published archives, and
now says so precisely: it declares `filesAnalyzed` with the package verification
code computed from exactly those two files. It is not an inventory of every asset
in the release, and it is not the component inventory.

### Release-manifest versions

The signed release manifest carries a schema version:

| Version | Shape |
| --- | --- |
| `1` | The shape published before component inventories existed. Exactly one `Sbom` artifact and no per-platform inventory. |
| `2` | An image release that additionally publishes one `ComponentSbom` artifact per platform, named by the platform it describes. |

The addition is additive, and `DistributionVerifier` accepts both. A version 1
manifest stays verifiable unchanged and must declare no inventory; a version 2
manifest must be an image release whose every platform names an inventory whose
own declared operating system and architecture match that platform. Only an image
release may declare version 2. The exactly-one-`Sbom` rule is unchanged, so the
installer and catalog trains are unaffected.

**Publish a version-2-capable installer release before the first version-2 image
release.** The verifier ships inside the installer, so an installation only
understands the manifest versions its own installer implements. An installer
built before a version existed rejects every release that declares it, and
refuses before it downloads anything. This is the same ordering obligation the
trust root already carries — an installer release that introduces the successor
key must precede any metadata signed with it — and it applies to every future
manifest version, not only to version 2. A release the installation cannot
understand reports the supported range and says to upgrade the installer, so the
failure names its own remedy rather than reading as a corrupt release.

### Architecture qualification

The release publishes `linux/amd64` and `linux/arm64`, and both archives are
built, identity-derived, scanned, and signed. Only the architecture the build
host can execute is smoke-tested; the other architecture's runtime evidence
belongs to a host of that architecture.

The `open(2)` flag defect that blocked `linux/arm64`
([#603](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/603)) is fixed:
the CameraAgent runtime and the deployment CLI select `O_DIRECTORY` and
`O_NOFOLLOW` per processor architecture, and the SQLite catalog selects
`O_NOFOLLOW` the same way (the arm and powerpc ABIs override the asm-generic
values that x86-64 uses). The advisory native arm64 workflow
(`.github/workflows/cameraagent-arm64.yml`) runs the CameraAgent, acceptance,
catalog, and deployment CLI Unit suites on aarch64 and smoke-tests a natively
built image there. The signed-release installer campaign
([#598](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/598)) proves the
signed lifecycle on `linux/amd64`: both architectures of both candidates are
built, identity-derived, scanned, and signed, and the amd64 archive is the one that an
installation consumes and runs. The native `linux/arm64` qualification under
[#651](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/651) runs that same
two-candidate signed install, upgrade, rollback, refusal, and uninstall campaign
on aarch64. It verifies the complete published-format candidate packages before
reuse and after the lifecycle, and proves that both native release archives run.
This qualifies the arm64 runtime and installer path end to end. The campaign uses
locally signed candidate packages because the production signing key is unavailable
outside the release workflow; an arm64 archive downloaded from an actual production
GitHub release has not yet been smoke-tested. Final release-readiness evidence must
retain that publication/download caveat until the production release smoke closes it.

A published image release must carry a vulnerability scan. The release tool
refuses a candidate whose scan report does not name a supported scanner and scan
time, cover exactly the published image IDs, record every severity count, and
record zero critical findings. Lower severities are recorded as evidence rather
than gated, because a maintained base image routinely carries them. An unscanned
candidate is accepted only for a version ending in `-dryrun`, which the tool
itself enforces, so a publishable version can never carry one.

### Building a candidate

```bash
HVO_RELEASE_ARM64_BUILDER=<arm64-capable buildx builder> \
  ./scripts/release:cameraagent-image --version <version> --output <candidate>
```

The script builds each platform with `docker buildx --output type=docker`,
derives the platform identities from the archive bytes, loads and smoke-tests the
architecture the build host can execute, scans both archives with a Trivy
container pinned by tag and digest, renders each platform's component inventory
from that same scan, and assembles the candidate. `--sign-key <pem>` signs and
re-verifies the candidate with a local key for rehearsal; a publishable release is
signed only by the production key through the workflow below.

`--sign-key` is also what the signed-release installer campaign uses. That
campaign builds two candidates from two committed revisions, signs both with one
ephemeral key, and publishes a campaign-only deployment CLI, built from the same
committed revision, whose embedded trust root is that ephemeral public key. It
then installs from the first candidate, forces candidate verification to fail
after the second candidate is running, proves the lifecycle automatically
restores the first image, Compose model, capture, and byte-identical retained
release record, resumes the same upgrade to the second candidate, rolls back,
and refuses five releases it must not accept: one signed by a key the trust root does
not hold, one declaring a key identity it does not carry, one carrying the
trusted key's real signature over a different release, one naming an archive that
is not the one it signed, and one whose signed compatibility record contradicts
the image labels. It asserts the retained
`image-distribution.json` after each transition. Nothing else about verification
is changed, and the substitution is proved to redirect trust rather than remove
it: the unmodified product CLI must refuse the same release, and the campaign CLI
must refuse four derived releases — the same release re-signed by a second key,
a manifest declaring a key identity the trust root does not carry, the trusted
key's real signature over a different release, and a release naming an archive
it did not sign.

The ephemeral key therefore establishes the lifecycle, verification,
post-mutation automatic-restore, and compatibility-agreement behaviour of the
signed image train against real containers. The post-mutation fault is injected
only into one campaign-local Docker inspection response after a healthy candidate
is running; production code, release bytes, and every recovery response remain
unchanged. It establishes nothing about the production key itself — that the
committed public key matches the Key Vault private key, that the workflow
identity can sign with it, that a Key Vault signature verifies against the
committed trust root, or that custody and rotation behave as described above.
Only a real publishing run can establish those, and no identity available to the
campaign can sign with or export the production key to substitute for one. See
[deployment-installer.md](deployment-installer.md) for the campaign's transition
table and evidence layout.

The build host needs a buildx builder for each published architecture that
supports the docker exporter. A remote host registered with the plain `docker`
driver cannot produce one, and a host registered as a Docker context is selected
by context rather than by `--builder`; the script resolves the difference, but a
`docker-container` driver builder is required either way:

```bash
docker buildx create --name hvo-arm64 --driver docker-container --platform linux/arm64 <context>
docker buildx inspect --bootstrap hvo-arm64
```

Prefer a builder whose Docker data root is on an NVMe device. The published
image is large, and on a host whose data root is an SD card the runtime layer
copy alone takes tens of minutes.

### Publishing

Run the `Signed Distribution Release` workflow with `train: image`. The workflow
sets up QEMU and Buildx, signs in to the registry with the workflow token, and
runs the same script with `--push`. The push happens **after** the smoke test and
the scan gate, adopts rather than overwrites a version the registry already
publishes, and then requires the published index to name exactly the platform
manifests that were examined, so the signed multi-architecture digest is the
registry's digest for the same bytes the release inspected.

That last check has never been executed: no run has reached the push, and it
depends on the registry publication carrying the same OCI media types as the
locally exported archives. The exporter is pinned to `oci-mediatypes=true` for
that reason, but **treat the index agreement as unproven and check it first on
the first real release** — rehearse against a throwaway repository before
publishing a version you intend to keep. Signing, index creation, immutable
publication, and anonymous public re-verification then follow the same path the
installer and catalog trains use.

### Registry attestations

The push carries `--provenance mode=max --sbom true`, so the published image also
answers for itself in the registry: alongside each platform image manifest the
index carries an attestation manifest whose layers are two in-toto statements, an
SPDX SBOM (`https://spdx.dev/Document`) and SLSA provenance
(`https://slsa.dev/provenance/v1`). A consumer who resolves the image by digest
can read them with `docker buildx imagetools inspect` or any in-toto client,
without the signed GitHub release.

Attestations do not change the platform image manifests. That was verified
locally rather than assumed: building the same Dockerfile with
`--output type=docker --provenance false` (what the release script exports and
examines), with `--output type=oci,oci-mediatypes=true --provenance false`, and
with `--output type=oci,oci-mediatypes=true --provenance mode=max --sbom=true`
produces the same platform image manifest digest in all three cases. The
attestation is a separate manifest carrying `platform: unknown/unknown` and the
annotations `vnd.docker.reference.type=attestation-manifest` and
`vnd.docker.reference.digest=<the platform manifest it attests>`.

The published-index agreement check therefore still holds: it selects on
`platform.os == "linux"`, which excludes attestation manifests. The check is
additionally tightened to require exactly one attestation manifest naming each
examined platform manifest as its subject, so the in-registry provenance is
proven to attach to the bytes the release inspected rather than merely to be
present somewhere in the index.

Each attestation is resolved, its in-toto layers are inspected, and **each statement is then fetched from the
registry and required to name that platform's manifest digest as its own `subject`**. The annotation on the
attestation manifest is only the wrapper's claim about what it attests; the statement's `subject` is what the
attestation is actually about. For a registry push BuildKit populates it with the platform manifest digest, so
checking the annotation alone would accept a correctly labelled attestation whose payload concerns different
bytes. That matters most on the adopt path below, where the wrapper came from a run this release did not
perform.

An earlier revision of this runbook asserted the opposite — that BuildKit leaves the statement subject empty and
binds attestations solely through the annotation. **That was wrong**, and it is recorded here because a reader
would otherwise have no reason to doubt it. The measurement behind it was taken on a local
`--output type=oci` export, where BuildKit has no image reference to name and the subject is legitimately empty.
The release pushes with `--output type=image,name=…,push=true`, and that exporter does populate the subject.
Verified against two public images built and pushed by buildx:

| Image | linux/amd64 platform manifest | Statement subject | Predicate types |
| --- | --- | --- | --- |
| `docker/dockerfile:1.9.0` | `sha256:dc9e2365…` | `dc9e2365…` (4 subjects, all the same digest) | `spdx.dev/Document`, `slsa.dev/provenance/v0.2` |
| `docker/buildx-bin:latest` | `sha256:108c5ca2…` | `108c5ca2…` | `spdx.dev/Document`, `slsa.dev/provenance/v1` |

Both SLSA predicate versions in that table are accepted. The version BuildKit emits depends on the BuildKit
release, and this check runs **after** the immutable push, so requiring one version would abort a release that
has already consumed its version on a toolchain change. `.github/workflows/release.yml` additionally pins the
buildx version and the BuildKit image by tag and digest, the same way this script pins the Trivy scanner, so the
emitted version is deterministic rather than merely tolerated.

**A version published without attestations can never be adopted.** The push
adopts an existing publication rather than overwriting it, so that a run whose
signing or publication step failed after the build can be re-dispatched. A tag
pushed before attestations existed carries none, so the attestation check fails
and — because published tags are immutable — that version can never be completed.
Use a version that has never been pushed. Do not relax the check to adopt an
unattested publication; refusing to sign a release that lacks the evidence it
promises is the reason the check exists.

The signed GitHub release remains the authoritative provenance channel: it is the
only one an air-gapped installation can use, the only one bound to the production
Key Vault trust root, and the only one the installer verifies. The registry
attestations are an additional channel for a consumer who resolves the image by
digest, not a replacement.

### What the installed instance records

An installation's retained release evidence (`image-distribution.json`) names the
file-level SBOM, the provenance, the vulnerability scan, and the per-platform
component inventory published for the installed architecture
(`componentInventoryAsset`), so component-level triage can start from the
instance's own evidence file and read the inventory the release signed for that
platform. A release published before inventories existed records no inventory,
and a version-1 evidence record written by an earlier installer has no such
member; both remain valid.

Without `--push` there is no registry, so the signed multi-architecture digest is
a canonical index computed from the two platform manifests. It is a stable
identity for those manifests, but `docker pull <repository>@<digest>` will not
resolve it. An offline installation never uses it: it installs from the signed
platform archive.

Only a real publishing run can prove the credentialed steps: the registry push,
the digest it returns and the index agreement check that follows it, the
registry's acceptance and re-serving of the attestation manifests, the attestation
wrapper, predicate, and payload-subject checks that follow them and the registry
blob reads they perform, Key Vault signing under the
production identity, the GitHub Release creation and its collision refusal, and
anonymous verification through public release URLs. It is also the only run that
builds `linux/arm64` under QEMU on a hosted runner rather than on native
hardware. Everything before those steps — the multi-platform build, the identity
derivation, the smoke check, the scan gate, the component inventory and its
binding to each platform's image ID, candidate assembly, signing, and end-to-end
verification — is exercised locally by the script and by the release-tool
contract tests.
