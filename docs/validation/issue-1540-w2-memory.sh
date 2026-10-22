#!/usr/bin/env bash
# Paired W2 process-memory evidence for #1540 (#1187 F3): the unchanged #1170 ComposedShippedGraph harness, steady
# w2-night, production runtime defaults, one fresh process per trial, two prebuilt revisions interleaved B-C/C-B.
#
# Usage:
#   issue-1540-w2-memory.sh prepare <arms dir> <label>=<sha> [<label>=<sha> ...]
#       Adds one detached worktree per revision under <arms dir> and builds its CameraAgent tests in Release.
#   HVO_ASTROMETRY_CATALOG_ROOT=<root> issue-1540-w2-memory.sh measure <arms dir> <new out dir> <pairs> <first> <second>
#       Runs <pairs> interleaved pairs (first-second, then second-first, ...). No collector is attached.
#   HVO_ASTROMETRY_CATALOG_ROOT=<root> HVO_1540_TOOLS=<dir> issue-1540-w2-memory.sh attribute <arms dir> <new out dir> <pairs> <first> <second>
#       The same pairs with the harness PID handshake: dotnet-counters (System.Runtime, 1 s), a /proc sampler
#       (status and smaps_rollup every 250 ms, full smaps every 5 s), a GC-keyword dotnet-trace stopped by the
#       harness's MeasurementComplete event, and a gcdump of the live heap after the last operation. Instrumented
#       trials are attribution evidence only; they never replace the measure pack.
#   issue-1540-w2-memory.sh remove <arms dir>
#       Removes the worktrees prepare added.
#
# The whole run holds /tmp/hvo-heavy-gate.lock (flock -o, re-executed so no child inherits it), so no other heavy
# step on the host overlaps a trial. Builds disable MSBuild node reuse and the shared compiler; nothing runs
# dotnet build-server shutdown. Every trial must pass exactly one test and write its evidence; failures are kept
# and the script exits nonzero after all trials. The pack gets an index.json and SHA256SUMS.
set -uo pipefail

