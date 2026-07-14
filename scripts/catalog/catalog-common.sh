#!/usr/bin/env bash

readonly HYG_CATALOG_NAME="HYG 4.2"
readonly HYG_CATALOG_VERSION="4.2"
readonly HYG_PACKAGE_VERSION="hyg-v4.2-p3-s2-r1"
readonly HYG_SOURCE_OID="5ca9431ff364c8002a4a3efa91b2b9296746aea1543374db4cb6b4fab049d601"
readonly HYG_COMPRESSED_SHA256="$HYG_SOURCE_OID"
readonly HYG_COMPRESSED_LENGTH="13636976"
readonly HYG_DECOMPRESSED_SHA256="b2983a8d934e4f031cdb67bdd6c3437f8c5143cd6606a9573a9a9ac4b6375fd2"
readonly HYG_DECOMPRESSED_LENGTH="33932800"
readonly HYG_DATABASE_SHA256="b51d18b722199e89aa8fe4622ebe507346c75effb375e546881452a263f0b9e2"
readonly HYG_DATABASE_LENGTH="9302016"
readonly HYG_EXPECTED_ROWS="119625"
readonly HYG_PREPROCESSING_VERSION="3"
readonly HYG_SCHEMA_VERSION="2"
readonly HYG_REQUIRED_SQLITE_VERSION="3.45.1"
readonly HYG_SOURCE_PROJECT_URL="https://codeberg.org/astronexus/hyg"
readonly HYG_SOURCE_URL="https://codeberg.org/astronexus/hyg.git/info/lfs/objects/$HYG_SOURCE_OID"
readonly HYG_LICENSE_IDENTIFIER="CC BY-SA 4.0"
readonly HYG_LICENSE_URL="https://creativecommons.org/licenses/by-sa/4.0/"
readonly HYG_LICENSE_LENGTH="423"
readonly HYG_LICENSE_SHA256="9ab0956d22d8390b54456c2afb3b47281b4a5a0313c6871f0af4489ed8395f05"
readonly HYG_ATTRIBUTION_LENGTH="1361"
readonly HYG_ATTRIBUTION_SHA256="e3addc3480a0d0f07129f332b0dea592fa315373f21111d54ac8e2aebd03b5f1"
readonly HYG_DATABASE_FILE="hyg_v42.sqlite"
readonly HYG_LICENSE_FILE="LICENSE-HYG.md"
readonly HYG_ATTRIBUTION_FILE="ATTRIBUTION-HYG.md"
readonly HYG_MANIFEST_FILE="manifest.json"
readonly HYG_TOPOLOGY_VERSION="d3-celestial-v0.7.32-hip-coordinate-map-v1"
readonly HYG_TOPOLOGY_SHA256="70c253a00e0909ae0236dec0411afe837ebf8e493b2be7f84373b63c95c91621"
readonly HYG_TOPOLOGY_LENGTH="12081"
readonly HYG_MAXIMUM_MANIFEST_LENGTH="65536"

hyg_fail() {
    printf 'catalog error: %s\n' "$*" >&2
    return 1
}

hyg_require_commands() {
    local command_name
    for command_name in "$@"; do
        command -v "$command_name" >/dev/null || hyg_fail "missing required command: $command_name"
    done
}

hyg_sha256() {
    local output
    output="$(sha256sum "$1")"
    printf '%s\n' "${output%% *}"
}

hyg_file_length() {
    local length
    length="$(wc -c < "$1")"
    printf '%d\n' "$length"
}

hyg_verify_file() {
    local path="$1"
    local expected_length="$2"
    local expected_sha256="$3"
    local label="$4"

    [[ -f "$path" && ! -L "$path" ]] || hyg_fail "$label is missing or is not a regular file: $path"
    [[ "$(hyg_file_length "$path")" == "$expected_length" ]] || hyg_fail "$label byte length does not match its manifest"
    [[ "$(hyg_sha256 "$path")" == "$expected_sha256" ]] || hyg_fail "$label SHA-256 does not match its manifest"
}

hyg_check_sqlite_version() {
    local actual_version
    actual_version="$(sqlite3 --version)"
    actual_version="${actual_version%% *}"
    [[ "$actual_version" == "$HYG_REQUIRED_SQLITE_VERSION" ]] || \
        hyg_fail "sqlite3 $HYG_REQUIRED_SQLITE_VERSION is required; found $actual_version"
}

hyg_sqlite_scalar() {
    sqlite3 -batch -noheader -readonly "$1" "$2"
}

