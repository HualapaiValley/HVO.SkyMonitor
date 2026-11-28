#!/usr/bin/env bash
# Shared constants and functions sourced by catalog build and fixture scripts.
# shellcheck disable=SC2034

readonly HYG_REQUIRED_SQLITE_VERSION="3.45.1"
readonly HYG_LICENSE_FILE="LICENSE-HYG.md"
readonly HYG_ATTRIBUTION_FILE="ATTRIBUTION-HYG.md"
readonly HYG_MANIFEST_FILE="manifest.json"
readonly HYG_TOPOLOGY_VERSION="d3-celestial-v0.7.32-hip-coordinate-map-v1"
readonly HYG_TOPOLOGY_SHA256="70c253a00e0909ae0236dec0411afe837ebf8e493b2be7f84373b63c95c91621"
readonly HYG_TOPOLOGY_LENGTH="12081"
readonly HYG_MAXIMUM_MANIFEST_LENGTH="65536"
HYG_VALIDATION_FAILED=0

# Returns 1; it does not exit. It exists for the accumulate-then-check validators below,
# which reset HYG_VALIDATION_FAILED, run a batch of independent checks so that a caller sees
# every problem rather than only the first, and end with `[[ "$HYG_VALIDATION_FAILED" == 0 ]]`
# as their return value.
#
# Everywhere else, whether the script stops is a property of where the call sits, not of this
# function. `cmd || hyg_fail "msg"` terminates under `set -e` because hyg_fail is the command
# following the final `||`, the one position in an AND-OR list that `set -e` does not exempt.
# Append `|| true`, put the list inside an `if`, or add a statement after it in a function
# whose caller tests it, and the script prints what reads as a fatal error and then carries on.
# So a caller that is not an accumulating validator must place hyg_fail as the last command of
# its list, or follow it with an explicit `return 1`.

hyg_fail() {
    HYG_VALIDATION_FAILED=1
    printf 'catalog error: %s\n' "$*" >&2
    return 1
}

hyg_require_commands() {
    local command_name
    for command_name in "$@"; do
        command -v "$command_name" >/dev/null || hyg_fail "missing required command: $command_name"
    done
}

hyg_sha256() {
    local output
    output="$(sha256sum "$1")"
    printf '%s\n' "${output%% *}"
}

hyg_file_length() {
    local length
    length="$(wc -c < "$1")"
    printf '%d\n' "$length"
}

hyg_verify_file() {
    local path="$1"
    local expected_length="$2"
    local expected_sha256="$3"
    local label="$4"

    [[ -f "$path" && ! -L "$path" ]] || hyg_fail "$label is missing or is not a regular file: $path"
    [[ "$(hyg_file_length "$path")" == "$expected_length" ]] || hyg_fail "$label byte length does not match its manifest"
    [[ "$(hyg_sha256 "$path")" == "$expected_sha256" ]] || hyg_fail "$label SHA-256 does not match its manifest"
}

hyg_check_sqlite_version() {
    local actual_version
    actual_version="$(sqlite3 --version)"
    actual_version="${actual_version%% *}"
    [[ "$actual_version" == "$HYG_REQUIRED_SQLITE_VERSION" ]] || \
        hyg_fail "sqlite3 $HYG_REQUIRED_SQLITE_VERSION is required; found $actual_version"
}

hyg_print_sqlite_identity() {
    local sqlite_command
    local sqlite_path
    local sqlite_version
    local sqlite_sha256
    local sqlite_secure_delete
    local via=""

    sqlite_command="$(type -P sqlite3)" || { hyg_fail "cannot identify sqlite3: it is not on PATH"; return 1; }
    sqlite_path="$(realpath -e -- "$sqlite_command" 2>/dev/null)" ||
        { hyg_fail "cannot identify sqlite3: $sqlite_command does not resolve to a file"; return 1; }
    [[ -f "$sqlite_path" && -x "$sqlite_path" ]] ||
        { hyg_fail "cannot identify sqlite3: $sqlite_path is not an executable regular file"; return 1; }
    sqlite_version="$("$sqlite_command" --version 2>/dev/null)" ||
        { hyg_fail "cannot identify sqlite3: $sqlite_command --version failed"; return 1; }
    sqlite_sha256="$(hyg_sha256 "$sqlite_path")" ||
        { hyg_fail "cannot identify sqlite3: cannot read $sqlite_path"; return 1; }
    sqlite_secure_delete="$("$sqlite_command" -batch -noheader ':memory:' 'PRAGMA secure_delete;' 2>/dev/null)" ||
        { hyg_fail "cannot identify sqlite3: $sqlite_command cannot report PRAGMA secure_delete"; return 1; }

    # One line is the contract: refuse a value that would break it or that is not what the tool is documented to print.
    [[ -n "$sqlite_version" && "$sqlite_version" != *[[:cntrl:]]* ]] ||
        { hyg_fail "cannot identify sqlite3: $sqlite_command --version did not print exactly one line"; return 1; }
    [[ "$sqlite_command" != *[[:cntrl:]]* && "$sqlite_path" != *[[:cntrl:]]* ]] ||
        { hyg_fail "cannot identify sqlite3: its path contains a control character"; return 1; }
    [[ "$sqlite_sha256" =~ ^[0-9a-f]{64}$ ]] ||
        { hyg_fail "cannot identify sqlite3: cannot hash $sqlite_path"; return 1; }
    [[ "$sqlite_secure_delete" =~ ^[012]$ ]] ||
        { hyg_fail "cannot identify sqlite3: PRAGMA secure_delete returned '$sqlite_secure_delete', expected 0, 1 or 2"; return 1; }

    [[ "$sqlite_command" == "$sqlite_path" ]] || via=" via=$sqlite_command"
    printf 'catalog sqlite3: path=%s%s version="%s" sha256=%s secure_delete=%s\n' \
        "$sqlite_path" "$via" "$sqlite_version" "$sqlite_sha256" "$sqlite_secure_delete"
}

