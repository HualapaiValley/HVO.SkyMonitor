#!/usr/bin/env bash
# Runs the predeclared #1170 celestial-pipeline manifest at the current committed
# head and writes a hashed evidence pack.
#
# Usage: HVO_ASTROMETRY_CATALOG_ROOT=<snapshot root> docs/validation/issue-1170-pipeline.sh <mode> <new output directory>
#   mode measure    S2-S4 cells x manifest trials, one process each, no collectors.
#   mode attribute  attribution cells x profiles, one process each, dotnet-trace attached (EventPipe).
#   mode s1         the unchanged #1106 manifest (DOTNET_TieredCompilation=0) under the same lock and host record.
# Optional: HVO_1170_CELLS=<regex> restricts measure/attribute cells (recorded in the index);
#           HVO_1170_TOOLS=<dir> holds dotnet-trace (default ~/.local/share/hvo-1170-tools);
#           HVO_1170_WORK_ROOT=<dir> holds per-trial durable roots, each deleted by the harness.
#
# Requires bash 5.1+, git, jq, flock, sha256sum, zstd, python3 and the pinned .NET SDK. Docker is not used.
# The heavy lock follows #520 addendum 003: the script re-executes itself under `flock -o`, so only flock holds
# the lock and no child (dotnet test, MSBuild nodes, build servers) inherits it or outlives the run holding it.
# MSBuild node reuse and the compiler and Razor build servers are disabled for every build and test the run
# starts, and `dotnet build-server shutdown` runs before S1 and before every measured trial.
# Every process must pass exactly one test and write its evidence; a filter that selects nothing is a
# failure, never a skip. Failures are kept in the pack and the script exits nonzero once all processes
# have finished. The pack gets an index, SHA256SUMS and a .tar.zst with its own hash beside it.
set -uo pipefail