hyg_validate_database() {
    local database="$1"
    local result
    local expected_metadata
    local suffix

    hyg_verify_file "$database" "$HYG_DATABASE_LENGTH" "$HYG_DATABASE_SHA256" "catalog database"
    for suffix in -journal -wal -shm; do
        [[ ! -e "$database$suffix" && ! -L "$database$suffix" ]] || hyg_fail "catalog database has an unexpected SQLite sidecar: $database$suffix"
    done

    result="$(hyg_sqlite_scalar "$database" 'PRAGMA integrity_check;')"
    [[ "$result" == "ok" ]] || hyg_fail "catalog database integrity_check failed: $result"
    result="$(hyg_sqlite_scalar "$database" 'PRAGMA user_version;')"
    [[ "$result" == "$HYG_SCHEMA_VERSION" ]] || hyg_fail "catalog database user_version is $result, expected $HYG_SCHEMA_VERSION"

    result="$(hyg_sqlite_scalar "$database" "SELECT group_concat(name || ':' || type, ',') FROM (SELECT name, type FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name);")"
    [[ "$result" == "celestial_objects_magnitude_id:index,catalog_metadata:table,celestial_objects:table" ]] || \
        hyg_fail "catalog database contains an unexpected table or index set"
    result="$(hyg_sqlite_scalar "$database" "SELECT group_concat(name || ':' || type || ':' || \"notnull\" || ':' || pk, ',') FROM (SELECT name, type, \"notnull\", pk FROM pragma_table_info('catalog_metadata') ORDER BY cid);")"
    [[ "$result" == "key:TEXT:1:1,value:TEXT:1:0" ]] || hyg_fail "catalog_metadata has an incompatible schema"
    result="$(hyg_sqlite_scalar "$database" "SELECT group_concat(name || ':' || type || ':' || \"notnull\" || ':' || pk, ',') FROM (SELECT name, type, \"notnull\", pk FROM pragma_table_info('celestial_objects') ORDER BY cid);")"
    [[ "$result" == "id:TEXT:1:1,display_name:TEXT:1:0,right_ascension_hours:REAL:1:0,declination_degrees:REAL:1:0,magnitude:REAL:1:0,color_index:REAL:0:0,hipparcos_id:TEXT:0:0" ]] || \
        hyg_fail "celestial_objects has an incompatible schema"
    result="$(hyg_sqlite_scalar "$database" "SELECT group_concat(name, ',') FROM (SELECT name FROM pragma_index_info('celestial_objects_magnitude_id') ORDER BY seqno);")"
    [[ "$result" == "magnitude,id" ]] || hyg_fail "catalog ordering index is missing or incompatible"

    expected_metadata=$'catalog_version=4.2\nlicense=CC BY-SA 4.0\nname=HYG 4.2\npreprocessing_version=3\nschema_version=2\nsource_url=https://codeberg.org/astronexus/hyg'
    result="$(hyg_sqlite_scalar "$database" "SELECT group_concat(key || '=' || value, char(10)) FROM (SELECT key, value FROM catalog_metadata ORDER BY key);")"
    [[ "$result" == "$expected_metadata" ]] || hyg_fail "catalog database metadata is missing or incompatible"
    result="$(hyg_sqlite_scalar "$database" 'SELECT count(*) FROM celestial_objects;')"
    [[ "$result" == "$HYG_EXPECTED_ROWS" ]] || hyg_fail "catalog database contains $result rows, expected $HYG_EXPECTED_ROWS"
    result="$(hyg_sqlite_scalar "$database" "SELECT count(*) FROM celestial_objects WHERE id = '0' OR lower(trim(display_name)) = 'sol';")"
    [[ "$result" == "0" ]] || hyg_fail "catalog database contains Sol"
    result="$(hyg_sqlite_scalar "$database" "SELECT count(*) FROM (SELECT hipparcos_id FROM celestial_objects WHERE hipparcos_id IS NOT NULL AND trim(hipparcos_id) != '' GROUP BY hipparcos_id HAVING count(*) != 1);")"
    [[ "$result" == "0" ]] || hyg_fail "catalog database contains duplicate nonblank Hipparcos IDs"

    for suffix in -journal -wal -shm; do
        [[ ! -e "$database$suffix" && ! -L "$database$suffix" ]] || hyg_fail "read-only validation created an SQLite sidecar: $database$suffix"
    done
}

HYG_MANIFEST_PACKAGE_VERSION=""

hyg_json_query() {
    local manifest="$1"
    local query="$2"
    local quoted_manifest="${manifest//\'/\'\'}"
    sqlite3 -batch -noheader ':memory:' \
        "WITH input(document) AS (SELECT CAST(readfile('$quoted_manifest') AS TEXT)) $query"
}

