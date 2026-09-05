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

The SBOM is a file-level manifest: it names the published archives and their
SHA-256 values, not the operating-system packages and .NET assemblies inside the
image. Use the vulnerability scan report for component-level triage until
[#597](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/597) replaces it
with a component inventory and registry attestation.

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
built image there. No smoke of a *published* arm64 release image and no arm64
installer campaign have run, so treat an arm64 installation as unqualified end
to end. The signed-release installer campaign
([#598](https://github.com/RoySalisbury/HVO.SkyMonitor/issues/598)) proves the
signed lifecycle on `linux/amd64`: both architectures of both candidates are
built, identity-derived, scanned, and signed, and the amd64 archive is the one an
installation consumes and runs. The arm64 archive of each candidate is published
and verifiable but is never installed, so arm64 remains unqualified end to end
and no open issue currently tracks qualifying it; open one before treating an
arm64 installation as supported.

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
container pinned by tag and digest, and assembles the candidate. `--sign-key
<pem>` signs and re-verifies the candidate with a local key for rehearsal; a
publishable release is signed only by the production key through the workflow
below.

`--sign-key` is also what the signed-release installer campaign uses. That
campaign builds two candidates from two committed revisions, signs both with one
ephemeral key, and publishes a campaign-only deployment CLI, built from the same
committed revision, whose embedded trust root is that ephemeral public key. It
then installs from the first candidate, upgrades to the second, rolls back, and
refuses four releases it must not accept: one signed by a key the trust root does
not hold, one carrying the trusted key's real signature over a different release,
one naming an archive that is not the one it signed, and one whose signed
compatibility record contradicts the image labels. It asserts the retained
`image-distribution.json` after each transition. Nothing else about verification
is changed, and the substitution is proved to redirect trust rather than remove
it: the unmodified product CLI must refuse the same release, and the campaign CLI
must refuse the same release re-signed by a second key.

The ephemeral key therefore establishes the lifecycle, verification, and
compatibility-agreement behaviour of the signed image train against real
containers. It establishes nothing about the production key itself — that the
committed public key matches the Key Vault private key, that the workflow
identity can sign with it, that a Key Vault signature verifies against the
committed trust root, or that custody and rotation behave as described above.
It also does not establish the manifest's declared-key-identity comparison, which
signature verification makes unreachable as a failure.
Only a real publishing run can establish those, and the production key cannot be
exported to substitute for one. See
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

Without `--push` there is no registry, so the signed multi-architecture digest is
a canonical index computed from the two platform manifests. It is a stable
identity for those manifests, but `docker pull <repository>@<digest>` will not
resolve it. An offline installation never uses it: it installs from the signed
platform archive.

Only a real publishing run can prove the credentialed steps: the registry push,
the digest it returns and the index agreement check that follows it, Key Vault
signing under the production identity, the GitHub Release creation and its
collision refusal, and anonymous verification through public release URLs. It is
also the only run that builds `linux/arm64` under QEMU on a hosted runner rather
than on native hardware. Everything before those steps — the multi-platform
build, the identity derivation, the smoke check, the scan gate, candidate
assembly, signing, and end-to-end verification — is exercised locally by the
script and by the release-tool contract tests.
