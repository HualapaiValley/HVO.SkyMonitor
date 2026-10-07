#!/usr/bin/env bash
# Extracts the key metrics of a #1126 projection-family evidence pack (tuning or
# final, written by issue-1126-qualification.sh) into one compact JSON object
# keyed by run name (<run id>-<family>). The per-report jq programs are those of
# issue-1106-metrics.sh, plus the report's projectionFamily, so the equidistant
# family compares field for field with the #1106 baseline.
# Usage: issue-1126-metrics.sh <pack> <output json>
# Requires jq >= 1.6. Read-only on the pack. Statistic conventions are those of
# issue-1106-metrics.sh: median = mean of the two middle values for even n;
# p95 = nearest rank, sorted[ceil(0.95n)-1].
set -euo pipefail

P=${1:?usage: issue-1126-metrics.sh <pack> <output json>}
OUT=${2:?usage: issue-1126-metrics.sh <pack> <output json>}
INDEX="$P/index.json"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

DEFS='
def med: sort | length as $n | if $n==0 then null elif $n%2==1 then .[($n-1)/2] else (.[$n/2-1]+.[$n/2])/2 end;
def p95: sort | length as $n | if $n==0 then null else .[((($n*0.95)|ceil)-1)] end;
def mx: if length==0 then null else max end;
def mn: if length==0 then null else min end;
# Undefined values (NaN, serialized as a string when a case has no associations) pass through unrounded.
def r6: if type != "number" then . else (. * 1000000 | round) / 1000000 end;
def num: select(type == "number");
def stats: {n:length, median:(med|r6), p95:(p95|r6), max:(mx|r6)};
'


sha() { sha256sum "$1" | cut -d' ' -f1; }

program() {
  case "$1" in
    virtual-astrometry-pixels.json) printf '%s' "$PIXELS" ;;
    virtual-astrometry-resources.json) printf '%s' "$RESOURCES" ;;
    virtual-measured-stars.json) printf '%s' "$MEASURED" ;;
    virtual-optical-calibration.json) printf '%s' "$OPTICAL" ;;
    virtual-astrometric-uncertainty.json) printf '%s' "$UNCERTAINTY" ;;
    *) echo "no metrics program for $1" >&2; return 1 ;;
  esac
}

