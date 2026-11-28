# Install the Production Star Catalog

The default CameraAgent star catalog is HYG 4.4. The first approved revision
of lineage `hyg-v44-production-p4-s3` is `hyg-v4.4-p4-s3-r1`.
The [catalog specification](hyg-v44.md#pinned-evidence) lists the source,
database and notice identities. Releases distribute the verified bundle as a
release asset.

## Build or verify a bundle

The canonical builder for package `hyg-v4.4-p4-s3-r1` is Ubuntu 24.04 on
`linux/amd64` with SQLite CLI 3.45.1. ARM64 operators must install the
same verified bundle. The [reproducibility procedure](hyg-v44-reproducibility.md)
documents the serializer and offline checks. Build from the pinned input, or
explicitly request the pinned HTTPS download.

```bash
./scripts/catalog/build-hyg-v44.sh --source /secure-cache/hyg_v44.csv.gz ./artifacts/hyg-v44
# With network access, use --fetch in place of --source and its path.
./scripts/catalog/test-hyg-v44-reproducibility.sh /secure-cache/hyg_v44.csv.gz
```

The build output directory must be new. It contains the bundle
`hyg-v4.4-p4-s3-r1.bundle` with its strict manifest, immutable `hyg_v44.sqlite`,
license and attribution. `bundle-hyg-v44.sh` can package an independently
verified database with the same checks.

## Install and activate

A fresh CameraAgent installation installs its verified star bundle. To add a
verified revision independently, run the deployment CLI as the deployment
runtime user.

```bash
hvo-sky-install catalog install \
  --catalog-bundle /mnt/catalog-bundles/hyg-v4.4-p4-s3-r1.bundle \
  --product-root /var/lib/hvo-sky
```

The CLI validates the exact four files, database bytes and schema against the
approved registry. It publishes the immutable package under
`/var/lib/hvo-sky/catalogs/hyg-v44-production/versions/hyg-v4.4-p4-s3-r1`
and updates the lineage's `current` pointer. Each installer-managed instance
pins an explicit version. Activate a new revision with
`hvo-sky-install catalog select --instance-id UUID --catalog-version VERSION`.
See [Silo lifecycle operations](../runbooks/cameraagent-silo-runtime.md#silo-lifecycle-operations)
for the Silo layout.

A directly configured CameraAgent uses `Catalog:Root` pointing to the lineage
root, `Catalog:RequiredCatalogId=hyg-v44-production` and
`Catalog:RequiredPackageKind=Production`. It uses `current` when the exact
`Catalog:RequiredPackageVersion` is unset. For the repository local Compose or
`scripts/with-env` profile, run `catalog install` with
`HVO_INSTALLER_ALLOW_TEST_ROOT=1` and pass the absolute repository `data`
directory as `--product-root`. Mount `data/catalogs/hyg-v44-production` in Compose.

Verify the Catalog health check reports the expected ID, revision, checksum
and 119,613 rows. CameraAgent reads the database locally and read-only.

## Roll back a revision

Run `hvo-sky-install catalog rollback --instance-id UUID` to select the
previous revision of that instance's star catalog lineage. First allow pending
capture processing and retries to drain. A fresh instance with no previous
revision refuses rollback. See [catalog lifecycle commands](../runbooks/deployment-installer.md#catalog-lifecycle)
and the [HYG 4.4 rollback contract](hyg-v44.md#select-and-roll-back-revisions).