fatal() { echo "$*" >&2; exit 1; }
script=$(realpath "${BASH_SOURCE[0]}") || exit 2
lock=/tmp/hvo-1170-heavy.lock
repo=$(git rev-parse --show-toplevel) || exit 2
manifest="$repo/docs/validation/issue-1170-pipeline-manifest.json"
mode=${1:?usage: issue-1170-pipeline.sh <measure|attribute|s1> <new output directory>}
out=${2:?usage: issue-1170-pipeline.sh <measure|attribute|s1> <new output directory>}
[[ "$mode" =~ ^(measure|attribute|s1)$ ]] || { echo "unknown mode: $mode" >&2; exit 2; }
[[ "$out" == /* ]] || out="$PWD/$out"
[[ -n "${HVO_ASTROMETRY_CATALOG_ROOT:-}" ]] || { echo "HVO_ASTROMETRY_CATALOG_ROOT is required" >&2; exit 2; }
for tool in jq flock sha256sum zstd python3 dotnet; do command -v "$tool" >/dev/null || { echo "$tool is required" >&2; exit 2; }; done
tools=${HVO_1170_TOOLS:-$HOME/.local/share/hvo-1170-tools}
work=${HVO_1170_WORK_ROOT:-$HOME/development-state/HVO.SkyMonitor/evidence/1170/work}
cells_filter=${HVO_1170_CELLS:-.}
[[ "$mode" != attribute || -x "$tools/dotnet-trace" ]] || { echo "dotnet-trace is required under $tools" >&2; exit 2; }
cd "$repo" || exit 2
[[ -z "$(git status --porcelain --untracked-files=all)" ]] || { echo "commit or remove all changes and untracked files before measuring" >&2; exit 2; }
revision=$(git rev-parse HEAD)
manifest_sha=$(sha256sum "$manifest" | cut -d' ' -f1) || exit 2
base=$(jq -r .productBase "$manifest")
git cat-file -e "$base^{commit}" 2>/dev/null || { echo "product base $base is not available" >&2; exit 2; }
product_unchanged=false
git diff --quiet "$base" HEAD -- src && product_unchanged=true
if [[ "${HVO_1170_HEAVY_LOCK:-}" != "$lock" ]]; then
    # MSBuild reads these variables as properties: no reused nodes, no compiler or Razor server outliving a build.
    export HVO_1170_HEAVY_LOCK=$lock MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false UseRazorBuildServer=false
    exec flock -o -w 7200 -E 75 "$lock" bash "$script" "$mode" "$out"
fi
mkdir -p "$(dirname "$out")" || exit 2
mkdir "$out" || { echo "output directory must not exist: $out" >&2; exit 2; }
mkdir "$out/runs" "$out/host" || exit 2
out=$(cd "$out" && pwd -P) || exit 2
mkdir -p "$work" || exit 2

utc() { date -u +%Y-%m-%dT%H:%M:%SZ; }
load1() { cut -d' ' -f1 /proc/loadavg; }
tenants() { { utc; cat /proc/loadavg; ps -eo pid,user,pcpu,rss,etimes,comm --sort=-pcpu | head -25; } >"$1"; }
# Stops build servers left by earlier builds so they are neither tenants nor part of CPU and memory snapshots.
shutdown_build_servers() { dotnet build-server shutdown >"$1" 2>&1 || fatal "dotnet build-server shutdown failed; see $1"; }
threshold=$(jq -r .loadGate.oneMinuteBelow "$manifest"); max_wait=$(jq -r .loadGate.maxWaitSeconds "$manifest")
# Waits for the one-minute load to fall below the manifest threshold; prints the seconds waited.
gate() {
    local waited=0
    while awk -v l="$(load1)" -v t="$threshold" 'BEGIN { exit !(l >= t) }'; do
        ((waited >= max_wait)) && break
        sleep 10; waited=$((waited + 10))
    done
    echo "$waited"
}

echo "revision $revision mode $mode"
paranoid=$(cat /proc/sys/kernel/perf_event_paranoid 2>/dev/null || echo unknown)
perf_state="unavailable (perf not installed)"
if command -v perf >/dev/null; then
    perf_state="unavailable (paranoid=$paranoid)"
    perf stat -e cpu-clock -- true >/dev/null 2>&1 && perf_state="available (paranoid=$paranoid)"
fi
governor="not exposed (VM)"
[[ -r /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor ]] && governor=$(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor)
dotnet --info >"$out/host/dotnet-info.txt" 2>&1
dotnet --list-sdks >"$out/host/dotnet-sdks.txt" 2>&1
lscpu >"$out/host/lscpu.txt" 2>&1
tenants "$out/host/tenants-before.txt"
trace_version=
[[ -x "$tools/dotnet-trace" ]] && trace_version=$("$tools/dotnet-trace" --version 2>/dev/null)
host=$(jq -n --arg kernel "$(uname -sr)" --arg cpu "$(lscpu | sed -n 's/^Model name:[[:space:]]*//p' | head -1)" \
    --arg hypervisor "$(lscpu | sed -n 's/^Hypervisor vendor:[[:space:]]*//p' | head -1)" \
    --arg cores "$(nproc)" --arg memory "$(sed -n 's/^MemTotal:[[:space:]]*//p' /proc/meminfo)" \
    --arg swap "$(sed -n 's/^SwapTotal:[[:space:]]*//p' /proc/meminfo)" --arg sdk "$(dotnet --version)" \
    --arg governor "$governor" --arg perf "$perf_state" --arg trace "$trace_version" --arg hostname "$(hostname)" \
    --arg dotnetvars "$(env | grep -E '^(DOTNET_|COMPlus_)' | sort)" \
    --arg buildvars "$(env | grep -E '^(MSBUILDDISABLENODEREUSE|UseSharedCompilation|UseRazorBuildServer)=' | sort)" \
    '{hostname: $hostname, kernel: $kernel, cpu: $cpu, hypervisor: $hypervisor, logicalProcessors: ($cores | tonumber),
      memTotal: $memory, swapTotal: $swap, sdk: $sdk, governor: $governor, perf: $perf,
      dotnetTrace: (if $trace == "" then null else $trace end), callerDotnetVariables: $dotnetvars,
      heavyLock: {path: "/tmp/hvo-1170-heavy.lock", form: "flock -o", buildVariables: $buildvars,
                  buildServerShutdown: "before S1 and before every measured trial"}}') || fatal "could not record host"

results='[]'; failed=0
record() { results=$(jq --argjson run "$1" '. + [$run]' <<<"$results") || fatal "could not record run"; }

if [[ "$mode" == s1 ]]; then
    shutdown_build_servers "$out/runs/s1-build-server-shutdown.log"
    waited=$(gate); before=$(load1); started=$(utc); seconds=$SECONDS
    "$repo/docs/validation/issue-1106-qualification.sh" "$out/s1" >"$out/runs/s1.log" 2>&1
    rc=$?; [[ $rc -eq 0 ]] || failed=1
    index_sha=
    [[ -f "$out/s1/index.json" ]] && index_sha=$(sha256sum "$out/s1/index.json" | cut -d' ' -f1)
    record "$(jq -n --arg started "$started" --arg finished "$(utc)" --argjson elapsed $((SECONDS - seconds)) --argjson rc "$rc" \
        --arg before "$before" --arg after "$(load1)" --argjson waited "$waited" --arg index "$index_sha" \
        '{name: "s1-issue-1106-manifest", section: "S1", runtimeProfile: "tc0-1106-comparability", exitCode: $rc,
          status: (if $rc == 0 then "passed" else "failed" end), startedUtc: $started, finishedUtc: $finished,
          elapsedSeconds: $elapsed, load: {before: $before, after: $after, gateWaitSeconds: $waited},
          issue1106IndexSha256: (if $index == "" then null else $index end)}')"