hyg_sqlite_scalar() {
    sqlite3 -batch -noheader -readonly "$1" "$2"
}

hyg_validate_database_invariants() {
    local database="$1"
    local result

    result="$(hyg_sqlite_scalar "$database" "SELECT count(*) FROM celestial_objects WHERE id = '0' OR lower(trim(display_name)) = 'sol';")"
    [[ "$result" == 0 ]] || hyg_fail "catalog database contains Sol"
    result="$(hyg_sqlite_scalar "$database" "SELECT count(*) FROM (SELECT id FROM celestial_objects GROUP BY id HAVING count(*) != 1);")"
    [[ "$result" == 0 ]] || hyg_fail "catalog database contains duplicate object IDs"
    result="$(hyg_sqlite_scalar "$database" "SELECT count(*) FROM (SELECT hipparcos_id FROM celestial_objects WHERE hipparcos_id IS NOT NULL AND trim(hipparcos_id) != '' GROUP BY hipparcos_id HAVING count(*) != 1);")"
    [[ "$result" == 0 ]] || hyg_fail "catalog database contains duplicate nonblank Hipparcos IDs"
}

hyg_json_query() {
    local manifest="$1"
    local query="$2"
    local quoted_manifest="${manifest//\'/\'\'}"
    sqlite3 -batch -noheader ':memory:' \
        "WITH input(document) AS (SELECT CAST(readfile('$quoted_manifest') AS TEXT)) $query"
}

hyg_json_type() {
    local path="${2//\'/\'\'}"
    hyg_json_query "$1" "SELECT json_type(document, '$path') FROM input;"
}

hyg_json_value() {
    local path="${2//\'/\'\'}"
    hyg_json_query "$1" "SELECT json_extract(document, '$path') FROM input;"
}

hyg_json_keys() {
    local path="${2//\'/\'\'}"
    hyg_json_query "$1" "SELECT group_concat(key, ',') FROM (SELECT key FROM input, json_each(document, '$path') ORDER BY key);"
}

hyg_json_exact() {
    local manifest="$1"
    local path="$2"
    local expected_type="$3"
    local expected_value="$4"
    [[ "$(hyg_json_type "$manifest" "$path")" == "$expected_type" ]] || hyg_fail "bundle manifest has an invalid type for $path"
    [[ "$(hyg_json_value "$manifest" "$path")" == "$expected_value" ]] || hyg_fail "bundle manifest has an incompatible value for $path"
}

hyg_json_exact_keys() {
    local manifest="$1"
    local path="$2"
    local expected="$3"
    [[ "$(hyg_json_type "$manifest" "$path")" == "object" ]] || hyg_fail "bundle manifest property $path must be an object"
    [[ "$(hyg_json_keys "$manifest" "$path")" == "$expected" ]] || hyg_fail "bundle manifest has missing or unknown properties at $path"
}

hyg_validate_manifest_document() {
    local manifest="$1"
    local duplicate_count
    [[ -f "$manifest" && ! -L "$manifest" ]] || { hyg_fail "bundle manifest is missing or is not a regular file: $manifest"; return 1; }
    [[ "$(hyg_file_length "$manifest")" -le "$HYG_MAXIMUM_MANIFEST_LENGTH" ]] || { hyg_fail "bundle manifest exceeds the maximum byte length"; return 1; }
    [[ "$(hyg_json_query "$manifest" 'SELECT json_valid(document) FROM input;')" == "1" ]] || { hyg_fail "bundle manifest is malformed JSON"; return 1; }
    duplicate_count="$(hyg_json_query "$manifest" "SELECT count(*) FROM (SELECT parent, key FROM input, json_tree(document) WHERE key IS NOT NULL GROUP BY parent, key HAVING count(*) > 1);")"
    [[ "$duplicate_count" == "0" ]] || { hyg_fail "bundle manifest contains duplicate properties"; return 1; }
}
