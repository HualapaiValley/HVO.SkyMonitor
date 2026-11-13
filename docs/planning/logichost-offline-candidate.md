# LogicHost offline identity candidate (first slice of #1242)

This implements the reader-first identity and integrity slice of
[#1242](https://github.com/HualapaiValley/HVO.SkyMonitor/issues/1242), under the
[approved product release contract](../hvo-install-contract.md#4-release-assets)
and [LogicHost preparation contract](logichost-lifecycle-contract.md#release-inputs-and-artifact-identity).
**The issue remains open. A successful check is not a complete installation
bundle, a trusted published release, or permission to install.** No release
workflow or CameraAgent command consumes this format yet.

From an immutable local folder containing a schema-1 `release.json`,
`SHA256SUMS` and every listed asset:

```bash
dotnet run --project tools/HVO.SkyMonitor.Deployment.ReleaseTool -- \
  verify-logichost-candidate --from /path/to/candidate
```

The command reads local bytes only. A cache or mirror folder is treated exactly
like any other local folder; there is no URL resolution or network fallback.
It neither extracts nor loads images and writes no files. Exit 0 means the
checks below succeeded; exit 1 reports a refusal. Inputs must remain immutable
throughout verification; the result is not a durable receipt or an authorization
token for later use of those paths.

The existing release reader now has an explicit `ParseLogicHost` entry point.
The existing CameraAgent entry point continues to require `images.cameraAgent`.
The shared archive reader has an explicit LogicHost path that returns central
image labels without interpreting CameraAgent identity or raw-ingress fields.
The release tool compiles these same readers, following its existing practice
for product-version and upgrade-note parsing.

## Candidate format and checks

Use the existing product envelope, version/tag/source/creation identity,
`images.logicHost`, `assets[]`, and checksum syntax. The image repository ends
in `/skymonitor-logichost`; archive naming, OCI platforms and asset runtime IDs
follow install-contract decisions D5/D6. This slice requires exactly one
`linux/amd64` image and its offline archive, consistent with the approved initial
full-stack topology. ARM64 qualification is a separate gate.

The additive `componentMetadata.logicHost` object contains exactly these fields:

| Field | Identity / accepted value in metadata schema 1 |
| --- | --- |
| `schemaVersion`, `minimumVerifierVersion` | Integer `1`; unsupported reader/schema versions refuse |
| `installationMode` | `fresh-database-only`; no compatibility promise for older unreleased databases |
| `migrationId` | Canonical migration identifier, bounded to 150 ASCII identifier characters |
| `initializationTarget` | Exact controlled-initialization target identifier, bounded to 128 characters |
| `generatedSqlSha256`, `seedManifestSha256` | Lowercase SHA-256 identities of the selected generated SQL and seed/recipe manifest |
| `configurationVersion`, `objectLayoutVersion`, `checkpointFormatVersion`, `backupFormatVersion` | Integer `1`; explicit initial format declarations, without claiming lifecycle implementation or qualification |
| `requiredCatalogId`, `requiredPackageVersion` | An exact production reference accepted by the existing approved catalog registry |

Identifiers start with an ASCII letter/digit, contain only ASCII letters,
digits, `_`, `-`, `.`, and contain no `..`. The exact object is also declared in
the image configuration's string label `io.hvo.skymonitor.logichost-release`.
Both objects are parsed independently, reject unknown/missing/duplicate fields,
and must agree structurally. JSON property order need not match. This declares
central identities; it does not use CameraAgent state as a substitute.

The existing bounded release/checksum/OCI readers enforce strict Unicode,
case-insensitive duplicate-field rejection, schema/source/tag/platform/archive
consistency, metadata bounds and safe canonical archive paths. Every asset's
length and SHA-256 must agree with both release.json and SHA256SUMS; checksums
cover exactly every asset plus release.json. Unlisted inputs, directories,
symbolic links and multiply-linked files refuse. Linked directory ancestors
also refuse. Candidate assets total at most 128 GiB; release.json is at most
1 MiB, SHA256SUMS 64 KiB, component metadata 16 Ki characters and individual OCI
metadata entries 4 MiB. Layers are hashed as streams rather than retained.

The image index, platform manifest, configuration/image ID, every layer and
Docker-classic manifest must describe the same image. Version, source revision,
created time, component and central declarations must agree with the archive
bytes. This proves the image ID a compatible loader should produce, without
claiming an actual Docker load, executable inspection or registry comparison.

## Remaining work on #1242

The first slice deliberately accepts a **complete identity candidate**, which
may contain only the image and supporting assets; it cannot certify bundle
completeness. The rest of #1242 still requires production candidate assembly,
binding the declared SQL/seed identities to actual initialization artifacts and
image contents, full central `stateContracts` compatibility ranges, precise
build/source-tree/base/tool identities, per-platform component SPDX SBOM and
scan subject/policy checks, provenance and attestation subject agreement, and
complete CLI/Compose/configuration/runbook/license/offline dependencies.

Catalog references are checked against the approved production registry here;
inner catalog/database/schema/row-count/license verification and binding an
exact catalog manifest/archive remain required when composing the full bundle.
Existing catalog verification is unchanged. Fixture identifiers and fake SQL/
seed digests demonstrate parser consistency only, never actual runtime state.

Publisher attestation verification, registry/release agreement, actual loaded
image/executable inspection, native runtime campaigns and anonymous public
reacquisition remain later #1242/#540 evidence under their existing gates.
Attestation/key creation, publication, installation/deployment, schema/storage
changes, upgrade/rollback and CameraAgent behavior are outside this slice.
