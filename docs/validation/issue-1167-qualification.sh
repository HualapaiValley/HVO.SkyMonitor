#!/usr/bin/env bash
# Runs the predeclared #1167 deep-selection matrix at the current committed head and writes a hashed evidence pack.
# Adapted from issue-1126-qualification.sh: one process per projection family and variant, selected through
# HVO_PROJECTION_FAMILY and HVO_DEEP_VARIANT.
#
# Usage: HVO_ASTROMETRY_CATALOG_ROOT=<snapshot root> docs/validation/issue-1167-qualification.sh tuning|final|mag7 <new output directory> [family:variant,...]
#
# "tuning" runs the tuning partition and is never decision evidence; it may be limited to named family:variant pairs.
# "final" runs the held-out partitions for exactly the manifest's matrix, and "mag7" the magnitude-7 evaluation once
# per matrix family; neither accepts a list. Requires bash, git, jq, sha256sum and the pinned .NET SDK; Docker is not
# used. A run is measured when it executed exactly one test and left exactly one report, whether or not that test's
# gates passed: a gate failure is an outcome recorded in the report, never retried. A run that is not measured is an
# infrastructure error and makes the script exit 1 once all runs have finished. Failing to copy, hash or index
# evidence is fatal, and the final index is validated against the manifest before success is reported.
set -uo pipefail

