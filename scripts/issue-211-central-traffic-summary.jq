def is_count:
  (type == "number") and (isnan | not) and (isinfinite | not) and . >= 0 and . == floor;
def expected_kind:
  if .trial == "mono8-control" then "mono8"
  elif .trial == "replay-inprocess" or .trial == "replay-localrunner" then "replay"
  elif (.trial | type == "string" and test("^trial-[1-9][0-9]*$")) then "w6"
  else null end;
def counters_valid:
  if expected_kind == "replay" then
    .inTestCountersApplicable == false and
    .inTestCountersReason == "Replay evidence schema does not record in-test central traffic counters." and
    .inTestCentralTrafficAttempts == null and .inTestCentralHandlerAttempts == null
  else
    .inTestCountersApplicable == true and
    .inTestCountersReason == "Recorded by the profile evidence schema." and
    (.inTestCentralTrafficAttempts | is_count) and
    (.inTestCentralHandlerAttempts | is_count)
  end;
def document_valid:
  .schemaVersion == "issue-211-central-traffic-gate-v2" and
  expected_kind != null and .trialKind == expected_kind and
  (.passed | type == "boolean") and counters_valid and
  (.finalCentralHandlerAttempts | is_count) and
  (.finalCentralHandlerReadyRecords | is_count) and
  (.finalDenyHits | is_count);
def recomputed_passed:
  document_valid and
  (.inTestCountersApplicable == false or
    (.inTestCentralTrafficAttempts == 0 and .inTestCentralHandlerAttempts == 0)) and
  .finalCentralHandlerAttempts == 0 and
  .finalCentralHandlerReadyRecords >= 1 and
  .finalDenyHits == 0;

($expectedTrials | sort) as $expected |
([.[].trial] | sort) as $actual |
{
  schemaVersion: "issue-211-final-central-traffic-summary-v5",
  gateCount: length,
  trialLabels: $actual,
  expectedTrialLabels: $expected,
  trialsIdentified: (
    ($expectedTrials | type == "array") and
    (($expectedTrials | unique | length) == ($expectedTrials | length)) and
    length == ($expected | length) and $actual == $expected),
  trialKindsMatchLabels: (length > 0 and all(document_valid)),
  trials: (sort_by(.trial) | map(.recordedPassed = .passed | .passed = recomputed_passed)),
  centralHandlerAttempts:
    (map(select(.inTestCountersApplicable == true) | .inTestCentralHandlerAttempts) | add // 0),
  finalCentralHandlerAttempts: (map(.finalCentralHandlerAttempts) | add // 0),
  finalDenyHits: (map(.finalDenyHits) | add // 0),
  recordedFlagsAgreeWithCounters: (length > 0 and all(document_valid and .passed == recomputed_passed)),
  allPassed: (length > 0 and all(recomputed_passed))
}
