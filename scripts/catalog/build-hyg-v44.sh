#!/usr/bin/env bash
set -euo pipefail
umask 022

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
# shellcheck source=scripts/catalog/hyg-v44-common.sh
source "$SCRIPT_DIR/hyg-v44-common.sh"

usage() {
    cat >&2 <<USAGE
Usage: $0 (--source COMPRESSED_FILE | --fetch) OUTPUT_DIRECTORY

--source uses an existing compressed HYG 4.4 input and performs no network I/O.
--fetch explicitly downloads the pinned compressed input over HTTPS.
Install the resulting bundle with the deployment CLI catalog install command; the shell installer is HYG 4.2-only.
USAGE
}

source_file=""
fetch=false
output_argument=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --source)
            [[ $# -ge 2 ]] || { usage; exit 2; }
            source_file="$2"
            shift 2
            ;;
        --fetch)
            fetch=true
            shift
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

if [[ $# -ne 0 || -z "$output_argument" || ( -n "$source_file" && "$fetch" == true ) || ( -z "$source_file" && "$fetch" == false ) ]]; then
    usage
    exit 2
fi

hyg_require_commands gzip mv realpath sha256sum sqlite3 sync wc
hyg_check_sqlite_version
if [[ "$fetch" == true ]]; then
    hyg_require_commands curl
fi

if [[ -n "$source_file" ]]; then
    source_file="$(realpath "$source_file")"
fi
OUTPUT="$(realpath -m "$output_argument")"
readonly OUTPUT
OUTPUT_PARENT="$(dirname "$OUTPUT")"
readonly OUTPUT_PARENT
readonly BUNDLE_NAME="$HYG44_PACKAGE_VERSION.bundle"

[[ "$OUTPUT" != "/" ]] || hyg_fail "refusing root as the output directory"
[[ ! -e "$OUTPUT" && ! -L "$OUTPUT" ]] || hyg_fail "output directory already exists: $OUTPUT"
mkdir -p "$OUTPUT_PARENT"
[[ -d "$OUTPUT_PARENT" && ! -L "$OUTPUT_PARENT" ]] || hyg_fail "output parent is not a safe directory: $OUTPUT_PARENT"

staging="$(mktemp -d "$OUTPUT_PARENT/.hyg-build.XXXXXX")"
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

compressed="$staging/hyg_v44.csv.gz"
csv="$staging/hyg_v44.csv"
database="$staging/$HYG44_DATABASE_FILE"

if [[ "$fetch" == true ]]; then
    printf 'Fetching explicitly requested pinned HYG 4.4 source: %s\n' "$HYG44_SOURCE_URL"
    curl --fail --location --proto '=https' --tlsv1.2 "$HYG44_SOURCE_URL" --output "$compressed"
else
    cp -- "$source_file" "$compressed"
fi
hyg_verify_file "$compressed" "$HYG44_COMPRESSED_LENGTH" "$HYG44_COMPRESSED_SHA256" "compressed HYG source"
gzip --decompress --stdout "$compressed" > "$csv"
hyg_verify_file "$csv" "$HYG44_DECOMPRESSED_LENGTH" "$HYG44_DECOMPRESSED_SHA256" "decompressed HYG source"

hyg44_build_database "$csv" "$database"

hyg44_validate_database "$database"
"$SCRIPT_DIR/bundle-hyg-v44.sh" "$database" "$staging/$BUNDLE_NAME"
chmod 0444 "$compressed" "$csv" "$database"
sync -f "$compressed"
sync -f "$csv"
sync -f "$database"
sync -f "$staging"
mv -T -- "$staging" "$OUTPUT"
staging=""
sync -f "$OUTPUT_PARENT"
trap - EXIT

printf 'Built deterministic HYG 4.4 artifacts: %s\n' "$OUTPUT"
printf 'Installable bundle: %s\n' "$OUTPUT/$BUNDLE_NAME"
