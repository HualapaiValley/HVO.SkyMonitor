#!/usr/bin/env bash
set -euo pipefail
umask 022
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
# shellcheck source=scripts/catalog/hyg-v44-openngc-common.sh
source "$SCRIPT_DIR/hyg-v44-openngc-common.sh"
[[ $# == 1 && "$1" != -* ]] || { printf 'Usage: %s OUTPUT_DIRECTORY\n' "$0" >&2; exit 2; }
output="$(realpath -m -- "$1")"
[[ ! -e "$output" && ! -L "$output" && "$output" != / ]] || hyg_fail 'output must be a new directory'
parent="$(dirname "$output")"
mkdir -p "$parent"
staging="$(mktemp -d "$parent/.openngc-fetch.XXXXXX")"
cleanup() {
    local result=$?
    trap - EXIT
    chmod -R u+w "$staging" 2>/dev/null || true
    rm -rf -- "$staging"
    exit "$result"
}
trap cleanup EXIT
pinned_git() {
    GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null GIT_CONFIG_NOSYSTEM=1 GIT_TERMINAL_PROMPT=0 \
        git -c protocol.allow=never -c protocol.https.allow=always -c advice.detachedHead=false "$@"
}
pinned_git init --quiet "$staging/checkout"
pinned_git -C "$staging/checkout" fetch --quiet --depth 1 "$OPENNGC_PROJECT_URL" "$OPENNGC_COMMIT"
[[ "$(pinned_git -C "$staging/checkout" rev-parse --verify 'FETCH_HEAD^{commit}')" == "$OPENNGC_COMMIT" ]] || hyg_fail 'unexpected source commit'
pinned_git -C "$staging/checkout" checkout --quiet --detach FETCH_HEAD
hygngc_stage_openngc_source "$staging/checkout" "$staging/verified"
chmod -R u+w "$staging/checkout"
rm -rf -- "$staging/checkout"
mv -T -- "$staging/verified" "$output"
rm -rf -- "$staging"
trap - EXIT
printf 'Verified pinned public OpenNGC inputs.\n'
