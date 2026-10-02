#!/usr/bin/env bash
# Runs the predeclared #1106 virtual astrometry qualification manifest at the
# current committed head and writes a hashed evidence pack.
#
# Usage: HVO_ASTROMETRY_CATALOG_ROOT=<snapshot root> docs/validation/issue-1106-qualification.sh <new output directory>
#
# Requires bash, git, jq, sha256sum and the pinned .NET SDK. Docker is not used.
# Every run must execute exactly its expected number of tests with no failures;
# a filter that selects nothing is a failure, never a skip. Failures are kept in
# the pack, and the script exits nonzero once all runs have finished.
set -uo pipefail

repo=$(git rev-parse --show-toplevel) || exit 2
manifest="$repo/docs/validation/issue-1106-qualification-manifest.json"
out=${1:?usage: issue-1106-qualification.sh <new output directory>}
[[ -e "$out" ]] && { echo "output directory already exists: $out" >&2; exit 2; }
[[ -n "${HVO_ASTROMETRY_CATALOG_ROOT:-}" ]] || { echo "HVO_ASTROMETRY_CATALOG_ROOT is required" >&2; exit 2; }
for tool in jq sha256sum dotnet; do command -v "$tool" >/dev/null || { echo "$tool is required" >&2; exit 2; }; done
cd "$repo" || exit 2
[[ -z "$(git status --porcelain --untracked-files=no)" ]] || { echo "commit all tracked changes before measuring" >&2; exit 2; }
revision=$(git rev-parse HEAD)
mkdir -p "$out/runs" "$out/reports" || exit 2
out=$(cd "$out" && pwd)

utc() { date -u +%Y-%m-%dT%H:%M:%SZ; }
echo "revision $revision"
dotnet build HVO.SkyMonitor.v9.slnx --configuration Release -warnaserror -v q >"$out/build.log" 2>&1 || { echo "Release build failed; see $out/build.log" >&2; exit 1; }

host=$(jq -n --arg kernel "$(uname -sr)" --arg cpu "$(lscpu 2>/dev/null | sed -n 's/^Model name:[[:space:]]*//p' | head -1)" \
    --arg cores "$(nproc)" --arg memory "$(sed -n 's/^MemTotal:[[:space:]]*//p' /proc/meminfo 2>/dev/null)" --arg sdk "$(dotnet --version)" \
    '{kernel: $kernel, cpu: $cpu, logicalProcessors: ($cores | tonumber), memTotal: $memory, sdk: $sdk}')

