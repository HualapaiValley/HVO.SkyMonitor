#!/usr/bin/env bash
# .NET 10.0.13 named mutexes use XDG_RUNTIME_DIR or HOME, not DOTNET_CLI_HOME.
# See https://github.com/dotnet/runtime/issues/135726.
_dotnet_runtime_directory_usable() {
    local path="$1" uid="$2" private="$3" metadata owner mode
    [[ -d "$path" && ! -L "$path" && -w "$path" && -x "$path" ]] || return 1
    metadata="$(stat -c '%u %a' -- "$path" 2>/dev/null)" || return 1
    read -r owner mode <<< "$metadata"
    [[ "$owner" == "$uid" && "$mode" =~ ^[0-7]{3,4}$ ]] || return 1
    [[ "$private" == no ]] || (( (8#$mode & 0022) == 0 ))
}

dotnet_runtime_home_check() {
    local path="${XDG_RUNTIME_DIR:-${HOME:-}}" variable=HOME current probe uid component valid=yes
    local -a components=(.dotnet shm)
    [[ -z "${XDG_RUNTIME_DIR:-}" ]] || variable=XDG_RUNTIME_DIR
    uid="$(id -u)" || valid=no
    if [[ "$path" == /* ]] && _dotnet_runtime_directory_usable "$path" "$uid" yes; then
        # Inspect each prefix before normalizing: link/../home must also fail.
        current="$path"
        while [[ "$current" != / ]]; do
            [[ ! -L "$current" ]] || { valid=no; break; }
            current="${current%/*}"
            current="${current:-/}"
        done
        current="$path"
        if [[ "$variable" == HOME ]]; then
            components=(.dotnet corefx Runtime SharedMemory .dotnet shm)
        fi
        if [[ "$valid" == yes ]]; then
            for component in "${components[@]}"; do
                path="$path/$component"
                if [[ -e "$path" || -L "$path" ]]; then
                    _dotnet_runtime_directory_usable "$path" "$uid" no || { valid=no; break; }
                    current="$path"
                else
                    break
                fi
            done
        fi
        if [[ "$valid" == yes ]] && probe="$(mktemp -- "$current/.hvo-dotnet-home.XXXXXX" 2>/dev/null)"; then
            rm -f -- "$probe" 2>/dev/null && return 0
        fi
    fi
    printf 'ARM64 CI preflight: repair the runner service account HOME or set XDG_RUNTIME_DIR to an existing absolute directory owned by the service UID, writable/searchable, without symlink ancestors or group/other write permissions. For HOME, repair every existing .dotnet/corefx/Runtime/SharedMemory/.dotnet/shm component (or .dotnet/shm under XDG_RUNTIME_DIR) to be a non-symlink directory owned by that UID and writable/searchable. DOTNET_CLI_HOME alone is insufficient.\n' >&2
    return 1
}
