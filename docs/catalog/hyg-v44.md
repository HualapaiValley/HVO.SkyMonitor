# HYG 4.4 catalog snapshot

HYG 4.4 is a second approved catalog lineage. It is installed beside HYG 4.2,
not over it. HYG 4.2 keeps its catalog ID, package versions, database bytes and
meaning ([HYG 4.2 catalog snapshot](hyg-v42.md)). Every instance selects exactly
one package version, and frames that were already recorded keep resolving to the
catalog identity they recorded.

Like 4.2, the bundle is an explicitly built deployment artifact. Capture,
application startup and installation never download or preprocess catalog data.
`scripts/catalog/build-hyg-v44.sh` takes either `--source` for an offline build
from a previously acquired compressed input or `--fetch` to request the pinned
HTTPS download. `scripts/catalog/bundle-hyg-v44.sh` then emits the
self-contained bundle. The shell installer `scripts/catalog:install` is
HYG 4.2-only: install a 4.4 bundle with the deployment CLI `catalog install`
command described in [Side-by-side selection](#side-by-side-selection).

## Pinned evidence

- Upstream project: https://codeberg.org/astronexus/hyg
- Upstream commit: `53e3df311869e813ace5f1ad2ec4ce909f13256c`
- Catalog version: HYG 4.4
- Author/compiler attribution: David Nash and the upstream catalogs identified
  by HYG's acknowledgments
- License: CC BY-SA 4.0,
  https://creativecommons.org/licenses/by-sa/4.0/
- Compressed Git LFS object SHA-256:
  `00b349893b9a53106dd488d8371e8d2fa586043e500bb3cdb8bff3931682197d`
- Decompressed CSV SHA-256:
  `ea0e1699a1e7d48daa197b5fe567fcf447420e367f37619862ee1d6f077599cd`
- Preprocessing version: `4`
- SQLite schema/user version: `3`
- SQLite serializer: `sqlite3 3.45.1`
- Canonical builder: Ubuntu 24.04 on `linux/amd64`. Build once and distribute the
  approved immutable bundle to every deployment architecture.
- Generated SQLite file: `hyg_v44.sqlite`
- Generated SQLite SHA-256:
  `2cc06890de1d4168f1eb91ca4ef5cde1c4923f275a52a1aedfec002df42a3321`
- Compressed input length: `13,636,362` bytes
- Decompressed input length: `33,929,696` bytes
- Generated SQLite length: `16,166,912` bytes
- Expected post-Sol-exclusion rows: `119,613`

| Identity | Value |
| --- | --- |
| Catalog ID | `hyg-v44-production` |
| Package version | `hyg-v4.4-p4-s3-r1` |
| Package lineage | `hyg-v44-production-p4-s3` (revisions `hyg-v4.4-p4-s3-r<N>`) |
| License file | `LICENSE-HYG.md`, SHA-256 `9ab0956d22d8390b54456c2afb3b47281b4a5a0313c6871f0af4489ed8395f05`, 423 bytes |
| Attribution file | `ATTRIBUTION-HYG.md`, SHA-256 `98f4eaaeb9458b58b5aab1d019d7d6d62c52dd9a3ef5ee29cbc7037d3d44152c`, 1,748 bytes |
| Constellation topology | `d3-celestial-v0.7.32-hip-coordinate-map-v1`, SHA-256 `70c253a00e0909ae0236dec0411afe837ebf8e493b2be7f84373b63c95c91621`, 88 constellations, 743 segments |
| Unresolved topology endpoints | HIP `55203` |

The build and bundle scripts validate every hash and byte length and fail
closed. The same identities are pinned in the embedded approved-catalog registry
`src/HVO.SkyMonitor.Catalog.Sqlite/ApprovedCatalogSpecifications.json`. The
deployment CLI, the release tool and both hosts read that registry, and they
refuse any catalog ID, package version, lineage, hash or row count the registry
does not name. Changing the source, preprocessing SQL, SQLite serialization,
builder architecture or schema requires a new reviewed package revision and a
registry update. A published version directory is never modified in place.

HIP 55203 is not present as a HYG 4.4 Hipparcos endpoint. Topology resolution
records it in the specification's `unresolvedEndpoints` instead of failing, and
any other unresolved endpoint is still refused.

The retained notices are [`hyg-v44-license.md`](hyg-v44-license.md) and
[`hyg-v44-attribution.md`](hyg-v44-attribution.md). Each bundle carries them as
`LICENSE-HYG.md` and `ATTRIBUTION-HYG.md`, covered by the bundle's payload
hashes. The checked-in test subset `tests/fixtures/catalog/hyg-v44-subset.sqlite`
has separate attribution, derivation and checksums in
`tests/fixtures/catalog/SOURCE-v44.md`. Its metadata `name` and
`catalog_version` differ from production, so it can never pass as the approved
package.

## Schema 3 and preprocessing 4

Preprocessing version 4 imports the decompressed CSV into SQLite 3.45.1 with a
4,096-byte page size and `user_version = 3`. It then writes three tables:

- `catalog_metadata` records the name, catalog version, source commit and URL,
  license, preprocessing and schema versions, and the conventions below.
- `celestial_objects` has one row per HYG row with a nonblank ID, right
  ascension, declination and magnitude. Row `0` (Sol) is excluded, as in 4.2.
  The columns are:
  - display name, J2000 position, magnitude and B−V color index;
  - `hipparcos_id`;
  - both proper-motion components;
  - the proper name, Bayer, Flamsteed and constellation designations, and the
    combined Bayer/Flamsteed designation;
  - the HD, HR and Gliese identifiers.
- `celestial_object_aliases` holds `(alias, object_id, kind)` with primary key
  `(alias, object_id)`. Kind is one of `proper`, `bayer`, `flamsteed`, `hd`,
  `hr` or `gliese`. Bayer and Flamsteed aliases append the constellation
  abbreviation, HD and HR aliases take an `HD ` or `HR ` prefix, and Gliese
  aliases are the upstream value. One alias may name several objects. For
  example, θ¹ Orionis (`The-1 Ori`, `41 Ori`) names rows 26155, 26156 and 26159.

The only secondary index is `celestial_objects_magnitude_id` on
`(magnitude, id)`. Validation refuses any other schema, index, metadata value,
orphaned alias, blank or untrimmed alias, or unsupported alias kind.

| Metadata key | Value |
| --- | --- |
| `coordinate_frame` | `equatorial-j2000-icrs-aligned` |
| `coordinate_equinox` | `J2000.0` |
| `coordinate_epoch` | `J2000.0` |
| `magnitude_band` | `V-heterogeneous` |
| `color_index` | `B-V` |
| `proper_motion_convention` | `mu-alpha-cos-delta-and-mu-delta-mas-per-year` |
| `proper_motion_unknown_rule` | `null-when-upstream-pair-is-zero-or-reaches-9999.99` |

Proper motion is μα·cos δ and μδ in milliarcseconds per year. Both components
are stored as `NULL` when the upstream pair is blank, when both components are
exactly zero, or when either component reaches the 9999.99 placeholder.
Barnard's Star is the canonical example: its upstream declination motion reaches
9999.99 mas/yr, so both of its components are `NULL`.

## Proper motion is off by default

The catalog retains proper motion, but rendering, projection and astrometry use
the fixed J2000 catalog positions. Astrometric uncertainty continues to report
`none-fixed-position-catalog-baseline`. A caller that needs motion reads it
explicitly through `ICelestialCatalogDetailsSource`, which returns
`CelestialCatalogObjectDetails`. It then applies the explicit
`ProperMotionPropagation` helper in `HVO.SkyMonitor.Astronomy`. Selecting
HYG 4.4 therefore changes which rows and designations are available. It does
not change how a star's position is computed.

## Side-by-side selection

Each approved lineage has its own root under the product root:

```text
/var/lib/hvo/skymonitor/catalogs/hyg-v42-production/versions/hyg-v4.2-p3-s2-r1
/var/lib/hvo/skymonitor/catalogs/hyg-v44-production/versions/hyg-v4.4-p4-s3-r1
```

`catalog install` reads the catalog ID from the bundle manifest, checks it
against the registry and installs the bundle beneath that lineage's root. It does
not select the catalog for any instance:

```bash
hvo-skymonitor catalog install --catalog-bundle /owner-private/hyg-v4.4-p4-s3-r1.bundle
hvo-skymonitor catalog select --instance-id <uuid> --catalog-version hyg-v4.4-p4-s3-r1
hvo-skymonitor catalog rollback --instance-id <uuid>
hvo-skymonitor catalog check --catalog-manifest <signed-manifest-url-or-path>
```

Package versions are unique across lineages, so `--catalog-version` names exactly
one root. A selection that crosses lineages does all of the following:

- rewrites the instance's `Catalog__RequiredCatalogId` setting and its
  `HVO_CATALOG_ROOT` mount;
- reauthenticates the changed Compose model;
- verifies health against the selected specification before it commits;
- pins the previously selected identity as a historical reference, so garbage
  collection cannot delete it while a frame still names it.

The running image must declare the `hvo-approved-catalogs-v1` catalog contract
or its superset `hvo-approved-catalogs-v2`, so an older image that only knows
HYG 4.2 refuses a HYG 4.4 selection. Cross-lineage
selection retires the retained image rollback because that rollback environment
mounts the old lineage's root. Catalog `rollback` returns to the previous catalog
selection, including across lineages.

Selection and rollback both refuse while CameraAgent still has capture-processing
work pending or awaiting retry at the drained boundary. A processing node that has
not committed regenerates its annotated derivative from whichever catalog is
mounted when it retries. Under another package, that derivative gets a different
scene identity, and central would refuse it as a second derivative of the same
capture. The refusal restores and resumes the exact prior selection and names the
pending count. Raw captures and queued uploads are untouched, so retry once
processing has drained.

Two instances on one host may select different catalog IDs, and offline
instances on different hosts may run different IDs. Each host resolves only the
root it mounts.

`catalog check` takes no lock, downloads no bundle, and writes nothing to
installed catalogs, instance selections, the distribution cache or index
rollback state. It verifies the signed manifest or index (or a previously
verified cached copy when offline) and reports one of two outcomes:

- `available`: the package is not installed;
- `installed`: the installed `versions/<version>` matches the signed identity.

An installed directory whose identity differs from the signed release is
refused. When the host is offline and no verified copy is cached, the check fails
closed without contacting the network.

## Runtime identity and health

The host requires `Catalog:RequiredCatalogId`. Installed instances also pin the
exact `Catalog:RequiredPackageVersion`; without it the resolver reads the
`current` pointer. The resolver reads the selected version's strict manifest and
checks every length, hash, schema, preprocessing, metadata and row-count
requirement against the registry. A corrupt database, a manifest from another
lineage, or a root bound to another catalog ID fails startup rather than falling
back. Schema-3 startup validation also proves in one streaming pass that the
alias table holds exactly the aliases the retained designations derive. The
alias lookup index is then built on first use.

The catalog health check reports `CatalogId`, `PackageVersion`,
`CatalogVersion`, `SchemaVersion`, the database checksum and the row count.
Scenes rendered from a schema-3 lineage such as HYG 4.4 record `CatalogId` and
`CatalogPackageVersion` alongside the existing `CatalogName`, `CatalogVersion`
and `CatalogChecksumSha256`. Schema-2 HYG 4.2 scenes record neither, because
name, version and checksum already identify them. As a result, provenance
regenerated for a retained 4.2 capture stays byte-identical to the evidence
recorded before HYG 4.4 existed. Frames recorded before these fields existed
deserialize with both values `null` and continue to resolve through their
recorded checksum.
A selection never rewrites recorded provenance. The displaced package stays
pinned as a historical reference, so garbage collection refuses to delete a
snapshot that recorded frames still name.

The scope is the HYG 4.4 star catalog only. It excludes Gaia data, a catalog
marketplace, in-place migration of an installed 4.2 root, and deep-sky object
data. Deep-sky objects are a separate composed lineage,
[HYG 4.4 + OpenNGC](hyg-v44-openngc.md), which leaves this one unchanged.
