# Production Catalog Build and Installation

The HYG production catalog is an operator-built-once deployment artifact.
Application startup and normal installation never fetch or rebuild catalog data.
The same verified bundle is copied to LogicHost and every CameraAgent, including
ARM64 targets, and installed without network access.

## Prerequisites

The scripts require GNU-compatible Bash tools, `sha256sum`, and exactly
`sqlite3 3.45.1`; the build additionally requires `gzip`. The pinned SQLite CLI
is both the deterministic serializer and the installer's JSON/integrity
validator. Installation also requires `flock`, `realpath`, and `sync`. None of
these HYG scripts requires `jq` or Python.

When the operating system does not package SQLite 3.45.1, install the isolated
checksum-pinned CLI without replacing its SQLite libraries. Offline installation
uses a previously acquired official source archive:

```bash
sudo ./scripts/catalog/install-sqlite-3.45.1.sh \
  --source /secure-cache/sqlite-autoconf-3450100.tar.gz
```

Network acquisition is opt-in with `--fetch`. The installer verifies the exact
source length and SHA-256, builds a static SQLite shell/library combination,
checks its version and JSON support, installs it beneath
`/usr/local/lib/hvo/sqlite-3.45.1`, and links `/usr/local/bin/sqlite3`. It does
not replace the distribution's `libsqlite3` package.

## Build a Bundle

The canonical builder for package `hyg-v4.2-p3-s2-r1` is Ubuntu 24.04 on
`linux/amd64`, using this repository's reviewed build scripts and exactly
`sqlite3 3.45.1`. The pinned input hashes, preprocessing version, schema version,
serializer version, database length, and database SHA-256 remain the package
authority. A changed environment that does not reproduce those values fails
closed and requires a reviewed package revision.

Build the production bundle once in that canonical environment. Deployment
targets do not run the builder. In particular, ARM64 operators must install the
approved bundle as described below rather than rebuild it locally. The current
package is platform-neutral at read time; byte-for-byte build reproducibility
across architectures is neither required nor claimed.

For an offline build, supply the pinned compressed source explicitly:

```bash
./scripts/catalog/build-hyg-v42.sh \
  --source /secure-cache/hyg_v42.csv.gz \
  ./artifacts/hyg-v42
```

Network acquisition is opt-in and is never inferred from a missing file:

```bash
./scripts/catalog/build-hyg-v42.sh --fetch ./artifacts/hyg-v42
```

The output directory must not already exist. It contains the verified compressed
and decompressed inputs, generated database, and the installable
`hyg-v4.2-p3-s2-r1.bundle` directory. `bundle-hyg-v42.sh` can separately wrap an
already reproduced database:

```bash
./scripts/catalog/bundle-hyg-v42.sh \
  /verified/hyg_v42.sqlite \
  ./artifacts/hyg-v4.2-p3-s2-r1.bundle
```

One command can build, bundle, and install:

```bash
./scripts/catalog/build-hyg-v42.sh \
  --source /secure-cache/hyg_v42.csv.gz \
  --install-root /var/lib/hvo/skymonitor/catalogs/hyg-v42-production \
  ./artifacts/hyg-v42
```

## Bundle Contract

The self-contained bundle contains only:

- `manifest.json`, the UTF-8 JSON v2 manifest consumed by both the installer and
  `CatalogSnapshotResolver`.
- `hyg_v42.sqlite`, the production database.
- `LICENSE-HYG.md`, the retained upstream license notice.
- `ATTRIBUTION-HYG.md`, attribution, source summary, and transformation notice.

The manifest records package kind and version; catalog, preprocessing, schema,
and serializer versions; source URLs and object identity; compressed,
decompressed, database, license, and attribution lengths and SHA-256 values;
expected row/Sol counts; the required HIP column; and separate D3 topology
provenance. The manifest itself is printed with a SHA-256 when built; all files
it describes are checked during installation and runtime resolution.

Production JSON has this exact property structure; duplicate, unknown, missing,
mistyped, or non-pinned values are rejected at every level:

```json
{
  "manifestVersion": 2,
  "package": { "kind": "production", "version": "hyg-v4.2-p3-s2-r1" },
  "catalog": { "id": "hyg-v42-production", "name": "HYG 4.2", "version": "4.2" },
  "source": {
    "projectUrl": "https://codeberg.org/astronexus/hyg",
    "downloadUrl": "https://codeberg.org/astronexus/hyg.git/info/lfs/objects/5ca9431ff364c8002a4a3efa91b2b9296746aea1543374db4cb6b4fab049d601",
    "oid": "5ca9431ff364c8002a4a3efa91b2b9296746aea1543374db4cb6b4fab049d601",
    "compressed": { "sha256": "5ca9431ff364c8002a4a3efa91b2b9296746aea1543374db4cb6b4fab049d601", "length": 13636976 },
    "decompressed": { "sha256": "b2983a8d934e4f031cdb67bdd6c3437f8c5143cd6606a9573a9a9ac4b6375fd2", "length": 33932800 }
  },
  "schemaVersion": "2",
  "preprocessingVersion": "3",
  "serializer": { "name": "sqlite3", "version": "3.45.1" },
  "database": {
    "relativePath": "hyg_v42.sqlite",
    "sha256": "b51d18b722199e89aa8fe4622ebe507346c75effb375e546881452a263f0b9e2",
    "length": 9302016,
    "rowCount": 119625,
    "solCount": 0,
    "requiredColumn": "hipparcos_id"
  },
  "license": {
    "identifier": "CC BY-SA 4.0",
    "url": "https://creativecommons.org/licenses/by-sa/4.0/",
    "file": { "relativePath": "LICENSE-HYG.md", "sha256": "9ab0956d22d8390b54456c2afb3b47281b4a5a0313c6871f0af4489ed8395f05", "length": 423 },
    "attribution": { "relativePath": "ATTRIBUTION-HYG.md", "sha256": "e3addc3480a0d0f07129f332b0dea592fa315373f21111d54ac8e2aebd03b5f1", "length": 1361 }
  },
  "topology": {
    "identity": "d3-celestial-v0.7.32-hip-coordinate-map-v1",
    "sha256": "70c253a00e0909ae0236dec0411afe837ebf8e493b2be7f84373b63c95c91621",
    "constellationCount": 88,
    "segmentCount": 743
  }
}
```