PIXELS='
def modeSummary: {
  n:length,
  status:(group_by(.assessment.status)|map({key:.[0].assessment.status, value:length})|from_entries),
  elapsedMs:([.[].metrics.elapsedMilliseconds]|stats),
  hypothesesMax:([.[].metrics.hypotheses]|mx), budgetExhausted:([.[]|select(.metrics.budgetExhausted)]|length),
  precisionMin:([.[].score.precision]|mn), missingMappingsSum:([.[].score.missingMappings]|add),
  poseErrorDegMax:([.[].score.poseErrorDegrees]|mx|r6), focalRelativeErrorMax:([.[].score.focalRelativeError]|mx),
  withheldRmsPxMax:([.[].score.withheldRmsPixels]|mx|r6), withheldP95PxMax:([.[].score.withheldP95Pixels]|mx|r6),
  withheldCountMin:([.[].score.withheldCount]|mn),
  fittingRmsPxMax:([.[].assessment.quality.fittingRmsPixels]|mx|r6), verificationRmsPxMax:([.[].assessment.quality.verificationRmsPixels]|mx|r6),
  qualityScoreMin:([.[].assessment.quality.score0To100]|mn),
  widthCoverageMin:([.[].assessment.quality.widthCoverageFraction]|mn|r6), heightCoverageMin:([.[].assessment.quality.heightCoverageFraction]|mn|r6)
};
{
  reportSha256:$sha, projectionFamily, schema, partition, revision, databaseSha256, rowCount, identitySha256, selectionIdentitySha256,
  finalExposureQualification, logicalExposurePolicy, detectorValidity, mappingInteriorMarginPixels,
  reportRows:(.reports|length), failures:(.failures|length),
  byMode:(.reports|group_by(.assessment.mode)|map({key:.[0].assessment.mode, value:modeSummary})|from_entries),
  byName:(.reports|group_by(.name)|map({key:.[0].name, value:{n:length,
     captureMs:([.[].captureMs]|stats), measurementMs:([.[].measurementMs]|stats),
     detectedMin:([.[].detected]|mn), candidateMax:([.[].candidateCount]|mx), configHashCount:([.[].configHash]|unique|length)}})|from_entries),
  allRows:{captureMs:([.reports[].captureMs]|stats), measurementMs:([.reports[].measurementMs]|stats)},
  distinct:{assessmentIdentities:([.reports[].assessment.identitySha256]|unique|length),
     sourcePayloadSha256:([.reports[].assessment.frame.sourcePayloadSha256]|unique|length),
     configHashes:([.reports[].configHash]|unique|length),
     catalogIdentities:([.reports[].assessment.catalogIdentitySha256]|unique),
     catalogSelectionIdentities:([.reports[].assessment.catalogSelectionIdentitySha256]|unique),
     settingsIdentities:([.reports[].assessment.settingsIdentitySha256]|unique|length),
     warmPreviousLinked:([.reports[]|select(.assessment.mode=="Warm" and .assessment.previousAssessmentIdentitySha256!=null)]|length)},
  grids:{rows:(.grids|length), supported:([.grids[]|select(.residualPixels!=null)]|length),
     residualPx:([.grids[].residualPixels|select(.!=null)]|stats)},
  sourceGeometry:{rows:(.sourceGeometry|length), supported:([.sourceGeometry[]|select(.rayErrorDegrees!=null)]|length),
     rayErrorDeg:([.sourceGeometry[].rayErrorDegrees|select(.!=null)]|stats),
     scaleRelErrorMax:([.sourceGeometry[]|(.scaleXRelativeError,.scaleYRelativeError)|select(.!=null)|fabs]|mx)},
  sourceCoverage:{rows:(.sourceCoverage|length), points:([.sourceCoverage[].points]|add),
     supported:([.sourceCoverage[].supportedPoints]|add), minSupported:([.sourceCoverage[].supportedPoints]|mn), minUnsupported:([.sourceCoverage[].unsupportedPoints]|mn)},
  mappingCoverage:{rows:(.mappingCoverage|length), points:([.mappingCoverage[].points]|add),
     supported:([.mappingCoverage[].supportedMappings]|add), minSupported:([.mappingCoverage[].supportedMappings]|mn), minUnsupported:([.mappingCoverage[].unsupportedMappings]|mn)},
  reproducibility:"no explicit reproducibility/repeat field in report; distinct-identity counts above are the only in-report evidence"
}'
RESOURCES='{
  reportSha256:$sha, projectionFamily, schema, revision, route, warmup, measured, concurrency, tieredCompilation, initialProcessPeakBytes, failures:(.failures|length),
  note:"report has capture and detection(=measure) timings only; no solve timing field",
  reports:[.reports[]|{name, configHash, identitySha256, captureMedianMs, captureP95Ms, detectionMedianMs, detectionP95Ms,
    medianAllocatedBytes, operationsPerSecond:(.operationsPerSecond|r6),
    cpuMs:([.samples[].cpuMs]|stats), peakWorkingSetBytes:([.samples[].processPeakWorkingSetBytes]|mx),
    samples:(.samples|length), detectedMin:([.samples[].detected]|mn), payloadBytes:.samples[0].payloadBytes,
    exposure:.samples[0].request.requestedSetpoint.exposure, gain:.samples[0].request.requestedSetpoint.gain,
    readBytes:([.samples[].readBytes]|add), writeBytes:([.samples[].writeBytes]|add)}],
  cumulativePeakBytes:([.rawSamples[].processPeakWorkingSetBytes]|mx)
}'
MEASURED='{
  reportSha256:$sha, projectionFamily, schema, partition, revision, identitySha256, selectionIdentitySha256, tieredCompilation, tolerances,
  cases:(.reports|length), failures:(.failures|length), windowPressure,
  perCase:[.reports[]|{caseId, v1Recall:(.v1.baseline.recall|r6), v2Recall:(.v2.candidate.recall|r6),
    v1Rms:(.v1.baseline.centroidRmsPixels|r6), v2Rms:(.v2.candidate.centroidRmsPixels|r6), v2P95:(.v2.candidate.centroidP95Pixels|r6),
    v1Ms:(.v1.measurementMs|r6), v2Ms:(.v2.measurementMs|r6), ratio:((.v2.measurementMs/.v1.measurementMs)|r6), v2CpuMs:.v2.resources.cpuMs,
    v2BPerPx:(.v2.allocatedBytesPerPixel|r6), v1Assoc:.v1.associations, v2Assoc:.v2.associations, v2Status:.v2.status, v1Status:.v1.status,
    v2False:.v2.candidate.falseAssociations, peak:([.v1.resources.peakWorkingSetBytes,.v2.resources.peakWorkingSetBytes]|mx),
    missed:.v2.missedEligibleReasonCounts}],
  summary:{
    v2RmsMax:([.reports[].v2.candidate.centroidRmsPixels|num]|mx|r6),
    rmsRegressionMax:([.reports[]|select((.v2.candidate.centroidRmsPixels|type == "number") and (.v1.baseline.centroidRmsPixels|type != "string"))|(.v2.candidate.centroidRmsPixels-(.v1.baseline.centroidRmsPixels // 0))]|mx|r6),
    v2RmsUndefined:([.reports[]|select(.v2.candidate.centroidRmsPixels|type != "number")|.caseId]),
    recallRegressionMax:([.reports[]|((.v1.baseline.recall // 0)-.v2.candidate.recall)]|mx|r6),
    v2MsMax:([.reports[].v2.measurementMs]|mx|r6), v2MsMin:([.reports[].v2.measurementMs]|mn|r6),
    ratioMax:([.reports[]|.v2.measurementMs/.v1.measurementMs]|mx|r6),
    v2BytesPerPixelMax:([.reports[].v2.allocatedBytesPerPixel]|mx|r6),
    falseAssociationsSum:([.reports[].v2.candidate.falseAssociations]|add),
    v2NotAccepted:([.reports[]|select(.v2.status!="Accepted")]|length),
    peakWorkingSetBytes:([.reports[]|.v1.resources.peakWorkingSetBytes,.v2.resources.peakWorkingSetBytes]+[.windowPressure.resources.peakWorkingSetBytes]|mx)}
}'
OPTICAL='.tolerances as $t | ((.fitPlan|length)+(.validationPlan|length)) as $solves | {
  reportSha256:$sha, projectionFamily, schema, partition, revision, identitySha256, selectionIdentitySha256, fitter, tieredCompilation, tolerances, solves:$solves,
  cases:(.reports|length), failures:(.failures|length),
  perCase:[.reports[]|{caseId, kind:.fit.kind, status:.fit.status, fitIdentitySha256:.fit.identitySha256, truth,
    errors,
    diagnostics:(.fit.diagnostics|del(.residualBins)|map_values(if type=="number" then r6 else . end)),
    validationVerificationRmsMax:([.fit.validations[].verificationRmsPixels]|mx|r6),
    withheld:[.withheldScores[]|{readout, precision:.score.precision, rms:(.score.withheldRmsPixels|r6), p95:(.score.withheldP95Pixels|r6),
      poseDeg:(.score.poseErrorDegrees|r6), focal:.score.focalRelativeError, missing:.score.missingMappings, n:.score.withheldCount}],
    omitted:{status:.omittedDistortion.status, rejections:.omittedDistortion.rejections},
    fitResources:.fit.resources, baseline:.fit.singleFrameColdSolveBaseline, phases:.fit.metrics,
    wallLimitMs:([$t.maximumFitMilliseconds, $t.maximumFitToSingleSolveRatioMargin*$solves*.fit.singleFrameColdSolveBaseline.wallMs+$t.fitOverheadMilliseconds]|mn|r6),
    allocLimitBytes:($t.maximumFitToSingleSolveRatioMargin*$solves*.fit.singleFrameColdSolveBaseline.allocatedBytes+$t.fitOverheadAllocatedBytes)}
    | .wallHeadroom=((1-.fitResources.wallMs/.wallLimitMs)|r6) | .allocHeadroom=((1-.fitResources.allocatedBytes/.allocLimitBytes)|r6)],
  peakWorkingSetBytes:([.reports[]|.fit.resources.peakWorkingSetBytes,.fit.singleFrameColdSolveBaseline.peakWorkingSetBytes,.omittedDistortion.resources.peakWorkingSetBytes]|mx)
}'
UNCERTAINTY='.tolerances as $t | {
  reportSha256:$sha, projectionFamily, schema, partition, revision, identitySha256, selectionIdentitySha256, estimator, tieredCompilation, tolerances,
  realizationsPerProfile, frames:(.frames|length), sessions:(.sessions|length), failures:(.failures|length),
  withheldFrames:([.frames[]|select(.status!="available")]|length),
  falseWithholdFraction:(([.frames[]|select(.status!="available")]|length)/(.frames|length)),
  frameResources, estimatorToFrameRatioMax:([.frames[]|.estimator.wallMs/.frame.wallMs]|mx|r6),
  coverage:[.coverage[]|{profile, frames, withheld, chi2PerDof:(.meanChiSquarePerDegree|r6), heldOutChi2PerDof:(.meanHeldOutChiSquarePerDegree|r6),
    ellipsoid:[.rows[].ellipsoid|r6], minInterval:[.rows[].intervals|min|r6], maxDeviationInterval:[.rows[]|.level as $l|.intervals|max_by((. - $l)|fabs)|r6],
    bound:[.rows[].bound|r6],
    minHeadroom:([.rows[]|.level as $l|.bound as $b|(.ellipsoid,.intervals[])|1-(((. - $l)|fabs)/$b)]|mn|r6)}],
  requiredFloorPx:(.frames|group_by(.profile)|map({key:.[0].profile, value:{min:([.[].requiredFloorPixels]|mn|r6), median:([.[].requiredFloorPixels]|med|r6), max:([.[].requiredFloorPixels]|mx|r6)}})|from_entries),
  sessionRows:[.sessions[]|.caseId as $c|.rows[]|{session:$c, readout, totalMahalanobis:(.totalMahalanobis|r6), conditionalOnlyMahalanobis:(.conditionalOnlyMahalanobis|r6)}],
  sessionTotalMax:([.sessions[].rows[].totalMahalanobis]|mx|r6), sessionTotalMean:([.sessions[].rows[].totalMahalanobis]|add/length|r6),
  distinctFrameIdentities:([.frames[].identitySha256]|unique|length)
}'

jq -r '.runs[] | .name as $n | .reports[] | select(.sha256 != null) | "\($n)\t\(.report)"' "$INDEX" |
while IFS=$'\t' read -r name report; do
  F="$P/reports/$name/$report"
  jq --arg sha "$(sha "$F")" --arg name "$name" "$DEFS"'{($name): ('"$(program "$report")"')}' "$F"
done | jq -s 'add // {}' > "$TMP/runs.json"

jq -n --slurpfile idx "$INDEX" --slurpfile runs "$TMP/runs.json" '{
  schema:"issue-1126-extracted-metrics-v1",
  index:($idx[0]|{mode, final, revision, manifestSha256, families, status, host,
    runs:[.runs[]|{id, name, family, status, elapsedSeconds, trx:.trx|{total,passed,failed}}]}),
  runs:$runs[0]
}' > "$OUT"
jq -c . "$OUT" > "$OUT.tmp" && mv "$OUT.tmp" "$OUT"
echo "wrote $OUT ($(wc -c < "$OUT") bytes)"