hyg_json_type() {
    local path="${2//\'/\'\'}"
    hyg_json_query "$1" "SELECT json_type(document, '$path') FROM input;"
}

hyg_json_value() {
    local path="${2//\'/\'\'}"
    hyg_json_query "$1" "SELECT json_extract(document, '$path') FROM input;"
}

hyg_json_keys() {
    local path="${2//\'/\'\'}"
    hyg_json_query "$1" "SELECT group_concat(key, ',') FROM (SELECT key FROM input, json_each(document, '$path') ORDER BY key);"
}

hyg_json_exact() {
    local manifest="$1"
    local path="$2"
    local expected_type="$3"
    local expected_value="$4"
    [[ "$(hyg_json_type "$manifest" "$path")" == "$expected_type" ]] || hyg_fail "bundle manifest has an invalid type for $path"
    [[ "$(hyg_json_value "$manifest" "$path")" == "$expected_value" ]] || hyg_fail "bundle manifest has an incompatible value for $path"
}

hyg_json_exact_keys() {
    local manifest="$1"
    local path="$2"
    local expected="$3"
    [[ "$(hyg_json_type "$manifest" "$path")" == "object" ]] || hyg_fail "bundle manifest property $path must be an object"
    [[ "$(hyg_json_keys "$manifest" "$path")" == "$expected" ]] || hyg_fail "bundle manifest has missing or unknown properties at $path"
}