fatal() { echo "$*" >&2; exit 1; }
script=$(realpath "${BASH_SOURCE[0]}") || exit 2
lock=/tmp/hvo-heavy-gate.lock
project=tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj
filter="FullyQualifiedName~Issue1170ComposedPipelineEvidenceTests.ComposedShippedGraph"
mode=${1:?usage: issue-1540-w2-memory.sh <prepare|measure|attribute|remove> ...}
arms=${2:?arms directory is required}
[[ "$arms" == /* ]] || arms="$PWD/$arms"
repo=$(git -C "$(dirname "$script")" rev-parse --show-toplevel) || exit 2
for tool in git jq flock sha256sum python3 dotnet zstd; do command -v "$tool" >/dev/null || fatal "$tool is required"; done

if [[ "${HVO_1540_HEAVY_LOCK:-}" != "$lock" ]]; then
    export HVO_1540_HEAVY_LOCK=$lock MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false UseRazorBuildServer=false
    exec flock -o -w 7200 -E 75 "$lock" timeout 7200 bash "$script" "$@"
fi
unset HVO_PRODUCTION_CATALOG_BUNDLE
export DOCKER_HOST=unix:///tmp/hvo-no-docker.sock

utc() { date -u +%Y-%m-%dT%H:%M:%S.%3NZ; }

case "$mode" in
prepare)
    shift 2
    (($# > 0)) || fatal "prepare needs at least one <label>=<sha>"
    mkdir -p "$arms" || exit 2
    for spec in "$@"; do
        label=${spec%%=*}; sha=$(git -C "$repo" rev-parse --verify "${spec#*=}^{commit}") || fatal "unknown revision in $spec"
        [[ "$label" =~ ^[a-z][a-z0-9-]*$ ]] || fatal "invalid label $label"
        [[ -e "$arms/$label" ]] && fatal "$arms/$label already exists"
        git -C "$repo" worktree add --detach "$arms/$label" "$sha" >/dev/null || fatal "worktree add failed for $label"
        echo "building $label at $sha"
        timeout 1800 dotnet build "$arms/$label/$project" --configuration Release -warnaserror -nodeReuse:false \
            -p:UseSharedCompilation=false -v q >"$arms/$label.build.log" 2>&1 || fatal "Release build failed; see $arms/$label.build.log"
        jq -n --arg label "$label" --arg sha "$sha" --arg sdk "$(dotnet --version)" --arg built "$(utc)" \
            '{label: $label, revision: $sha, sdk: $sdk, builtUtc: $built}' >"$arms/$label.json"
    done
    exit 0
    ;;
remove)
    for meta in "$arms"/*.json; do
        [[ -f "$meta" ]] || continue
        label=$(jq -r .label "$meta")
        git -C "$repo" worktree remove --force "$arms/$label" && rm -f "$meta" "$arms/$label.build.log"
    done
    rmdir "$arms" 2>/dev/null
    exit 0
    ;;
measure | attribute) ;;
*) fatal "unknown mode: $mode" ;;
esac

out=${3:?output directory is required}; pairs=${4:?pairs is required}; first=${5:?first label is required}; second=${6:?second label is required}
[[ "$out" == /* ]] || out="$PWD/$out"
[[ "$pairs" =~ ^[1-9][0-9]*$ ]] || fatal "pairs must be a positive integer"
[[ -n "${HVO_ASTROMETRY_CATALOG_ROOT:-}" ]] || fatal "HVO_ASTROMETRY_CATALOG_ROOT is required"
if [[ "$mode" == attribute ]]; then
    tools=${HVO_1540_TOOLS:?HVO_1540_TOOLS must hold dotnet-counters, dotnet-gcdump and dotnet-trace}
    for tool in dotnet-counters dotnet-gcdump dotnet-trace; do [[ -x "$tools/$tool" ]] || fatal "$tools/$tool is required"; done
fi
# DOTNET_ROOT locates the installed runtime; it does not change GC or JIT behavior. The harness records it.
env | grep -E '^(DOTNET_|COMPlus_)' | grep -qv '^DOTNET_ROOT=' &&
    fatal "production-default trials must not inherit DOTNET_ runtime overrides or COMPlus_ variables"
for label in "$first" "$second"; do
    [[ -f "$arms/$label.json" ]] || fatal "arm $label is not prepared"
    sha=$(jq -r .revision "$arms/$label.json")
    [[ "$(git -C "$arms/$label" rev-parse HEAD)" == "$sha" ]] || fatal "arm $label moved from $sha"
    [[ -z "$(git -C "$arms/$label" status --porcelain --untracked-files=no)" ]] || fatal "arm $label has tracked changes"
done
mkdir -p "$(dirname "$out")" || exit 2
mkdir "$out" || fatal "output directory must not exist: $out"
mkdir "$out/runs" "$out/host" || exit 2
work=$(mktemp -d "${TMPDIR:-/tmp}/hvo-1540-w2-work.XXXXXX") || exit 2
trap 'rm -rf "$work"' EXIT

dotnet --info >"$out/host/dotnet-info.txt" 2>&1
lscpu >"$out/host/lscpu.txt" 2>&1
cp /proc/meminfo "$out/host/meminfo-before.txt"
tenants() { { utc; cat /proc/loadavg; ps -eo pid,user,pcpu,rss,etimes,comm --sort=-pcpu | head -25; } >"$1"; }
tenants "$out/host/tenants-before.txt"

# Waits up to 10 minutes for the one-minute load to fall below 1.0 and prints the seconds waited; a trial that
# starts after the wait expired is still run, and the index records it as outside the load gate.
gate() {
    local waited=0
    while ! awk -v l="$(cut -d' ' -f1 /proc/loadavg)" 'BEGIN { exit !(l < 1.0) }'; do
        ((waited >= 600)) && break
        sleep 10; waited=$((waited + 10))
    done
    echo "$waited"
}

# Attribution collector for one trial: waits for the harness PID, attaches, then finalizes after the stopping event.
collect() {
    local dir=$1 pid sampler counters trace i=0
    while [[ ! -f "$dir/attach/pid" ]]; do sleep 0.05; ((++i > 2400)) && return 1; done
    pid=$(cat "$dir/attach/pid")
    "$tools/dotnet-counters" collect --process-id "$pid" --counters System.Runtime --refresh-interval 1 \
        --format csv --output "$dir/counters.csv" >"$dir/counters.log" 2>&1 &
    counters=$!
    # GC keyword at verbose: GCStart/End, heap stats, per-heap history and AllocationTick with type names and stacks.
    "$tools/dotnet-trace" collect --process-id "$pid" \
        --providers "Microsoft-Windows-DotNETRuntime:0x40000001:5,HVO-Issue1170-Harness" \
        --stopping-event-provider-name HVO-Issue1170-Harness --stopping-event-event-name MeasurementComplete \
        --output "$dir/gc.nettrace" >"$dir/trace.log" 2>&1 &
    trace=$!
    mkdir "$dir/smaps"
    (
        n=0
        while [[ -r "/proc/$pid/status" ]]; do
            {
                printf '%s ' "$(date +%s.%N)"
                awk '/^(VmRSS|VmHWM|RssAnon|RssFile|RssShmem):/ { printf "%s=%s ", $1, $2 }' "/proc/$pid/status"
                awk 'NR > 1 && /^(Rss|Pss|Anonymous|Private_Dirty|Swap):/ { printf "%s=%s ", $1, $2 }' "/proc/$pid/smaps_rollup"
                echo
            } >>"$dir/proc-samples.txt" 2>/dev/null
            ((n++ % 20 == 0)) && cp "/proc/$pid/smaps" "$dir/smaps/$(date +%s.%N).smaps" 2>/dev/null
            sleep 0.25
        done
    ) &
    sampler=$!
    # Both sessions are live once each tool reports its output; then release the harness.
    i=0; while [[ ! -s "$dir/trace.log" || ! -s "$dir/counters.log" ]] && ((i++ < 300)); do sleep 0.1; done
    sleep 2
    touch "$dir/attach/attached"
    wait "$trace"
    echo "$?" >"$dir/trace.exit"
    cp "/proc/$pid/smaps" "$dir/smaps/final.smaps" 2>/dev/null
    "$tools/dotnet-gcdump" collect --process-id "$pid" --output "$dir/final.gcdump" >"$dir/gcdump.log" 2>&1
    echo "$?" >"$dir/gcdump.exit"
    kill "$sampler" 2>/dev/null; wait "$sampler" 2>/dev/null
    touch "$dir/attach/detached"
    # dotnet-counters ends with the target process; SIGINT is ignored by background jobs of a script.
    i=0; while kill -0 "$counters" 2>/dev/null && ((i++ < 600)); do sleep 0.1; done
    kill -0 "$counters" 2>/dev/null && kill -TERM "$counters"
    wait "$counters" 2>/dev/null
    zstd -q --rm -19 "$dir"/smaps/*.smaps 2>/dev/null
    return 0
}

results='[]'; failed=0
for ((pair = 1; pair <= pairs; pair++)); do
    if ((pair % 2 == 1)); then order=("$first" "$second"); else order=("$second" "$first"); fi
    for label in "${order[@]}"; do
        sha=$(jq -r .revision "$arms/$label.json")
        name=w2-night-p$pair-$label; dir="$out/runs/$name"; mkdir -p "$dir"
        extra=()
        [[ "$mode" == attribute ]] && { mkdir "$dir/attach"; extra+=(HVO_PIPELINE_ATTACH_DIR="$dir/attach"); }
        waited=$(gate); before=$(cat /proc/loadavg); started=$(utc); seconds=$SECONDS
        tenants "$dir/tenants-before.txt"
        collector=
        [[ "$mode" == attribute ]] && { collect "$dir" & collector=$!; }
        (cd "$arms/$label" && env HVO_PIPELINE_EVIDENCE=1 HVO_EVIDENCE_REVISION="$sha" HVO_PIPELINE_SCENARIO=steady \
            HVO_PIPELINE_WORKLOAD=w2-night HVO_PIPELINE_LABEL="$label" HVO_PIPELINE_TRIAL="$pair" \
            HVO_PIPELINE_RUNTIME_PROFILE=production-defaults HVO_PIPELINE_OUTPUT="$dir/data.json" \
            HVO_PIPELINE_WORK_ROOT="$work/$name" "${extra[@]}" \
            timeout 1200 dotnet test "$project" --no-build --no-restore --configuration Release --filter "$filter" \
            --logger "console;verbosity=normal" --logger "trx;LogFileName=$name.trx" --results-directory "$dir/results") >"$dir/test.log" 2>&1
        rc=$?
        if [[ -n "$collector" ]]; then
            wait "$collector" || failed=1
            [[ -s "$dir/gc.nettrace" && -s "$dir/counters.csv" && -s "$dir/final.gcdump" &&
                $(cat "$dir/trace.exit" "$dir/gcdump.exit" 2>/dev/null) == $'0\n0' ]] || failed=1
        fi
        passed=$(grep -cE '^[[:space:]]+Passed ComposedShippedGraph' "$dir/test.log")
        [[ $rc -eq 0 && "$passed" == 1 && -f "$dir/data.json" ]] || failed=1
        echo "$name rc=$rc passed=$passed elapsed=$((SECONDS - seconds))s"
        results=$(jq --arg name "$name" --arg label "$label" --arg sha "$sha" --argjson pair "$pair" --argjson rc "$rc" \
            --arg passed "$passed" --arg started "$started" --arg finished "$(utc)" --argjson elapsed $((SECONDS - seconds)) \
            --arg before "$before" --arg after "$(cat /proc/loadavg)" --argjson waited "$waited" \
            '. + [{name: $name, label: $label, revision: $sha, pair: $pair, exitCode: $rc, passedTests: ($passed | tonumber),
                   startedUtc: $started, finishedUtc: $finished, elapsedSeconds: $elapsed,
                   load: {before: $before, after: $after, gateWaitSeconds: $waited, outsideGate: ($waited >= 600)}}]' \
            <<<"$results") || fatal "could not record $name"
    done
done

cp /proc/meminfo "$out/host/meminfo-after.txt"
tenants "$out/host/tenants-after.txt"
jq -n --arg mode "$mode" --arg script "$(sha256sum "$script" | cut -d' ' -f1)" --arg host "$(uname -sr)" \
    --arg cpu "$(lscpu | sed -n 's/^Model name:[[:space:]]*//p' | head -1)" --argjson cpus "$(nproc)" \
    --arg memory "$(sed -n 's/^MemTotal:[[:space:]]*//p' /proc/meminfo)" --arg sdk "$(dotnet --version)" \
    --argjson pairs "$pairs" --arg first "$first" --arg second "$second" \
    --slurpfile a "$arms/$first.json" --slurpfile b "$arms/$second.json" --argjson runs "$results" \
    '{schema: "issue1540-w2-memory-pack-v1", mode: $mode, driverSha256: $script, kernel: $host, cpu: $cpu,
      logicalProcessors: $cpus, memTotal: $memory, sdk: $sdk, workload: "w2-night", scenario: "steady",
      runtimeProfile: "production-defaults", pairs: $pairs, order: [$first, $second], arms: ($a + $b), runs: $runs}' \
    >"$out/index.json" || fatal "could not write index"
(cd "$out" && find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum >SHA256SUMS)
echo "pack $out failed=$failed"
exit "$failed"
