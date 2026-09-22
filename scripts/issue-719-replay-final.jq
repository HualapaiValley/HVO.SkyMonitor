def uppercase_sha256: type == "string" and test("^[0-9A-F]{64}$");
def pair_keys: [
  "bindings", "inProcessEvidence", "inProcessProfileGate", "localRunnerEvidence",
  "localRunnerProfileGate", "passed", "schemaVersion", "stateKey"
];
def pair_binding_keys: [
  "graphDefinitionIdentity", "graphRevisionId", "inProcessExecutionId",
  "liveExecutionId", "localPlanIdentity", "localRunnerExecutionId",
  "primaryArtifactId", "sharedPlanIdentity", "sourceCaptureId"
];
def profile_gate($gate; $trial; $profile; $reused; $evidence_path; $evidence_sha;
    $validator_path; $validator_sha):
  ($gate | type) == "object" and
  ($gate | keys | sort) == ([
    "evidence", "evidenceSha256", "passed", "replayProfile", "schemaVersion",
    "stateKey", "stateReused", "trial", "validator", "validatorSha256"
  ] | sort) and
  $gate.schemaVersion == "issue-719-replay-profile-gate-v1" and
  $gate.trial == $trial and
  $gate.replayProfile == $profile and
  $gate.stateKey == $stateKey and
  $gate.stateReused == $reused and
  $gate.evidence == $evidence_path and
  $gate.evidenceSha256 == $evidence_sha and
  ($gate.evidenceSha256 | uppercase_sha256) and
  $gate.validator == $validator_path and
  $gate.validatorSha256 == $validator_sha and
  ($gate.validatorSha256 | uppercase_sha256) and
  $gate.passed == true;

($inProcessEvidence[0]) as $in_process_evidence |
($localRunnerEvidence[0]) as $local_runner_evidence |
($inProcessGate[0]) as $in_process_gate |
($localRunnerGate[0]) as $local_runner_gate |
($pair[0]) as $pair_gate |
($inProcessEvidenceSha256 | ascii_upcase) as $in_process_evidence_sha |
($localRunnerEvidenceSha256 | ascii_upcase) as $local_runner_evidence_sha |
($inProcessGateSha256 | ascii_upcase) as $in_process_gate_sha |
($localRunnerGateSha256 | ascii_upcase) as $local_runner_gate_sha |
($validatorSha256 | ascii_upcase) as $validator_sha |
{
  schemaVersion: "issue-719-replay-final-v1",
  stateKey: $stateKey,
  bindings: {
    inProcessEvidence: {path: $inProcessEvidencePath, sha256: $in_process_evidence_sha},
    localRunnerEvidence: {path: $localRunnerEvidencePath, sha256: $local_runner_evidence_sha},
    inProcessProfileGate: {path: $inProcessGatePath, sha256: $in_process_gate_sha},
    localRunnerProfileGate: {path: $localRunnerGatePath, sha256: $local_runner_gate_sha},
    replayPair: {path: $pairPath, sha256: ($pairSha256 | ascii_upcase)}
  },
  passed: (
    ($inProcessEvidence | length) == 1 and ($localRunnerEvidence | length) == 1 and
    ($inProcessGate | length) == 1 and ($localRunnerGate | length) == 1 and
    ($pair | length) == 1 and
    ($in_process_evidence_sha | uppercase_sha256) and
    ($local_runner_evidence_sha | uppercase_sha256) and
    ($in_process_gate_sha | uppercase_sha256) and
    ($local_runner_gate_sha | uppercase_sha256) and
    ($validatorPath | type == "string" and length > 0) and
    ($validator_sha | uppercase_sha256) and
    ($pairSha256 | ascii_upcase | uppercase_sha256) and
    $in_process_evidence.schemaVersion == "issue-719-replay-evidence-v1" and
    $in_process_evidence.profile == "InProcess" and
    $in_process_evidence.declaredProfile == "InProcess" and
    $in_process_evidence.stateKey == $stateKey and $in_process_evidence.stateReused == false and
    $in_process_evidence.result.passed == true and
    $local_runner_evidence.schemaVersion == "issue-719-replay-evidence-v1" and
    $local_runner_evidence.profile == "LocalRunner" and
    $local_runner_evidence.declaredProfile == "LocalRunner" and
    $local_runner_evidence.stateKey == $stateKey and $local_runner_evidence.stateReused == true and
    $local_runner_evidence.result.passed == true and
    profile_gate($in_process_gate; "replay-inprocess"; "InProcess"; false;
      $inProcessEvidencePath; $in_process_evidence_sha; $validatorPath; $validator_sha) and
    profile_gate($local_runner_gate; "replay-localrunner"; "LocalRunner"; true;
      $localRunnerEvidencePath; $local_runner_evidence_sha; $validatorPath; $validator_sha) and
    ($pair_gate | type) == "object" and
    ($pair_gate | keys | sort) == pair_keys and
    ($pair_gate.bindings | type) == "object" and
    ($pair_gate.bindings | keys | sort) == pair_binding_keys and
    $pair_gate.schemaVersion == "issue-719-replay-pair-v2" and
    $pair_gate.stateKey == $stateKey and $pair_gate.passed == true and
    ([ $pair_gate.bindings[] ] | all) and
    $pair_gate.inProcessEvidence == {path: $inProcessEvidencePath, sha256: $in_process_evidence_sha} and
    $pair_gate.localRunnerEvidence == {path: $localRunnerEvidencePath, sha256: $local_runner_evidence_sha} and
    $pair_gate.inProcessProfileGate == {path: $inProcessGatePath, sha256: $in_process_gate_sha} and
    $pair_gate.localRunnerProfileGate == {path: $localRunnerGatePath, sha256: $local_runner_gate_sha})
}
