def nonempty_string: type == "string" and length > 0;
def uuid: nonempty_string and test("^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$");
def sha256: nonempty_string and test("^[0-9A-Fa-f]{64}$");
def execution:
  (.executionId | uuid) and
  (.status == 2) and
  (.acceptedUtc | nonempty_string) and
  (.startedUtc | nonempty_string) and
  (.completedUtc | nonempty_string) and
  (.startIntervalMilliseconds | type == "number" and . >= 0 and . <= 12000) and
  (.graphIntervalMilliseconds | type == "number" and . >= 0 and . <= 180000) and
  (.graphDefinitionIdentitySha256 | sha256) and
  (.sharedPlanIdentitySha256 | sha256) and
  (.localPlanIdentitySha256 | sha256) and
  (.attemptCount | type == "number" and . >= 1 and . == floor) and
  (.nodes | type == "array" and length > 0 and all(.[];
    (.nodeId | nonempty_string) and (.required | type == "boolean") and
    .status == "Completed" and
    (.outputs | type == "number" and . >= 0 and . == floor)));

.schemaVersion == "issue-719-replay-evidence-v1" and
.profile == $replayProfile and
.stateKey == $stateKey and
.stateReused == $stateReused and
.declaredProfile == $replayProfile and
.result.passed == true and
(if $replayProfile == "InProcess" then
  .replay.role == "establish" and
  .replay.captureCount == $measuredCaptureCount and
  (.replay.graphRevisionId | nonempty_string) and
  (.replay.sourceCaptureId | uuid) and
  (.replay.sourceCaptureSequence | type == "number" and . >= 0 and . == floor) and
  (.replay.primaryArtifactId | uuid) and
  (.replay.liveExecutionId | uuid) and
  (.replay.replay | execution) and
  (.replay.replay.executionId != .replay.liveExecutionId) and
  (.replay.note | nonempty_string)
elif $replayProfile == "LocalRunner" then
  .replay.role == "compare" and
  .replay.readFrom == "durable state" and
  (.replay.sourceCaptureId | uuid) and
  (.replay.primaryArtifactId | uuid) and
  (.replay.graphRevisionId | nonempty_string) and
  (.replay.liveExecutionId | uuid) and
  (.replay.inProcessExecutionId | uuid) and
  (.replay.localRunnerExecutionId | uuid) and
  (.replay.replay.executionId == .replay.localRunnerExecutionId) and
  (.replay.inProcessExecutionId != .replay.localRunnerExecutionId) and
  (.replay.liveExecutionId != .replay.localRunnerExecutionId) and
  (.replay.liveExecutionId != .replay.inProcessExecutionId) and
  (.replay.replay | execution) and
  (.replay.identity.comparedNodes | type == "number" and . > 0 and . == floor) and
  (.replay.identity.comparedOutputs | type == "number" and . > 0 and . == floor) and
  (.replay.identity.comparedNodes) as $comparedNodes |
  (.replay.identity.comparedOutputs) as $comparedOutputs |
  (.replay.identity.outputs | type == "object" and length == $comparedNodes) and
  ([.replay.identity.outputs[] | length] | add == $comparedOutputs) and
  all(.replay.identity.outputs[]; type == "array" and all(.[]; sha256))
else false end)
