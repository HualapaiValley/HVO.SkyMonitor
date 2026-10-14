#!/usr/bin/env bash
set -euo pipefail
umask 022

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
readonly REPO_ROOT
# shellcheck source=scripts/catalog/hyg-v44-common.sh
source "$SCRIPT_DIR/hyg-v44-common.sh"

usage() {
    printf 'Usage: %s DATABASE OUTPUT_BUNDLE_DIRECTORY\n' "$0" >&2
}

if [[ $# -ne 2 ]]; then
    usage
    exit 2
fi

hyg_require_commands mv realpath sha256sum sqlite3 sync wc
hyg_check_sqlite_version

DATABASE="$(realpath "$1")"
readonly DATABASE
OUTPUT="$(realpath -m "$2")"
readonly OUTPUT
OUTPUT_PARENT="$(dirname "$OUTPUT")"
readonly OUTPUT_PARENT
readonly LICENSE_SOURCE="$REPO_ROOT/docs/catalog/hyg-v44-license.md"
readonly ATTRIBUTION_SOURCE="$REPO_ROOT/docs/catalog/hyg-v44-attribution.md"
readonly TOPOLOGY_SOURCE="$REPO_ROOT/src/HVO.SkyMonitor.Astronomy/Data/d3-celestial-v0.7.32-topology.tsv"

[[ ! -e "$OUTPUT" && ! -L "$OUTPUT" ]] || hyg_fail "output bundle already exists: $OUTPUT"
mkdir -p "$OUTPUT_PARENT"
[[ -d "$OUTPUT_PARENT" && ! -L "$OUTPUT_PARENT" ]] || hyg_fail "output parent is not a safe directory: $OUTPUT_PARENT"
hyg_verify_file "$TOPOLOGY_SOURCE" "$HYG_TOPOLOGY_LENGTH" "$HYG_TOPOLOGY_SHA256" "embedded constellation topology"
hyg44_validate_database "$DATABASE"

staging="$(mktemp -d "$OUTPUT_PARENT/.hyg-bundle.XXXXXX")"
cleanup() {
    local status=$?
    trap - EXIT
    chmod -R u+w "$staging" 2>/dev/null || true
    rm -rf -- "$staging"
    exit "$status"
}
trap cleanup EXIT

cp -- "$DATABASE" "$staging/$HYG44_DATABASE_FILE"
cp -- "$LICENSE_SOURCE" "$staging/$HYG_LICENSE_FILE"
cp -- "$ATTRIBUTION_SOURCE" "$staging/$HYG_ATTRIBUTION_FILE"

license_length="$(hyg_file_length "$staging/$HYG_LICENSE_FILE")"
license_sha256="$(hyg_sha256 "$staging/$HYG_LICENSE_FILE")"
attribution_length="$(hyg_file_length "$staging/$HYG_ATTRIBUTION_FILE")"
attribution_sha256="$(hyg_sha256 "$staging/$HYG_ATTRIBUTION_FILE")"

cat > "$staging/$HYG_MANIFEST_FILE" <<JSON
{
  "manifestVersion": 2,
  "package": {
    "kind": "production",
    "version": "$HYG44_PACKAGE_VERSION"
  },
  "catalog": {
    "id": "$HYG44_CATALOG_ID",
    "name": "$HYG44_CATALOG_NAME",
    "version": "$HYG44_CATALOG_VERSION"
  },
  "source": {
    "projectUrl": "$HYG44_SOURCE_PROJECT_URL",
    "downloadUrl": "$HYG44_SOURCE_URL",
    "oid": "$HYG44_SOURCE_OID",
    "compressed": {
      "sha256": "$HYG44_COMPRESSED_SHA256",
      "length": $HYG44_COMPRESSED_LENGTH
    },
    "decompressed": {
      "sha256": "$HYG44_DECOMPRESSED_SHA256",
      "length": $HYG44_DECOMPRESSED_LENGTH
    }
  },
  "schemaVersion": "$HYG44_SCHEMA_VERSION",
  "preprocessingVersion": "$HYG44_PREPROCESSING_VERSION",
  "serializer": {
    "name": "sqlite3",
    "version": "$HYG_REQUIRED_SQLITE_VERSION"
  },
  "database": {
    "relativePath": "$HYG44_DATABASE_FILE",
    "sha256": "$HYG44_DATABASE_SHA256",
    "length": $HYG44_DATABASE_LENGTH,
    "rowCount": $HYG44_EXPECTED_ROWS,
    "solCount": 0,
    "requiredColumn": "hipparcos_id"
  },
  "license": {
    "identifier": "$HYG44_LICENSE_IDENTIFIER",
    "url": "$HYG44_LICENSE_URL",
    "file": {
      "relativePath": "$HYG_LICENSE_FILE",
      "sha256": "$license_sha256",
      "length": $license_length
    },
    "attribution": {
      "relativePath": "$HYG_ATTRIBUTION_FILE",
      "sha256": "$attribution_sha256",
      "length": $attribution_length
    }
  },
  "topology": {
    "identity": "$HYG_TOPOLOGY_VERSION",
    "sha256": "$HYG_TOPOLOGY_SHA256",
    "constellationCount": 88,
    "segmentCount": 743
  }
}
JSON

hyg44_validate_bundle "$staging"
chmod 0444 "$staging"/*
sync -f "$staging/$HYG_MANIFEST_FILE"
sync -f "$staging/$HYG44_DATABASE_FILE"
sync -f "$staging/$HYG_LICENSE_FILE"
sync -f "$staging/$HYG_ATTRIBUTION_FILE"
chmod 0555 "$staging"
sync -f "$staging"
mv -T -- "$staging" "$OUTPUT"
staging=""
sync -f "$OUTPUT_PARENT"
trap - EXIT

printf 'Created verified HYG 4.4 production bundle: %s\n' "$OUTPUT"
printf 'Manifest SHA-256: %s\n' "$(hyg_sha256 "$OUTPUT/$HYG_MANIFEST_FILE")"
