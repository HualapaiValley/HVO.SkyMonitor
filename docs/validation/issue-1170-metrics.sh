#!/usr/bin/env bash
# Summarizes #1170 measure packs and, given two, applies the manifest regression rule.
#
# Usage: docs/validation/issue-1170-metrics.sh <pack directory>
#        docs/validation/issue-1170-metrics.sh <baseline pack directory> <after pack directory>
#
# A pack is the output directory of docs/validation/issue-1170-pipeline.sh in measure mode. Per cell the
# summary reports trial medians as median/min/max, p95 per trial and pooled over every measured operation
# (nearest rank, never derived from trial medians), allocated bytes per operation, peak RSS (VmHWM) and,
# for S4, the drain after the last accept. Comparison exits 1 when any cell regresses under the rule:
# median of trial medians above max(5%, baseline trial-median spread), allocated bytes above 1%,
# peak RSS above max(5%, 32 MiB), or any S4 drain increase. Full-frame copy counts are not measurable from
# the evidence; they are compared from the dataflow map in docs/performance/celestial-pipeline-dataflow.md.
set -euo pipefail

# shellcheck disable=SC2016 # jq program variables, not shell expansions.
summarize() {
    local pack=$1
    [[ -f "$pack/index.json" ]] || { echo "not a pack: $pack" >&2; exit 2; }
    jq -e '.mode == "measure"' "$pack/index.json" >/dev/null || { echo "not a measure pack: $pack" >&2; exit 2; }
    jq -r '.runs[] | select(.status == "passed") | .name' "$pack/index.json" |
        while read -r name; do printf '%s\n' "$pack/runs/$name/evidence.json"; done |
        xargs -d '\n' jq -s --slurpfile index "$pack/index.json" '
            def median: sort | if length == 0 then null elif length % 2 == 1 then .[length / 2 | floor]
                else (.[length / 2 - 1] + .[length / 2]) / 2 end;
            def p95: sort | if length == 0 then null else .[((length * 0.95) | ceil) - 1] end;
            def spread($xs): {median: ($xs | median), minimum: ($xs | min), maximum: ($xs | max), trials: ($xs | length)};
            def cell: "\(.runtimeProfile)|\(.scenario)|\(.workload)";
            {revision: $index[0].revision, productBase: $index[0].productBase,
             productSourceUnchangedFromBase: $index[0].productSourceUnchangedFromBase, host: $index[0].host.hostname,
             failedRuns: [$index[0].runs[] | select(.status != "passed") | .name],
             cells: (group_by(cell) | map(
                (map([.samples[] | select(.measured)])) as $trials
                | {key: (.[0] | cell), value: {
                    scenario: .[0].scenario, workload: .[0].workload, runtimeProfile: .[0].runtimeProfile,
                    productionLatency: .[0].productionLatency,
                    foregroundMs: spread(map(.summary.foregroundMilliseconds.median)),
                    processingMs: spread(map(.summary.processingMilliseconds.median)),
                    serviceMs: spread(map(.summary.serviceMilliseconds.median)),
                    p95PerTrial: {foreground: map(.summary.foregroundMilliseconds.p95), processing: map(.summary.processingMilliseconds.p95),
                                  service: map(.summary.serviceMilliseconds.p95)},
                    p95Pooled: (if .[0].scenario != "steady" then null else {foreground: ([$trials[][] | .foregroundMilliseconds] | p95),
                                processing: ([$trials[][] | .processingMilliseconds] | p95),
                                service: ([$trials[][] | .foregroundMilliseconds + .processingMilliseconds] | p95),
                                operations: ([$trials[][]] | length)} end),
                    allocatedBytesPerOperation: spread(map(.summary.allocatedBytesPerOperation.median)),
                    allocatedBytesPerRawByte: spread(map(.summary.allocatedBytesPerRawByte.median)),
                    peakRssBytes: spread(map(.memory.status.VmHWM)),
                    lohBytes: spread(map(.memory.lohSizeAfterBytes)),
                    jitMilliseconds: spread(map(.jit.compilationMilliseconds)),
                    cold: (if .[0].scenario == "cold" then {
                        firstOperationServiceMs: spread(map(.samples[0] | .foregroundMilliseconds + .processingMilliseconds)),
                        testStartToFirstCompletionMs: spread(map(.setup.testStartToFirstCompletion.milliseconds)),
                        processUptimeAtFirstCompletionMs: spread(map(.setup.processUptimeAtFirstCompletionMilliseconds))} else null end),
                    saturation: (if .[0].scenario == "saturation" then {
                        drainAfterLastAcceptMs: spread(map(.saturation.drainAfterLastAcceptMilliseconds)),
                        totalElapsedMs: spread(map(.saturation.totalElapsedMilliseconds)),
                        completionRatePerSecond: spread(map(.saturation.completionRatePerSecond)),
                        backlogPeak: spread(map(.saturation.backlogPeak))} else null end)}})
                | from_entries)}'
}