An explicitly expected fixture uses a reduced exact root containing only
`manifestVersion`, `package`, `catalog`, `schemaVersion`,
`preprocessingVersion`, and `database`. Its `package.kind` must be `fixture`;
the database object contains only `relativePath`, `sha256`, `length`, and
`rowCount`. Production resolution never accepts this reduced form.

## Install and Roll Back

Normal installation on every supported architecture consumes a local approved
bundle and has no network or build code path:

```bash
./scripts/catalog:install install \
  /mnt/catalog-bundles/hyg-v4.2-p3-s2-r1.bundle \
  /var/lib/hvo/skymonitor/catalogs/hyg-v42-production
```

The stable logical catalog ID for this specification is required as `catalog.id`
in manifest version 2 and is `hyg-v42-production`. Install it at
`/var/lib/hvo/skymonitor/catalogs/hyg-v42-production` and select that ID per
application target as described in the
[product-instance layout runbook](../runbooks/product-instance-layout.md).
Inventory also pins package `version`, `schemaVersion`, and
`preprocessingVersion`; deployment rejects disagreement in the source bundle,
installed manifest, phase ledger, or active `current` selection. Resume validates
package kind/version, catalog ID, schema/preprocessing versions, manifest database
SHA-256/length/row count, and the actual database bytes and row count.
The product root must be a nonsymlink and must not be group- or world-writable.
Configure `Catalog:RequiredCatalogId=hyg-v42-production` and
`Catalog:RequiredPackageKind=Production`. The resolver reads the active pointer
and all identity, checksum, length, and provenance requirements from the strict
manifest; there is no independently configurable database path or checksum.
Activation applies to the next host start; a running process retains its already
loaded immutable catalog.

The installer first takes the application-state operation lock shared with
start, rebuild, reset, backup, and restore, then takes its catalog-exclusive
`.catalog.lock`, shared by production and fixture publishers. This prevents catalog pointer mutation while restore preserves
the target catalog. The lock is an owner-only, single-link regular file and its
device/inode identity is revalidated against the held descriptor. Under that
lock, `.catalog-lineage.json` permanently binds the root to its catalog ID,
package kind, and schema/preprocessing lineage. A pre-binding installation is
adopted only after its complete active snapshot validates; an empty root is
bound before candidate publication. Conflicting IDs, package kinds, or lineage
fail closed. It removes abandoned
`.staging.*` directories, and validates the local bundle before staging. It
checks every retained payload length and hash, the pinned production identity,
SQLite integrity/user version/schema/index/metadata, exactly 119,625 rows, Sol
exclusion, and unique nonblank HIP IDs. It repeats validation after copying into
a same-filesystem staging directory, publishes the unreferenced candidate as a
read-only `versions/PACKAGE_VERSION` directory, validates it again, and
commits the relative `current` and `previous` symlinks through a durable
`.pointer-transaction`. Each pointer replacement is atomic; after an
interruption between replacements, the next locked catalog operation validates
the recorded targets and finishes the old-to-new transition before new work. A
valid displaced active target is retained as `previous`. Candidate failure
before the transaction never changes `current`.

Fixture publication uses the same root lock and lineage binding. Its durable
pointer transaction authenticates both the candidate and displaced active
snapshot, publishes the displaced target as `previous`, then publishes
`current`. Restart recovery independently revalidates both targets before
completing either pointer move; reactivating the already-current version leaves
`previous` unchanged.

Rollback validates the complete previous bundle before changing either pointer:

```bash
./scripts/catalog:install rollback /var/lib/hvo/skymonitor/catalogs/hyg-v42-production
```

The equivalent catalog-scoped command is
`./scripts/catalog/rollback-hyg-v42.sh /var/lib/hvo/skymonitor/catalogs/hyg-v42-production`.

On a normal rollback, `previous` becomes the displaced current version, allowing
the operator to reverse the selection again. Do not modify a published version
directory in place; produce a new reviewed package revision instead.

## Focused Validation

The fixture-mode smoke test requires no production catalog and proves that a
fixture bundle and invalid rollback target are rejected, the active pointer is
preserved, and orphan staging is reconciled:

```bash
./scripts/catalog/smoke-test.sh
```

With a verified production bundle, run the complete automated install lifecycle
including eight install and three rollback interruption boundaries, revision
bounds, same-version collision and corrupt-upgrade rejection, offline installer
revalidation, read-only orphan reconciliation, and rollback:

```bash
./scripts/catalog/test-hyg-v42-install.sh \
  /mnt/catalog-bundles/hyg-v4.2-p3-s2-r1.bundle
```

Capture the five-trial `W3-CAT-INSTALL` latency, CPU, RSS, filesystem block, and
logical byte evidence plus deterministic validation, SQLite command, fsync, and
atomic-rename operation counts with:

```bash
./scripts/catalog/measure-hyg-v42-install.sh \
  /mnt/catalog-bundles/hyg-v4.2-p3-s2-r1.bundle \
  TestResults/issue-111/REVISION/catalog-install.json
```
