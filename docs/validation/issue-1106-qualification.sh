#!/usr/bin/env bash
# Runs the predeclared #1106 virtual astrometry qualification manifest at the
# current committed head and writes a hashed evidence pack.
#
# Usage: HVO_ASTROMETRY_CATALOG_ROOT=<snapshot root> docs/validation/issue-1106-qualification.sh <new output directory>
#
# Requires bash, git, jq, sha256sum and the pinned .NET SDK. Docker is not used.
# Every run must execute exactly its expected number of tests with no failures;
# a filter that selects nothing is a failure, never a skip. Failures are kept in
# the pack, and the script exits nonzero once all runs have finished. Failing to
# copy, hash or index evidence is fatal, and the final index is validated
# against the manifest before success is reported.
set -uo pipefail

fatal() { echo "$*" >&2; exit 1; }
repo=$(git rev-parse --show-toplevel) || exit 2
manifest="$repo/docs/validation/issue-1106-qualification-manifest.json"
out=${1:?usage: issue-1106-qualification.sh <new output directory>}
# Resolve against the caller's directory before anything changes it, and create
# the pack root with a plain mkdir so an existing directory is always refused.
[[ "$out" == /* ]] || out="$PWD/$out"
[[ -n "${HVO_ASTROMETRY_CATALOG_ROOT:-}" ]] || { echo "HVO_ASTROMETRY_CATALOG_ROOT is required" >&2; exit 2; }
for tool in jq sha256sum dotnet; do command -v "$tool" >/dev/null || { echo "$tool is required" >&2; exit 2; }; done
cd "$repo" || exit 2
# Untracked, non-ignored files can enter an SDK-style build, so they are refused too.
[[ -z "$(git status --porcelain --untracked-files=all)" ]] || { echo "commit or remove all changes and untracked files before measuring" >&2; exit 2; }
revision=$(git rev-parse HEAD)
manifest_sha=$(sha256sum "$manifest" | cut -d' ' -f1) || { echo "could not hash $manifest" >&2; exit 2; }
mkdir -p "$(dirname "$out")" && mkdir "$out" || { echo "output directory must not exist: $out" >&2; exit 2; }
mkdir "$out/runs" "$out/reports" || exit 2
out=$(cd "$out" && pwd -P) || exit 2

utc() { date -u +%Y-%m-%dT%H:%M:%SZ; }
echo "revision $revision"
dotnet build HVO.SkyMonitor.v9.slnx --configuration Release -warnaserror -v q >"$out/build.log" 2>&1 || { echo "Release build failed; see $out/build.log" >&2; exit 1; }

host=$(jq -n --arg kernel "$(uname -sr)" --arg cpu "$(lscpu 2>/dev/null | sed -n 's/^Model name:[[:space:]]*//p' | head -1)" \
    --arg cores "$(nproc)" --arg memory "$(sed -n 's/^MemTotal:[[:space:]]*//p' /proc/meminfo 2>/dev/null)" --arg sdk "$(dotnet --version)" \
    '{kernel: $kernel, cpu: $cpu, logicalProcessors: ($cores | tonumber), memTotal: $memory, sdk: $sdk}') || fatal "could not record host"

results='[]'; failed=0
count=$(jq '.runs | length' "$manifest")
for ((i = 0; i < count; i++)); do
    run=$(jq -c ".runs[$i]" "$manifest")
    id=$(jq -r .id <<<"$run"); project=$(jq -r .project <<<"$run"); filter=$(jq -r .filter <<<"$run")
    expected=$(jq -r .expectedPassed <<<"$run"); variable=$(jq -r '.perProcess.variable // empty' <<<"$run")
    class=${filter#*~}; class=${class%%.*}
    files=$(git ls-files tests | grep -E "/(${class}|VirtualAstrometryFixture|VirtualAstrometryReference|VirtualAstrometryPixels)\.cs$") ||
        fatal "no harness sources found for $id"
    sources='[]'
    while read -r file; do
        blob=$(git rev-parse "HEAD:$file") || fatal "could not resolve $file"
        sources=$(jq --arg path "$file" --arg blob "$blob" '. + [{path: $path, blob: $blob}]' <<<"$sources") ||
            fatal "could not record harness sources for $id"
    done <<<"$files"
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
        trx=$(find "$dir" -name run.trx -print -quit); trx_sha=
        [[ -z "$trx" ]] || trx_sha=$(sha256sum "$trx" | cut -d' ' -f1) || fatal "could not hash $trx"
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
                reports=$(jq --arg p "$pattern" --argjson n "${#matches[@]}" '. + [{report: $p, matches: $n}]' <<<"$reports") || fatal "could not record $pattern"; continue
            fi
            match=${matches[0]}
            target="$out/reports/$name/$(basename "$pattern")"
            mkdir -p "$(dirname "$target")" && cp "$match" "$target" || fatal "could not copy $match"
            digest=$(sha256sum "$target" | cut -d' ' -f1) && bytes=$(stat -c %s "$target") || fatal "could not hash $target"
            reports=$(jq --arg p "$pattern" --arg s "$digest" --arg b "$bytes" \
                '. + [{report: $p, sha256: $s, bytes: ($b | tonumber)}]' <<<"$reports") || fatal "could not record $target"
        done < <(jq -r '.reports[]' <<<"$run")
        echo "    $status rc=$rc passed=${passed:-?}/$expected ${elapsed}s"
        results=$(jq --arg id "$id" --arg name "$name" --arg value "$value" --arg status "$status" --arg started "$started" --arg finished "$finished" \
            --argjson elapsed "$elapsed" --argjson rc "$rc" --arg total "${total:-}" --arg executed "${executed:-}" --arg passed "${passed:-}" \
            --arg failures "${failures:-}" --argjson expected "$expected" --argjson reports "$reports" --argjson sources "$sources" \
            --arg trx "$trx_sha" \
            '. + [{id: $id, name: $name, workload: (if $value == "" then null else $value end), status: $status, startedUtc: $started,
                finishedUtc: $finished, elapsedSeconds: $elapsed, exitCode: $rc, expectedPassed: $expected,
                trx: {sha256: $trx, total: $total, executed: $executed, passed: $passed, failed: $failures},
                reports: $reports, harnessSources: $sources}]' <<<"$results") || fatal "could not record run $name"
    done
done

jq -n --arg revision "$revision" --arg manifest "$manifest_sha" --argjson host "$host" \
    --argjson runs "$results" --arg status "$([[ $failed -eq 0 ]] && echo passed || echo failed)" \
    '{schema: "virtual-astrometry-final-qualification-index-v1", issue: 1106, revision: $revision, manifestSha256: $manifest,
      status: $status, host: $host, runs: $runs}' >"$out/index.json" || fatal "could not write $out/index.json"
# The index must name the measured revision and manifest hash and every manifest process, record each run's
# harness source blobs, and, when passed, carry a well-formed TRX hash for every run and a hash for every report.
jq -e --slurpfile m "$manifest" --arg revision "$revision" --arg manifest "$manifest_sha" '
    def sha256: type == "string" and test("^[0-9a-f]{64}$");
    ([$m[0].runs[] | (.perProcess.values // [null]) | length] | add) as $processes
    | ([$m[0].runs[] | ((.perProcess.values // [null]) | length) * (.reports | length)] | add) as $reports
    | .revision == $revision and .manifestSha256 == $manifest and ($manifest | sha256) and (.runs | length) == $processes
      and all(.runs[]; (.harnessSources | length) > 0 and all(.harnessSources[]; .blob | test("^[0-9a-f]{40}$")))
      and (.status != "passed"
           or (all(.runs[]; .trx.sha256 | sha256)
               and ([.runs[].reports[] | select(.sha256 | sha256)] | length) == $reports))' \
    "$out/index.json" >/dev/null || fatal "index $out/index.json does not cover the manifest"
echo "index $out/index.json status=$(jq -r .status "$out/index.json")"
exit $failed
