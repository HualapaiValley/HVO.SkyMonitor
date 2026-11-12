#!/usr/bin/env bash
set -euo pipefail
umask 022

# Offline test of the SQLite identity line (Refs #1774): hyg_print_sqlite_identity in catalog-common.sh, and the
# promise that every catalog builder prints it before it builds. It needs a sqlite3 on PATH but not a particular
# version, builds no catalog, and performs no network I/O. Failures are produced with a stand-in sqlite3 rather than
# described, and each refusal must leave nothing on stdout, so a caller can never mistake a failed identity for one.
if [[ $# -ne 0 ]]; then
    printf 'Usage: %s\n' "$0" >&2
    exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
readonly REPO_ROOT
# shellcheck source=scripts/catalog/catalog-common.sh
source "$SCRIPT_DIR/catalog-common.sh"
hyg_require_commands bash cat chmod dirname grep ln mkdir mktemp realpath rm sha256sum sqlite3 wc

# Canonical, so that a file created beneath it is its own resolved path and is named without `via=`.
work="$(realpath -e -- "$(mktemp -d "${TMPDIR:-/tmp}/hvo-sqlite-identity-test.XXXXXX")")"
cleanup() {
    rm -rf -- "$work"
}
trap cleanup EXIT

cases=0
fail() {
    printf 'SQLite identity test failed: %s\n' "$*" >&2
    for stream in stdout stderr; do
        [[ ! -s "$work/$stream" ]] || { printf -- '--- %s\n' "$stream" >&2; cat "$work/$stream" >&2; }
    done
    exit 1
}

# A PATH that holds the tools the helper itself runs and no sqlite3, so each case chooses the only sqlite3 there is.
mkdir "$work/tools"
for tool in bash realpath sha256sum; do
    ln -s "$(type -P "$tool")" "$work/tools/$tool"
done
readonly TOOLS="$work/tools"

# Runs the helper without `set -e`, so the status recorded is the helper's own return value and not the shell's.
identity_status=0
run_identity() {
    local search_path="$1"
    set +e
    ( set +e; PATH="$search_path"; hyg_print_sqlite_identity ) > "$work/stdout" 2> "$work/stderr"
    identity_status=$?
    set -e
}

expect_identity() {
    local label="$1" expected="$2"
    [[ "$identity_status" == 0 ]] || fail "$label: the helper returned $identity_status"
    [[ ! -s "$work/stderr" ]] || fail "$label: the helper wrote to stderr"
    [[ "$(wc -l < "$work/stdout")" == 1 ]] || fail "$label: the helper did not print exactly one line"
    [[ "$(< "$work/stdout")" == "$expected" ]] || fail "$label: expected '$expected'"
    cases=$((cases + 1))
}

expect_refusal() {
    local label="$1" message="$2"
    [[ "$identity_status" != 0 ]] || fail "$label: the helper accepted a sqlite3 it could not identify"
    [[ ! -s "$work/stdout" ]] || fail "$label: a refused identity still printed a line"
    grep -Fq -- "catalog error: cannot identify sqlite3: $message" "$work/stderr" ||
        fail "$label: expected the refusal '$message'"
    cases=$((cases + 1))
}

# The tool this host really has, described by plain commands rather than by the helper under test.
real_command="$(type -P sqlite3)"
real_path="$(realpath -e -- "$real_command")"
real_version="$("$real_command" --version)"
real_sha256="$(sha256sum -- "$real_path")"
real_sha256="${real_sha256%% *}"
real_default="$("$real_command" -batch -noheader ':memory:' 'PRAGMA secure_delete;')"

# A regular file is named as it is; reaching it through a link also names the PATH entry.
mkdir "$work/linked"
ln -s "$real_path" "$work/linked/sqlite3"
run_identity "$work/linked:$TOOLS"
expect_identity "sqlite3 reached through a symbolic link" \
    "catalog sqlite3: path=$real_path via=$work/linked/sqlite3 version=\"$real_version\" sha256=$real_sha256 secure_delete=$real_default"
if [[ "$real_command" == "$real_path" ]]; then
    run_identity "$(dirname "$real_command"):$TOOLS"
    expect_identity "sqlite3 that is a regular file on PATH" \
        "catalog sqlite3: path=$real_path version=\"$real_version\" sha256=$real_sha256 secure_delete=$real_default"
fi

# The value reported is the default a new connection really starts with, not a compile-time guess. The wrapper is
# the one the reproducibility regressions use, so this is also what a builder prints while it runs under them.
mkdir "$work/forced"
cat > "$work/forced/sqlite3" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
exec "$HVO_REPRO_SQLITE" -cmd '.output /dev/null' \
    -cmd "PRAGMA secure_delete=$HVO_REPRO_SECURE_DELETE;" -cmd '.output stdout' "$@"
SH
chmod 0755 "$work/forced/sqlite3"
forced_sha256="$(sha256sum -- "$work/forced/sqlite3")"
forced_sha256="${forced_sha256%% *}"
export HVO_REPRO_SQLITE="$real_path"
for forced in OFF:0 ON:1 FAST:2; do
    export HVO_REPRO_SECURE_DELETE="${forced%%:*}"
    run_identity "$work/forced:$TOOLS"
    expect_identity "connection default forced ${forced%%:*}" \
        "catalog sqlite3: path=$work/forced/sqlite3 version=\"$real_version\" sha256=$forced_sha256 secure_delete=${forced##*:}"
done
unset HVO_REPRO_SQLITE HVO_REPRO_SECURE_DELETE

# A stand-in sqlite3 whose answers each case chooses. It records every invocation, and refuses anything other than
# the two questions the identity and version gates ask, so a builder that reached a real build is caught by its log.
mkdir "$work/fake"
cat > "$work/fake/sqlite3" <<'SH'
#!/usr/bin/env bash
printf '%s\n' "$*" >> "$HVO_FAKE_SQLITE_LOG"
case "$*" in
    --version)
        [[ "$HVO_FAKE_SQLITE_VERSION_STATUS" == 0 ]] || exit "$HVO_FAKE_SQLITE_VERSION_STATUS"
        printf '%b' "$HVO_FAKE_SQLITE_VERSION"
        ;;
    *'PRAGMA secure_delete;')
        [[ "$HVO_FAKE_SQLITE_PRAGMA_STATUS" == 0 ]] || exit "$HVO_FAKE_SQLITE_PRAGMA_STATUS"
        printf '%b' "$HVO_FAKE_SQLITE_PRAGMA"
        ;;
    *)
        printf 'stand-in sqlite3 was asked to do real work: %s\n' "$*" >&2
        exit 96
        ;;
