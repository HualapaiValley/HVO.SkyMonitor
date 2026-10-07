#!/usr/bin/env bash
# Runs the predeclared #1168 long-exposure qualification manifest at the current
# committed head and writes a hashed evidence pack. Adapted from
# issue-1126-qualification.sh: every manifest entry runs once per declared cell,
# and each cell is an explicit assignment of the four #1168 selectors.
#
# Usage: HVO_ASTROMETRY_CATALOG_ROOT=<snapshot root> [HVO_HEAVY_LOCK=<lock file>] \
#     docs/validation/issue-1168-qualification.sh tuning|final|continuity <new output directory>
#
# "tuning" runs the manifest's tuning section and is never final evidence.
# "final" runs the held-out, resource and renderer-capacity runs. "continuity"
# runs every #1126 manifest entry with all selectors unset at the declared base
# revision and at this head, base then head in one session, and classifies every
# report leaf that differs by the manifest's continuity rules. With
# HVO_HEAVY_LOCK set, the whole run, builds included, holds that lock.
#
# Requires bash, git, jq, sha256sum, GNU join and sort, the pinned .NET SDK, and
# flock when HVO_HEAVY_LOCK is set. Docker is not used. A process is passed when
# it executed exactly its expected tests and they passed, failed when it executed
# them and attached every report but a test failed, and incomplete otherwise. The
# pack is incomplete if any process is, failed if any process failed, and passed
# otherwise. In continuity mode the pair verdicts decide instead: a pair is
# invalid when its base does not reproduce exactly the pinned #1126 outcome,
# which leaves the pack incomplete; failed on an unexplained head outcome, a
# deterministic failure difference or an unclassified leaf; and passed
# otherwise. Resource failures, matched by the manifest's patterns, are timing,
# never a pair failure. The script exits 0 only when the pack passed. Failing to copy, hash or index evidence is fatal, and the index is
# validated against the manifest before SHA256SUMS seals the pack.
set -uo pipefail

if [[ -n "${HVO_HEAVY_LOCK:-}" && -z "${HVO_HEAVY_LOCK_HELD:-}" ]]; then
    command -v flock >/dev/null || { echo "flock is required with HVO_HEAVY_LOCK" >&2; exit 2; }
    self=$(readlink -f "$0") || exit 2
    exec env MSBUILDDISABLENODEREUSE=1 HVO_HEAVY_LOCK_HELD="$HVO_HEAVY_LOCK" flock -o "$HVO_HEAVY_LOCK" bash "$self" "$@"
fi
# No build server or reused MSBuild node outlives the run or shares state with another session.
export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false UseRazorBuildServer=false DOTNET_CLI_TELEMETRY_OPTOUT=1
build_flags=(--configuration Release -warnaserror -v q -nodeReuse:false -p:UseSharedCompilation=false -p:UseRazorBuildServer=false)

