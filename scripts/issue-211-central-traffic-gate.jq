def count: type == "number" and (isnan | not) and (isinfinite | not) and . >= 0 and . == floor;
def network_only: $ARGS.named.networkOnly == true;
def profile_counters:
  if $trialKind == "replay" then
    $inTestCountersApplicable == false and
    $inTestCountersReason == "Replay evidence schema does not record in-test central traffic counters." and
    $inTestCentralTrafficAttempts == null and $inTestCentralHandlerAttempts == null
  elif $trialKind == "w6" or $trialKind == "mono8" then
    $inTestCountersApplicable == true and
    $inTestCountersReason == "Recorded by the profile evidence schema." and
    $inTestCentralTrafficAttempts == 0 and
    (if network_only then $inTestCentralHandlerAttempts == null
     else $inTestCentralHandlerAttempts == 0 end)
  else false end;
def valid:
  ($trial | type == "string" and length > 0) and
  ($trialKind == "w6" or $trialKind == "mono8" or $trialKind == "replay") and
  ($inTestCountersApplicable | type == "boolean") and
  profile_counters and
  ($finalDenyHits | count);

{
  schemaVersion: (if network_only then "issue-211-central-traffic-gate-v3"
                  else "issue-211-central-traffic-gate-v2" end),
  trial: $trial,
  trialKind: $trialKind,
  inTestCountersApplicable: $inTestCountersApplicable,
  inTestCountersReason: $inTestCountersReason,
  inTestCentralTrafficAttempts: $inTestCentralTrafficAttempts,
  inTestCentralHandlerAttempts: $inTestCentralHandlerAttempts,
  finalCentralHandlerAttempts: $finalCentralHandlerAttempts,
  finalCentralHandlerReadyRecords: $finalCentralHandlerReadyRecords,
  finalDenyHits: $finalDenyHits,
  passed: (valid and $finalDenyHits == 0 and
    (if network_only then
       $finalCentralHandlerAttempts == null and $finalCentralHandlerReadyRecords == null
     else
       $finalCentralHandlerAttempts == 0 and
       ($finalCentralHandlerReadyRecords | count) and $finalCentralHandlerReadyRecords >= 1
     end))
} | if network_only then
  del(.inTestCentralHandlerAttempts, .finalCentralHandlerAttempts, .finalCentralHandlerReadyRecords)
else . end