else
    dotnet build "$(jq -r .project "$manifest")" --configuration Release -warnaserror -v q >"$out/build.log" 2>&1 ||
        fatal "Release build failed; see $out/build.log"
    project=$(jq -r .project "$manifest"); filter=$(jq -r .filter "$manifest")
    sources='[]'
    while read -r file; do
        blob=$(git rev-parse "HEAD:$file") || fatal "could not resolve $file"
        sources=$(jq --arg path "$file" --arg blob "$blob" '. + [{path: $path, blob: $blob}]' <<<"$sources") || exit 1
    done < <(jq -r '.harnessSources[], .analysisSources[]' "$manifest")
    harness_provider=$(jq -r .attribution.stoppingProvider "$manifest")
    if [[ "$mode" == measure ]]; then
        mapfile -t cells < <(jq -r '.cells[].id' "$manifest" | grep -E "$cells_filter")
        trials=$(jq -r .trials "$manifest"); profiles=("")
    else
        mapfile -t cells < <(jq -r '.attribution.cells[]' "$manifest" | grep -E "$cells_filter")
        trials=$(jq -r .attribution.trials "$manifest"); mapfile -t profiles < <(jq -r '.attribution.profiles | keys[]' "$manifest")
    fi
    ((${#cells[@]} > 0)) || fatal "no cells selected by HVO_1170_CELLS=$cells_filter"
    for cell in "${cells[@]}"; do
        spec=$(jq -c --arg id "$cell" '.cells[] | select(.id == $id)' "$manifest")
        [[ -n "$spec" ]] || fatal "unknown cell $cell"
        scenario=$(jq -r .scenario <<<"$spec"); workload=$(jq -r .workload <<<"$spec"); profile=$(jq -r .runtimeProfile <<<"$spec")
        for collector in "${profiles[@]}"; do
            for ((trial = 1; trial <= trials; trial++)); do
                name=$cell-t$trial${collector:+-$collector}; dir="$out/runs/$name"; mkdir -p "$dir"
                extra=(); [[ "$profile" == tc0-side-cell ]] && extra+=(DOTNET_TieredCompilation=0)
                [[ -n "$collector" ]] && { mkdir "$dir/attach"; extra+=(HVO_PIPELINE_ATTACH_DIR="$dir/attach"); }
                shutdown_build_servers "$dir/build-server-shutdown.log"
                waited=$(gate); before=$(load1); tenants "$dir/tenants-before.txt"
                started=$(utc); seconds=$SECONDS
                echo "=== $name $started load=$before waited=${waited}s"
                env HVO_PIPELINE_EVIDENCE=1 HVO_EVIDENCE_REVISION="$revision" HVO_PIPELINE_SCENARIO="$scenario" \
                    HVO_PIPELINE_WORKLOAD="$workload" HVO_PIPELINE_LABEL="$mode" HVO_PIPELINE_TRIAL="$trial" \
                    HVO_PIPELINE_RUNTIME_PROFILE="$profile" HVO_PIPELINE_OUTPUT="$dir/evidence.json" \
                    HVO_PIPELINE_WORK_ROOT="$work/$(basename "$out")-$name" "${extra[@]}" \
                    dotnet test "$project" --no-build --no-restore --configuration Release --filter "$filter" \
                    --results-directory "$dir/results" --logger "trx;LogFileName=run.trx" >"$dir/test.log" 2>&1 &
                test_pid=$!; trace_pid=
                if [[ -n "$collector" ]]; then
                    i=0; while [[ ! -f "$dir/attach/pid" ]] && ((i++ < 1200)); do sleep 0.1; done
                    if [[ -f "$dir/attach/pid" ]]; then
                        # The harness emits MeasurementComplete and waits for "detached", so the session ends with
                        # rundown while the process is alive; process exit would truncate the trace.
                        { "$tools/dotnet-trace" collect -p "$(cat "$dir/attach/pid")" \
                              --profile "$(jq -r --arg p "$collector" '.attribution.profiles[$p]' "$manifest")" \
                              --providers "$harness_provider" --stopping-event-provider-name "$harness_provider" \
                              --stopping-event-event-name MeasurementComplete -o "$dir/trace.nettrace" >"$dir/trace.log" 2>&1
                          echo "$?" >"$dir/trace.exit"; touch "$dir/attach/detached"; } &
                        trace_pid=$!
                        # The session is live once dotnet-trace reports its output file; then release the harness.
                        i=0; while [[ ! -s "$dir/trace.log" ]] && ((i++ < 300)); do sleep 0.1; done
                        sleep 2; touch "$dir/attach/attached"
                    fi
                fi
                wait "$test_pid"; rc=$?
                [[ -z "$trace_pid" ]] || wait "$trace_pid"
                finished=$(utc); elapsed=$((SECONDS - seconds)); after=$(load1); tenants "$dir/tenants-after.txt"
                trx=$(find "$dir/results" -name run.trx -print -quit 2>/dev/null); counters=
                [[ -z "$trx" ]] || counters=$(grep -o '<Counters [^>]*>' "$trx" | head -1)
                attr() { sed -n "s/.* $1=\"\\([0-9]*\\)\".*/\\1/p" <<<"$counters"; }
                status=passed
                [[ $rc -eq 0 && "$(attr passed)" == 1 && "$(attr executed)" == 1 && "$(attr total)" == 1 && "$(attr failed)" == 0 ]] || status=failed
                evidence_sha=
                if [[ -f "$dir/evidence.json" ]]; then
                    evidence_sha=$(sha256sum "$dir/evidence.json" | cut -d' ' -f1)
                    jq -e --arg r "$revision" '.revision == $r and (.failures | length) == 0' "$dir/evidence.json" >/dev/null || status=failed
                else status=failed; fi
                trace_sha=
                if [[ -n "$collector" ]]; then
                    if [[ -s "$dir/trace.nettrace" && "$(cat "$dir/trace.exit" 2>/dev/null)" == 0 ]]; then
                        trace_sha=$(sha256sum "$dir/trace.nettrace" | cut -d' ' -f1)
                        "$tools/dotnet-trace" report "$dir/trace.nettrace" topN -n 60 >"$dir/topN-exclusive.txt" 2>&1
                        "$tools/dotnet-trace" report "$dir/trace.nettrace" topN -n 200 --inclusive >"$dir/topN-inclusive.txt" 2>&1
                        if [[ "$collector" == cpu ]]; then
                            "$tools/dotnet-trace" convert "$dir/trace.nettrace" --format Speedscope -o "$dir/trace" >"$dir/convert.log" 2>&1 &&
                                python3 -I "$repo/docs/validation/issue-1170-oncpu.py" "$dir/trace.speedscope.json" "$dir/oncpu.json" 200 ||
                                status=failed
                            rm -f "$dir/trace.speedscope.json" "$dir/trace.nettrace.etlx"
                        fi
                    else status=failed; fi
                fi
                [[ "$status" == passed ]] || failed=1
                echo "    $status rc=$rc ${elapsed}s"
                record "$(jq -n --arg name "$name" --arg cell "$cell" --argjson spec "$spec" --argjson trial "$trial" \
                    --arg collector "$collector" --arg status "$status" --argjson rc "$rc" --arg started "$started" \
                    --arg finished "$finished" --argjson elapsed "$elapsed" --arg before "$before" --arg after "$after" \
                    --argjson waited "$waited" --arg evidence "$evidence_sha" --arg trace "$trace_sha" \
                    --arg passed "$(attr passed)" --arg executed "$(attr executed)" --arg total "$(attr total)" --arg failures "$(attr failed)" \
                    '{name: $name, cell: $cell, section: $spec.section, scenario: $spec.scenario, workload: $spec.workload,
                      runtimeProfile: $spec.runtimeProfile, trial: $trial, collector: (if $collector == "" then null else $collector end),
                      status: $status, exitCode: $rc, startedUtc: $started, finishedUtc: $finished, elapsedSeconds: $elapsed,
                      load: {before: $before, after: $after, gateWaitSeconds: $waited},
                      trx: {total: $total, executed: $executed, passed: $passed, failed: $failures},
                      evidenceSha256: (if $evidence == "" then null else $evidence end),
                      traceSha256: (if $trace == "" then null else $trace end)}')"
            done
        done
    done
fi

tenants "$out/host/tenants-after.txt"
jq -n --arg revision "$revision" --arg manifest "$manifest_sha" --arg mode "$mode" --arg base "$base" \
    --argjson unchanged "$product_unchanged" --arg cells "$cells_filter" --argjson host "$host" \
    --argjson sources "${sources:-[]}" --argjson runs "$results" --arg status "$([[ $failed -eq 0 ]] && echo passed || echo failed)" \
    '{schema: "issue1170-pipeline-evidence-index-v1", issue: 1170, mode: $mode, revision: $revision, manifestSha256: $manifest,
      productBase: $base, productSourceUnchangedFromBase: $unchanged, cellFilter: $cells, status: $status, host: $host,
      harnessSources: $sources, runs: $runs}' >"$out/index.json" || fatal "could not write $out/index.json"
jq -e --arg revision "$revision" --arg manifest "$manifest_sha" '
    def sha256: type == "string" and test("^[0-9a-f]{64}$");
    .revision == $revision and .manifestSha256 == $manifest and (.runs | length) > 0
      and (.mode == "s1" or ((.harnessSources | length) > 0 and all(.harnessSources[]; .blob | test("^[0-9a-f]{40}$"))))
      and (.status != "passed" or all(.runs[]; .status == "passed"
           and (.section == "S1" or (.evidenceSha256 | sha256)) and (.collector == null or (.traceSha256 | sha256))))' \
    "$out/index.json" >/dev/null || fatal "index $out/index.json is incomplete"
(cd "$out" && find . -type f -print0 | sort -z | xargs -0 sha256sum >"$out.SHA256SUMS" && mv "$out.SHA256SUMS" SHA256SUMS) ||
    fatal "could not hash the pack"
tar --zstd -C "$(dirname "$out")" -cf "$out.tar.zst" "$(basename "$out")" || fatal "could not archive the pack"
sha256sum "$out.tar.zst" >"$out.tar.zst.sha256" || fatal "could not hash the archive"
echo "index $out/index.json status=$(jq -r .status "$out/index.json") archive $(cut -d' ' -f1 "$out.tar.zst.sha256")"
exit $failed
