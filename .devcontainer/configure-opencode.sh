#!/usr/bin/env bash
set -euo pipefail

readonly OPENCODE_CONFIG_DIR="${OPENCODE_CONFIG_DIR:-$HOME/.config/opencode}"
readonly OPENCODE_DATA_DIR="${OPENCODE_DATA_DIR:-$HOME/.local/share/opencode}"
readonly OPENCODE_CONFIG_FILE="$OPENCODE_CONFIG_DIR/opencode.jsonc"
readonly OPENCODE_SERVER_PASSWORD_FILE="$OPENCODE_DATA_DIR/server-password"
readonly LEGACY_OPENCODE_CONFIG=$'{\n\t"$schema": "https://opencode.ai/config.json",\n\t"permission": "allow"\n}'
EXPECTED_OWNER="$(id -u):$(id -g)"
readonly EXPECTED_OWNER

umask 077

fail() {
    echo "OpenCode configuration prerequisite failed: $1" >&2
    exit 1
}

require_secure_directory() {
    local path="$1"
    local label="$2"
    local actual_mode
    local actual_owner

    [[ ! -L "$path" && -d "$path" ]] \
        || fail "$label must be an existing non-symlink directory: $path"
    [[ -w "$path" ]] || fail "$label is not writable: $path"
    actual_owner="$(stat -c '%u:%g' -- "$path")"
    actual_mode="$(stat -c '%a' -- "$path")"
    [[ "$actual_owner" == "$EXPECTED_OWNER" ]] \
        || fail "$label must be owned by $EXPECTED_OWNER, not $actual_owner: $path"
    [[ "$actual_mode" == 700 ]] \
        || fail "$label must have mode 0700, not $actual_mode: $path"
}

require_private_file() {
    local path="$1"
    local label="$2"
    local actual_mode
    local actual_owner

    [[ ! -L "$path" && -f "$path" ]] \
        || fail "$label must be a regular non-symlink file: $path"
    [[ -s "$path" ]] || fail "$label must not be empty: $path"
    actual_owner="$(stat -c '%u:%g' -- "$path")"
    actual_mode="$(stat -c '%a' -- "$path")"
    [[ "$actual_owner" == "$EXPECTED_OWNER" ]] \
        || fail "$label must be owned by $EXPECTED_OWNER, not $actual_owner: $path"
    [[ "$actual_mode" == 600 ]] \
        || fail "$label must have mode 0600, not $actual_mode: $path"
}

require_owned_private_entry() {
    local path="$1"
    local label="$2"
    local actual_mode
    local actual_owner

    [[ ! -L "$path" && -f "$path" ]] \
        || fail "$label must be a regular non-symlink file: $path"
    actual_owner="$(stat -c '%u:%g' -- "$path")"
    actual_mode="$(stat -c '%a' -- "$path")"
    [[ "$actual_owner" == "$EXPECTED_OWNER" && "$actual_mode" == 600 ]] \
        || fail "$label must be owned by $EXPECTED_OWNER with mode 0600: $path"
}