results='[]'; failed=0
count=$(jq '.runs | length' "$manifest")
for ((i = 0; i < count; i++)); do
    run=$(jq -c ".runs[$i]" "$manifest")
    id=$(jq -r .id <<<"$run"); project=$(jq -r .project <<<"$run"); filter=$(jq -r .filter <<<"$run")
    expected=$(jq -r .expectedPassed <<<"$run"); variable=$(jq -r '.perProcess.variable // empty' <<<"$run")
    class=${filter#*~}; class=${class%%.*}
    sources=$(git ls-files tests | grep -E "/(${class}|VirtualAstrometryFixture|VirtualAstrometryReference|VirtualAstrometryPixels)\.cs$" |
        while read -r file; do jq -n --arg path "$file" --arg blob "$(git rev-parse "HEAD:$file")" '{path: $path, blob: $blob}'; done | jq -s .)
    mapfile -t values < <(if [[ -n "$variable" ]]; then jq -r '.perProcess.values[]' <<<"$run"; else echo ""; fi)
    for value in "${values[@]}"; do
        name=$id${value:+-$value}; dir="$out/runs/$name"; mkdir -p "$dir"
        extra=()
        if [[ -n "$variable" ]]; then
            extra+=("$variable=$value")
            while IFS='=' read -r key fixed; do extra+=("$key=$fixed"); done < <(jq -r '.perProcess.fixed // {} | to_entries[] | "\(.key)=\(.value)"' <<<"$run")
        fi
        started=$(utc); seconds=$SECONDS
        echo "=== $name $started"
        env DOTNET_TieredCompilation=0 HVO_EVIDENCE_REVISION="$revision" "${extra[@]}" \
            dotnet test "$project" --no-build --no-restore --configuration Release --filter "$filter" \
            --results-directory "$dir" --logger "trx;LogFileName=run.trx" >"$dir/test.log" 2>&1
        rc=$?; finished=$(utc); elapsed=$((SECONDS - seconds))
        trx=$(find "$dir" -name run.trx -print -quit)
        counters=$( [[ -n "$trx" ]] && grep -o '<Counters [^>]*>' "$trx" | head -1 )
        attr() { sed -n "s/.* $1=\"\\([0-9]*\\)\".*/\\1/p" <<<"$counters"; }
        total=$(attr total); executed=$(attr executed); passed=$(attr passed); failures=$(attr failed)
        status=passed
        [[ $rc -eq 0 && "$passed" == "$expected" && "$executed" == "$expected" && "$total" == "$expected" && "$failures" == "0" ]] || { status=failed; failed=1; }
        reports='[]'
        while read -r pattern; do
            pattern=${pattern//\{value\}/$value}
            # MSTest keeps attached result files flat under In/<id>/<host>/, without the partition directory.
            mapfile -t matches < <(find "$dir" -type f -name "$(basename "$pattern")")
            if [[ ${#matches[@]} -ne 1 ]]; then
                status=failed; failed=1
                reports=$(jq --arg p "$pattern" --argjson n "${#matches[@]}" '. + [{report: $p, matches: $n}]' <<<"$reports"); continue
            fi
            match=${matches[0]}
            target="$out/reports/$name/$(basename "$pattern")"; mkdir -p "$(dirname "$target")"; cp "$match" "$target"
            reports=$(jq --arg p "$pattern" --arg s "$(sha256sum "$target" | cut -d' ' -f1)" --arg b "$(stat -c %s "$target")" \
                '. + [{report: $p, sha256: $s, bytes: ($b | tonumber)}]' <<<"$reports")
        done < <(jq -r '.reports[]' <<<"$run")
        echo "    $status rc=$rc passed=${passed:-?}/$expected ${elapsed}s"
        results=$(jq --arg id "$id" --arg name "$name" --arg value "$value" --arg status "$status" --arg started "$started" --arg finished "$finished" \
            --argjson elapsed "$elapsed" --argjson rc "$rc" --arg total "${total:-}" --arg executed "${executed:-}" --arg passed "${passed:-}" \
            --arg failures "${failures:-}" --argjson expected "$expected" --argjson reports "$reports" --argjson sources "$sources" \
            --arg trx "$( [[ -n "$trx" ]] && sha256sum "$trx" | cut -d' ' -f1 )" \
            '. + [{id: $id, name: $name, workload: (if $value == "" then null else $value end), status: $status, startedUtc: $started,
                finishedUtc: $finished, elapsedSeconds: $elapsed, exitCode: $rc, expectedPassed: $expected,
                trx: {sha256: $trx, total: $total, executed: $executed, passed: $passed, failed: $failures},
                reports: $reports, harnessSources: $sources}]' <<<"$results")
    done
done

jq -n --arg revision "$revision" --arg manifest "$(sha256sum "$manifest" | cut -d' ' -f1)" --argjson host "$host" \
    --argjson runs "$results" --arg status "$([[ $failed -eq 0 ]] && echo passed || echo failed)" \
    '{schema: "virtual-astrometry-final-qualification-index-v1", issue: 1106, revision: $revision, manifestSha256: $manifest,
      status: $status, host: $host, runs: $runs}' >"$out/index.json"
echo "index $out/index.json status=$(jq -r .status "$out/index.json")"
exit $failed
