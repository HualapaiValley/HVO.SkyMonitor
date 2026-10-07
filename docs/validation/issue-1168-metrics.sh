#!/usr/bin/env bash
# Reads a sealed #1168 evidence pack written by issue-1168-qualification.sh. Truth enters only through the reports' own
# scored fields; nothing here re-scores a frame.
#
# Usage: docs/validation/issue-1168-metrics.sh summary <pack>
#        docs/validation/issue-1168-metrics.sh floors <tuning pack>
#
# Both verify the pack's SHA256SUMS before reading it. "summary" prints one JSON document with each process's status
# and the envelope metrics of its report: pixel cells, dispositions and the ambiguity and rejected-underconstrained
# rates, exclusion counts, midpoint deltas, trail and round-trip extremes, worst scores, refusals and the negative
# control; measured-star recall, centroid and trail metadata; calibration and uncertainty status; resource
# percentiles; and the renderer capacity boundary. "floors" applies the manifest's floor rule to the uncertainty
# tuning frames and prints the declared floors, with every input count, as JSON followed by the C# initializer for
# DeclaredFloorPixels.
set -euo pipefail

fatal() { echo "$*" >&2; exit 1; }
usage="usage: issue-1168-metrics.sh summary|floors <pack>"
[[ $# -eq 2 ]] || fatal "$usage"
command=$1; pack=$2
command -v jq >/dev/null || fatal "jq is required"
[[ -f "$pack/index.json" && -f "$pack/SHA256SUMS" ]] || fatal "$pack is not a sealed evidence pack"
(cd "$pack" && sha256sum -c --quiet SHA256SUMS) || fatal "$pack does not verify against its SHA256SUMS"
repo=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
manifest="$repo/docs/validation/issue-1168-qualification-manifest.json"
index="$pack/index.json"
sums=$(sha256sum "$pack/SHA256SUMS" | cut -d' ' -f1)

# Shared jq helpers: the extreme of the numbers in an array (null when there are none), and a ratio that is null for
# an empty denominator.
helpers='
def numbers_of: [.[] | select(type == "number")];
def maximum: numbers_of | if length == 0 then null else max end;
def minimum: numbers_of | if length == 0 then null else min end;
def ratio($n; $d): if $d == 0 then null else $n / $d end;
'

pixels='
{exposureSeconds, measurer, variant: .variant.name, renderedTruthStars,
 cells,
 cellsExpectedOutcomeMet: all(.cells[]; .expectedOutcomeMet),
 cases: (.cases | length),
 dispositions: ([.cases[].disposition] | group_by(.) | map({key: .[0], value: length}) | from_entries),
 ambiguityRate: ratio([.cases[] | select(.disposition | test("ambig"))] | length; .cases | length),
 rejectedUnderconstrainedRate: ratio([.cases[] | select(.disposition == "rejected-underconstrained")] | length; .cases | length),
 exclusionCounts: ([.cases[].measurement.exclusionCounts // {} | to_entries[]] | group_by(.key) | map({key: .[0].key, value: (map(.value) | add)}) | from_entries),
 maximumAbsoluteMidpointDeltaTicks: ([.cases[].midpointDeltaTicks | numbers | fabs] | maximum),
 trails: {
   maximumTruthMedianPixels: ([.cases[].truth.medianTrailPixels] | maximum),
   maximumTruthPixels: ([.cases[].truth.maximumTrailPixels] | maximum),
   truncatedTruthTrails: ([.cases[].truth.truncated] | add),
   maximumMidpointBiasPixels: ([.cases[].truth.maximumMidpointBiasPixels] | maximum),
   maximumMeasuredPixels: ([.cases[].measurement.maximumTrailPixels] | maximum),
   saturatedAssociations: ([.cases[].sources.saturatedAssociations] | add),
   trailedAssociations: ([.cases[].sources.trailedAssociations] | add),
   unmodeledSourceAssociations: ([.cases[].sources.unmodeledSourceAssociations] | add)},
 maximumRoundTripErrorPixels: ([.roundTrips[].maximumErrorPixels] | maximum),
 maximumRoundTripErrorPixelsByDestination: (.roundTrips | group_by(.destination // "same-frame")
   | map({key: (.[0].destination // "same-frame"), value: ([.[].maximumErrorPixels] | maximum)}) | from_entries),
 score: ([.reports[].score | objects] as $s | {
   scoredCases: ($s | length),
   worstPrecision: ([$s[].precision] | minimum),
   worstPoseErrorDegrees: ([$s[].poseErrorDegrees] | maximum),
   worstFocalRelativeError: ([$s[].focalRelativeError | numbers | fabs] | maximum),
   worstWithheldP95Pixels: ([$s[].withheldP95Pixels] | maximum),
   missingMappings: ([$s[].missingMappings] | add)}),
 unsupported: ([.unsupported[] | "\(.cell) \(.stage) \(.type): \(.message)"] | group_by(.) | map({key: .[0], value: length}) | from_entries),
 negativeControl: [.negativeControl[] | {caseId, predictedHalfExposureRotationDegrees,
   midpointSkyPoseErrorDegrees: .midpointBound.skyPoseErrorDegrees, midpointGroundPoseErrorDegrees: .midpointBound.groundPoseErrorDegrees,
   startStatus: .startBound.status, startReasonCode: .startBound.reasonCode,
   startSkyPoseErrorDegrees: .startBound.fit.skyPoseErrorDegrees, startGroundPoseErrorDegrees: .startBound.fit.groundPoseErrorDegrees,
   startTimeSkyThroughMidpoint: .startTimeSkyThroughMidpointMapping}],
 failures: (.failures | length)}'

measured='
{exposureSeconds, v1ComparisonGated, cases: (.reports | length),
 minimumV2Recall: ([.reports[].v2.candidate.recall] | minimum),
 minimumV1Recall: ([.reports[].v1.baseline.recall] | minimum),
 worstV2CentroidP95Pixels: ([.reports[].v2.candidate.centroidP95Pixels] | maximum),
 v2FalseAssociations: ([.reports[].v2.candidate.falseAssociations] | add),
 truncatedTruthTrails: ([.reports[].truncatedTruthTrails] | add),
 missedEligibleReasonCounts: ([.reports[].v2.missedEligibleReasonCounts // {} | to_entries[]] | group_by(.key) | map({key: .[0].key, value: (map(.value) | add)}) | from_entries),
 trailMetadata: {
   status: ([.reports[].trailMetadata.status] | group_by(.) | map({key: .[0], value: length}) | from_entries),
   scoredTrails: ([.reports[].trailMetadata.scoredTrails] | add),
   worstNormalizedLengthErrorP95: ([.reports[].trailMetadata.normalizedLengthErrorP95] | maximum),
   worstAngleErrorP95Degrees: ([.reports[].trailMetadata.angleErrorP95Degrees] | maximum)},
 failures: (.failures | length)}'

calibration='
{exposureSeconds, cases: (.reports | length),
 fitStatus: ([.reports[].fit.status] | group_by(.) | map({key: .[0], value: length}) | from_entries),
 withheldScores: ([.reports[].withheldScores[]] | length),
 worstWithheldPoseErrorDegrees: ([.reports[].withheldScores[].score.poseErrorDegrees] | maximum),
 worstWithheldP95Pixels: ([.reports[].withheldScores[].score.withheldP95Pixels] | maximum),
 failures: (.failures | length)}'

uncertainty='
{exposureSeconds, floorSource, declaredFloorPixels: .tolerances.declaredFloorPixels, realizationsPerProfile,
 frames: (.frames | length),
 status: ([.frames[].status] | group_by(.) | map({key: (.[0] // "null"), value: length}) | from_entries),
 coverage: [.coverage[] | {profile, frames, withheld, meanChiSquarePerDegree, meanHeldOutChiSquarePerDegree,
   ellipsoid: [.rows[] | {level, n, ellipsoid, bound}]}],
 failures: (.failures | length)}'

resources='
{exposureSeconds, measurer, reports: [.reports[] | {name, captureP95Ms, captureBudgetMs, detectionMedianMs, detectionP95Ms,
   solveMedianMs, solveP95Ms, medianAllocatedBytes, operationsPerSecond}],
 failures: (.failures | length)}'

capacity='{rectilinearBoundary, probes: [.probes[] | {probe, outcome, maximumTemporalSamples, maximumSampleMotionPixels, elapsedMs}], failures}'

case "$command" in
summary)
    runs='[]'
    while read -r run; do
        summaries='[]'
        while read -r report; do
            name=$(jq -r .report <<<"$report"); file=$(jq -r '.file // empty' <<<"$report")
            if [[ -z "$file" ]]; then
                summaries=$(jq --arg r "$name" '. + [{report: $r, missing: true}]' <<<"$summaries"); continue
            fi
            case "$name" in
                virtual-astrometry-pixels.json) program=$pixels ;;
                virtual-measured-stars.json) program=$measured ;;
                virtual-optical-calibration.json) program=$calibration ;;
                virtual-astrometric-uncertainty.json) program=$uncertainty ;;
                virtual-astrometry-resources.json) program=$resources ;;
                virtual-long-exposure-capacity.json) program=$capacity ;;
                *) fatal "no summary for $name" ;;
            esac
            summary=$(jq "$helpers $program" "$pack/$file") || fatal "could not summarize $pack/$file"
            summaries=$(jq --arg r "$name" --argjson s "$summary" '. + [{report: $r} + $s]' <<<"$summaries")
        done < <(jq -c '.reports[]' <<<"$run")
        runs=$(jq --argjson run "$run" --argjson s "$summaries" \
            '. + [{name: $run.name, id: $run.id, revision: $run.revision, cell: $run.cell, status: $run.status, elapsedSeconds: $run.elapsedSeconds, reports: $s}]' <<<"$runs")
    done < <(jq -c '.runs[]' "$index")
    jq -n --slurpfile i "$index" --arg sums "$sums" --argjson runs "$runs" \
        '$i[0] as $x | {pack: {mode: $x.mode, final: $x.final, revision: $x.revision, manifestSha256: $x.manifestSha256, status: $x.status,
            sha256sums: $sums, host: $x.host.name, startedUtc: $x.startedUtc, finishedUtc: $x.finishedUtc},
          continuity: ($x.continuity | if . == null then null else {verdict, pairs: [.pairs[] | {id, verdict, baseStatus, headStatus,
              reports: [.reports[] | {report, verdict, identicalLeaves, counts}]}]} end),
          runs: $runs}'
    ;;
floors)
    [[ "$(jq -r .mode "$index")" == tuning ]] || fatal "floors are derived only from a tuning pack"
    [[ "$(jq -r .manifestSha256 "$index")" == "$(sha256sum "$manifest" | cut -d' ' -f1)" ]] ||
        fatal "the tuning pack was not run against this manifest"
    # Every uncertainty tuning process at a floor exposure must have attached its report; its coverage outcome at the
    # provisional floors is not an outcome, but every other failure is listed with the derivation.
    inputs=$(jq -c --slurpfile m "$manifest" '[$m[0].floors.exposures[] | tostring] as $exposures
        | [.runs[] | select(.id == "astrometric-uncertainty-tuning") | select(.cell.HVO_EXPOSURE_SECONDS as $x | any($exposures[]; . == $x))]' "$index")
    expected=$(jq '.floors.exposures as $exposures | [.tuning[] | select(.id == "astrometric-uncertainty-tuning") | .cells[]
        | select((.HVO_EXPOSURE_SECONDS | tonumber) as $x | any($exposures[]; . == $x))] | length' "$manifest")
    [[ "$(jq length <<<"$inputs")" == "$expected" ]] || fatal "the tuning pack does not carry every declared uncertainty tuning cell at the floor exposures"
    frames='[]'; processes='[]'
    while read -r run; do
        file=$(jq -r '.reports[0].file // empty' <<<"$run")
        [[ -n "$file" && "$(jq -r .status <<<"$run")" != incomplete ]] || fatal "$(jq -r .name <<<"$run") is incomplete"
        report="$pack/$file"
        [[ "$(jq -r .floorSource "$report")" == provisional-1s-tuning-only ]] || fatal "$report was not run at the provisional floors"
        frames=$(jq --slurpfile r "$report" '. + [$r[0] as $x | $x.frames[] | {exposure: $x.exposureSeconds, family: $x.projectionFamily,
            profile, index, requiredFloorPixels}]' <<<"$frames")
        processes=$(jq --argjson run "$run" --slurpfile r "$report" '. + [{name: $run.name, status: $run.status,
            family: $r[0].projectionFamily, exposureSeconds: $r[0].exposureSeconds, frames: ($r[0].frames | length),
            coverageFailures: ([$r[0].failures[] | select(test("coverage"))] | length),
            otherFailures: [$r[0].failures[] | select(test("coverage") | not)]}]' <<<"$processes")
    done < <(jq -c '.[]' <<<"$inputs")
    derived=$(jq -n --slurpfile m "$manifest" --argjson frames "$frames" '
        def median: sort | length as $n | if $n == 0 then null elif $n % 2 == 1 then .[($n - 1) / 2] else (.[$n / 2 - 1] + .[$n / 2]) / 2 end;
        def half_up: if . == null then null else (. * 1000 + 0.5 | floor) / 1000 end;
        $m[0].floors as $f
        | [$f.exposures[] as $e | $f.formatGroups | to_entries[] | .key as $group | .value as $views
            | [$frames[] | select(.exposure == $e and (.profile as $p | any($views[]; . == $p)))] as $rows
            | ([$rows[].requiredFloorPixels | numbers]) as $values
            | {exposureSeconds: $e, group: $group, views: $views, families: ([$rows[].family] | unique), frames: ($rows | length),
               excludedRequiredFloors: (($rows | length) - ($values | length)), median: ($values | median), declaredPixels: ($values | median | half_up)}]')
    jq -e 'all(.[]; .frames > 0 and .declaredPixels != null)' <<<"$derived" >/dev/null || fatal "a format group has no required floor: $derived"
    jq -n --arg sums "$sums" --arg revision "$(jq -r .revision "$index")" --argjson processes "$processes" --argjson floors "$derived" \
        '{tuningPack: {revision: $revision, sha256sums: $sums}, rule: "median per format group pooled across tuned families, half-up to 0.001 px",
          processes: $processes, floors: $floors}'
    # The C# initializer entries for DeclaredFloorPixels, one per exposure, in view order.
    jq -r '
        def literal: tostring | sub("^0\\."; ".");
        group_by(.exposureSeconds)[] | "        [\(.[0].exposureSeconds)] = new(StringComparer.Ordinal)\n        {\n"
            + ([.[] | .declaredPixels as $p | .views[] | "            [\"\(.)\"] = \($p | literal)"] | join(",\n")) + "\n        },"' <<<"$derived"
    ;;
*) fatal "$usage" ;;
esac
