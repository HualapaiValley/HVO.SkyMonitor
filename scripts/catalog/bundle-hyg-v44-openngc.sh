#!/usr/bin/env bash
set -euo pipefail
umask 022

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
readonly REPO_ROOT
# shellcheck source=scripts/catalog/hyg-v44-openngc-common.sh
source "$SCRIPT_DIR/hyg-v44-openngc-common.sh"

usage() {
    printf 'Usage: %s DATABASE OUTPUT_BUNDLE_DIRECTORY\n' "$0" >&2
}

if [[ $# -ne 2 ]]; then
    usage
    exit 2
fi

hyg_require_commands chmod cmp cp mkdir mktemp mv realpath rm sha256sum sqlite3 sync wc
hyg_check_sqlite_version

DATABASE="$(realpath "$1")"
readonly DATABASE
OUTPUT="$(realpath -m "$2")"
readonly OUTPUT
OUTPUT_PARENT="$(dirname "$OUTPUT")"
readonly OUTPUT_PARENT
readonly LICENSE_SOURCE="$REPO_ROOT/docs/catalog/hyg-v44-openngc-license.md"
readonly ATTRIBUTION_SOURCE="$REPO_ROOT/docs/catalog/hyg-v44-openngc-attribution.md"
readonly TOPOLOGY_SOURCE="$REPO_ROOT/src/HVO.SkyMonitor.Astronomy/Data/d3-celestial-v0.7.32-topology.tsv"

[[ ! -e "$OUTPUT" && ! -L "$OUTPUT" ]] || hyg_fail "output bundle already exists: $OUTPUT"
mkdir -p "$OUTPUT_PARENT"
[[ -d "$OUTPUT_PARENT" && ! -L "$OUTPUT_PARENT" ]] || hyg_fail "output parent is not a safe directory: $OUTPUT_PARENT"
hyg_verify_file "$TOPOLOGY_SOURCE" "$HYG_TOPOLOGY_LENGTH" "$HYG_TOPOLOGY_SHA256" "embedded constellation topology"
hyg_verify_file "$LICENSE_SOURCE" "$HYGNGC_LICENSE_LENGTH" "$HYGNGC_LICENSE_SHA256" "composed catalog license source"
hyg_verify_file "$ATTRIBUTION_SOURCE" "$HYGNGC_ATTRIBUTION_LENGTH" "$HYGNGC_ATTRIBUTION_SHA256" "composed catalog attribution source"
hygngc_validate_database "$DATABASE"

staging="$(mktemp -d "$OUTPUT_PARENT/.hyg-openngc-bundle.XXXXXX")"
cleanup() {
    local status=$?
    trap - EXIT
    chmod -R u+w "$staging" 2>/dev/null || true
    rm -rf -- "$staging"
    exit "$status"
}
trap cleanup EXIT

cp -- "$DATABASE" "$staging/$HYGNGC_DATABASE_FILE"
cp -- "$LICENSE_SOURCE" "$staging/$HYGNGC_LICENSE_FILE"
cp -- "$ATTRIBUTION_SOURCE" "$staging/$HYGNGC_ATTRIBUTION_FILE"
hygngc_manifest_document > "$staging/$HYG_MANIFEST_FILE"

hygngc_validate_bundle "$staging"
chmod 0444 "$staging"/*
sync -f "$staging/$HYG_MANIFEST_FILE"
sync -f "$staging/$HYGNGC_DATABASE_FILE"
sync -f "$staging/$HYGNGC_LICENSE_FILE"
sync -f "$staging/$HYGNGC_ATTRIBUTION_FILE"
chmod 0555 "$staging"
sync -f "$staging"
mv -T -- "$staging" "$OUTPUT"
staging=""
sync -f "$OUTPUT_PARENT"
trap - EXIT

printf 'Created verified HYG 4.4 + OpenNGC production bundle: %s\n' "$OUTPUT"
printf 'Manifest SHA-256: %s\n' "$(hyg_sha256 "$OUTPUT/$HYG_MANIFEST_FILE")"
