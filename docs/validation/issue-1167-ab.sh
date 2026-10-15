#!/usr/bin/env bash
# Runs the #1167 magnitude-5 A/B declared in issue-1167-qualification-manifest.json: the unchanged #1126 qualification at
# the base (A) and at this head (B), then held-out pixel performance pairs in B-then-A order per family, so that the
# arms are compared as A1/B1 and B2/A2 adjacent pairs. Run from the B worktree; the evaluator decides identity and
# performance from the pack.
#
# Usage: HVO_ASTROMETRY_CATALOG_ROOT=<snapshot root> docs/validation/issue-1167-ab.sh <A worktree> <new output directory>
#
# Both worktrees must be clean, A must be at the manifest's base revision, and B is this script's own worktree. Each #1126
# invocation builds its own tree. Under #1126 the measured-stars harness fails at baseline for three families, so a
# nonzero #1126 exit is accepted when its index exists and every run executed its one test; anything else is fatal.
set -uo pipefail

fatal() { echo "$*" >&2; exit 1; }
usage="usage: issue-1167-ab.sh <A worktree> <new output directory>"
a=${1:?$usage}; out=${2:?$usage}
[[ "$out" == /* ]] || out="$PWD/$out"
[[ -n "${HVO_ASTROMETRY_CATALOG_ROOT:-}" ]] || { echo "HVO_ASTROMETRY_CATALOG_ROOT is required" >&2; exit 2; }
b=$(git rev-parse --show-toplevel) || exit 2
a=$(cd "$a" && git rev-parse --show-toplevel) || { echo "$1 is not a worktree" >&2; exit 2; }
manifest="$b/docs/validation/issue-1167-qualification-manifest.json"
base=$(jq -er .base.revision "$manifest") || exit 2
for tree in "$a" "$b"; do
    [[ -z "$(git -C "$tree" status --porcelain --untracked-files=all)" ]] || { echo "$tree has changes or untracked files" >&2; exit 2; }
done
[[ "$(git -C "$a" rev-parse HEAD)" == "$base" ]] || { echo "A must be at the manifest base $base" >&2; exit 2; }
[[ "$a" != "$b" ]] || { echo "A and B must be different worktrees" >&2; exit 2; }
mkdir -p "$(dirname "$out")" && mkdir "$out" || { echo "output directory must not exist: $out" >&2; exit 2; }
out=$(cd "$out" && pwd -P) || exit 2
# The #1126 runner builds without these flags; MSBuild reads them from the environment.
export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false UseRazorBuildServer=false

utc() { date -u +%Y-%m-%dT%H:%M:%SZ; }
qualify() {
    local tree=$1 mode=$2 target=$3
    echo "=== $(basename "$target") $(utc)"
    (cd "$tree" && docs/validation/issue-1126-qualification.sh "$mode" "$target") >"$target.log" 2>&1
    local rc=$?
    [[ -f "$target/index.json" ]] || fatal "$target: no index (exit $rc); see $target.log"
    [[ $rc -le 1 ]] || fatal "$target: #1126 runner exited $rc; see $target.log"
    jq -e 'all(.runs[]; .trx.executed == "1" and .trx.total == "1" and all(.reports[]; .sha256 | type == "string" and test("^[0-9a-f]{64}$")))' \
        "$target/index.json" >/dev/null || fatal "$target: a run did not execute or left no report; see $target.log"
    echo "    $(jq -r .status "$target/index.json") rc=$rc"
}
pixels() {
    local tree=$1 family=$2 target=$3 revision rc
    revision=$(git -C "$tree" rev-parse HEAD)
    mkdir -p "$target" || exit 1
    echo "=== $(basename "$(dirname "$target")")/$family $(utc)"
    (cd "$tree" && env DOTNET_TieredCompilation=0 HVO_EVIDENCE_REVISION="$revision" HVO_PROJECTION_FAMILY="$family" \
        dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --no-build --no-restore \
        --configuration Release --filter "FullyQualifiedName~VirtualAstrometryQualificationTests.ActualPixelsBlindWarmReadouts" \
        --results-directory "$target/results" --logger "trx;LogFileName=run.trx") >"$target/test.log" 2>&1
    rc=$?
    mapfile -t matches < <(find "$target/results" -type f -name virtual-astrometry-pixels.json)
    [[ ${#matches[@]} -eq 1 ]] || fatal "$target: expected one pixel report, found ${#matches[@]} (exit $rc)"
    cp "${matches[0]}" "$target/virtual-astrometry-pixels.json" || fatal "could not copy ${matches[0]}"
    # The raw16 payloads are identical to the qualification pack's and are not evidence for timing.
    find "$target/results" -type f -name '*.raw16' -delete
    echo "    rc=$rc"
}

started=$(utc)
qualify "$a" final "$out/a1-final"
qualify "$a" tuning "$out/a1-tuning"
qualify "$b" final "$out/b1-final"
qualify "$b" tuning "$out/b1-tuning"
mapfile -t families < <(jq -r '.families.values[]' "$b/docs/validation/issue-1126-qualification-manifest.json")
for family in "${families[@]}"; do
    pixels "$b" "$family" "$out/b2/$family"
    pixels "$a" "$family" "$out/a2/$family"
done
jq -n --arg a "$(git -C "$a" rev-parse HEAD)" --arg b "$(git -C "$b" rev-parse HEAD)" --arg started "$started" --arg finished "$(utc)" \
    --arg manifest "$(sha256sum "$manifest" | cut -d' ' -f1)" --argjson families "$(printf '%s\n' "${families[@]}" | jq -R . | jq -s .)" \
    '{schema: "virtual-deep-astrometry-ab-index-v1", issue: 1167, a: $a, b: $b, manifestSha256: $manifest, families: $families,
      startedUtc: $started, finishedUtc: $finished, order: ["a1-final", "a1-tuning", "b1-final", "b1-tuning", "b2/<family> then a2/<family>"]}' \
    >"$out/ab-index.json" || fatal "could not write $out/ab-index.json"
python3 -I "$b/docs/validation/issue-1167-evaluate.py" ab "$manifest" "$out" "$out/ab-decision.json"
