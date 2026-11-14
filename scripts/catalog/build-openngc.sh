#!/usr/bin/env bash
set -euo pipefail
umask 022

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
# shellcheck source=scripts/catalog/hyg-v44-openngc-common.sh
source "$SCRIPT_DIR/hyg-v44-openngc-common.sh"

if [[ $# != 3 || "$1" != --openngc-source ]]; then
    printf 'Usage: %s --openngc-source PINNED_CHECKOUT OUTPUT_DIRECTORY\n' "$0" >&2
    exit 2
fi
hyg_require_commands chmod mkdir mktemp mv python3 realpath rm sha256sum sqlite3 sync
hyg_print_sqlite_identity
hyg_check_sqlite_version
source_root="$(realpath -- "$2")"
output="$(realpath -m -- "$3")"
[[ "$output" != / && ! -e "$output" && ! -L "$output" ]] || hyg_fail 'output must be a new directory'
parent="$(dirname "$output")"
mkdir -p "$parent"
staging="$(mktemp -d "$parent/.openngc.XXXXXX")"
cleanup() {
    local result=$?
    trap - EXIT
    chmod -R u+w "$staging" 2>/dev/null || true
    rm -rf -- "$staging"
    exit "$result"
}
trap cleanup EXIT
hygngc_stage_openngc_source "$source_root" "$staging/inputs"
database="$staging/openngc.sqlite"
sqlite3 -batch "$database" 'CREATE TABLE catalog_metadata (key TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL) WITHOUT ROWID;'
# The existing source transformation is independent of star tables. Retain exactly
# its facts/aliases/tombstones/outlines, then publish a standalone schema identity.
hygngc_compose_database "$staging/inputs" "$database"
expected=""
for transformation in "${HYGNGC_TRANSFORMATIONS[@]}"; do
    expected+="${expected:+$'\n'}$transformation"
done
hygngc_expect "$database" 'recorded transformations' "$expected" \
    "SELECT group_concat(id || '|' || count || '|' || subjects, char(10) ORDER BY ordinal) FROM build_transformations;"
hygngc_finish_database "$database"
sqlite3 -batch "$database" > /dev/null <<'SQL'
.bail on
PRAGMA secure_delete=ON;
INSERT INTO catalog_metadata VALUES
 ('name','OpenNGC v20260501'), ('catalog_version','v20260501'),
 ('schema_version','5'), ('preprocessing_version','6'),
 ('source_url','https://github.com/mattiaverga/OpenNGC'), ('license','CC BY-SA 4.0');
PRAGMA user_version=5;
VACUUM;
SQL
python3 "$SCRIPT_DIR/package-openngc.py" "$database" "$staging/bundle"
chmod -R u+w "$staging/inputs"
rm -rf -- "$staging/inputs"
rm -- "$database"
mv -- "$staging/bundle" "$staging/openngc-v20260501-p6-s5-r1.bundle"
sync -f "$staging"
mv -T -- "$staging" "$output"
trap - EXIT
sync -f "$parent"
printf 'Built verified standalone OpenNGC bundle.\n'
