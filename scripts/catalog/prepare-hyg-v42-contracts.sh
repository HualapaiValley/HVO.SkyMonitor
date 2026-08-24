#!/usr/bin/env bash
set -euo pipefail
umask 077

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
# shellcheck source=scripts/catalog/catalog-common.sh
. "$SCRIPT_DIR/catalog-common.sh"

if [[ $# -ne 1 ]]; then
    printf 'Usage: %s OUTPUT_DIRECTORY\n' "${0##*/}" >&2
    exit 2
fi

OUTPUT="$(realpath -m -- "$1")"
readonly OUTPUT
readonly BUILD_ROOT="$OUTPUT/build"
readonly V2_BUNDLE="$BUILD_ROOT/$HYG_PACKAGE_VERSION.bundle"
readonly V1_BUNDLE="$OUTPUT/$HYG_PACKAGE_VERSION-legacy.bundle"
[[ "$OUTPUT" != / && ! -e "$OUTPUT" && ! -L "$OUTPUT" ]] || hyg_fail "contract bundle output must not already exist"
mkdir -m 700 -- "$OUTPUT"

"$SCRIPT_DIR/build-hyg-v42.sh" --fetch "$BUILD_ROOT" >&2
cp -a -- "$V2_BUNDLE" "$V1_BUNDLE"
chmod u+w "$V1_BUNDLE" "$V1_BUNDLE/$HYG_MANIFEST_FILE"
jq '.manifestVersion = 1 | del(.catalog.id)' "$V1_BUNDLE/$HYG_MANIFEST_FILE" > "$V1_BUNDLE/$HYG_MANIFEST_FILE.tmp"
mv -T -- "$V1_BUNDLE/$HYG_MANIFEST_FILE.tmp" "$V1_BUNDLE/$HYG_MANIFEST_FILE"
chmod 0444 "$V1_BUNDLE"/*
chmod 0555 "$V1_BUNDLE"

[[ "$(hyg_resolve_catalog_identity "$V2_BUNDLE")" == "$HYG_CATALOG_ID" ]]
[[ "$(hyg_resolve_catalog_identity "$V1_BUNDLE")" == "$HYG_CATALOG_ID" ]]
printf 'HVO_PHASE14_PRODUCTION_CATALOG_BUNDLE=%s\n' "$V2_BUNDLE"
printf 'HVO_PHASE14_LEGACY_PRODUCTION_CATALOG_BUNDLE=%s\n' "$V1_BUNDLE"
printf 'HVO_LEGACY_CATALOG_BUNDLE=%s\n' "$V1_BUNDLE"
