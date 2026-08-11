# HYG 4.2 catalog snapshot

The SQLite catalog is an explicitly built deployment artifact. Normal capture,
application startup, and installation do not download or preprocess catalog
data. Source acquisition is explicit: an operator supplies `--source` for an
offline build or `--fetch` to request the pinned HTTPS download. The build emits
a self-contained production bundle that the installer validates and activates
atomically. See [Production Catalog Build and Installation](production-install.md).

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
- Canonical builder: Ubuntu 24.04 on `linux/amd64`; build once and distribute the
  approved immutable bundle to every deployment architecture
- Generated SQLite SHA-256:
  `b51d18b722199e89aa8fe4622ebe507346c75effb375e546881452a263f0b9e2`
- Compressed input length: `13,636,976` bytes
- Decompressed input length: `33,932,800` bytes
- Generated SQLite length: `9,302,016` bytes
- Expected post-Sol-exclusion rows: `119,625`

The scripts validate all three hashes and byte lengths and fail closed. Updating
source, preprocessing SQL, SQLite serialization behavior, builder architecture,
or schema requires a reviewed checksum update. The build requires the canonical
`linux/amd64` environment and pinned SQLite serializer. SQLite library or
architecture changes can legitimately alter serialization and must not be
accepted without review. ARM64 hosts consume the approved bundle; they do not
rebuild it.

The retained HYG license and attribution notices are
[`hyg-v42-license.md`](hyg-v42-license.md) and
[`hyg-v42-attribution.md`](hyg-v42-attribution.md). They are copied into every
production bundle and covered by that bundle's payload hashes.

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
magnitude. They may also apply an inclusive storage-neutral J2000 spherical-cap
hint after the snapshot is loaded. Astronomy derives that cap conservatively
from the calibrated optical projection or geometric horizon and continues to own
exact horizon/image visibility and `MaximumResults`. The adapter preserves its
validated immutable cache and deterministic magnitude-then-ID order; this hint
does not justify a schema, preprocessing, or SQLite index change.

The canonical VirtualSky default query uses magnitude `<= 6.5` and at most
`2000` visible results. The adapter must not globally truncate candidates to
`2000` before projection. Astronomy performs exact projection/horizon rejection
and only then applies the visible-result limit in deterministic
magnitude-then-ID order. A regression test must prove that brighter off-frame
rows cannot displace a dimmer in-frame result.
