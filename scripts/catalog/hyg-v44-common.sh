#!/usr/bin/env bash
# HYG 4.4 schema-3 constants and validators, sourced by build-hyg-v44.sh and bundle-hyg-v44.sh. The generic
# helpers and the HYG 4.2 constants come from catalog-common.sh, which this file sources first; nothing here
# redefines or weakens a HYG 4.2 value. The values below are the shell copy of the hyg-v44-production entry in
# src/HVO.SkyMonitor.Catalog.Sqlite/ApprovedCatalogSpecifications.json, and a unit test proves they agree.
# shellcheck disable=SC2034

HYG44_LIBRARY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly HYG44_LIBRARY_DIR
# shellcheck source=scripts/catalog/catalog-common.sh
source "$HYG44_LIBRARY_DIR/catalog-common.sh"

readonly HYG44_CATALOG_NAME="HYG 4.4"
readonly HYG44_CATALOG_ID="hyg-v44-production"
readonly HYG44_CATALOG_VERSION="4.4"
readonly HYG44_PACKAGE_VERSION="hyg-v4.4-p4-s3-r1"
readonly HYG44_PACKAGE_LINEAGE="hyg-v44-production-p4-s3"
readonly HYG44_SOURCE_COMMIT="53e3df311869e813ace5f1ad2ec4ce909f13256c"
readonly HYG44_SOURCE_OID="00b349893b9a53106dd488d8371e8d2fa586043e500bb3cdb8bff3931682197d"
readonly HYG44_COMPRESSED_SHA256="$HYG44_SOURCE_OID"
readonly HYG44_COMPRESSED_LENGTH="13636362"
readonly HYG44_DECOMPRESSED_SHA256="ea0e1699a1e7d48daa197b5fe567fcf447420e367f37619862ee1d6f077599cd"
readonly HYG44_DECOMPRESSED_LENGTH="33929696"
readonly HYG44_DATABASE_SHA256="2cc06890de1d4168f1eb91ca4ef5cde1c4923f275a52a1aedfec002df42a3321"
readonly HYG44_DATABASE_LENGTH="16166912"
readonly HYG44_EXPECTED_ROWS="119613"
readonly HYG44_PREPROCESSING_VERSION="4"
readonly HYG44_SCHEMA_VERSION="3"
readonly HYG44_SOURCE_PROJECT_URL="https://codeberg.org/astronexus/hyg"
readonly HYG44_SOURCE_URL="https://codeberg.org/astronexus/hyg.git/info/lfs/objects/$HYG44_SOURCE_OID"
readonly HYG44_LICENSE_IDENTIFIER="CC BY-SA 4.0"
readonly HYG44_LICENSE_URL="https://creativecommons.org/licenses/by-sa/4.0/"
readonly HYG44_LICENSE_LENGTH="423"
readonly HYG44_LICENSE_SHA256="9ab0956d22d8390b54456c2afb3b47281b4a5a0313c6871f0af4489ed8395f05"
readonly HYG44_ATTRIBUTION_LENGTH="1748"
readonly HYG44_ATTRIBUTION_SHA256="98f4eaaeb9458b58b5aab1d019d7d6d62c52dd9a3ef5ee29cbc7037d3d44152c"
readonly HYG44_DATABASE_FILE="hyg_v44.sqlite"
readonly HYG44_COORDINATE_FRAME="equatorial-j2000-icrs-aligned"
readonly HYG44_COORDINATE_EQUINOX="J2000.0"
readonly HYG44_COORDINATE_EPOCH="J2000.0"
readonly HYG44_PROPER_MOTION_CONVENTION="mu-alpha-cos-delta-and-mu-delta-mas-per-year"
readonly HYG44_PROPER_MOTION_UNKNOWN_RULE="null-when-upstream-pair-is-zero-or-reaches-9999.99"
readonly HYG44_MAGNITUDE_BAND="V-heterogeneous"
readonly HYG44_COLOR_INDEX="B-V"

