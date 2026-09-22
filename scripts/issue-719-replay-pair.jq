($inProcess[0]) as $first |
($localRunner[0]) as $second |
def uppercase_sha256: type == "string" and test("^[0-9A-F]{64}$");
def binding_keys: [
  "graphDefinitionIdentity", "graphRevisionId", "inProcessExecutionId",
  "liveExecutionId", "localPlanIdentity", "localRunnerExecutionId",
  "primaryArtifactId", "sharedPlanIdentity", "sourceCaptureId"
];
{
  schemaVersion: "issue-719-replay-pair-v2",
  stateKey: $stateKey,
  inProcessEvidence: {path: $inProcessPath, sha256: ($inProcessSha256 | ascii_upcase)},
  localRunnerEvidence: {path: $localRunnerPath, sha256: ($localRunnerSha256 | ascii_upcase)},
  inProcessProfileGate: {path: $inProcessGatePath, sha256: ($inProcessGateSha256 | ascii_upcase)},
  localRunnerProfileGate: {path: $localRunnerGatePath, sha256: ($localRunnerGateSha256 | ascii_upcase)},
  bindings: {
    sourceCaptureId: ($second.replay.sourceCaptureId == $first.replay.sourceCaptureId),
    primaryArtifactId: ($second.replay.primaryArtifactId == $first.replay.primaryArtifactId),
    graphRevisionId: ($second.replay.graphRevisionId == $first.replay.graphRevisionId),
    liveExecutionId: ($second.replay.liveExecutionId == $first.replay.liveExecutionId),
    inProcessExecutionId: ($second.replay.inProcessExecutionId == $first.replay.replay.executionId),
    localRunnerExecutionId: ($second.replay.localRunnerExecutionId == $second.replay.replay.executionId),
    graphDefinitionIdentity: ($second.replay.replay.graphDefinitionIdentitySha256 ==
      $first.replay.replay.graphDefinitionIdentitySha256),
    sharedPlanIdentity: ($second.replay.replay.sharedPlanIdentitySha256 ==
      $first.replay.replay.sharedPlanIdentitySha256),
    localPlanIdentity: ($second.replay.replay.localPlanIdentitySha256 ==
      $first.replay.replay.localPlanIdentitySha256)
  },
  passed: (
    ($inProcess | length) == 1 and ($localRunner | length) == 1 and
    ($inProcessSha256 | ascii_upcase | uppercase_sha256) and
    ($localRunnerSha256 | ascii_upcase | uppercase_sha256) and
    ($inProcessGateSha256 | ascii_upcase | uppercase_sha256) and
    ($localRunnerGateSha256 | ascii_upcase | uppercase_sha256) and
    ($inProcessPath | type == "string" and length > 0) and
    ($localRunnerPath | type == "string" and length > 0) and
    ($inProcessGatePath | type == "string" and length > 0) and
    ($localRunnerGatePath | type == "string" and length > 0) and
    $first.schemaVersion == "issue-719-replay-evidence-v2" and
    $second.schemaVersion == "issue-719-replay-evidence-v2" and
    $first.profile == "InProcess" and $first.declaredProfile == "InProcess" and
    $first.stateKey == $stateKey and $first.stateReused == false and
    $first.replay.role == "establish" and $first.result.passed == true and
    $second.profile == "LocalRunner" and $second.declaredProfile == "LocalRunner" and
    $second.stateKey == $stateKey and $second.stateReused == true and
    $second.replay.role == "compare" and $second.result.passed == true and
    $second.replay.sourceCaptureId == $first.replay.sourceCaptureId and
    $second.replay.primaryArtifactId == $first.replay.primaryArtifactId and
    $second.replay.graphRevisionId == $first.replay.graphRevisionId and
    $second.replay.liveExecutionId == $first.replay.liveExecutionId and
    $second.replay.inProcessExecutionId == $first.replay.replay.executionId and
    $second.replay.localRunnerExecutionId == $second.replay.replay.executionId and
    $second.replay.localRunnerExecutionId != $first.replay.replay.executionId and
    $second.replay.localRunnerExecutionId != $first.replay.liveExecutionId and
    $second.replay.replay.graphDefinitionIdentitySha256 == $first.replay.replay.graphDefinitionIdentitySha256 and
    $second.replay.replay.sharedPlanIdentitySha256 == $first.replay.replay.sharedPlanIdentitySha256 and
    $second.replay.replay.localPlanIdentitySha256 == $first.replay.replay.localPlanIdentitySha256)
}
| .passed = (.passed and (.bindings | keys | sort) == binding_keys)
