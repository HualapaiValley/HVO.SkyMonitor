# Signed Distribution Releases

## Boundary And Identity

GitHub Releases are the initial source of truth for self-contained installer
archives, production catalog bundles, signed manifests and indexes, checksums,
SBOMs, provenance, licenses, and attribution. GHCR remains the source for
CameraAgent and LogicHost images, which are selected only by immutable digest.
Installer and catalog trains have independent versions, tags, index sequences,
retention, and rollback histories:

```text
installer-v<version>
catalog-<package-version>
installer-index-<sequence>
catalog-index-<sequence>
```

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
confirmation. The source commit must already have successful `Required CI`.

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
