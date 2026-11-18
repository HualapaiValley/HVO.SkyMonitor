#!/usr/bin/env bash
set -euo pipefail
umask 022

# Offline, production-input regression. Exercise the real builders and pinned
# validators with both SQLite defaults; no expected hash is supplied by the test.
if [[ $# -ne 1 ]]; then
    printf 'Usage: %s HYG_V42_CSV_GZ\n' "$0" >&2
    exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
# shellcheck source=scripts/catalog/catalog-common.sh
source "$SCRIPT_DIR/catalog-common.sh"
hyg_require_commands cmp realpath sqlite3
hyg_check_sqlite_version

source_file="$(realpath "$1")"
HVO_REPRO_SQLITE="$(realpath "$(command -v sqlite3)")"
export HVO_REPRO_SQLITE
work="$(mktemp -d "${TMPDIR:-/tmp}/hvo-hyg42-repro-test.XXXXXX")"
cleanup() {
    chmod -R u+w "$work"
    rm -rf -- "$work"
}
trap cleanup EXIT

mkdir "$work/bin"
cat > "$work/bin/sqlite3" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
# Silence only the setup PRAGMA's result, preserving all builder diagnostics and
# query output. Each new connection starts with the selected nonpersistent setting.
exec "$HVO_REPRO_SQLITE" -cmd '.output /dev/null' \
    -cmd "PRAGMA secure_delete=$HVO_REPRO_SECURE_DELETE;" -cmd '.output stdout' "$@"
SH
chmod 0755 "$work/bin/sqlite3"
export PATH="$work/bin:$PATH"
export HVO_REPRO_SECURE_DELETE=OFF

printf 'SQLite under test: %s\n' "$(sqlite3 --version)"
for setting in OFF ON; do
    export HVO_REPRO_SECURE_DELETE="$setting"
    actual="$(sqlite3 :memory: 'PRAGMA secure_delete;')"
    expected=0
    [[ "$setting" == OFF ]] || expected=1
    [[ "$actual" == "$expected" ]] || hyg_fail "test did not establish secure_delete=$setting"

    for repeat in 1 2; do
        candidate="$work/$setting-$repeat"
        "$SCRIPT_DIR/build-hyg-v42.sh" --source "$source_file" "$candidate"
        bundle="$candidate/$HYG_PACKAGE_VERSION.bundle"
        hyg_verify_file "$bundle/$HYG_DATABASE_FILE" "$HYG_DATABASE_LENGTH" "$HYG_DATABASE_SHA256" "catalog database"

        # Include the manifest and notices, across clean repeats and both defaults.
        if [[ "$setting-$repeat" != OFF-1 ]]; then
            for file in "$HYG_DATABASE_FILE" "$HYG_MANIFEST_FILE" "$HYG_LICENSE_FILE" "$HYG_ATTRIBUTION_FILE"; do
                cmp "$work/OFF-1/$HYG_PACKAGE_VERSION.bundle/$file" "$bundle/$file"
            done
        fi
        printf 'PASS secure_delete=%s repeat=%s HYG=%s\n' "$setting" "$repeat" \
            "$(hyg_sha256 "$bundle/$HYG_DATABASE_FILE")"
    done
done
printf 'Four clean HYG 4.2 builds and all bundle payloads are byte-identical.\n'