fatal() { echo "$*" >&2; exit 1; }
utc() { date -u +%Y-%m-%dT%H:%M:%SZ; }
repo=$(git rev-parse --show-toplevel) || exit 2
manifest="$repo/docs/validation/issue-1168-qualification-manifest.json"
usage="usage: issue-1168-qualification.sh tuning|final|continuity <new output directory>"
mode=${1:?$usage}; out=${2:?$usage}
case "$mode" in tuning) section=tuning ;; final) section=runs ;; continuity) section= ;; *) echo "$usage" >&2; exit 2 ;; esac
[[ $# -eq 2 ]] || { echo "$usage" >&2; exit 2; }
# Resolve against the caller's directory before anything changes it, and create
# the pack root with a plain mkdir so an existing directory is always refused.
[[ "$out" == /* ]] || out="$PWD/$out"
[[ -n "${HVO_ASTROMETRY_CATALOG_ROOT:-}" ]] || { echo "HVO_ASTROMETRY_CATALOG_ROOT is required" >&2; exit 2; }
for tool in jq sha256sum join sort dotnet; do command -v "$tool" >/dev/null || { echo "$tool is required" >&2; exit 2; }; done
cd "$repo" || exit 2
# Untracked, non-ignored files can enter an SDK-style build, so they are refused too.
[[ -z "$(git status --porcelain --untracked-files=all)" ]] || { echo "commit or remove all changes and untracked files before measuring" >&2; exit 2; }
revision=$(git rev-parse HEAD)
manifest_sha=$(sha256sum "$manifest" | cut -d' ' -f1) || { echo "could not hash $manifest" >&2; exit 2; }
base=$(jq -re .base.revision "$manifest") || exit 2
git cat-file -e "$base^{commit}" 2>/dev/null || { echo "base revision $base is not available; fetch it first" >&2; exit 2; }
selectors=(HVO_PROJECTION_FAMILY HVO_EXPOSURE_SECONDS HVO_ASTROMETRY_MEASURER HVO_LONG_EXPOSURE_VARIANT)
unset_selectors=(); for key in "${selectors[@]}"; do unset_selectors+=(-u "$key"); done

# Every declared base input must be the named blob at the base; the index records whether this head still carries it.
inputs='[]'
while read -r input; do
    path=$(jq -r .path <<<"$input"); blob=$(jq -r .blob <<<"$input"); sha=$(jq -r .sha256 <<<"$input")
    [[ "$(git rev-parse "$base:$path" 2>/dev/null)" == "$blob" ]] || { echo "base input $path is not blob $blob at $base" >&2; exit 2; }
    [[ "$(git cat-file blob "$blob" | sha256sum | cut -d' ' -f1)" == "$sha" ]] || { echo "base input $path does not hash to $sha" >&2; exit 2; }
    head_blob=$(git rev-parse "HEAD:$path" 2>/dev/null) || head_blob=
    inputs=$(jq --arg path "$path" --arg blob "$blob" --arg head "$head_blob" '. + [{path: $path, blob: $blob, headMatchesBase: ($head == $blob)}]' <<<"$inputs") || exit 2
done < <(jq -c '.base.inputs[]' "$manifest")

# Every cell names only the four selectors, each with a declared value; anything else is refused before measuring.
if [[ -n "$section" ]]; then
    jq -e --arg s "$section" '([.selectors[] | objects | {key: .variable, value: .values}] | from_entries) as $allowed
        | (.[$s] | length) > 0
          and all(.[$s][]; (.cells | length) > 0 and all(.cells[]; to_entries | all(.[];
              .value as $v | ($v | type) == "string" and any(($allowed[.key] // [])[]; . == $v))))' \
        "$manifest" >/dev/null || { echo "manifest $section cells must assign only declared selector values" >&2; exit 2; }
fi

mkdir -p "$(dirname "$out")" && mkdir "$out" || { echo "output directory must not exist: $out" >&2; exit 2; }
mkdir "$out/runs" "$out/reports" || exit 2
out=$(cd "$out" && pwd -P) || exit 2
tab=$'\t'; missing='@@missing@@'

loadavg() { cut -d' ' -f1-3 /proc/loadavg 2>/dev/null || echo unavailable; }
governor=$(cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_governor 2>/dev/null) || governor=unavailable
host=$(jq -n --arg name "$(hostname)" --arg kernel "$(uname -sr)" --arg cpu "$(lscpu 2>/dev/null | sed -n 's/^Model name:[[:space:]]*//p' | head -1)" \
    --arg cores "$(nproc)" --arg memory "$(sed -n 's/^MemTotal:[[:space:]]*//p' /proc/meminfo 2>/dev/null)" --arg sdk "$(dotnet --version)" \
    --arg governor "$governor" --arg load "$(loadavg)" --arg lock "${HVO_HEAVY_LOCK_HELD:-}" \
    '{name: $name, kernel: $kernel, cpu: $cpu, logicalProcessors: ($cores | tonumber), memTotal: $memory, sdk: $sdk,
      governor: $governor, loadAverageStart: $load, heavyLock: (if $lock == "" then null else $lock end)}') || fatal "could not record host"
started_utc=$(utc)
echo "revision $revision mode $mode $started_utc"

# Harness source blobs for one manifest entry at one revision: its test class plus the shared fixture, reference and pixels.
sources_for() {
    local rev=$1 filter=$2 class files file blob sources='[]'
    class=${filter#*~}; class=${class%%.*}
    files=$(git ls-tree -r --name-only "$rev" tests | grep -E "/(${class}|VirtualAstrometryFixture|VirtualAstrometryReference|VirtualAstrometryPixels)\.cs$") ||
        fatal "no harness sources found for $class at $rev"
    while read -r file; do
        blob=$(git rev-parse "$rev:$file") || fatal "could not resolve $file at $rev"
        sources=$(jq --arg path "$file" --arg blob "$blob" '. + [{path: $path, blob: $blob}]' <<<"$sources") || fatal "could not record $file"
    done <<<"$files"
    printf '%s' "$sources"
}

# run_process <name> <tree> <revision> <project> <filter> <expected> <reports json> <report dir> [SELECTOR=value ...]
# Runs one test process with every selector unset except those assigned, copies and hashes its reports, and appends
# its record to $results. Sets $last_status.
results='[]'; last_status=
run_process() {
    local name=$1 tree=$2 rev=$3 project=$4 filter=$5 expected=$6 wanted=$7 report_dir=$8; shift 8
    local dir="$out/runs/$name" started finished seconds elapsed rc trx trx_sha counters total executed passed failures status complete=1
    local reports='[]' pattern match target digest bytes cell
    mkdir -p "$dir" "$report_dir" || fatal "could not create $dir"
    cell=$(printf '%s\n' "$@" | jq -Rn '[inputs | select(length > 0) | capture("^(?<key>[^=]+)=(?<value>.*)$")] | from_entries') || fatal "could not record cell $name"
    started=$(utc); seconds=$SECONDS
    echo "=== $name $started"
    (cd "$tree" && env "${unset_selectors[@]}" DOTNET_TieredCompilation=0 HVO_EVIDENCE_REVISION="$rev" "$@" \
        dotnet test "$project" --no-build --no-restore --configuration Release --filter "$filter" \
        --results-directory "$dir" --logger "trx;LogFileName=run.trx") >"$dir/test.log" 2>&1
    rc=$?; finished=$(utc); elapsed=$((SECONDS - seconds))
    trx=$(find "$dir" -name run.trx -print -quit); trx_sha=; counters=
    if [[ -n "$trx" ]]; then
        trx_sha=$(sha256sum "$trx" | cut -d' ' -f1) || fatal "could not hash $trx"
        counters=$(grep -o '<Counters [^>]*>' "$trx" | head -1)
    fi
    attr() { sed -n "s/.* $1=\"\\([0-9]*\\)\".*/\\1/p" <<<"$counters"; }
    total=$(attr total); executed=$(attr executed); passed=$(attr passed); failures=$(attr failed)
    [[ -n "$trx" && "$total" == "$expected" && "$executed" == "$expected" ]] || complete=0
    while read -r pattern; do
        # MSTest keeps attached result files flat under In/<id>/<host>/, without the partition directory.
        mapfile -t matches < <(find "$dir" -type f -name "$(basename "$pattern")")
        if [[ ${#matches[@]} -ne 1 ]]; then
            complete=0
            reports=$(jq --arg p "$pattern" --argjson n "${#matches[@]}" '. + [{report: $p, matches: $n}]' <<<"$reports") || fatal "could not record $pattern"; continue
        fi
        match=${matches[0]}
        target="$report_dir/$(basename "$pattern")"
        cp "$match" "$target" || fatal "could not copy $match"
        digest=$(sha256sum "$target" | cut -d' ' -f1) && bytes=$(stat -c %s "$target") || fatal "could not hash $target"
        reports=$(jq --arg p "$pattern" --arg s "$digest" --arg b "$bytes" --arg f "${target#"$out"/}" \
            '. + [{report: $p, file: $f, sha256: $s, bytes: ($b | tonumber)}]' <<<"$reports") || fatal "could not record $target"
    done < <(jq -r '.[]' <<<"$wanted")
    if [[ $complete -eq 0 ]]; then status=incomplete
    elif [[ $rc -eq 0 && "$passed" == "$expected" && "$failures" == "0" ]]; then status=passed
    else status=failed; fi
    last_status=$status
    echo "    $status rc=$rc passed=${passed:-?}/$expected ${elapsed}s"
    results=$(jq --arg name "$name" --arg rev "$rev" --argjson cell "$cell" --arg status "$status" --arg started "$started" --arg finished "$finished" \
        --argjson elapsed "$elapsed" --argjson rc "$rc" --arg total "${total:-}" --arg executed "${executed:-}" --arg passed "${passed:-}" \
        --arg failures "${failures:-}" --argjson expected "$expected" --argjson reports "$reports" --arg trx "$trx_sha" \
        '. + [{name: $name, revision: $rev, cell: $cell, status: $status, startedUtc: $started, finishedUtc: $finished,
            elapsedSeconds: $elapsed, exitCode: $rc, expectedPassed: $expected,
            trx: {sha256: $trx, total: $total, executed: $executed, passed: $passed, failed: $failures}, reports: $reports}]' <<<"$results") ||
        fatal "could not record run $name"
}

# Every report leaf, including empty containers, as: exact path, path with array indices collapsed to N, JSON value. Each
# failures list is split first: entries matching a continuity resource-failure pattern move to a sibling resourceFailures
# list, so a timing or resource outcome never shifts the indices of the deterministic failures.
leaves() {
    jq -r --argjson patterns "$resource_patterns" 'def resource: type == "string" and (. as $f | any($patterns[]; . as $p | $f | test($p)));
        walk(if type == "object" and (.failures | type) == "array"
            then .resourceFailures = [.failures[] | select(resource)] | .failures |= map(select(resource | not)) else . end)
        | tostream | select(length == 2)
        | [(.[0] | tojson), (.[0] | map(if type == "number" then "N" else tostring end) | join(".")), (.[1] | tojson)] | @tsv' "$1" |
        LC_ALL=C sort -t "$tab" -k1,1
}

# classify <report name> <base report> <head report> <output prefix>: writes <prefix>.differences.tsv (class, collapsed
# path, exact path, base value, head value for every differing leaf) and prints the report's classification as JSON.
classify() {
    local report=$1 before=$2 after=$3 prefix=$4 rules identical summary
    rules=$(jq -re --arg r "$report" '.continuity as $c | ($c.reports[$r] // error("no continuity rules for \($r)")) as $rep
        | ($c.identityLeaves[] | "identity\t\(.)"), ($c.runVaryingKeys[] | "runvarying\t\(.)"),
          ($rep.changed | to_entries[] | "changed\t\(.key)\t\(.value[0] | tojson)\t\(.value[1] | tojson)"),
          ($rep.added[] | "added\t\(.)"), ($rep.scorer[] | "scorer\t\(.)")' "$manifest") || fatal "could not read continuity rules for $report"
    leaves "$before" >"$prefix.base.tsv" || fatal "could not read leaves of $before"
    leaves "$after" >"$prefix.head.tsv" || fatal "could not read leaves of $after"
    LC_ALL=C join -t "$tab" -a 1 -a 2 -e "$missing" -o 0,1.2,2.2,1.3,2.3 "$prefix.base.tsv" "$prefix.head.tsv" |
        awk -F "$tab" -v OFS="$tab" -v missing="$missing" -v identical_file="$prefix.identical" '
            FNR == NR { kind = $1; rule[kind, ++n[kind]] = $2; if (kind == "changed") { from[$2] = $3; to[$2] = $4 }; next }
            function listed(p, kind,   i) { for (i = 1; i <= n[kind]; i++) if (p == rule[kind, i]) return 1; return 0 }
            function under(p, kind,   i, q) { for (i = 1; i <= n[kind]; i++) { q = rule[kind, i]; if (p == q || index(p, q ".") == 1) return 1 } return 0 }
            function resource(p) { return p ~ /(^|\.)resourceFailures(\.|$)/ }
            {
                path = $2 != missing ? $2 : $3; b = $4; h = $5
                if (b == h) { identical++; next }
                key = path; sub(/(\.N)+$/, "", key); sub(/.*\./, "", key)
                if (b != missing && h != missing)
                    class = listed(path, "identity") ? "identity" : listed(key, "runvarying") ? "run-varying" \
                        : resource(path) ? "resource-outcome" \
                        : (path in from) && b == from[path] && h == to[path] ? "declared-changed" \
                        : under(path, "scorer") ? "scorer-by-design" : "unclassified"
                else if (b == missing)
                    class = under(path, "added") ? "declared-added" : resource(path) ? "resource-outcome" \
                        : under(path, "scorer") ? "scorer-by-design" : "unclassified"
                else
                    class = resource(path) ? "resource-outcome" : under(path, "scorer") ? "scorer-by-design" : "unclassified"
                print class, path, $1, b, h
            }
            END { print identical + 0 > identical_file }' <(printf '%s\n' "$rules") - >"$prefix.differences.tsv" ||
        fatal "could not classify $report"
    identical=$(cat "$prefix.identical") || fatal "could not count identical leaves of $report"
    rm -f "$prefix.base.tsv" "$prefix.head.tsv" "$prefix.identical"
    summary=$(jq -Rn --arg report "$report" --argjson identical "$identical" '
        [inputs | split("\t") | {class: .[0], path: .[1], exact: .[2], base: .[3], head: .[4]}] as $rows
        | {report: $report, identicalLeaves: $identical,
           counts: ($rows | group_by(.class) | map({key: .[0].class, value: length}) | from_entries),
           paths: ($rows | group_by(.class) | map({key: .[0].class,
               value: (group_by(.path) | map({key: .[0].path, value: length}) | from_entries)}) | from_entries),
           unclassified: [$rows[] | select(.class == "unclassified")][:50],
           verdict: (if any($rows[]; .class == "unclassified") then "failed" else "passed" end)}' <"$prefix.differences.tsv") ||
        fatal "could not summarize $report"
    printf '%s' "$summary"
}

# split_failures <report>: its top-level failures in order, and split into {deterministic, resource} by the continuity
# resource-failure patterns.
split_failures() {
    jq -c --argjson patterns "$resource_patterns" 'def resource: type == "string" and (. as $f | any($patterns[]; . as $p | $f | test($p)));
        (.failures // []) as $f | {all: $f, deterministic: [$f[] | select(resource | not)], resource: [$f[] | select(resource)]}' "$1"
}

# trx_message <run directory>: the decoded text of the run's TRX failure message; fails unless there is exactly one.
trx_message() {
    local trx
    trx=$(find "$1" -name run.trx -print -quit); [[ -n "$trx" && "$(grep -c '<Message>' "$trx")" == 1 ]] || return 1
    awk '/<Message>/ { on = 1; sub(/^.*<Message>/, "") } on { if (sub(/<\/Message>.*$/, "")) { print; exit } print }' "$trx" |
        sed -e 's/&lt;/</g; s/&gt;/>/g; s/&quot;/"/g; s/&apos;/'"'"'/g; s/&amp;/\&/g'
}

# explained <run directory> <status> <recorded failures json> [pinned message]: true when the process outcome is the one
# its report records. Without a pinned message, no recorded failure must pass, and a failure must be exactly the
# harness's final Assert.IsEmpty(failures) over the recorded failures in order. With one, the process must fail with
# exactly that message.
explained() {
    local dir=$1 status=$2 recorded=$3 pinned=${4-} message expected
    if [[ -z "$pinned" && "$(jq '.all | length' <<<"$recorded")" == 0 ]]; then [[ $status == passed ]]; return; fi
    [[ $status == failed ]] && message=$(trx_message "$dir") || return 1
    [[ -n "$pinned" ]] && { [[ "$message" == "$pinned" ]]; return; }
    expected=$(jq -r '.all | "Assertion failed. Expected collection to be empty.\n\(join("\n"))\n\nexpected count: 0\nactual count:   \(length)\n\nAssert.IsEmpty(failures)"' \
        <<<"$recorded") || return 1
    [[ "$message" == "$expected" ]]
}

elapsed_of() { jq --arg n "$1" '[.[] | select(.name == $n) | .elapsedSeconds][0]' <<<"$results"; }

pack_status=passed
note_status() {
    case "$1" in incomplete) pack_status=incomplete ;; failed) [[ $pack_status == incomplete ]] || pack_status=failed ;; esac
}

continuity='null'; sections='[]'; resource_patterns='[]'
if [[ "$mode" == continuity ]]; then
    resource_patterns=$(jq -ce '.continuity.resourceFailures.patterns | select(type == "array" and length > 0 and all(.[]; type == "string"))' "$manifest") ||
        fatal "continuity.resourceFailures.patterns must be a non-empty string array"
    jq -n --argjson patterns "$resource_patterns" '[$patterns[] as $p | "" | test($p)] | length' >/dev/null || fatal "a resource-failure pattern does not compile"
    jq -e '.continuity.baseFailures.sets | type == "object" and all(.[]; type == "array" and all(.[]; type == "string"))' "$manifest" >/dev/null ||
        fatal "continuity.baseFailures.sets must map pair ids to string arrays"
    prior_path=$(jq -re .continuity.manifest "$manifest") || exit 2
    [[ "$(jq -r .continuity.baseRevision "$manifest")" == "$base" ]] || fatal "continuity base must be the manifest base"
    [[ "$(git rev-parse "$base:$prior_path")" == "$(git rev-parse "HEAD:$prior_path")" ]] || fatal "$prior_path differs between base and head"
    prior="$repo/$prior_path"
    # The base is built in its own detached worktree outside the pack and removed afterwards.
    scratch=$(mktemp -d "${TMPDIR:-/tmp}/issue-1168-base.XXXXXX") || fatal "could not create a base worktree directory"
    basetree="$scratch/tree"
    trap 'git -C "$repo" worktree remove --force "$basetree" >/dev/null 2>&1; rm -rf -- "$scratch"' EXIT
    git worktree add --detach "$basetree" "$base" >"$out/base-worktree.log" 2>&1 || fatal "could not check out $base; see $out/base-worktree.log"
    echo "build base $base $(utc)"
    (cd "$basetree" && dotnet build HVO.SkyMonitor.v9.slnx "${build_flags[@]}") >"$out/build-base.log" 2>&1 || fatal "base Release build failed; see $out/build-base.log"
    echo "build head $revision $(utc)"
    dotnet build HVO.SkyMonitor.v9.slnx "${build_flags[@]}" >"$out/build.log" 2>&1 || fatal "head Release build failed; see $out/build.log"
    mkdir "$out/continuity" || exit 2
    pairs='[]'
    for prior_section in tuning runs; do
        count=$(jq ".$prior_section | length" "$prior") || exit 1
        for ((i = 0; i < count; i++)); do
            entry=$(jq -c ".${prior_section}[$i]" "$prior")
            id=$(jq -r .id <<<"$entry"); project=$(jq -r .project <<<"$entry"); filter=$(jq -r .filter <<<"$entry")
            expected=$(jq -r .expectedPassed <<<"$entry"); wanted=$(jq -c .reports <<<"$entry")
            run_process "$id-base" "$basetree" "$base" "$project" "$filter" "$expected" "$wanted" "$out/reports/$id/base"; base_status=$last_status
            run_process "$id-head" "$repo" "$revision" "$project" "$filter" "$expected" "$wanted" "$out/reports/$id/head"; head_status=$last_status
            # A process failure counts only through its pair: the pinned base failures, identical head deterministic
            # failures, outcomes explained by the reports, and classified leaves. Resource outcomes are timing, not output.
            classified='[]'; verdict=passed; reasons='[]'
            recorded_base='{"all":[],"deterministic":[],"resource":[]}'; recorded_head=$recorded_base
            pinned=$(jq -c --arg id "$id" '.continuity.baseFailures.sets[$id] // [] | sort' "$manifest") || exit 1
            pinned_message=$(jq -r --arg id "$id" '.continuity.baseFailures.processMessages[$id] // empty' "$manifest") || exit 1
            [[ $base_status == incomplete || $head_status == incomplete ]] && verdict=incomplete
            while read -r report; do
                before="$out/reports/$id/base/$(basename "$report")"; after="$out/reports/$id/head/$(basename "$report")"
                if [[ ! -f "$before" || ! -f "$after" ]]; then
                    verdict=incomplete
                    classified=$(jq --arg r "$report" '. + [{report: $r, verdict: "incomplete"}]' <<<"$classified") || exit 1; continue
                fi
                mkdir -p "$out/continuity/$id" || exit 1
                summary=$(classify "$(basename "$report")" "$before" "$after" "$out/continuity/$id/$(basename "$report" .json)") || exit 1
                [[ "$(jq -r .verdict <<<"$summary")" == passed ]] || reasons=$(jq -c '. + ["unclassified-leaves"]' <<<"$reasons")
                classified=$(jq --argjson s "$summary" '. + [$s]' <<<"$classified") || exit 1
                recorded_base=$(jq -c --argjson s "$(split_failures "$before")" 'with_entries(.value += $s[.key])' <<<"$recorded_base") &&
                    recorded_head=$(jq -c --argjson s "$(split_failures "$after")" 'with_entries(.value += $s[.key])' <<<"$recorded_head") ||
                    fatal "could not read the failures of $id"
            done < <(jq -r '.[]' <<<"$wanted")
            if [[ $verdict != incomplete ]]; then
                [[ "$(jq -c '.deterministic | sort' <<<"$recorded_base")" == "$pinned" ]] || reasons=$(jq -c '. + ["base-failures-not-pinned"]' <<<"$reasons")
                [[ "$(jq -c '.deterministic | sort' <<<"$recorded_head")" == "$(jq -c '.deterministic | sort' <<<"$recorded_base")" ]] ||
                    reasons=$(jq -c '. + ["deterministic-failures-differ"]' <<<"$reasons")
                explained "$out/runs/$id-base" "$base_status" "$recorded_base" "$pinned_message" ||
                    reasons=$(jq -c '. + ["base-outcome-unexplained"]' <<<"$reasons")
                explained "$out/runs/$id-head" "$head_status" "$recorded_head" || reasons=$(jq -c '. + ["head-outcome-unexplained"]' <<<"$reasons")
                # A base that does not reproduce exactly the recorded #1126 outcome invalidates the run as environment or setup.
                if jq -e 'index("base-failures-not-pinned") or index("base-outcome-unexplained")' <<<"$reasons" >/dev/null; then verdict=invalid
                elif [[ $reasons != '[]' ]]; then verdict=failed; fi
            fi
            # An invalid pair is not evidence; the pack records it as incomplete.
            if [[ $verdict == invalid ]]; then note_status incomplete; else note_status "$verdict"; fi
            pair=$(jq -n --arg id "$id" --arg section "$prior_section" --arg base "$base_status" --arg head "$head_status" \
                --argjson baseSeconds "$(elapsed_of "$id-base")" --argjson headSeconds "$(elapsed_of "$id-head")" \
                --argjson pinned "$pinned" --argjson baseFailures "$recorded_base" --argjson headFailures "$recorded_head" \
                --argjson reasons "$reasons" --argjson reports "$classified" --arg verdict "$verdict" \
                '{id: $id, section: $section, baseStatus: $base, headStatus: $head, baseElapsedSeconds: $baseSeconds,
                  headElapsedSeconds: $headSeconds, pinnedBaseFailures: $pinned, baseFailures: $baseFailures, headFailures: $headFailures,
                  reasons: $reasons, reports: $reports, verdict: $verdict}') || exit 1
            mkdir -p "$out/continuity/$id" && jq . <<<"$pair" >"$out/continuity/$id/continuity.json" || fatal "could not write continuity for $id"
            echo "    continuity $id: $verdict $(jq -c '[.reports[] | {(.report): (.counts // .verdict)}] | add' <<<"$pair")"
            pairs=$(jq --argjson p "$pair" '. + [$p]' <<<"$pairs") || exit 1
        done
    done
    continuity=$(jq -n --arg base "$base" --arg prior "$prior_path" --arg sha "$(sha256sum "$prior" | cut -d' ' -f1)" --argjson pairs "$pairs" \
        '{baseRevision: $base, priorManifest: $prior, priorManifestSha256: $sha, pairs: $pairs,
          verdict: (if any($pairs[]; .verdict == "invalid") then "invalid" elif any($pairs[]; .verdict == "incomplete") then "incomplete"
                    elif all($pairs[]; .verdict == "passed") then "passed" else "failed" end)}') ||
        fatal "could not record continuity"
    entries=$(jq -c '[(.tuning[], .runs[]) | {id, filter}]' "$prior") || exit 1
    harness='[]'
    while read -r entry; do
        id=$(jq -r .id <<<"$entry"); filter=$(jq -r .filter <<<"$entry")
        harness=$(jq --arg id "$id" --argjson b "$(sources_for "$base" "$filter")" --argjson h "$(sources_for HEAD "$filter")" \
            '. + [{id: $id, base: $b, head: $h}]' <<<"$harness") || exit 1
    done < <(jq -c '.[]' <<<"$entries")
    sections=$(jq -n --argjson h "$harness" '{continuity: $h}')
else
    dotnet build HVO.SkyMonitor.v9.slnx "${build_flags[@]}" >"$out/build.log" 2>&1 || fatal "Release build failed; see $out/build.log"
    harness='[]'
    count=$(jq ".$section | length" "$manifest")
    for ((i = 0; i < count; i++)); do
        entry=$(jq -c ".${section}[$i]" "$manifest")
        id=$(jq -r .id <<<"$entry"); project=$(jq -r .project <<<"$entry"); filter=$(jq -r .filter <<<"$entry")
        expected=$(jq -r .expectedPassed <<<"$entry"); wanted=$(jq -c .reports <<<"$entry")
        harness=$(jq --arg id "$id" --argjson s "$(sources_for HEAD "$filter")" '. + [{id: $id, sources: $s}]' <<<"$harness") || exit 1
        cells=$(jq '.cells | length' <<<"$entry")
        for ((c = 0; c < cells; c++)); do
            cell=$(jq -c ".cells[$c]" <<<"$entry")
            name=$id-$(printf %02d $((c + 1)))
            for key in "${selectors[@]}"; do
                value=$(jq -r --arg k "$key" '.[$k] // empty' <<<"$cell")
                [[ -n "$value" ]] || continue
                [[ $key == HVO_EXPOSURE_SECONDS ]] && value=${value}s
                name+=-$value
            done
            mapfile -t assignments < <(jq -r 'to_entries[] | "\(.key)=\(.value)"' <<<"$cell")
            run_process "$name" "$repo" "$revision" "$project" "$filter" "$expected" "$wanted" "$out/reports/$name" "${assignments[@]}"
            note_status "$last_status"
            results=$(jq --arg id "$id" '.[-1] += {id: $id}' <<<"$results") || exit 1
        done
    done
    sections=$(jq -n --argjson h "$harness" --arg s "$section" '{($s): $h}')
fi

host=$(jq --arg load "$(loadavg)" '. + {loadAverageEnd: $load}' <<<"$host") || fatal "could not record host"
jq -n --arg revision "$revision" --arg base "$base" --arg manifest "$manifest_sha" --argjson host "$host" --arg mode "$mode" \
    --arg started "$started_utc" --arg finished "$(utc)" --argjson inputs "$inputs" --argjson runs "$results" \
    --argjson continuity "$continuity" --argjson harness "$sections" --arg status "$pack_status" \
    '{schema: "virtual-astrometry-long-exposure-qualification-index-v1", issue: 1168, mode: $mode, final: ($mode == "final"),
      revision: $revision, baseRevision: $base, manifestSha256: $manifest, startedUtc: $started, finishedUtc: $finished,
      status: $status, host: $host, baseInputs: $inputs, harnessSources: $harness, runs: $runs, continuity: $continuity}' >"$out/index.json" ||
    fatal "could not write $out/index.json"

# The index must name the measured revision and manifest hash, carry one process per declared cell (continuity: one base
# and one head process per #1126 entry, each pair classified), record harness source blobs, and, when passed, carry a
# well-formed TRX hash for every process and a hash for every declared report.
if [[ "$mode" == continuity ]]; then
    jq -e --slurpfile p "$prior" --arg revision "$revision" --arg manifest "$manifest_sha" '
        def sha256: type == "string" and test("^[0-9a-f]{64}$");
        ([$p[0].tuning[], $p[0].runs[]]) as $entries
        | .revision == $revision and .manifestSha256 == $manifest and ($manifest | sha256)
          and (.runs | length) == 2 * ($entries | length)
          and (.continuity.pairs | length) == ($entries | length)
          and ([.continuity.pairs[].id] == [$entries[].id])
          and all(.harnessSources.continuity[]; (.base | length) > 0 and (.head | length) > 0)
          and (.status != "passed"
               or (all(.runs[]; .trx.sha256 | sha256)
                   and ([.runs[].reports[] | select(.sha256 | sha256)] | length) == 2 * ([$entries[].reports | length] | add)
                   and .continuity.verdict == "passed"))' "$out/index.json" >/dev/null ||
        fatal "index $out/index.json does not cover the continuity manifest"
else
    jq -e --slurpfile m "$manifest" --arg revision "$revision" --arg manifest "$manifest_sha" --arg section "$section" '
        def sha256: type == "string" and test("^[0-9a-f]{64}$");
        ($m[0][$section]) as $entries
        | .revision == $revision and .manifestSha256 == $manifest and ($manifest | sha256)
          and (.runs | length) == ([$entries[].cells | length] | add)
          and ([.runs[] | {id, cell}] == [$entries[] | .id as $id | .cells[] | {id: $id, cell: .}])
          and (.harnessSources[$section] | length) == ($entries | length)
          and all(.harnessSources[$section][]; (.sources | length) > 0 and all(.sources[]; .blob | test("^[0-9a-f]{40}$")))
          and (.status != "passed"
               or (all(.runs[]; .trx.sha256 | sha256)
                   and ([.runs[].reports[] | select(.sha256 | sha256)] | length) == ([$entries[] | (.reports | length) * (.cells | length)] | add)))' \
        "$out/index.json" >/dev/null || fatal "index $out/index.json does not cover the manifest"
fi

# Seal the pack: every file under it, hashed and verified.
(cd "$out" && find . -type f ! -name SHA256SUMS -print0 | LC_ALL=C sort -z | xargs -0 sha256sum >SHA256SUMS && sha256sum -c --quiet SHA256SUMS) ||
    fatal "could not seal $out"
echo "index $out/index.json status=$pack_status sha256sums=$(sha256sum "$out/SHA256SUMS" | cut -d' ' -f1) $(utc)"
[[ $pack_status == passed ]]
