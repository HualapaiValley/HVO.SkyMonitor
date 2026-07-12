# HYG 4.2 catalog snapshot

The SQLite catalog is an explicitly built deployment artifact. Normal capture,
application startup, and package build do not download or preprocess catalog
data. An operator runs `scripts/catalog/build-hyg-v42.sh OUTPUT_DIRECTORY`, then
installs the validated `hyg_v42.sqlite` atomically at the host-configured path.

## Pinned evidence

- Upstream project: https://codeberg.org/astronexus/hyg
- Catalog version: HYG 4.2
- Author/compiler attribution: David Nash and the upstream catalogs identified
  by HYG's acknowledgments
- License: CC BY-SA 4.0,
  https://creativecommons.org/licenses/by-sa/4.0/
- Compressed Git LFS object SHA-256:
  `5ca9431ff364c8002a4a3efa91b2b9296746aea1543374db4cb6b4fab049d601`
- Decompressed CSV SHA-256:
  `b2983a8d934e4f031cdb67bdd6c3437f8c5143cd6606a9573a9a9ac4b6375fd2`
- Preprocessing version: `3`
- SQLite schema/user version: `2`
- SQLite serializer: `sqlite3 3.45.1`
- Generated SQLite SHA-256:
  `b51d18b722199e89aa8fe4622ebe507346c75effb375e546881452a263f0b9e2`

The script validates all three hashes and fails closed. Updating source,
preprocessing code, Python/SQLite serialization behavior, or schema requires a
reviewed checksum update. The script requires the pinned SQLite serializer;
SQLite library changes can legitimately alter serialization and must not be
accepted without review.

The legacy SQLite checksum
`95A720585139452227F2201BDDB03E8DD7E98094E378D3FFB3D9EBC1AFAA2C76`
identifies only a historical derived file. It does not establish the identity
of its source CSV, attribution, license, or preprocessing and is not used by
this acquisition path as provenance evidence.

The checked-in test subset has separate attribution, derivation, and checksums
in `tests/fixtures/catalog/`.

The SQLite adapter loads the validated, magnitude-ordered snapshot once per
process. Preprocessing version 3 excludes HYG row `0` (`Sol`) because solar-system
bodies are supplied by the time-dependent ephemeris rather than treated as fixed
J2000 catalog stars. Schema version 2 preserves the source `hip` value as `hipparcos_id` so
reusable constellation topology can resolve stable Hipparcos endpoints while
projected provenance continues to use the catalog row ID. Candidate queries use
a binary-search upper bound and copy only rows at or brighter than the requested
magnitude. Exact horizon and image visibility
remain Astronomy responsibilities. A future catalog contract may add an
explicit coarse sky-region hint; the current all-sky fisheye query intentionally
does not invent one.
