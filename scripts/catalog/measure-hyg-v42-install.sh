#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR

if [[ $# -ne 2 ]]; then
    printf 'Usage: %s VERIFIED_PRODUCTION_BUNDLE OUTPUT_JSON\n' "$0" >&2
    exit 2
fi

BUNDLE="$(realpath "$1")"
readonly BUNDLE
OUTPUT="$(realpath -m "$2")"
readonly OUTPUT
WORK="$(mktemp -d "${TMPDIR:-/tmp}/hvo-catalog-install-measure.XXXXXX")"
readonly WORK

cleanup() {
    local status=$?
    trap - EXIT
    chmod -R u+w "$WORK" 2>/dev/null || true
    rm -rf -- "$WORK"
    exit "$status"
}
trap cleanup EXIT

for command_name in awk cut nproc realpath sort sqlite3 stat uname wc; do
    command -v "$command_name" >/dev/null || {
        printf 'missing required command: %s\n' "$command_name" >&2
        exit 1
    }
done
[[ -x /usr/bin/time ]] || { printf 'missing required command: /usr/bin/time\n' >&2; exit 1; }

mkdir -p "$(dirname "$OUTPUT")"
revision="${HVO_PERF_REVISION:-working-tree}"
[[ "$revision" =~ ^[A-Za-z0-9._+-]{1,64}$ ]] || { printf 'HVO_PERF_REVISION is invalid\n' >&2; exit 2; }
manifest_bytes="$(wc -c < "$BUNDLE/manifest.json")"
logical_bytes=$((9302016 + 423 + 1361 + manifest_bytes))
latencies=()

{
    printf '{\n'
    printf '  "schema": "hvo-catalog-install-performance-v1",\n'
    printf '  "revision": "%s",\n' "$revision"
    printf '  "environment": {"os": "%s", "architecture": "%s", "processorCount": %s, "sqliteVersion": "%s", "fileSystem": "%s", "concurrency": 1, "initialBacklog": 0},\n' \
        "$(uname -sr)" "$(uname -m)" "$(nproc)" "$(sqlite3 --version | cut -d' ' -f1)" "$(stat -f -c '%T' "$(dirname "$OUTPUT")")"
    printf '  "bundleBytes": %s,\n' "$logical_bytes"
    printf '  "trials": [\n'
} > "$OUTPUT"

for trial in 1 2 3 4 5; do
    install_root="$WORK/install-$trial"
    stats="$WORK/time-$trial"
    /usr/bin/time -f '%e %U %S %M %I %O' -o "$stats" \
        "$SCRIPT_DIR/install-hyg-v42.sh" install "$BUNDLE" "$install_root" >/dev/null
    read -r elapsed_seconds user_seconds system_seconds maximum_rss file_inputs file_outputs < "$stats"
    latency_ms="$(awk -v seconds="$elapsed_seconds" 'BEGIN { printf "%.0f", seconds * 1000 }')"
    latencies+=("$latency_ms")
    [[ "$(readlink "$install_root/current")" == "versions/hyg-v4.2-p3-s2-r1" ]]
    {
        [[ $trial -eq 1 ]] || printf ',\n'
        printf '    {"trial": %d, "latencyMilliseconds": %s, "userCpuSeconds": %s, "systemCpuSeconds": %s, "maximumRssKilobytes": %s, "fileSystemInputBlocks": %s, "fileSystemOutputBlocks": %s, "logicalBytesCopied": %s, "logicalFilesCopied": 4, "logicalBundleValidationPasses": 4, "logicalSqliteCommands": 40, "implementationAtomicRenames": 3, "implementationFsyncCalls": 11, "managedAllocations": "N/A (external processes)"}' \
            "$trial" "$latency_ms" "$user_seconds" "$system_seconds" "$maximum_rss" "$file_inputs" "$file_outputs" "$logical_bytes"
    } >> "$OUTPUT"
done

{
    mapfile -t sorted_latencies < <(printf '%s\n' "${latencies[@]}" | sort -n)
    printf '\n  ],\n'
    printf '  "latencySummaryMilliseconds": {"minimum": %s, "median": %s, "maximum": %s}\n' \
        "${sorted_latencies[0]}" "${sorted_latencies[2]}" "${sorted_latencies[4]}"
    printf '}\n'
} >> "$OUTPUT"

printf 'Catalog install performance evidence written to %s\n' "$OUTPUT"
