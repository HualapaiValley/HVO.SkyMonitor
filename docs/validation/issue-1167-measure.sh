#!/usr/bin/env bash
# Runs the complete #1167 measured sequence declared in issue-1167-qualification-manifest.json, in order: deep tuning,
# the magnitude-5 A/B, the held-out deep matrix, the magnitude-7 evaluation, then the decision. Run it from the B
# worktree under the host's exclusive heavy lock for its whole duration, for example:
#
#   flock -o /tmp/hvo-520-heavy.lock docs/validation/issue-1167-measure.sh <A worktree> <new output directory>
#
# HVO_ASTROMETRY_CATALOG_ROOT is required. Measurement stops after tuning on an infrastructure error, and after the A/B
# when it reports an output change or cannot decide; a resolvable regression is recorded and measurement continues. Each
# step's exit code and UTC bounds are written to steps.json.
set -uo pipefail

usage="usage: issue-1167-measure.sh <A worktree> <new output directory>"
a=${1:?$usage}; out=${2:?$usage}
[[ "$out" == /* ]] || out="$PWD/$out"
[[ -n "${HVO_ASTROMETRY_CATALOG_ROOT:-}" ]] || { echo "HVO_ASTROMETRY_CATALOG_ROOT is required" >&2; exit 2; }
repo=$(git rev-parse --show-toplevel) || exit 2
cd "$repo" || exit 2
manifest=docs/validation/issue-1167-qualification-manifest.json
mkdir -p "$(dirname "$out")" && mkdir "$out" || { echo "output directory must not exist: $out" >&2; exit 2; }
out=$(cd "$out" && pwd -P) || exit 2
echo "$$" >"$out/driver.pid"
utc() { date -u +%Y-%m-%dT%H:%M:%SZ; }
steps='[]'
step() {
    local name=$1; shift
    local started rc
    started=$(utc); echo "### step $name started $started"
    "$@"; rc=$?
    echo "### step $name finished $(utc) exit $rc"
    steps=$(jq --arg n "$name" --arg s "$started" --arg f "$(utc)" --argjson rc "$rc" '. + [{step: $n, startedUtc: $s, finishedUtc: $f, exitCode: $rc}]' <<<"$steps")
    jq -n --arg revision "$(git rev-parse HEAD)" --argjson steps "$steps" '{schema: "virtual-deep-astrometry-measure-steps-v1", revision: $revision, steps: $steps}' \
        >"$out/steps.json"
    return "$rc"
}
finish() { echo "### measured run ended $(utc): $1"; rm -f "$out/driver.pid"; exit "$2"; }

step tuning docs/validation/issue-1167-qualification.sh tuning "$out/tuning" || finish "tuning infrastructure error" 1
step ab docs/validation/issue-1167-ab.sh "$a" "$out/ab"
verdict=$(jq -r '.verdict // empty' "$out/ab/ab-decision.json" 2>/dev/null)
case "$verdict" in
    pass | resolvable-regression) ;;
    *) finish "A/B verdict '${verdict:-none}'; measurement stopped" 1 ;;
esac
step final docs/validation/issue-1167-qualification.sh final "$out/final"
step mag7 docs/validation/issue-1167-qualification.sh mag7 "$out/mag7"
step decision python3 -I docs/validation/issue-1167-evaluate.py deep "$manifest" "$out/final" "$out/decision.json" "$out/tuning" "$out/mag7"
decided=$?
finish "A/B $verdict; decision exit $decided" 0
