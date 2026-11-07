#!/usr/bin/env bash
set -euo pipefail
umask 022

# Offline, production-input regression. Exercise the real builders and pinned
# validators with both SQLite defaults; no expected hash is supplied by the test.
if [[ $# -ne 2 ]]; then
    printf 'Usage: %s HYG_CSV_GZ OPENNGC_CHECKOUT\n' "$0" >&2
    exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
# shellcheck source=scripts/catalog/hyg-v44-openngc-common.sh
source "$SCRIPT_DIR/hyg-v44-openngc-common.sh"
hyg_require_commands cmp realpath sqlite3
hyg_check_sqlite_version

source_file="$(realpath "$1")"
openngc_source="$(realpath "$2")"
HVO_REPRO_SQLITE="$(realpath "$(command -v sqlite3)")"
export HVO_REPRO_SQLITE
work="$(mktemp -d "${TMPDIR:-/tmp}/hvo-hyg44-repro-test.XXXXXX")"
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
        "$SCRIPT_DIR/build-hyg-v44.sh" --source "$source_file" "$candidate/hyg"
        stars="$candidate/hyg/$HYG44_PACKAGE_VERSION.bundle"
        "$SCRIPT_DIR/build-hyg-v44-openngc.sh" --hyg-bundle "$stars" \
            --openngc-source "$openngc_source" "$candidate/openngc"
        composed="$candidate/openngc/$HYGNGC_PACKAGE_VERSION.bundle"

        # Compare every published payload (including manifests and notices), both
        # across clean repeats and across the two connection defaults.
        if [[ "$setting-$repeat" != OFF-1 ]]; then
            for file in "$HYG44_DATABASE_FILE" "$HYG_MANIFEST_FILE" "$HYG_LICENSE_FILE" "$HYG_ATTRIBUTION_FILE"; do
                cmp "$work/OFF-1/hyg/$HYG44_PACKAGE_VERSION.bundle/$file" "$stars/$file"
            done
            for file in "$HYGNGC_DATABASE_FILE" "$HYG_MANIFEST_FILE" "$HYGNGC_LICENSE_FILE" "$HYGNGC_ATTRIBUTION_FILE"; do
                cmp "$work/OFF-1/openngc/$HYGNGC_PACKAGE_VERSION.bundle/$file" "$composed/$file"
            done
        fi
        printf 'PASS secure_delete=%s repeat=%s HYG=%s OpenNGC=%s\n' "$setting" "$repeat" \
            "$(hyg_sha256 "$stars/$HYG44_DATABASE_FILE")" "$(hyg_sha256 "$composed/$HYGNGC_DATABASE_FILE")"
    done
done
printf 'Four clean HYG/OpenNGC builds and all bundle payloads are byte-identical.\n'