hyg_is_supported_package_version() {
    local value="$1"
    local revision
    [[ "$value" =~ ^hyg-v4\.2-p3-s2-r([1-9][0-9]*)$ ]] || return 1
    revision="${BASH_REMATCH[1]}"
    (( ${#revision} < 10 )) || { [[ ${#revision} -eq 10 ]] && (( 10#$revision <= 2147483647 )); }
}

hyg_validate_manifest() {
    local bundle="$1"
    local manifest="$bundle/$HYG_MANIFEST_FILE"
    local duplicate_count

    [[ -f "$manifest" && ! -L "$manifest" ]] || hyg_fail "bundle manifest is missing or is not a regular file: $manifest"
    [[ "$(hyg_file_length "$manifest")" -le "$HYG_MAXIMUM_MANIFEST_LENGTH" ]] || hyg_fail "bundle manifest exceeds the maximum byte length"
    [[ "$(hyg_json_query "$manifest" 'SELECT json_valid(document) FROM input;')" == "1" ]] || hyg_fail "bundle manifest is malformed JSON"
    duplicate_count="$(hyg_json_query "$manifest" "SELECT count(*) FROM (SELECT parent, key FROM input, json_tree(document) WHERE key IS NOT NULL GROUP BY parent, key HAVING count(*) > 1);")"
    [[ "$duplicate_count" == "0" ]] || hyg_fail "bundle manifest contains duplicate properties"

    hyg_json_exact "$manifest" '$.package.kind' text production
    hyg_json_exact_keys "$manifest" '$' 'catalog,database,license,manifestVersion,package,preprocessingVersion,schemaVersion,serializer,source,topology'
    hyg_json_exact_keys "$manifest" '$.package' 'kind,version'
    hyg_json_exact_keys "$manifest" '$.catalog' 'name,version'
    hyg_json_exact_keys "$manifest" '$.source' 'compressed,decompressed,downloadUrl,oid,projectUrl'
    hyg_json_exact_keys "$manifest" '$.source.compressed' 'length,sha256'
    hyg_json_exact_keys "$manifest" '$.source.decompressed' 'length,sha256'
    hyg_json_exact_keys "$manifest" '$.serializer' 'name,version'
    hyg_json_exact_keys "$manifest" '$.database' 'length,relativePath,requiredColumn,rowCount,sha256,solCount'
    hyg_json_exact_keys "$manifest" '$.license' 'attribution,file,identifier,url'
    hyg_json_exact_keys "$manifest" '$.license.file' 'length,relativePath,sha256'
    hyg_json_exact_keys "$manifest" '$.license.attribution' 'length,relativePath,sha256'
    hyg_json_exact_keys "$manifest" '$.topology' 'constellationCount,identity,segmentCount,sha256'

    hyg_json_exact "$manifest" '$.manifestVersion' integer 1
    HYG_MANIFEST_PACKAGE_VERSION="$(hyg_json_value "$manifest" '$.package.version')"
    [[ "$(hyg_json_type "$manifest" '$.package.version')" == "text" ]] &&
        hyg_is_supported_package_version "$HYG_MANIFEST_PACKAGE_VERSION" || \
        hyg_fail "bundle manifest has an invalid package version"
    hyg_json_exact "$manifest" '$.catalog.name' text "$HYG_CATALOG_NAME"
    hyg_json_exact "$manifest" '$.catalog.version' text "$HYG_CATALOG_VERSION"
    hyg_json_exact "$manifest" '$.source.projectUrl' text "$HYG_SOURCE_PROJECT_URL"
    hyg_json_exact "$manifest" '$.source.downloadUrl' text "$HYG_SOURCE_URL"
    hyg_json_exact "$manifest" '$.source.oid' text "$HYG_SOURCE_OID"
    hyg_json_exact "$manifest" '$.source.compressed.length' integer "$HYG_COMPRESSED_LENGTH"
    hyg_json_exact "$manifest" '$.source.compressed.sha256' text "$HYG_COMPRESSED_SHA256"
    hyg_json_exact "$manifest" '$.source.decompressed.length' integer "$HYG_DECOMPRESSED_LENGTH"
    hyg_json_exact "$manifest" '$.source.decompressed.sha256' text "$HYG_DECOMPRESSED_SHA256"
    hyg_json_exact "$manifest" '$.preprocessingVersion' text "$HYG_PREPROCESSING_VERSION"
    hyg_json_exact "$manifest" '$.schemaVersion' text "$HYG_SCHEMA_VERSION"
    hyg_json_exact "$manifest" '$.serializer.name' text sqlite3
    hyg_json_exact "$manifest" '$.serializer.version' text "$HYG_REQUIRED_SQLITE_VERSION"
    hyg_json_exact "$manifest" '$.database.relativePath' text "$HYG_DATABASE_FILE"
    hyg_json_exact "$manifest" '$.database.length' integer "$HYG_DATABASE_LENGTH"
    hyg_json_exact "$manifest" '$.database.sha256' text "$HYG_DATABASE_SHA256"
    hyg_json_exact "$manifest" '$.database.rowCount' integer "$HYG_EXPECTED_ROWS"
    hyg_json_exact "$manifest" '$.database.solCount' integer 0
    hyg_json_exact "$manifest" '$.database.requiredColumn' text hipparcos_id
    hyg_json_exact "$manifest" '$.license.identifier' text "$HYG_LICENSE_IDENTIFIER"
    hyg_json_exact "$manifest" '$.license.url' text "$HYG_LICENSE_URL"
    hyg_json_exact "$manifest" '$.license.file.relativePath' text "$HYG_LICENSE_FILE"
    hyg_json_exact "$manifest" '$.license.file.length' integer "$HYG_LICENSE_LENGTH"
    hyg_json_exact "$manifest" '$.license.file.sha256' text "$HYG_LICENSE_SHA256"
    hyg_json_exact "$manifest" '$.license.attribution.relativePath' text "$HYG_ATTRIBUTION_FILE"
    hyg_json_exact "$manifest" '$.license.attribution.length' integer "$HYG_ATTRIBUTION_LENGTH"
    hyg_json_exact "$manifest" '$.license.attribution.sha256' text "$HYG_ATTRIBUTION_SHA256"
    hyg_json_exact "$manifest" '$.topology.identity' text "$HYG_TOPOLOGY_VERSION"
    hyg_json_exact "$manifest" '$.topology.sha256' text "$HYG_TOPOLOGY_SHA256"
    hyg_json_exact "$manifest" '$.topology.constellationCount' integer 88
    hyg_json_exact "$manifest" '$.topology.segmentCount' integer 743
}

hyg_validate_bundle() {
    local bundle="$1"
    local -a entries

    [[ -d "$bundle" && ! -L "$bundle" ]] || hyg_fail "bundle is missing or is not a directory: $bundle"
    shopt -s nullglob dotglob
    entries=("$bundle"/*)
    shopt -u nullglob dotglob
    [[ ${#entries[@]} -eq 4 ]] || hyg_fail "bundle must contain exactly the manifest and three retained payload files"

    hyg_validate_manifest "$bundle"
    hyg_verify_file "$bundle/$HYG_LICENSE_FILE" "$HYG_LICENSE_LENGTH" "$HYG_LICENSE_SHA256" "HYG license"
    hyg_verify_file "$bundle/$HYG_ATTRIBUTION_FILE" "$HYG_ATTRIBUTION_LENGTH" "$HYG_ATTRIBUTION_SHA256" "HYG attribution"
    hyg_validate_database "$bundle/$HYG_DATABASE_FILE"
}
