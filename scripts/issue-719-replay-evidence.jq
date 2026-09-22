def nonempty_string: type == "string" and length > 0;
def uuid: nonempty_string and test("^[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}$");
def sha256: nonempty_string and test("^[0-9A-Fa-f]{64}$");
def canonical_nodes: [
  "calibrated-preview", "calibration", "cloud", "cloud-presentation", "combined-preview",
  "environment-presentation", "overlay-manifest", "presentation-materializer", "projected-scene",
  "quality", "rolling", "scene-presentation", "storage", "telemetry"
];
def whole_count: type == "number" and (isnan | not) and (isinfinite | not) and . >= 0 and . == floor;
# The recipe-backed nodes reach CameraAgentRecipeExecutionAdapter and record a route per
# attempt; the complementary nodes never reach it and keep Unknown. Under LocalRunner every
# recipe-backed attempt of a replay must have routed to the runner; under InProcess none may.
def recipe_backed_nodes: [
  "projected-scene", "calibration", "calibrated-preview", "rolling", "combined-preview", "quality", "cloud"
];
def expected_route: if $replayProfile == "LocalRunner" then "LocalRunner" else "InProcess" end;
def node_routes_valid:
  (.executionRoutes | type == "array" and length >= 1) and
  (if (.nodeId | IN(recipe_backed_nodes[])) then
     all(.executionRoutes[]; . == expected_route)
   else
     all(.executionRoutes[]; . == "Unknown")
   end);
def execution:
  (.executionId | uuid) and
  (.status == 2) and
  (.acceptedUtc | nonempty_string) and
  (.startedUtc | nonempty_string) and
  (.completedUtc | nonempty_string) and
  (.startIntervalMilliseconds | whole_count and . <= 12000) and
  (.graphIntervalMilliseconds | whole_count and . <= 180000) and
  (.graphDefinitionIdentitySha256 | sha256) and
  (.sharedPlanIdentitySha256 | sha256) and
  (.localPlanIdentitySha256 | sha256) and
  (.attemptCount | whole_count and . >= 1) and
  (.nodes | type == "array" and length == 14 and
    ([.[].nodeId] | sort) == canonical_nodes and
    ([.[].nodeId] | unique | length) == 14 and all(.[];
    (.nodeId | nonempty_string) and (.required | type == "boolean") and
    .status == "Completed" and
    (.outputs | whole_count) and
    (.publishedOutputs == 0) and
    node_routes_valid) and
    ([.[].outputs] | add) > 0);

def valid:
  .schemaVersion == "issue-719-replay-evidence-v2" and
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
    (.replay.sourceCaptureSequence | whole_count) and
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
    .replay.identity.comparedNodes == 14 and
    (.replay.identity.comparedOutputs | whole_count and . > 0) and
    (.replay.identity.comparedNodes) as $comparedNodes |
    (.replay.identity.comparedOutputs) as $comparedOutputs |
    (.replay.identity.outputs | type == "object" and length == $comparedNodes) and
    (.replay.identity.outputs | keys | sort) == canonical_nodes and
    ([.replay.identity.outputs[] | length] | add == $comparedOutputs) and
    all(.replay.identity.outputs[]; type == "array" and all(.[]; sha256)) and
    ([.replay.replay.nodes[] as $node |
      (.replay.identity.outputs[$node.nodeId] | length) == $node.outputs] | all)
  else false end) and
  (if $replayProfile == "InProcess" then (.replay | has("identity") | not) else true end);

valid as $valid |
if ($ARGS.named | has("trial")) then
  if ($valid and
      ($ARGS.named.trial | nonempty_string) and
      ($ARGS.named.evidencePath | nonempty_string) and
      ($ARGS.named.evidenceSha256 | sha256) and
      ($ARGS.named.validatorPath | nonempty_string) and
      ($ARGS.named.validatorSha256 | sha256)) then
    {
      schemaVersion: "issue-719-replay-profile-gate-v1",
      trial: $ARGS.named.trial,
      replayProfile: $replayProfile,
      stateKey: $stateKey,
      stateReused: $stateReused,
      evidence: $ARGS.named.evidencePath,
      evidenceSha256: ($ARGS.named.evidenceSha256 | ascii_upcase),
      validator: $ARGS.named.validatorPath,
      validatorSha256: ($ARGS.named.validatorSha256 | ascii_upcase),
      passed: true
    }
  else error("replay evidence failed semantic validation") end
else $valid end
