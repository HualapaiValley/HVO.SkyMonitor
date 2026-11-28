#!/usr/bin/env bash
set -euo pipefail
umask 077

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
# shellcheck source=scripts/catalog/hyg-v44-common.sh
source "$SCRIPT_DIR/hyg-v44-common.sh"

if [[ $# -ne 1 ]]; then
    printf 'Usage: %s OUTPUT_DIRECTORY\n' "${0##*/}" >&2
    exit 2
fi

output="$(realpath -m -- "$1")"
[[ "$output" != / && ! -e "$output" && ! -L "$output" ]] ||
    hyg_fail "contract bundle output must not already exist"
mkdir -m 700 -- "$output"
"$SCRIPT_DIR/build-hyg-v44.sh" --fetch "$output/build" >&2
bundle="$output/build/$HYG44_PACKAGE_VERSION.bundle"
hyg44_validate_bundle "$bundle"
printf 'HVO_PRODUCTION_CATALOG_BUNDLE=%s\n' "$bundle"