fatal() { echo "$*" >&2; exit 1; }
repo=$(git rev-parse --show-toplevel) || exit 2
manifest="$repo/docs/validation/issue-1167-qualification-manifest.json"
usage="usage: issue-1167-qualification.sh tuning|final|mag7 <new output directory> [family:variant,...]"
mode=${1:?$usage}; out=${2:?$usage}
case "$mode" in tuning | final | mag7) ;; *) echo "$usage" >&2; exit 2 ;; esac
[[ "$out" == /* ]] || out="$PWD/$out"
[[ -n "${HVO_ASTROMETRY_CATALOG_ROOT:-}" ]] || { echo "HVO_ASTROMETRY_CATALOG_ROOT is required" >&2; exit 2; }
for tool in jq sha256sum dotnet; do command -v "$tool" >/dev/null || { echo "$tool is required" >&2; exit 2; }; done
cd "$repo" || exit 2
[[ -z "$(git status --porcelain --untracked-files=all)" ]] || { echo "commit or remove all changes and untracked files before measuring" >&2; exit 2; }
revision=$(git rev-parse HEAD)
manifest_sha=$(sha256sum "$manifest" | cut -d' ' -f1) || { echo "could not hash $manifest" >&2; exit 2; }
project=$(jq -er ".runs.$mode.project" "$manifest") && filter=$(jq -er ".runs.$mode.filter" "$manifest") &&
    report=$(jq -er ".runs.$mode.report" "$manifest") || { echo "manifest has no $mode run" >&2; exit 2; }
family_variable=$(jq -er .families.variable "$manifest") && variant_variable=$(jq -er .variants.variable "$manifest") || exit 2
# The magnitude-7 evaluation has no variant: one run per family, recorded with the variant "-".
if [[ "$mode" == mag7 ]]; then
    mapfile -t pairs < <(jq -r '.matrix[] | .family + ":-"' "$manifest")
else
    mapfile -t pairs < <(jq -r '.matrix[] | .family as $f | .variants[] | $f + ":" + .' "$manifest")
fi
if [[ -n "${3:-}" ]]; then
    [[ "$mode" == tuning ]] || { echo "only tuning runs may be limited" >&2; exit 2; }
    IFS=, read -ra selected <<<"$3"
    for pair in "${selected[@]}"; do
        printf '%s\n' "${pairs[@]}" | grep -qxF -- "$pair" || { echo "$pair is not in the manifest matrix" >&2; exit 2; }
    done
    pairs=("${selected[@]}")
fi
[[ ${#pairs[@]} -gt 0 ]] || { echo "no runs selected" >&2; exit 2; }
mkdir -p "$(dirname "$out")" && mkdir "$out" || { echo "output directory must not exist: $out" >&2; exit 2; }
mkdir "$out/runs" "$out/reports" || exit 2
out=$(cd "$out" && pwd -P) || exit 2

utc() { date -u +%Y-%m-%dT%H:%M:%SZ; }
echo "revision $revision"
# No persistent build nodes or compiler servers: the heavy host is shared and its lock covers this process tree only.
export MSBUILDDISABLENODEREUSE=1
dotnet build HVO.SkyMonitor.v9.slnx --configuration Release -warnaserror -nodeReuse:false -p:UseSharedCompilation=false \
    -p:UseRazorBuildServer=false -v q >"$out/build.log" 2>&1 || { echo "Release build failed; see $out/build.log" >&2; exit 1; }

host=$(jq -n --arg kernel "$(uname -sr)" --arg cpu "$(lscpu 2>/dev/null | sed -n 's/^Model name:[[:space:]]*//p' | head -1)" \
    --arg cores "$(nproc)" --arg memory "$(sed -n 's/^MemTotal:[[:space:]]*//p' /proc/meminfo 2>/dev/null)" --arg sdk "$(dotnet --version)" \
    --arg sdks "$(dotnet --list-sdks | cut -d' ' -f1 | paste -sd, -)" \
    '{kernel: $kernel, cpu: $cpu, logicalProcessors: ($cores | tonumber), memTotal: $memory, sdk: $sdk, installedSdks: ($sdks | split(","))}') ||
    fatal "could not record host"

sources='[]'
files=$(git ls-files tests | grep -E '/(VirtualDeepAstrometryQualificationTests|VirtualAstrometryQualificationTests|VirtualAstrometryFixture|VirtualAstrometryReference)\.cs$') ||
    fatal "no harness sources found"
while read -r file; do
    blob=$(git rev-parse "HEAD:$file") || fatal "could not resolve $file"
    sources=$(jq --arg path "$file" --arg blob "$blob" '. + [{path: $path, blob: $blob}]' <<<"$sources") || fatal "could not record harness sources"
done <<<"$files"

results='[]'; errors=0
for pair in "${pairs[@]}"; do
    family=${pair%%:*}; variant=${pair#*:}
    name=$mode-$family; [[ "$variant" == - ]] || name=$name-$variant
    dir="$out/runs/$name"; mkdir -p "$dir"
    extra=("$family_variable=$family"); [[ "$variant" == - ]] || extra+=("$variant_variable=$variant")
    started=$(utc); seconds=$SECONDS
    echo "=== $name $started"
    env DOTNET_TieredCompilation=0 HVO_EVIDENCE_REVISION="$revision" "${extra[@]}" \
        dotnet test "$project" --no-build --no-restore --configuration Release --filter "$filter" \
        --results-directory "$dir" --logger "trx;LogFileName=run.trx" >"$dir/test.log" 2>&1
    rc=$?; finished=$(utc); elapsed=$((SECONDS - seconds))
    trx=$(find "$dir" -name run.trx -print -quit); trx_sha=
    [[ -z "$trx" ]] || trx_sha=$(sha256sum "$trx" | cut -d' ' -f1) || fatal "could not hash $trx"
    counters=$([[ -n "$trx" ]] && grep -o '<Counters [^>]*>' "$trx" | head -1)
    attr() { sed -n "s/.* $1=\"\\([0-9]*\\)\".*/\\1/p" <<<"$counters"; }
    total=$(attr total); executed=$(attr executed); passed=$(attr passed); failures=$(attr failed)
    # MSTest may leave the written report beside its attached copy; identical copies are one report.
    mapfile -t matches < <(find "$dir" -type f -name "$report")
    digests=$( ((${#matches[@]})) && sha256sum "${matches[@]}" | cut -d' ' -f1 | sort -u)
    status=error; reports='[]'
    if [[ "$total" == 1 && "$executed" == 1 && ${#matches[@]} -ge 1 && $(wc -l <<<"$digests") -eq 1 ]]; then
        status=$([[ "$passed" == 1 && "$failures" == 0 ]] && echo passed || echo failed)
        target="$out/reports/$name/$report"
        mkdir -p "$(dirname "$target")" && cp "${matches[0]}" "$target" || fatal "could not copy ${matches[0]}"
        digest=$(sha256sum "$target" | cut -d' ' -f1) && bytes=$(stat -c %s "$target") || fatal "could not hash $target"
        reports=$(jq -n --arg p "$report" --arg s "$digest" --arg b "$bytes" '[{report: $p, sha256: $s, bytes: ($b | tonumber)}]') ||
            fatal "could not record $target"
    else
        errors=1
    fi
    echo "    $status rc=$rc executed=${executed:-?} passed=${passed:-?} reports=${#matches[@]} ${elapsed}s"
    results=$(jq --arg name "$name" --arg family "$family" --arg variant "$variant" --arg status "$status" --arg started "$started" \
        --arg finished "$finished" --argjson elapsed "$elapsed" --argjson rc "$rc" --arg total "${total:-}" --arg executed "${executed:-}" \
        --arg passed "${passed:-}" --arg failures "${failures:-}" --argjson reports "$reports" --arg trx "$trx_sha" \
        '. + [{name: $name, family: $family, variant: (if $variant == "-" then null else $variant end), status: $status,
            startedUtc: $started, finishedUtc: $finished, elapsedSeconds: $elapsed, exitCode: $rc,
            trx: {sha256: $trx, total: $total, executed: $executed, passed: $passed, failed: $failures}, reports: $reports}]' <<<"$results") ||
        fatal "could not record run $name"
done

jq -n --arg revision "$revision" --arg manifest "$manifest_sha" --argjson host "$host" --arg mode "$mode" --argjson sources "$sources" \
    --argjson pairs "$(printf '%s\n' "${pairs[@]}" | jq -R . | jq -s .)" --argjson runs "$results" \
    --arg status "$([[ $errors -eq 0 ]] && echo measured || echo error)" \
    '{schema: "virtual-deep-astrometry-qualification-index-v1", issue: 1167, mode: $mode, final: ($mode != "tuning"),
      revision: $revision, manifestSha256: $manifest, pairs: $pairs, status: $status, host: $host, harnessSources: $sources,
      runs: $runs}' >"$out/index.json" || fatal "could not write $out/index.json"
# The index must name the measured revision and manifest hash, cover exactly the manifest matrix for final and mag7, record
# the harness source blobs, and, when measured, carry a well-formed TRX hash and exactly one report hash for every run.
jq -e --slurpfile m "$manifest" --arg revision "$revision" --arg manifest "$manifest_sha" '
    def sha256: type == "string" and test("^[0-9a-f]{64}$");
    (if .mode == "mag7" then [$m[0].matrix[] | .family + ":-"]
     else [$m[0].matrix[] | .family as $f | .variants[] | $f + ":" + .] end) as $matrix
    | .revision == $revision and .manifestSha256 == $manifest and ($manifest | sha256)
      and ((.final | not) or .pairs == $matrix)
      and (.runs | length) == (.pairs | length)
      and (.harnessSources | length) == 4 and all(.harnessSources[]; .blob | test("^[0-9a-f]{40}$"))
      and (.status != "measured"
           or all(.runs[]; (.trx.sha256 | sha256) and (.reports | length) == 1 and (.reports[0].sha256 | sha256)))' \
    "$out/index.json" >/dev/null || fatal "index $out/index.json does not cover the manifest"
echo "index $out/index.json status=$(jq -r .status "$out/index.json")"
exit $errors
