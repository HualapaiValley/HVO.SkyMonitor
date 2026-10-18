#!/usr/bin/env bash
# .NET 10.0.13 named mutexes use XDG_RUNTIME_DIR or HOME, not DOTNET_CLI_HOME.
# See https://github.com/dotnet/runtime/issues/135726.
dotnet_runtime_home_check() {
    local path="${XDG_RUNTIME_DIR:-${HOME:-}}" variable=HOME current probe
    [[ -z "${XDG_RUNTIME_DIR:-}" ]] || variable=XDG_RUNTIME_DIR
    if [[ "$path" == /* && -d "$path" && -w "$path" && -x "$path" ]]; then
        # Inspect each prefix before normalizing: link/../home must also fail.
        current="$path"
        while [[ "$current" != / ]]; do
            [[ ! -L "$current" ]] || break
            current="${current%/*}"
            current="${current:-/}"
        done
        if [[ "$current" == / ]]; then
            if probe="$(mktemp -- "$path/.hvo-dotnet-home.XXXXXX")"; then
                rm -f -- "$probe" && return 0
            fi
        fi
    fi
    printf 'ARM64 CI preflight: %s must be an existing, writable, non-symlinked absolute directory for .NET named mutexes. Set a real HOME or a valid XDG_RUNTIME_DIR in the runner service; DOTNET_CLI_HOME alone is insufficient.\n' "$variable" >&2
    return 1
}
