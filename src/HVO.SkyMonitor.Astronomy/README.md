# HVO.SkyMonitor.Astronomy

Reusable deterministic time, coordinate, projection, refraction, and catalog
contracts shared by CameraAgent and LogicHost. It intentionally has no host,
database, rendering, or image-library dependency.

## Catalog data

The initial `CsvCelestialCatalog` accepts HYG-compatible CSV columns `id`,
`proper`, `ra`, `dec`, and `mag`, with optional `ci`. Production HYG data is
not packaged yet; before adding it, record its license, source URL, version,
SHA-256 checksum, and preprocessing process in `CatalogMetadata` and this file.
