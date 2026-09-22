def uppercase_sha256: type == "string" and test("^[0-9A-F]{64}$");

type == "object" and
(keys | sort) == ([
  "evidence", "evidenceSha256", "passed", "replayProfile", "schemaVersion",
  "stateKey", "stateReused", "trial", "validator", "validatorSha256"
] | sort) and
.schemaVersion == "issue-719-replay-profile-gate-v1" and
.trial == $trial and
.replayProfile == $replayProfile and
.stateKey == $stateKey and
.stateReused == $stateReused and
.evidence == $evidencePath and
.evidenceSha256 == ($evidenceSha256 | ascii_upcase) and
(.evidenceSha256 | uppercase_sha256) and
.validator == $validatorPath and
.validatorSha256 == ($validatorSha256 | ascii_upcase) and
(.validatorSha256 | uppercase_sha256) and
.passed == true
