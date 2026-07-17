# Production Catalog Build and Installation

The HYG production catalog is an operator-built, host-local deployment artifact.
Application startup and normal installation never fetch catalog data. The same
verified bundle can be copied to LogicHost and every CameraAgent and installed
without network access.

## Prerequisites

The scripts require GNU-compatible Bash tools, `sha256sum`, and exactly
`sqlite3 3.45.1`; the build additionally requires `gzip`. The pinned SQLite CLI
is both the deterministic serializer and the installer's JSON/integrity
validator. Installation also requires `flock`, `realpath`, and `sync`. None of
these HYG scripts requires `jq` or Python.

## Build a Bundle

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
  --install-root /var/lib/hvo/data/catalog \
  ./artifacts/hyg-v42
```

## Bundle Contract

The self-contained bundle contains only:

- `manifest.json`, the UTF-8 JSON v1 manifest consumed by both the installer and
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
  "manifestVersion": 1,
  "package": { "kind": "production", "version": "hyg-v4.2-p3-s2-r1" },
  "catalog": { "name": "HYG 4.2", "version": "4.2" },
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

Normal installation consumes a local bundle and has no network code path:

```bash
./scripts/catalog:install install \
  /mnt/catalog-bundles/hyg-v4.2-p3-s2-r1.bundle \
  /var/lib/hvo/data/catalog
```

Configure each host with `HVO_RUNTIME_DATA_ROOT=/var/lib/hvo/data` and
`Catalog:Root=/var/lib/hvo/data/catalog`; `/var/lib/hvo` must be a nonsymlink
directory owned by the application operator so the shared operation lock can be
created without changing parent permissions, and it must not be group- or
world-writable. When `HVO_RUNTIME_DATA_ROOT` is supplied, the requested install
root must be exactly its canonical `catalog` child. Configure
`Catalog:RequiredPackageKind=Production`. The resolver reads the active pointer
and all identity, checksum, length, and provenance requirements from the strict
manifest; there is no independently configurable database path or checksum.
Activation applies to the next host start; a running process retains its already
loaded immutable catalog.

The installer first takes the application-state operation lock shared with
start, rebuild, reset, backup, and restore, then takes its catalog-exclusive
`.install.lock`. This prevents catalog pointer mutation while restore preserves
the target catalog. It removes abandoned
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

Rollback validates the complete previous bundle before changing either pointer:

```bash
./scripts/catalog:install rollback /var/lib/hvo/data/catalog
```

The equivalent catalog-scoped command is
`./scripts/catalog/rollback-hyg-v42.sh /var/lib/hvo/data/catalog`.

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