directory_is_empty() (
    local -a entries
    shopt -s dotglob nullglob
    entries=("$1"/*)
    ((${#entries[@]} == 0))
)

directory_contains_only() (
    local root="$1"
    local entry
    local expected
    local matched
    local -a entries
    shift
    shopt -s dotglob nullglob
    entries=("$root"/*)
    for entry in "${entries[@]}"; do
        matched=false
        for expected in "$@"; do
            if [[ "$entry" == "$expected" ]]; then
                matched=true
                break
            fi
        done
        [[ "$matched" == true ]] || exit 1
    done
)

recover_committed_initialization() {
    local committed_dir
    local inventory_file
    local -a committed_dirs

    shopt -s nullglob
    committed_dirs=("$OPENCODE_CONFIG_DIR"/.hvo-opencode-initialized-v1-*)
    shopt -u nullglob
    ((${#committed_dirs[@]} > 0)) || return 0
    ((${#committed_dirs[@]} == 1)) \
        || fail "multiple committed initialization markers require manual inspection"

    committed_dir="${committed_dirs[0]}"
    require_secure_directory "$committed_dir" "Committed OpenCode initialization marker"
    inventory_file="$committed_dir/inventory"
    directory_contains_only "$committed_dir" "$inventory_file" \
        || fail "committed initialization marker contains unrelated state"
    if [[ -e "$inventory_file" || -L "$inventory_file" ]]; then
        require_owned_private_entry "$inventory_file" "OpenCode initialization inventory"
        rm -f -- "$inventory_file"
    fi
    rmdir -- "$committed_dir"
}

recover_interrupted_initialization() {
    local transaction_dir
    local transaction_id
    local committed_dir
    local temporary_config
    local temporary_password
    local inventory_file
    local temporary_inventory
    local inventory_contents
    local inventory_identity
    local inventory_version
    local inventory_transaction_id
    local expected_config_hash
    local expected_password_hash
    local inventory_extra
    local path
    local -a transaction_dirs

    shopt -s nullglob
    transaction_dirs=("$OPENCODE_CONFIG_DIR"/.hvo-opencode-initialization-v1-*)
    shopt -u nullglob
    ((${#transaction_dirs[@]} > 0)) || return 0
    ((${#transaction_dirs[@]} == 1)) \
        || fail "multiple interrupted initialization markers require manual inspection"

    transaction_dir="${transaction_dirs[0]}"
    transaction_id="${transaction_dir##*-v1-}"
    [[ "$transaction_id" =~ ^[0-9a-f]{32}$ ]] \
        || fail "invalid interrupted initialization marker: $transaction_dir"
    require_secure_directory "$transaction_dir" "OpenCode initialization marker"

    temporary_config="$transaction_dir/opencode.jsonc"
    temporary_password="$OPENCODE_DATA_DIR/.server-password.$transaction_id"
    inventory_file="$transaction_dir/inventory"
    temporary_inventory="$transaction_dir/.inventory.tmp"
    committed_dir="$OPENCODE_CONFIG_DIR/.hvo-opencode-initialized-v1-$transaction_id"
    directory_contains_only "$OPENCODE_CONFIG_DIR" "$transaction_dir" "$OPENCODE_CONFIG_FILE" \
        || fail "interrupted initialization is mixed with unrelated configuration state"
    directory_contains_only "$OPENCODE_DATA_DIR" "$temporary_password" "$OPENCODE_SERVER_PASSWORD_FILE" \
        || fail "interrupted initialization is mixed with unrelated data state"
    directory_contains_only "$transaction_dir" "$temporary_config" "$inventory_file" "$temporary_inventory" \
        || fail "interrupted initialization marker contains unrelated state"

    for path in \
        "$temporary_config" \
        "$temporary_password" \
        "$inventory_file" \
        "$temporary_inventory" \
        "$OPENCODE_CONFIG_FILE" \
        "$OPENCODE_SERVER_PASSWORD_FILE"; do
        if [[ -e "$path" || -L "$path" ]]; then
            require_owned_private_entry "$path" "Interrupted OpenCode initialization artifact"
        fi
    done

    if [[ -e "$inventory_file" ]]; then
        inventory_contents="$(<"$inventory_file")"
        IFS=$'\t' read -r \
            inventory_identity \
            inventory_version \
            inventory_transaction_id \
            expected_config_hash \
            expected_password_hash \
            inventory_extra <<< "$inventory_contents"
        [[ "$inventory_identity" == HVO-OPENCODE-INITIALIZATION \
            && "$inventory_version" == 1 \
            && "$inventory_transaction_id" == "$transaction_id" \
            && "$expected_config_hash" =~ ^[0-9a-f]{64}$ \
            && "$expected_password_hash" =~ ^[0-9a-f]{64}$ \
            && -z "$inventory_extra" ]] \
            || fail "interrupted initialization inventory is invalid: $inventory_file"

        for path in "$temporary_config" "$OPENCODE_CONFIG_FILE"; do
            if [[ -e "$path" && "$(sha256sum "$path" | cut -d ' ' -f 1)" != "$expected_config_hash" ]]; then
                fail "interrupted initialization does not own configuration artifact: $path"
            fi
        done
        for path in "$temporary_password" "$OPENCODE_SERVER_PASSWORD_FILE"; do
            if [[ -e "$path" && "$(sha256sum "$path" | cut -d ' ' -f 1)" != "$expected_password_hash" ]]; then
                fail "interrupted initialization does not own password artifact: $path"
            fi
        done
    elif [[ -e "$OPENCODE_CONFIG_FILE" || -e "$OPENCODE_SERVER_PASSWORD_FILE" ]]; then
        fail "interrupted initialization cannot authenticate installed state without its inventory"
    fi

    if [[ -e "$OPENCODE_CONFIG_FILE" && -e "$OPENCODE_SERVER_PASSWORD_FILE" ]]; then
        mv -- "$transaction_dir" "$committed_dir"
        recover_committed_initialization
        return
    fi

    rm -f -- "$temporary_config"
    run_initialization_failpoint after-recovery-temporary-config-remove
    rm -f -- "$temporary_password"
    run_initialization_failpoint after-recovery-temporary-password-remove
    rm -f -- "$OPENCODE_CONFIG_FILE"
    run_initialization_failpoint after-recovery-config-remove
    rm -f -- "$OPENCODE_SERVER_PASSWORD_FILE"
    run_initialization_failpoint after-recovery-password-remove
    rm -f -- "$temporary_inventory"
    run_initialization_failpoint after-recovery-temporary-inventory-remove
    rm -f -- "$inventory_file"
    run_initialization_failpoint after-recovery-inventory-remove
    rmdir -- "$transaction_dir"
}

run_initialization_failpoint() {
    if [[ "${HVO_OPENCODE_INIT_PAUSEPOINT:-}" == "$1" ]]; then
        sleep "${HVO_OPENCODE_INIT_PAUSE_SECONDS:-1}"
    fi
    if [[ "${HVO_OPENCODE_INIT_FAILPOINT:-}" == "$1" ]]; then
        kill -KILL "$$"
    fi
}

require_secure_directory "$OPENCODE_CONFIG_DIR" "OpenCode configuration root"
require_secure_directory "$OPENCODE_DATA_DIR" "OpenCode data root"
exec 8< "$OPENCODE_CONFIG_DIR"
flock -x 8
recover_committed_initialization
recover_interrupted_initialization

config_exists=false
password_exists=false
[[ -e "$OPENCODE_CONFIG_FILE" || -L "$OPENCODE_CONFIG_FILE" ]] && config_exists=true
[[ -e "$OPENCODE_SERVER_PASSWORD_FILE" || -L "$OPENCODE_SERVER_PASSWORD_FILE" ]] && password_exists=true

if [[ "$config_exists" == false && "$password_exists" == false ]]; then
    if ! directory_is_empty "$OPENCODE_CONFIG_DIR" || ! directory_is_empty "$OPENCODE_DATA_DIR"; then
        fail "fresh initialization requires empty configuration and data roots"
    fi

    transaction_id="$(openssl rand -hex 16)"
    transaction_dir="$OPENCODE_CONFIG_DIR/.hvo-opencode-initialization-v1-$transaction_id"
    committed_dir="$OPENCODE_CONFIG_DIR/.hvo-opencode-initialized-v1-$transaction_id"
    temporary_config="$transaction_dir/opencode.jsonc"
    temporary_password="$OPENCODE_DATA_DIR/.server-password.$transaction_id"
    inventory_file="$transaction_dir/inventory"
    temporary_inventory="$transaction_dir/.inventory.tmp"
    mkdir -m 0700 -- "$transaction_dir"
    run_initialization_failpoint after-marker
    cat > "$temporary_config" <<'EOF'
{
	"$schema": "https://opencode.ai/config.json"
}
EOF
    chmod 600 "$temporary_config"
    run_initialization_failpoint after-config-write
    openssl rand -hex 32 > "$temporary_password"
    chmod 600 "$temporary_password"
    run_initialization_failpoint after-password-write
    printf 'HVO-OPENCODE-INITIALIZATION\t1\t%s\t%s\t%s\n' \
        "$transaction_id" \
        "$(sha256sum "$temporary_config" | cut -d ' ' -f 1)" \
        "$(sha256sum "$temporary_password" | cut -d ' ' -f 1)" \
        > "$temporary_inventory"
    chmod 600 "$temporary_inventory"
    mv -- "$temporary_inventory" "$inventory_file"
    run_initialization_failpoint after-inventory
    mv -- "$temporary_config" "$OPENCODE_CONFIG_FILE"
    run_initialization_failpoint after-config-install
    mv -- "$temporary_password" "$OPENCODE_SERVER_PASSWORD_FILE"
    run_initialization_failpoint after-password-install
    mv -- "$transaction_dir" "$committed_dir"
    run_initialization_failpoint after-commit
    recover_committed_initialization
elif [[ "$config_exists" != "$password_exists" ]]; then
    fail "configuration and server password must either both exist or both be absent in empty roots"
fi

require_private_file "$OPENCODE_CONFIG_FILE" "OpenCode configuration"
require_private_file "$OPENCODE_SERVER_PASSWORD_FILE" "OpenCode server password"

if [[ "$(<"$OPENCODE_CONFIG_FILE")" == "$LEGACY_OPENCODE_CONFIG" ]]; then
    fail "obsolete blanket-allow configuration detected at $OPENCODE_CONFIG_FILE; replace it with the current explicit permission policy or a schema-only configuration"
fi
