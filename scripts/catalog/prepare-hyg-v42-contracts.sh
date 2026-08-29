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
[[ "$OUTPUT" != / && ! -e "$OUTPUT" && ! -L "$OUTPUT" ]] || hyg_fail "contract bundle output must not already exist"
mkdir -m 700 -- "$OUTPUT"

"$SCRIPT_DIR/build-hyg-v42.sh" --fetch "$BUILD_ROOT" >&2

[[ "$(hyg_resolve_catalog_identity "$V2_BUNDLE")" == "$HYG_CATALOG_ID" ]]
printf 'HVO_PRODUCTION_CATALOG_BUNDLE=%s\n' "$V2_BUNDLE"