esac
SH
chmod 0755 "$work/fake/sqlite3"
export HVO_FAKE_SQLITE_LOG="$work/fake.log"
fake_sha256="$(sha256sum -- "$work/fake/sqlite3")"
fake_sha256="${fake_sha256%% *}"
reset_fake() {
    export HVO_FAKE_SQLITE_VERSION='3.45.1 2024-01-30 16:01:20 stand-in (64-bit)\n'
    export HVO_FAKE_SQLITE_VERSION_STATUS=0
    export HVO_FAKE_SQLITE_PRAGMA='1\n'
    export HVO_FAKE_SQLITE_PRAGMA_STATUS=0
    : > "$HVO_FAKE_SQLITE_LOG"
}

reset_fake
run_identity "$work/fake:$TOOLS"
expect_identity "stand-in control" \
    "catalog sqlite3: path=$work/fake/sqlite3 version=\"3.45.1 2024-01-30 16:01:20 stand-in (64-bit)\" sha256=$fake_sha256 secure_delete=1"

run_identity "$TOOLS"
expect_refusal "no sqlite3 on PATH" "it is not on PATH"

mkdir "$work/dangling"
ln -s "$work/absent/sqlite3" "$work/dangling/sqlite3"
run_identity "$work/dangling:$TOOLS"
expect_refusal "sqlite3 that is a dangling link" "it is not on PATH"

reset_fake
export HVO_FAKE_SQLITE_VERSION_STATUS=3
run_identity "$work/fake:$TOOLS"
expect_refusal "sqlite3 --version fails" "$work/fake/sqlite3 --version failed"

reset_fake
export HVO_FAKE_SQLITE_VERSION=''
run_identity "$work/fake:$TOOLS"
expect_refusal "sqlite3 --version prints nothing" "$work/fake/sqlite3 --version did not print exactly one line"

reset_fake
export HVO_FAKE_SQLITE_VERSION='3.45.1 first\n3.45.1 second\n'
run_identity "$work/fake:$TOOLS"
expect_refusal "sqlite3 --version prints two lines" "$work/fake/sqlite3 --version did not print exactly one line"

reset_fake
export HVO_FAKE_SQLITE_PRAGMA_STATUS=1
run_identity "$work/fake:$TOOLS"
expect_refusal "PRAGMA secure_delete fails" "$work/fake/sqlite3 cannot report PRAGMA secure_delete"

for unusable in '' 'fast\n' '3\n' 'secure_delete\n1\n'; do
    reset_fake
    export HVO_FAKE_SQLITE_PRAGMA="$unusable"
    run_identity "$work/fake:$TOOLS"
    expect_refusal "PRAGMA secure_delete prints '$unusable'" "PRAGMA secure_delete returned"
done

