# HYG 4.4 + OpenNGC composed catalog

`hyg-v44-openngc-production` is a third approved catalog lineage (issue #525). It
contains the HYG 4.4 star catalog, unchanged, plus OpenNGC v20260501 deep-sky
objects: identities, extents, sourced outlines and search aliases. It installs
beside HYG 4.2 and HYG 4.4 and changes neither of them
([HYG 4.4 catalog snapshot](hyg-v44.md)). An instance that does not select it
renders exactly as before.

Like the other lineages, the bundle is an explicitly built deployment artifact.
Capture, application startup and installation never download or preprocess
catalog data. The builders read only previously acquired, pinned inputs:

- `scripts/catalog/build-hyg-v44-openngc.sh --hyg-bundle BUNDLE --openngc-source CHECKOUT OUTPUT`
  composes the database. `--hyg-bundle` names a verified `hyg-v4.4-p4-s3-r1`
  bundle, whose database becomes the stars component byte for byte.
  `--openngc-source` names an OpenNGC checkout at the pinned commit, and only the
  pinned files are read from it.
- `scripts/catalog/bundle-hyg-v44-openngc.sh DATABASE OUTPUT` validates the
  database again and emits the self-contained bundle.

Install it with the deployment CLI `catalog install` command, as for HYG 4.4.

## Pinned evidence

- Stars component: catalog `hyg-v44-production`, package `hyg-v4.4-p4-s3-r1`.
  Its source, hashes and row count are pinned in [HYG 4.4](hyg-v44.md#pinned-evidence).
- Deep-sky source: OpenNGC v20260501, https://github.com/mattiaverga/OpenNGC,
  commit `36cb178a0f69dba8bfc03a99c10512831edf1c6b`
- Deep-sky inputs:
  - `database_files/NGC.csv`, SHA-256
    `840fe0c9ee1332e551b2e722a0e92726cd7b157914a3d2177602832aadd3aa9e`,
    3,876,288 bytes, 13,969 rows
  - `database_files/addendum.csv`, SHA-256
    `1d8f0914e643ada325a5a94d88d8fefad6a4937a2f77cc34f21483af22b11983`,
    17,484 bytes, 64 rows
  - `outlines/objects`: 379 files and 102,239 source points, each pinned by
    the checked-in listing `scripts/catalog/openngc-v20260501-outlines.sha256`
    (SHA-256 `f8db977ecb1ecd2f7c9156f570bacddce5316b601c3a92e4a530ab6bc0d5de1f`,
    37,407 bytes)
- License: CC BY-SA 4.0 for both components,
  https://creativecommons.org/licenses/by-sa/4.0/
- Preprocessing version: `5`
- SQLite schema/user version: `4`
- Manifest version: `3`
- SQLite serializer: `sqlite3 3.45.1`
- Canonical builder: Ubuntu 24.04 on `linux/amd64`. Two independent builds
  produced byte-identical databases and bundles.
- Generated SQLite file: `hyg_v44_openngc.sqlite`
- Generated SQLite SHA-256: `eea1181ffae1dca2935aeed2c7790305b28045061f38ca6dda5eaf19694a8f1f`
- Generated SQLite length: `23244800` bytes
- Star rows: `119,613`, the HYG 4.4 count

| Identity | Value |
| --- | --- |
| Catalog ID | `hyg-v44-openngc-production` |
| Catalog name and version | `HYG 4.4 + OpenNGC v20260501`, `4.4+openngc-v20260501` |
| Package version | `hyg-v4.4-openngc-v20260501-p5-s4-r1` |
| Package lineage | `hyg-v44-openngc-production-p5-s4` (revisions `hyg-v4.4-openngc-v20260501-p5-s4-r<N>`) |
| License file | `LICENSE-HYG-OPENNGC.md`, SHA-256 `35e3828ef35eb0d4bbaf5d9b4e88e7e235f502d60ecdb8b98e1b721f7a33b49e`, 1,273 bytes |
| Attribution file | `ATTRIBUTION-HYG-OPENNGC.md`, SHA-256 `15a3ce6129d61310bc6405734f0cdc18dc59d5e237519669244436a6cd0a19d2`, 5,647 bytes |
| Constellation topology | unchanged from HYG 4.4 |

The build and bundle scripts check every input hash, byte length, row count and
recorded count, and they fail closed. They also prove that the star tables are
exactly HYG 4.4's: the SHA3 digests of the ordered `celestial_objects` and
`celestial_object_aliases` contents must equal the values pinned from the
HYG 4.4 database. A composed build therefore cannot change a star.

The same identities are pinned in version 2 of the embedded approved-catalog
registry, `src/HVO.SkyMonitor.Catalog.Sqlite/ApprovedCatalogSpecifications.json`.
The registry refuses a composed specification unless its stars component names
an approved, uncomposed package of the same registry with that package's
database hash, length and row count.

The retained notices are [`hyg-v44-openngc-license.md`](hyg-v44-openngc-license.md)
and [`hyg-v44-openngc-attribution.md`](hyg-v44-openngc-attribution.md). Each
bundle carries them as `LICENSE-HYG-OPENNGC.md` and
`ATTRIBUTION-HYG-OPENNGC.md`, covered by the bundle's payload hashes. A bundle
whose license or attribution file differs from these bytes is refused. The
checked-in test subset `tests/fixtures/catalog/hyg-v44-openngc-subset.sqlite` is
a minimal excerpt. Its attribution, derivation and checksums are in
`tests/fixtures/catalog/SOURCE-v44-openngc.md`. Its metadata `name` and
`catalog_version` differ from production, so it can never pass as the approved
package. `THIRD-PARTY-NOTICES.md` names both upstream projects.

## Schema 4 and preprocessing 5

Schema 4 keeps the schema-3 star tables and their only secondary index,
`celestial_objects_magnitude_id`, and adds four `WITHOUT ROWID` deep-sky
tables:

- `deep_sky_objects` has one row per live OpenNGC object. The columns are the
  ID, designation, display name, object type, J2000 right ascension (hours) and
  declination (degrees), constellation, major and minor axis (arcminutes),
  position angle, B and V magnitude, surface brightness, Hubble type, and the
  Messier number, Caldwell number, Hipparcos ID and common name. The display
  name is `M<n>` for a Messier object and the designation otherwise.
- `deep_sky_aliases` holds `(alias, object_id, kind)` with primary key
  `(alias, object_id)`. Where several sources give the same alias for the same
  object, the lowest-ranked kind wins: `designation`, `messier`, `caldwell`,
  `disputed`, `duplicate`, `cross-reference`, `hipparcos`, `common`,
  `identifier`.
- `deep_sky_tombstones` holds `(id, designation, reason, candidates)` for
  OpenNGC rows that name no object. The reason is `nonexistent`,
  `duplicate-unresolved` or `duplicate-conflict`.
- `deep_sky_outline_points` holds `(object_id, level, ring, sequence,
  right_ascension_degrees, declination_degrees)`. Every ring is closed, has at
  least four points, is numbered without gaps and never repeats a point
  consecutively.

| Metadata key | Value |
| --- | --- |
| `deep_sky_axis_unit` | `arcminute` |
| `deep_sky_outline_level_convention` | `1-widest-2-standard-3-narrowest` |
| `deep_sky_position_angle_convention` | `degrees-north-through-east-0-inclusive-to-180-exclusive` |
| `deep_sky_surface_brightness_unit` | `b-mag-per-square-arcsecond-within-25-mag-isophote` |

An unknown OpenNGC value is stored as `NULL`, never as zero. In particular, an
object with no position angle has a `NULL` angle, not 0.

| Count | Value |
| --- | --- |
| Deep-sky objects | 13,371 |
| Aliases | 65,822 |
| Tombstones | 10, all `nonexistent` |
| Outline objects / sets / rings / points | 182 / 359 / 441 / 90,861 |
| Outline sets by level | 1: 97, 2: 164, 3: 98 |
| Constellations | 89 |

Alias kinds: `caldwell` 105, `common` 182, `cross-reference` 74,
`designation` 13,371, `disputed` 1, `duplicate` 651, `hipparcos` 28,
`identifier` 51,302, `messier` 108.

Object types: `*` 546, `**` 244, `*Ass` 64, `Cl+N` 67, `DrkN` 2, `EmN` 8,
`G` 10,521, `GCl` 208, `GGroup` 13, `GPair` 231, `GTrpl` 26, `HII` 83, `Neb` 94,
`Nova` 3, `OCl` 663, `Other` 419, `PN` 130, `RfN` 38, `SNR` 11.

### Rulings

- **Duplicates.** An OpenNGC duplicate row becomes a `duplicate` alias of the
  object it names; it never becomes a second object. 651 are resolved this way.
- **M102.** M102 is recorded only as the `disputed` alias of NGC 5457. It is
  never a display name, label or glyph, and NGC 5457 keeps `M101`.
- **Nonexistent rows** are tombstoned: IC0067, IC0068, IC1064, IC1326, IC1642,
  IC2688, IC2915, IC3398, IC5112 and NGC0412.
- **Stellar rows.** `*` and `**` rows are catalog identities only. A scene
  places one only when it has a Messier number, and draws it as a star symbol.
  M40 is the double-star case.
- **Sharpless 2** identifiers are dropped. One identifier, `SH 2-155` on C009,
  is affected.
- **Hipparcos cross-check.** Every OpenNGC row with a Hipparcos ID must lie
  within 5″ of that HYG 4.4 star. Where OpenNGC gives a V magnitude, it must
  agree within 0.1. All 28 rows pass; 15 of them give V.

### Recorded transformations

Every change preprocessing makes to the inputs is recorded in the manifest's
`transformations[]` with its count and subjects. The build recomputes the list
from the inputs and fails on any difference.

| # | Transformation | Count |
| --- | --- | --- |
| 1 | `position-angle-180-to-0` | 44 |
| 2 | `duplicate-resolved` | 651 |
| 3 | `duplicate-disputed` (M102 → NGC5457) | 1 |
| 4 | `duplicate-tombstoned` | 0 |
| 5 | `nonexistent-tombstoned` | 10 |
| 6 | `caldwell-identifier-normalised` | 105 |
| 7 | `hipparcos-identifier-normalised` | 23 |
| 8 | `sh2-identifier-dropped` | 1 |
| 9 | `live-cross-reference-skipped` | 6 |
| 10 | `list-token-trimmed` | 16 |
| 11 | `empty-list-token-dropped` | 1 |
| 12 | `bare-prefix-identifier-dropped` | 3 |
| 13 | `duplicate-row-identifier-dropped` | 1 |
| 14 | `alias-precedence-collapsed` | 14 |
| 15 | `outline-stem-caldwell-padding` (C9 → C009) | 1 |
| 16 | `outline-stem-messier` (M045 → Mel022) | 1 |
| 17 | `outline-regional-stem-dropped` | 11 |
| 18 | `outline-consecutive-duplicate-removed` | 588 |

The eleven regional outline stems that name no single object (Antlia, Cepheus,
CoronaAustralis, Cygnus, LBN1032, LBN1037, LMC, Orion, Puppis, Scorpion and
Vela) are not imported.

## Manifest version 3

The bundle manifest keeps every manifest-version-2 member and adds three:

- `database.deepSky` records the object, alias, tombstone, outline-object,
  outline-set, outline-ring and outline-point counts.
- `components[]` names the `stars` component (the HYG package ID, version and
  database evidence) and the `deep-sky` component (the OpenNGC name, version,
  commit, URL, license, both CSV inputs and the outline listing).
- `transformations[]` lists the recorded transformations above.

`database.rowCount` stays the star count, so it equals the stars component's
row count. Manifests of versions 1 and 2 keep validating unchanged.

## Selection and image compatibility

The lineage installs under its own root:

```text
/var/lib/hvo/skymonitor/catalogs/hyg-v44-openngc-production/versions/hyg-v4.4-openngc-v20260501-p5-s4-r1
```

`catalog install`, `select`, `rollback` and `check` behave exactly as described
in [HYG 4.4 side-by-side selection](hyg-v44.md#side-by-side-selection). The only
difference is the image requirement. Selecting this catalog requires an image
whose catalog contract label is `hvo-approved-catalogs-v2`, the image that
embeds registry version 2. Registry version 2 approves every version-1 catalog,
so a v2 image can still select HYG 4.2 or HYG 4.4. An image labelled
`hvo-approved-catalogs-v1` refuses the composed catalog before anything is
changed.

Deploy LogicHost before switching any CameraAgent to this catalog. Scenes from
this catalog may carry deep-sky content in `projected-scene-v3`, which an older
LogicHost refuses ([Deep-sky objects](../astronomy/deep-sky-openngc-v1.md)).

## Runtime identity and health

Resolution and startup validation work as for HYG 4.4. The resolver additionally
checks the composed specification's components, transformations and deep-sky
counts against the registry. A composed database whose deep-sky tables are
missing, or whose counts differ, fails startup rather than falling back.

The catalog health check reports `CatalogIdentitySource` as
`explicit-manifest-v3`. `RowCount` stays the star count, as for every other
lineage. A composed catalog adds `DeepSkyObjectCount` and
`DeepSkyOutlineObjectCount` beside it.

The scope is deep-sky identities, extents, outlines and search aliases. It
excludes Sharpless 2, survey imagery and the search experience (#524). Search
uses the `CelestialObjectSearch` seam only.
