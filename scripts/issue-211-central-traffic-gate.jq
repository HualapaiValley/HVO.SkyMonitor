def count: type == "number" and (isnan | not) and (isinfinite | not) and . >= 0 and . == floor;
def profile_counters:
  if $trialKind == "replay" then
    $inTestCountersApplicable == false and
    $inTestCountersReason == "Replay evidence schema does not record in-test central traffic counters." and
    $inTestCentralTrafficAttempts == null and $inTestCentralHandlerAttempts == null
  elif $trialKind == "w6" or $trialKind == "mono8" then
    $inTestCountersApplicable == true and
    $inTestCountersReason == "Recorded by the profile evidence schema." and
    ($inTestCentralTrafficAttempts | count) and ($inTestCentralHandlerAttempts | count) and
    $inTestCentralTrafficAttempts == 0 and $inTestCentralHandlerAttempts == 0
  else false end;
def valid:
  ($trial | type == "string" and length > 0) and
  ($trialKind == "w6" or $trialKind == "mono8" or $trialKind == "replay") and
  ($inTestCountersApplicable | type == "boolean") and
  profile_counters and
  ($finalCentralHandlerAttempts | count) and
  ($finalCentralHandlerReadyRecords | count) and
  ($finalDenyHits | count);

{
  schemaVersion: "issue-211-central-traffic-gate-v2",
  trial: $trial,
  trialKind: $trialKind,
  inTestCountersApplicable: $inTestCountersApplicable,
  inTestCountersReason: $inTestCountersReason,
  inTestCentralTrafficAttempts: $inTestCentralTrafficAttempts,
  inTestCentralHandlerAttempts: $inTestCentralHandlerAttempts,
  finalCentralHandlerAttempts: $finalCentralHandlerAttempts,
  finalCentralHandlerReadyRecords: $finalCentralHandlerReadyRecords,
  finalDenyHits: $finalDenyHits,
  passed: (valid and
    $finalCentralHandlerAttempts == 0 and $finalCentralHandlerReadyRecords >= 1 and $finalDenyHits == 0)
}
