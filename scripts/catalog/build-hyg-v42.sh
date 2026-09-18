#!/usr/bin/env bash
set -euo pipefail
umask 022

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
# shellcheck source=scripts/catalog/catalog-common.sh
source "$SCRIPT_DIR/catalog-common.sh"

usage() {
    cat >&2 <<USAGE
Usage: $0 (--source COMPRESSED_FILE | --fetch) [--install-root INSTALL_ROOT] OUTPUT_DIRECTORY

--source uses an existing compressed HYG 4.2 input and performs no network I/O.
--fetch explicitly downloads the pinned compressed input over HTTPS.
USAGE
}

source_file=""
fetch=false
install_root=""
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
        --install-root)
            [[ $# -ge 2 ]] || { usage; exit 2; }
            install_root="$2"
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
readonly BUNDLE_NAME="$HYG_PACKAGE_VERSION.bundle"

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

compressed="$staging/hyg_v42.csv.gz"
csv="$staging/hyg_v42.csv"
database="$staging/$HYG_DATABASE_FILE"

if [[ "$fetch" == true ]]; then
    printf 'Fetching explicitly requested pinned HYG source: %s\n' "$HYG_SOURCE_URL"
    curl --fail --location --proto '=https' --tlsv1.2 "$HYG_SOURCE_URL" --output "$compressed"
else
    cp -- "$source_file" "$compressed"
fi
hyg_verify_file "$compressed" "$HYG_COMPRESSED_LENGTH" "$HYG_COMPRESSED_SHA256" "compressed HYG source"
gzip --decompress --stdout "$compressed" > "$csv"
hyg_verify_file "$csv" "$HYG_DECOMPRESSED_LENGTH" "$HYG_DECOMPRESSED_SHA256" "decompressed HYG source"

case "$csv" in
    *$'\n'*|*'"'*) hyg_fail "output path contains a character unsupported by sqlite3 .import" ;;
esac

sqlite3 "$database" <<SQL
.bail on
PRAGMA page_size = 4096;
PRAGMA journal_mode = DELETE;
PRAGMA synchronous = OFF;
PRAGMA user_version = 2;
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
  hipparcos_id TEXT
) WITHOUT ROWID;
INSERT INTO catalog_metadata VALUES
  ('catalog_version', '4.2'),
  ('license', 'CC BY-SA 4.0'),
  ('name', 'HYG 4.2'),
  ('preprocessing_version', '3'),
  ('schema_version', '2'),
  ('source_url', 'https://codeberg.org/astronexus/hyg');
INSERT INTO celestial_objects
SELECT id, CASE WHEN proper = '' THEN id ELSE proper END,
       CAST(ra AS REAL), CAST(dec AS REAL), CAST(mag AS REAL),
       CASE WHEN ci = '' THEN NULL ELSE CAST(ci AS REAL) END,
       NULLIF(hip, '')
FROM source_hyg
WHERE id != '' AND id != '0' AND ra != '' AND dec != '' AND mag != ''
ORDER BY id;
DROP TABLE source_hyg;
CREATE INDEX celestial_objects_magnitude_id ON celestial_objects (magnitude, id COLLATE BINARY);
VACUUM;
SQL

hyg_validate_database "$database"
"$SCRIPT_DIR/bundle-hyg-v42.sh" "$database" "$staging/$BUNDLE_NAME"
chmod 0444 "$compressed" "$csv" "$database"
sync -f "$compressed"
sync -f "$csv"
sync -f "$database"
sync -f "$staging"
mv -T -- "$staging" "$OUTPUT"
staging=""
sync -f "$OUTPUT_PARENT"
trap - EXIT

printf 'Built deterministic HYG 4.2 artifacts: %s\n' "$OUTPUT"
printf 'Installable bundle: %s\n' "$OUTPUT/$BUNDLE_NAME"
if [[ -n "$install_root" ]]; then
    "$SCRIPT_DIR/install-hyg-v42.sh" install "$OUTPUT/$BUNDLE_NAME" "$install_root"
fi