hyg44_is_supported_package_version() {
    local value="$1"
    local revision
    [[ "$value" =~ ^hyg-v4\.4-p4-s3-r([1-9][0-9]*)$ ]] || return 1
    revision="${BASH_REMATCH[1]}"
    (( ${#revision} < 10 )) || { [[ ${#revision} -eq 10 ]] && (( 10#$revision <= 2147483647 )); }
}

# Writes the schema-3 database for one verified decompressed source. The SQL is the whole transformation
# recipe for preprocessing version 4; any change to it is a new preprocessing version, never a new revision.
hyg44_build_database() {
    local csv="$1"
    local database="$2"

    case "$csv" in
        *$'\n'*|*'"'*) hyg_fail "source path contains a character unsupported by sqlite3 .import"; return 1 ;;
    esac
    sqlite3 "$database" <<SQL
.bail on
PRAGMA page_size = 4096;
PRAGMA journal_mode = DELETE;
PRAGMA synchronous = OFF;
PRAGMA user_version = 3;
CREATE TABLE source_hyg (
  id TEXT, hip TEXT, hd TEXT, hr TEXT, gl TEXT, bf TEXT, proper TEXT, ra TEXT,
  dec TEXT, dist TEXT, pmra TEXT, pmdec TEXT, rv TEXT, mag TEXT, absmag TEXT,
  spect TEXT, ci TEXT, x TEXT, y TEXT, z TEXT, vx TEXT, vy TEXT, vz TEXT,
  rarad TEXT, decrad TEXT, pmrarad TEXT, pmdecrad TEXT, bayer TEXT, flam TEXT,
  con TEXT, comp TEXT, comp_primary TEXT, base TEXT, lum TEXT, var TEXT,
  var_min TEXT, var_max TEXT
);
.mode csv
.import --skip 1 "$csv" source_hyg
CREATE TABLE catalog_metadata (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL) WITHOUT ROWID;
CREATE TABLE celestial_objects (
  id TEXT PRIMARY KEY NOT NULL,
  display_name TEXT NOT NULL,
  right_ascension_hours REAL NOT NULL,
  declination_degrees REAL NOT NULL,
  magnitude REAL NOT NULL,
  color_index REAL,
  hipparcos_id TEXT,
  proper_motion_ra_cos_dec_mas_per_year REAL,
  proper_motion_dec_mas_per_year REAL,
  proper_name TEXT,
  bayer TEXT,
  flamsteed TEXT,
  constellation TEXT,
  bayer_flamsteed TEXT,
  henry_draper_id TEXT,
  harvard_revised_id TEXT,
  gliese_id TEXT
) WITHOUT ROWID;
CREATE TABLE celestial_object_aliases (
  alias TEXT NOT NULL,
  object_id TEXT NOT NULL,
  kind TEXT NOT NULL,
  PRIMARY KEY (alias, object_id)
) WITHOUT ROWID;
INSERT INTO catalog_metadata VALUES
  ('catalog_version', '$HYG44_CATALOG_VERSION'),
  ('color_index', '$HYG44_COLOR_INDEX'),
  ('coordinate_epoch', '$HYG44_COORDINATE_EPOCH'),
  ('coordinate_equinox', '$HYG44_COORDINATE_EQUINOX'),
  ('coordinate_frame', '$HYG44_COORDINATE_FRAME'),
  ('license', '$HYG44_LICENSE_IDENTIFIER'),
  ('magnitude_band', '$HYG44_MAGNITUDE_BAND'),
  ('name', '$HYG44_CATALOG_NAME'),
  ('preprocessing_version', '$HYG44_PREPROCESSING_VERSION'),
  ('proper_motion_convention', '$HYG44_PROPER_MOTION_CONVENTION'),
  ('proper_motion_unknown_rule', '$HYG44_PROPER_MOTION_UNKNOWN_RULE'),
  ('schema_version', '$HYG44_SCHEMA_VERSION'),
  ('source_commit', '$HYG44_SOURCE_COMMIT'),
  ('source_url', '$HYG44_SOURCE_PROJECT_URL');
CREATE TEMP VIEW source_motion AS
SELECT id,
       pmra = '' OR pmdec = '' OR
       abs(CAST(pmra AS REAL)) >= 9999.99 OR abs(CAST(pmdec AS REAL)) >= 9999.99 OR
       (CAST(pmra AS REAL) = 0 AND CAST(pmdec AS REAL) = 0) AS unknown
FROM source_hyg;
INSERT INTO celestial_objects
SELECT s.id, CASE WHEN s.proper = '' THEN s.id ELSE s.proper END,
       CAST(s.ra AS REAL), CAST(s.dec AS REAL), CAST(s.mag AS REAL),
       CASE WHEN s.ci = '' THEN NULL ELSE CAST(s.ci AS REAL) END,
       NULLIF(s.hip, ''),
       CASE WHEN m.unknown THEN NULL ELSE CAST(s.pmra AS REAL) END,
       CASE WHEN m.unknown THEN NULL ELSE CAST(s.pmdec AS REAL) END,
       NULLIF(s.proper, ''), NULLIF(s.bayer, ''), NULLIF(s.flam, ''), NULLIF(s.con, ''), NULLIF(s.bf, ''),
       NULLIF(s.hd, ''), NULLIF(s.hr, ''), NULLIF(s.gl, '')
FROM source_hyg AS s JOIN source_motion AS m ON m.id = s.id
WHERE s.id != '' AND s.id != '0' AND s.ra != '' AND s.dec != '' AND s.mag != ''
ORDER BY s.id;
INSERT INTO celestial_object_aliases (alias, object_id, kind)
SELECT alias, object_id, kind FROM (
  SELECT proper_name AS alias, id AS object_id, 'proper' AS kind FROM celestial_objects WHERE proper_name IS NOT NULL
  UNION ALL
  SELECT bayer || ' ' || constellation, id, 'bayer' FROM celestial_objects WHERE bayer IS NOT NULL AND constellation IS NOT NULL
  UNION ALL
  SELECT flamsteed || ' ' || constellation, id, 'flamsteed' FROM celestial_objects WHERE flamsteed IS NOT NULL AND constellation IS NOT NULL
  UNION ALL
  SELECT 'HD ' || henry_draper_id, id, 'hd' FROM celestial_objects WHERE henry_draper_id IS NOT NULL
  UNION ALL
  SELECT 'HR ' || harvard_revised_id, id, 'hr' FROM celestial_objects WHERE harvard_revised_id IS NOT NULL
  UNION ALL
  SELECT gliese_id, id, 'gliese' FROM celestial_objects WHERE gliese_id IS NOT NULL
)
ORDER BY alias, object_id;
DROP VIEW source_motion;
DROP TABLE source_hyg;
CREATE INDEX celestial_objects_magnitude_id ON celestial_objects (magnitude, id COLLATE BINARY);
VACUUM;
SQL
}

hyg44_validate_database_structure() {
    local database="$1"
    local result

    result="$(hyg_sqlite_scalar "$database" "SELECT group_concat(name || ':' || type, ',') FROM (SELECT name, type FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name);")"
    [[ "$result" == "celestial_objects_magnitude_id:index,catalog_metadata:table,celestial_object_aliases:table,celestial_objects:table" ]] || \
        hyg_fail "catalog database contains an unexpected table or index set"
    result="$(hyg_sqlite_scalar "$database" "SELECT group_concat(name || ':' || type || ':' || ncol || ':' || wr || ':' || strict, ',') FROM (SELECT name, type, ncol, wr, strict FROM pragma_table_list WHERE schema = 'main' AND name NOT LIKE 'sqlite_%' ORDER BY name);")"
    [[ "$result" == "catalog_metadata:table:2:1:0,celestial_object_aliases:table:3:1:0,celestial_objects:table:17:1:0" ]] || \
        hyg_fail "catalog database tables have incompatible options"
    result="$(hyg_sqlite_scalar "$database" "SELECT group_concat(name || ':' || type || ':' || \"notnull\" || ':' || coalesce(dflt_value, '-') || ':' || pk || ':' || hidden, ',') FROM (SELECT name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo('catalog_metadata') ORDER BY cid);")"
    [[ "$result" == "key:TEXT:1:-:1:0,value:TEXT:1:-:0:0" ]] || hyg_fail "catalog_metadata has an incompatible schema"
    result="$(hyg_sqlite_scalar "$database" "SELECT group_concat(name || ':' || type || ':' || \"notnull\" || ':' || coalesce(dflt_value, '-') || ':' || pk || ':' || hidden, ',') FROM (SELECT name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo('celestial_objects') ORDER BY cid);")"
    [[ "$result" == "id:TEXT:1:-:1:0,display_name:TEXT:1:-:0:0,right_ascension_hours:REAL:1:-:0:0,declination_degrees:REAL:1:-:0:0,magnitude:REAL:1:-:0:0,color_index:REAL:0:-:0:0,hipparcos_id:TEXT:0:-:0:0,proper_motion_ra_cos_dec_mas_per_year:REAL:0:-:0:0,proper_motion_dec_mas_per_year:REAL:0:-:0:0,proper_name:TEXT:0:-:0:0,bayer:TEXT:0:-:0:0,flamsteed:TEXT:0:-:0:0,constellation:TEXT:0:-:0:0,bayer_flamsteed:TEXT:0:-:0:0,henry_draper_id:TEXT:0:-:0:0,harvard_revised_id:TEXT:0:-:0:0,gliese_id:TEXT:0:-:0:0" ]] || \
        hyg_fail "celestial_objects has an incompatible schema"
    result="$(hyg_sqlite_scalar "$database" "SELECT group_concat(name || ':' || type || ':' || \"notnull\" || ':' || coalesce(dflt_value, '-') || ':' || pk || ':' || hidden, ',') FROM (SELECT name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo('celestial_object_aliases') ORDER BY cid);")"
    [[ "$result" == "alias:TEXT:1:-:1:0,object_id:TEXT:1:-:2:0,kind:TEXT:1:-:0:0" ]] || \
        hyg_fail "celestial_object_aliases has an incompatible schema"
    result="$(hyg_sqlite_scalar "$database" "SELECT group_concat(name || ':' || \"unique\" || ':' || origin || ':' || partial, ',') FROM (SELECT name, \"unique\", origin, partial FROM pragma_index_list('celestial_objects') WHERE origin != 'pk' ORDER BY name);")"
    [[ "$result" == "celestial_objects_magnitude_id:0:c:0" ]] || hyg_fail "catalog database contains an unexpected secondary index"
    result="$(hyg_sqlite_scalar "$database" "SELECT count(*) FROM pragma_index_list('celestial_object_aliases') WHERE origin != 'pk';")"
    [[ "$result" == 0 ]] || hyg_fail "catalog alias table contains an unexpected secondary index"
    result="$(hyg_sqlite_scalar "$database" "SELECT group_concat(name || ':' || desc || ':' || coll || ':' || key, ',') FROM (SELECT name, desc, coll, key FROM pragma_index_xinfo('celestial_objects_magnitude_id') ORDER BY seqno);")"
    [[ "$result" == "magnitude:0:BINARY:1,id:0:BINARY:1" ]] || hyg_fail "catalog ordering index is missing or incompatible"
}

hyg44_validate_database_invariants() {
    local database="$1"
    local result

    hyg_validate_database_invariants "$database"
    result="$(hyg_sqlite_scalar "$database" "SELECT count(*) FROM celestial_objects WHERE (proper_motion_ra_cos_dec_mas_per_year IS NULL) != (proper_motion_dec_mas_per_year IS NULL);")"
    [[ "$result" == 0 ]] || hyg_fail "catalog database contains a half-known proper motion"
    result="$(hyg_sqlite_scalar "$database" "SELECT count(*) FROM celestial_object_aliases AS a LEFT JOIN celestial_objects AS o ON o.id = a.object_id WHERE o.id IS NULL OR trim(a.alias) = '' OR a.alias != trim(a.alias) OR a.kind NOT IN ('proper', 'bayer', 'flamsteed', 'hd', 'hr', 'gliese');")"
    [[ "$result" == 0 ]] || hyg_fail "catalog alias table contains an orphaned, blank, or unsupported alias"
}

hyg44_validate_database() {
    local database="$1"
    local result
    local expected_metadata
    local suffix

    hyg_verify_file "$database" "$HYG44_DATABASE_LENGTH" "$HYG44_DATABASE_SHA256" "catalog database"
    for suffix in -journal -wal -shm; do
        [[ ! -e "$database$suffix" && ! -L "$database$suffix" ]] || hyg_fail "catalog database has an unexpected SQLite sidecar: $database$suffix"
    done
    result="$(hyg_sqlite_scalar "$database" 'PRAGMA integrity_check;')"
    [[ "$result" == "ok" ]] || hyg_fail "catalog database integrity_check failed: $result"
    result="$(hyg_sqlite_scalar "$database" 'PRAGMA user_version;')"
    [[ "$result" == "$HYG44_SCHEMA_VERSION" ]] || hyg_fail "catalog database user_version is $result, expected $HYG44_SCHEMA_VERSION"
    hyg44_validate_database_structure "$database"
    expected_metadata="catalog_version=$HYG44_CATALOG_VERSION"$'\n'"color_index=$HYG44_COLOR_INDEX"$'\n'"coordinate_epoch=$HYG44_COORDINATE_EPOCH"$'\n'"coordinate_equinox=$HYG44_COORDINATE_EQUINOX"$'\n'"coordinate_frame=$HYG44_COORDINATE_FRAME"$'\n'"license=$HYG44_LICENSE_IDENTIFIER"$'\n'"magnitude_band=$HYG44_MAGNITUDE_BAND"$'\n'"name=$HYG44_CATALOG_NAME"$'\n'"preprocessing_version=$HYG44_PREPROCESSING_VERSION"$'\n'"proper_motion_convention=$HYG44_PROPER_MOTION_CONVENTION"$'\n'"proper_motion_unknown_rule=$HYG44_PROPER_MOTION_UNKNOWN_RULE"$'\n'"schema_version=$HYG44_SCHEMA_VERSION"$'\n'"source_commit=$HYG44_SOURCE_COMMIT"$'\n'"source_url=$HYG44_SOURCE_PROJECT_URL"
    result="$(hyg_sqlite_scalar "$database" "SELECT group_concat(key || '=' || value, char(10)) FROM (SELECT key, value FROM catalog_metadata ORDER BY key);")"
    [[ "$result" == "$expected_metadata" ]] || hyg_fail "catalog database metadata is missing or incompatible"
    result="$(hyg_sqlite_scalar "$database" 'SELECT count(*) FROM celestial_objects;')"
    [[ "$result" == "$HYG44_EXPECTED_ROWS" ]] || hyg_fail "catalog database contains $result rows, expected $HYG44_EXPECTED_ROWS"
    hyg44_validate_database_invariants "$database"
    for suffix in -journal -wal -shm; do
        [[ ! -e "$database$suffix" && ! -L "$database$suffix" ]] || hyg_fail "read-only validation created an SQLite sidecar: $database$suffix"
    done
}

hyg44_validate_manifest() {
    local bundle="$1"
    local manifest="$bundle/$HYG_MANIFEST_FILE"
    local package_version
    hyg_validate_manifest_document "$manifest" || return 1

    hyg_json_exact "$manifest" '$.package.kind' text production
    hyg_json_exact_keys "$manifest" '$' 'catalog,database,license,manifestVersion,package,preprocessingVersion,schemaVersion,serializer,source,topology'
    hyg_json_exact_keys "$manifest" '$.package' 'kind,version'
    hyg_json_exact_keys "$manifest" '$.catalog' 'id,name,version'
    hyg_json_exact_keys "$manifest" '$.source' 'compressed,decompressed,downloadUrl,oid,projectUrl'
    hyg_json_exact_keys "$manifest" '$.source.compressed' 'length,sha256'
    hyg_json_exact_keys "$manifest" '$.source.decompressed' 'length,sha256'
    hyg_json_exact_keys "$manifest" '$.serializer' 'name,version'
    hyg_json_exact_keys "$manifest" '$.database' 'length,relativePath,requiredColumn,rowCount,sha256,solCount'
    hyg_json_exact_keys "$manifest" '$.license' 'attribution,file,identifier,url'
    hyg_json_exact_keys "$manifest" '$.license.file' 'length,relativePath,sha256'
    hyg_json_exact_keys "$manifest" '$.license.attribution' 'length,relativePath,sha256'
    hyg_json_exact_keys "$manifest" '$.topology' 'constellationCount,identity,segmentCount,sha256'

    hyg_json_exact "$manifest" '$.manifestVersion' integer 2
    package_version="$(hyg_json_value "$manifest" '$.package.version')"
    [[ "$(hyg_json_type "$manifest" '$.package.version')" == "text" ]] &&
        hyg44_is_supported_package_version "$package_version" || \
        hyg_fail "bundle manifest has an invalid package version"
    hyg_json_exact "$manifest" '$.catalog.name' text "$HYG44_CATALOG_NAME"
    hyg_json_exact "$manifest" '$.catalog.id' text "$HYG44_CATALOG_ID"
    hyg_json_exact "$manifest" '$.catalog.version' text "$HYG44_CATALOG_VERSION"
    hyg_json_exact "$manifest" '$.source.projectUrl' text "$HYG44_SOURCE_PROJECT_URL"
    hyg_json_exact "$manifest" '$.source.downloadUrl' text "$HYG44_SOURCE_URL"
    hyg_json_exact "$manifest" '$.source.oid' text "$HYG44_SOURCE_OID"
    hyg_json_exact "$manifest" '$.source.compressed.length' integer "$HYG44_COMPRESSED_LENGTH"
    hyg_json_exact "$manifest" '$.source.compressed.sha256' text "$HYG44_COMPRESSED_SHA256"
    hyg_json_exact "$manifest" '$.source.decompressed.length' integer "$HYG44_DECOMPRESSED_LENGTH"
    hyg_json_exact "$manifest" '$.source.decompressed.sha256' text "$HYG44_DECOMPRESSED_SHA256"
    hyg_json_exact "$manifest" '$.preprocessingVersion' text "$HYG44_PREPROCESSING_VERSION"
    hyg_json_exact "$manifest" '$.schemaVersion' text "$HYG44_SCHEMA_VERSION"
    hyg_json_exact "$manifest" '$.serializer.name' text sqlite3
    hyg_json_exact "$manifest" '$.serializer.version' text "$HYG_REQUIRED_SQLITE_VERSION"
    hyg_json_exact "$manifest" '$.database.relativePath' text "$HYG44_DATABASE_FILE"
    hyg_json_exact "$manifest" '$.database.length' integer "$HYG44_DATABASE_LENGTH"
    hyg_json_exact "$manifest" '$.database.sha256' text "$HYG44_DATABASE_SHA256"
    hyg_json_exact "$manifest" '$.database.rowCount' integer "$HYG44_EXPECTED_ROWS"
    hyg_json_exact "$manifest" '$.database.solCount' integer 0
    hyg_json_exact "$manifest" '$.database.requiredColumn' text hipparcos_id
    hyg_json_exact "$manifest" '$.license.identifier' text "$HYG44_LICENSE_IDENTIFIER"
    hyg_json_exact "$manifest" '$.license.url' text "$HYG44_LICENSE_URL"
    hyg_json_exact "$manifest" '$.license.file.relativePath' text "$HYG_LICENSE_FILE"
    hyg_json_exact "$manifest" '$.license.file.length' integer "$HYG44_LICENSE_LENGTH"
    hyg_json_exact "$manifest" '$.license.file.sha256' text "$HYG44_LICENSE_SHA256"
    hyg_json_exact "$manifest" '$.license.attribution.relativePath' text "$HYG_ATTRIBUTION_FILE"
    hyg_json_exact "$manifest" '$.license.attribution.length' integer "$HYG44_ATTRIBUTION_LENGTH"
    hyg_json_exact "$manifest" '$.license.attribution.sha256' text "$HYG44_ATTRIBUTION_SHA256"
    hyg_json_exact "$manifest" '$.topology.identity' text "$HYG_TOPOLOGY_VERSION"
    hyg_json_exact "$manifest" '$.topology.sha256' text "$HYG_TOPOLOGY_SHA256"
    hyg_json_exact "$manifest" '$.topology.constellationCount' integer 88
    hyg_json_exact "$manifest" '$.topology.segmentCount' integer 743
}

hyg44_validate_bundle() {
    local bundle="$1"
    local -a entries

    HYG_VALIDATION_FAILED=0
    [[ -d "$bundle" && ! -L "$bundle" ]] || hyg_fail "bundle is missing or is not a directory: $bundle"
    shopt -s nullglob dotglob
    entries=("$bundle"/*)
    shopt -u nullglob dotglob
    [[ ${#entries[@]} -eq 4 ]] || hyg_fail "bundle must contain exactly the manifest and three retained payload files"

    hyg44_validate_manifest "$bundle"
    hyg_verify_file "$bundle/$HYG_LICENSE_FILE" "$HYG44_LICENSE_LENGTH" "$HYG44_LICENSE_SHA256" "HYG license"
    hyg_verify_file "$bundle/$HYG_ATTRIBUTION_FILE" "$HYG44_ATTRIBUTION_LENGTH" "$HYG44_ATTRIBUTION_SHA256" "HYG attribution"
    hyg44_validate_database "$bundle/$HYG44_DATABASE_FILE"
    [[ "$HYG_VALIDATION_FAILED" == 0 ]]
}
