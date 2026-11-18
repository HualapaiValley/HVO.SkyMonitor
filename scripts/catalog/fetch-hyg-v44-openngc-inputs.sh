#!/usr/bin/env bash
set -euo pipefail
umask 022

# Acquires the production inputs of the HYG 4.4 and OpenNGC builders from their pinned sources and verifies them
# before anything reads them. Full qualification runs this ahead of test-hyg-v44-reproducibility.sh (Refs #1774); it
# is the only part of that regression that touches the network, and the builders it feeds stay offline.
#
#   OUTPUT_DIRECTORY/hyg_v44.csv.gz   the compressed HYG 4.4 input
#   OUTPUT_DIRECTORY/OpenNGC          the OpenNGC files the composer reads, and nothing else
#
# Every byte left in OUTPUT_DIRECTORY has been checked against a pin in this repository:
#   - the HYG input by length and SHA-256 (hyg-v44-common.sh);
#   - OpenNGC's NGC.csv and addendum.csv by length and SHA-256 (hyg-v44-openngc-common.sh);
#   - the OpenNGC outline directory by its exact set of 379 regular files and the SHA-256 of each, against the
#     committed listing, which is itself checked by length and SHA-256. The repository pins no per-file outline
#     length; a matching SHA-256 leaves none to check.
# OpenNGC is fetched as the one pinned commit, and the fetched commit ID must equal the pin. Any mismatch fails and
# leaves no OUTPUT_DIRECTORY behind, so a caller can never read a partly verified input.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
# shellcheck source=scripts/catalog/hyg-v44-openngc-common.sh
source "$SCRIPT_DIR/hyg-v44-openngc-common.sh"

if [[ $# -ne 1 || "$1" == -* ]]; then
    printf 'Usage: %s OUTPUT_DIRECTORY\n' "$0" >&2
    exit 2
fi

hyg_require_commands chmod cp curl dirname find git mkdir mktemp mv realpath rm sha256sum sort wc

OUTPUT="$(realpath -m -- "$1")"
readonly OUTPUT
OUTPUT_PARENT="$(dirname "$OUTPUT")"
readonly OUTPUT_PARENT

[[ "$OUTPUT" != "/" ]] || hyg_fail "refusing root as the output directory"
[[ ! -e "$OUTPUT" && ! -L "$OUTPUT" ]] || hyg_fail "output directory already exists: $OUTPUT"
mkdir -p "$OUTPUT_PARENT"
[[ -d "$OUTPUT_PARENT" && ! -L "$OUTPUT_PARENT" ]] || hyg_fail "output parent is not a safe directory: $OUTPUT_PARENT"

staging="$(mktemp -d "$OUTPUT_PARENT/.hyg-v44-openngc-inputs.XXXXXX")"
cleanup() {
    local status=$?
    trap - EXIT
    if [[ -n "$staging" ]]; then
        chmod -R u+w "$staging" 2>/dev/null || true
        rm -rf -- "$staging"
    fi
    exit "$status"
}
trap cleanup EXIT

# The fetch reads no ambient Git configuration, so a rewritten URL, a credential helper or a filter configured on
# the host cannot change what is fetched or run anything during checkout, and it speaks HTTPS only.
pinned_git() {
    GIT_CONFIG_GLOBAL=/dev/null GIT_CONFIG_SYSTEM=/dev/null GIT_CONFIG_NOSYSTEM=1 GIT_TERMINAL_PROMPT=0 \
        git -c protocol.allow=never -c protocol.https.allow=always -c advice.detachedHead=false "$@"
}

compressed="$staging/hyg_v44.csv.gz"
checkout="$staging/checkout"
openngc="$staging/OpenNGC"

printf 'Fetching pinned HYG 4.4 source: %s\n' "$HYG44_SOURCE_URL"
curl --fail --location --proto '=https' --tlsv1.2 "$HYG44_SOURCE_URL" --output "$compressed"
hyg_verify_file "$compressed" "$HYG44_COMPRESSED_LENGTH" "$HYG44_COMPRESSED_SHA256" "compressed HYG source"

printf 'Fetching pinned OpenNGC source: %s at %s\n' "$OPENNGC_PROJECT_URL" "$OPENNGC_COMMIT"
pinned_git init --quiet "$checkout"
pinned_git -C "$checkout" fetch --quiet --depth 1 "$OPENNGC_PROJECT_URL" "$OPENNGC_COMMIT"
fetched_commit="$(pinned_git -C "$checkout" rev-parse --verify 'FETCH_HEAD^{commit}')"
[[ "$fetched_commit" == "$OPENNGC_COMMIT" ]] ||
    hyg_fail "OpenNGC fetch returned commit $fetched_commit, expected $OPENNGC_COMMIT"
pinned_git -C "$checkout" checkout --quiet --detach FETCH_HEAD

# Copies only the pinned files out of the checkout and verifies the copies, exactly as the composer stages them.
hygngc_stage_openngc_source "$checkout" "$openngc"

chmod -R u+w "$checkout"
rm -rf -- "$checkout"
chmod 0444 "$compressed"
mv -T -- "$staging" "$OUTPUT"
staging=""
trap - EXIT

printf 'Verified HYG 4.4 input: %s\n' "$OUTPUT/hyg_v44.csv.gz"
printf 'Verified OpenNGC inputs: %s\n' "$OUTPUT/OpenNGC"
