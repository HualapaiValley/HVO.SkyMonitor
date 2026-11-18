#!/usr/bin/env bash
set -euo pipefail
umask 022

# Offline regression for the committed catalog test fixtures (Refs #1774). It runs every
# tests/fixtures/catalog/build-fixture*.sh recipe with the incoming SQLite connection default forced first OFF and
# then ON, and requires each to pass its own `sha256sum --check`. A fixture whose bytes depend on that default,
# which is what a recipe without its secure_delete pin produces, fails here instead of on the next person who
# rebuilds it with a differently compiled sqlite3.
#
# The recipes rebuild their database in place, so they run in a scratch copy of the fixture directory. The checkout
# is never written, and each rebuilt database must also equal the committed one byte for byte. No expected hash is
# supplied by this test, and it performs no network I/O.
if [[ $# -ne 0 ]]; then
    printf 'Usage: %s\n' "$0" >&2
    exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
# shellcheck source=scripts/catalog/catalog-common.sh
source "$SCRIPT_DIR/catalog-common.sh"
hyg_require_commands chmod cmp cp mktemp realpath rm sha256sum sqlite3
# The wrapped tool's own line, before the wrapper below shadows it on PATH.
hyg_print_sqlite_identity
hyg_check_sqlite_version

FIXTURES="$(realpath -e -- "$SCRIPT_DIR/../../tests/fixtures/catalog")"
readonly FIXTURES
readonly -a REQUIRED_RECIPES=(build-fixture.sh build-fixture-v44.sh build-fixture-v44-openngc.sh)

HVO_REPRO_SQLITE="$(realpath "$(command -v sqlite3)")"
export HVO_REPRO_SQLITE
work="$(mktemp -d "${TMPDIR:-/tmp}/hvo-fixture-repro-test.XXXXXX")"
cleanup() {
    chmod -R u+w "$work"
    rm -rf -- "$work"
}
trap cleanup EXIT

mkdir "$work/bin"
cat > "$work/bin/sqlite3" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
# Silence only the setup PRAGMA's result, preserving all recipe diagnostics and
# query output. Each new connection starts with the selected nonpersistent setting.
exec "$HVO_REPRO_SQLITE" -cmd '.output /dev/null' \
    -cmd "PRAGMA secure_delete=$HVO_REPRO_SECURE_DELETE;" -cmd '.output stdout' "$@"
SH
chmod 0755 "$work/bin/sqlite3"
export PATH="$work/bin:$PATH"
export HVO_REPRO_SECURE_DELETE=OFF

# Every recipe in the directory runs, so a new one is covered without being listed here; the three this regression
# was written for must be among them, so that renaming one cannot quietly drop it.
recipes=()
for recipe in "$FIXTURES"/build-fixture*.sh; do
    [[ -f "$recipe" && ! -L "$recipe" && -x "$recipe" ]] || hyg_fail "fixture recipe is not an executable regular file: $recipe"
    recipes+=("${recipe##*/}")
done
for recipe in "${REQUIRED_RECIPES[@]}"; do
    [[ " ${recipes[*]} " == *" $recipe "* ]] || hyg_fail "fixture recipe is missing: tests/fixtures/catalog/$recipe"
done

for setting in OFF ON; do
    export HVO_REPRO_SECURE_DELETE="$setting"
    actual="$(sqlite3 :memory: 'PRAGMA secure_delete;')"
    expected=0
    [[ "$setting" == OFF ]] || expected=1
    [[ "$actual" == "$expected" ]] || hyg_fail "test did not establish secure_delete=$setting"

    candidate="$work/$setting"
    cp -R -- "$FIXTURES" "$candidate"
    for recipe in "${recipes[@]}"; do
        # The recipe ends with its own `sha256sum --check`; a mismatch stops the regression here.
        "$candidate/$recipe"
        printf 'PASS secure_delete=%s recipe=%s\n' "$setting" "$recipe"
    done

    rebuilt=0
    for database in "$candidate"/*.sqlite; do
        [[ -f "$database" ]] || hyg_fail "fixture rebuild left no database in $candidate"
        cmp -- "$FIXTURES/${database##*/}" "$database"
        rebuilt=$((rebuilt + 1))
    done
    [[ "$rebuilt" -ge "${#REQUIRED_RECIPES[@]}" ]] ||
        hyg_fail "only $rebuilt fixture databases were rebuilt, expected at least ${#REQUIRED_RECIPES[@]}"
done
printf 'All %s fixture recipes rebuild the committed bytes with secure_delete forced OFF and ON.\n' "${#recipes[@]}"
