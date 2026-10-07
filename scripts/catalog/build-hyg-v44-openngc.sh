#!/usr/bin/env bash
set -euo pipefail
umask 022

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
# shellcheck source=scripts/catalog/hyg-v44-openngc-common.sh
source "$SCRIPT_DIR/hyg-v44-openngc-common.sh"

usage() {
    cat >&2 <<USAGE
Usage: $0 --hyg-bundle BUNDLE_DIRECTORY --openngc-source CHECKOUT_DIRECTORY OUTPUT_DIRECTORY

--hyg-bundle names a verified $HYG44_PACKAGE_VERSION bundle; its database becomes the unchanged stars component.
--openngc-source names an OpenNGC checkout at $OPENNGC_COMMIT; only the pinned files are read.
The build performs no network I/O. Install the resulting bundle with the deployment CLI catalog install command.
USAGE
}

hyg_bundle=""
openngc_source=""
output_argument=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --hyg-bundle)
            [[ $# -ge 2 && -z "$hyg_bundle" ]] || { usage; exit 2; }
            hyg_bundle="$2"
            shift 2
            ;;
        --openngc-source)
            [[ $# -ge 2 && -z "$openngc_source" ]] || { usage; exit 2; }
            openngc_source="$2"
            shift 2
            ;;
        --help|-h)
            usage
            exit 0
            ;;
        --)
            shift
            break
            ;;
        -*)
            usage
            exit 2
            ;;
        *)
            [[ -z "$output_argument" ]] || { usage; exit 2; }
            output_argument="$1"
            shift
            ;;
    esac
done

if [[ $# -ne 0 || -z "$output_argument" || -z "$hyg_bundle" || -z "$openngc_source" ]]; then
    usage
    exit 2
fi

hyg_require_commands chmod cmp cp find mkdir mktemp mv realpath rm sha256sum sort sqlite3 sync wc
hyg_check_sqlite_version

hyg_bundle="$(realpath "$hyg_bundle")"
openngc_source="$(realpath "$openngc_source")"
OUTPUT="$(realpath -m "$output_argument")"
readonly OUTPUT
OUTPUT_PARENT="$(dirname "$OUTPUT")"
readonly OUTPUT_PARENT
readonly BUNDLE_NAME="$HYGNGC_PACKAGE_VERSION.bundle"

[[ "$OUTPUT" != "/" ]] || hyg_fail "refusing root as the output directory"
[[ ! -e "$OUTPUT" && ! -L "$OUTPUT" ]] || hyg_fail "output directory already exists: $OUTPUT"
mkdir -p "$OUTPUT_PARENT"
[[ -d "$OUTPUT_PARENT" && ! -L "$OUTPUT_PARENT" ]] || hyg_fail "output parent is not a safe directory: $OUTPUT_PARENT"

staging="$(mktemp -d "$OUTPUT_PARENT/.hyg-openngc-build.XXXXXX")"
cleanup() {
    local status=$?
    trap - EXIT
    if [[ -n "$staging" ]]; then
        chmod -R u+w "$staging" 2>/dev/null || true
        rm -rf -- "$staging"
    fi
    exit "$status"
}
trap cleanup EXIT

stars="$staging/stars-input.sqlite"
openngc="$staging/openngc"
database="$staging/$HYGNGC_DATABASE_FILE"

# The stars component is the approved HYG 4.4 package database, byte for byte.
hyg44_validate_bundle "$hyg_bundle"
cp -- "$hyg_bundle/$HYG44_DATABASE_FILE" "$stars"
hyg44_validate_database "$stars"
hygngc_stage_openngc_source "$openngc_source" "$openngc"

cp -- "$stars" "$database"
chmod 0644 "$database"
hygngc_compose_database "$openngc" "$database"
hygngc_validate_composition "$database" "$stars"
hygngc_finish_database "$database"
printf 'Composed database: %s bytes, SHA-256 %s\n' "$(hyg_file_length "$database")" "$(hyg_sha256 "$database")"

hygngc_validate_database "$database"
"$SCRIPT_DIR/bundle-hyg-v44-openngc.sh" "$database" "$staging/$BUNDLE_NAME"
chmod -R u+w "$openngc"
rm -rf -- "$openngc"
rm -f -- "$stars" "$database"
sync -f "$staging"
mv -T -- "$staging" "$OUTPUT"
staging=""
sync -f "$OUTPUT_PARENT"
trap - EXIT

printf 'Built deterministic HYG 4.4 + OpenNGC artifacts: %s\n' "$OUTPUT"
printf 'Installable bundle: %s\n' "$OUTPUT/$BUNDLE_NAME"