compare() {
    jq -n --argjson base "$1" --argjson after "$2" '
        def rel($b; $a): if $b == 0 then null else ($a - $b) / $b end;
        def latency($b; $a): if $b == null or $b.median == null then null else ([0.05, (if $b.median == 0 then 0 else ($b.maximum - $b.minimum) / $b.median end)] | max) as $limit
            | {baseline: $b.median, after: $a.median, change: rel($b.median; $a.median), limit: $limit,
               regressed: ($a.median == null or (rel($b.median; $a.median) // 0) > $limit)} end;
        {baseline: $base.revision, after: $after.revision,
         cells: [$base.cells | to_entries[] | .key as $k | .value as $b | ($after.cells[$k] // null) as $a
            | if $a == null then {cell: $k, missing: true, regressed: true} else
              {cell: $k,
               foregroundMs: latency($b.foregroundMs; $a.foregroundMs),
               processingMs: latency($b.processingMs; $a.processingMs),
               serviceMs: latency($b.serviceMs; $a.serviceMs),
               coldFirstOperationServiceMs: latency($b.cold.firstOperationServiceMs; $a.cold.firstOperationServiceMs),
               allocatedBytesPerOperation: {baseline: $b.allocatedBytesPerOperation.median, after: $a.allocatedBytesPerOperation.median,
                   change: rel($b.allocatedBytesPerOperation.median; $a.allocatedBytesPerOperation.median),
                   regressed: ((rel($b.allocatedBytesPerOperation.median; $a.allocatedBytesPerOperation.median) // 0) > 0.01)},
               peakRssBytes: {baseline: $b.peakRssBytes.median, after: $a.peakRssBytes.median,
                   regressed: (($a.peakRssBytes.median - $b.peakRssBytes.median) > ([0.05 * $b.peakRssBytes.median, 33554432] | max))},
               drainAfterLastAcceptMs: (if $b.saturation == null then null else
                   {baseline: $b.saturation.drainAfterLastAcceptMs.median, after: $a.saturation.drainAfterLastAcceptMs.median,
                    regressed: ($a.saturation.drainAfterLastAcceptMs.median > $b.saturation.drainAfterLastAcceptMs.median)} end)}
              | .regressed = ([.foregroundMs, .processingMs, .serviceMs, .coldFirstOperationServiceMs, .allocatedBytesPerOperation, .peakRssBytes,
                               .drainAfterLastAcceptMs] | map(select(. != null) | .regressed) | any) end]}
        | .regressed = (.cells | map(.regressed) | any)'
}

case $# in
    1) summarize "$1" ;;
    2) base=$(summarize "$1"); after=$(summarize "$2"); result=$(compare "$base" "$after"); printf '%s\n' "$result"
       jq -e '.regressed | not' <<<"$result" >/dev/null ;;
    *) echo "usage: issue-1170-metrics.sh <pack> [<after pack>]" >&2; exit 2 ;;
esac