# Every builder prints the line before it builds, and before the version gate can reject the tool. The stand-in
# reports a version the gate refuses: the builder must have named it first, on stdout, and asked it nothing else.
identity_line='^catalog sqlite3: path=.+ version="[^"]+" sha256=[0-9a-f]{64} secure_delete=[012]$'
run_builder() {
    set +e
    PATH="$work/fake:$PATH" "$@" > "$work/stdout" 2> "$work/stderr"
    identity_status=$?
    set -e
}
expect_builder_named_tool_first() {
    local label="$1" refusal="$2" output="$3" first_line unexpected
    [[ "$identity_status" != 0 ]] || fail "$label: the builder succeeded"
    IFS= read -r first_line < "$work/stdout" || fail "$label: the builder printed nothing on stdout"
    [[ "$first_line" =~ $identity_line ]] || fail "$label: the first line on stdout is not the identity line"
    [[ "$(grep -Ec -- "$identity_line" "$work/stdout")" == 1 ]] || fail "$label: the identity line is not printed exactly once"
    grep -Fq -- "$refusal" "$work/stderr" || fail "$label: expected the refusal '$refusal'"
    [[ ! -e "$output" && ! -L "$output" ]] || fail "$label: a refused build left output behind"
    grep -Fq -- 'PRAGMA secure_delete;' "$HVO_FAKE_SQLITE_LOG" || fail "$label: the builder never asked the stand-in for its identity"
    # No pipe: a quiet reader that stops at its first match can SIGPIPE the writer and hide the match under pipefail.
    unexpected="$(grep -Fv -e '--version' -e 'PRAGMA secure_delete;' "$HVO_FAKE_SQLITE_LOG" || true)"
    [[ -z "$unexpected" ]] || fail "$label: sqlite3 was asked to build before the build was refused: $unexpected"
    cases=$((cases + 1))
}

printf 'not a catalog input\n' > "$work/bogus-input"
for builder in build-hyg-v42.sh build-hyg-v44.sh build-hyg-v44-openngc.sh; do
    if [[ "$builder" == build-hyg-v44-openngc.sh ]]; then
        arguments=(--hyg-bundle "$work/absent-bundle" --openngc-source "$work/absent-openngc")
        input_refusal="bundle is missing or is not a directory"
    else
        arguments=(--source "$work/bogus-input")
        input_refusal="compressed HYG source byte length does not match its manifest"
    fi

    reset_fake
    export HVO_FAKE_SQLITE_VERSION='9.99.9 2099-01-01 00:00:00 stand-in (64-bit)\n'
    run_builder "$SCRIPT_DIR/$builder" "${arguments[@]}" "$work/out-$builder-rejected"
    expect_builder_named_tool_first "$builder with a sqlite3 the version gate rejects" \
        "sqlite3 $HYG_REQUIRED_SQLITE_VERSION is required; found 9.99.9" "$work/out-$builder-rejected"

    # With a version the gate accepts, the builder goes on to its inputs and stops there: the line still comes
    # first, and nothing was built.
    reset_fake
    run_builder "$SCRIPT_DIR/$builder" "${arguments[@]}" "$work/out-$builder-input"
    expect_builder_named_tool_first "$builder with an input it refuses" "$input_refusal" "$work/out-$builder-input"
done

# The two jobs that build the release lineage reach build-hyg-v42.sh through a caller that discards or redirects
# stdout. ci.yml runs prepare-hyg-v42-contracts.sh with stdout sent to /dev/null, so the line has to arrive on
# stderr; run it exactly that way. The rejected version stops the builder before --fetch can open a connection.
reset_fake
export HVO_FAKE_SQLITE_VERSION='9.99.9 2099-01-01 00:00:00 stand-in (64-bit)\n'
set +e
PATH="$work/fake:$PATH" "$SCRIPT_DIR/prepare-hyg-v42-contracts.sh" "$work/contracts" > /dev/null 2> "$work/stderr"
identity_status=$?
set -e
: > "$work/stdout"
[[ "$identity_status" != 0 ]] || fail "prepare-hyg-v42-contracts.sh: accepted a sqlite3 the version gate rejects"
[[ "$(grep -Ec -- "$identity_line" "$work/stderr")" == 1 ]] ||
    fail "prepare-hyg-v42-contracts.sh: the identity line did not reach stderr exactly once with stdout discarded"
grep -Fq -- "sqlite3 $HYG_REQUIRED_SQLITE_VERSION is required; found 9.99.9" "$work/stderr" ||
    fail "prepare-hyg-v42-contracts.sh: the version gate did not stop the build"
cases=$((cases + 1))

# scripts/release:assets publishes four executables and an image before it reaches the catalog, so it cannot be run
# here. What can be held is the line that makes release and prerelease print the identity: the catalog is built by
# build-hyg-v42.sh, with its stdout kept in the job log.
# shellcheck disable=SC2016 # The release script's own variable is matched literally.
grep -Fxq -- '    ./scripts/catalog/build-hyg-v42.sh --fetch "$work/catalog-build" >&2' "$REPO_ROOT/scripts/release:assets" ||
    fail "scripts/release:assets no longer builds the catalog through build-hyg-v42.sh with its output in the log"
cases=$((cases + 1))

printf 'SQLite identity test passed: %s cases.\n' "$cases"
