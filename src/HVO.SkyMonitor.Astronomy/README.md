# HVO.SkyMonitor.Astronomy

Reusable deterministic time, coordinate, projection, refraction, and catalog
contracts used by CameraAgent and the shared processing libraries. This project
has no host, database, rendering, or image-library dependency.

## Catalog data

`CsvCelestialCatalog` accepts HYG-compatible CSV columns `id`, `proper`, `ra`,
`dec`, and `mag`, with optional `ci`. Runtime hosts consume the verified HYG 4.4
SQLite package installed separately under the configured catalog root; production
catalog data is intentionally not embedded in this assembly. Build, provenance,
checksum, and installation details are maintained in
[`docs/catalog/production-install.md`](../../docs/catalog/production-install.md).
