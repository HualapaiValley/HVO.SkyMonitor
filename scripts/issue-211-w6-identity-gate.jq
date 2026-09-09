# Issue #211 / #770 -- the pass/fail expression over the W6 evidence identity summary.
#
# This is a separate file for the same reason the identity program is: it is the
# claim the campaign makes about its own evidence, and it has to be runnable by a
# reader over a finished summary, and by a regression test over a corpus built to
# be refused. Keeping one copy is the point. When this lived inline in
# scripts/test:cameraagent-standalone-211 the only way to exercise it was to copy
# it, and a copy of a gate is a gate that can drift away from the one that ships.
#
#   jq --exit-status -f scripts/issue-211-w6-identity-gate.jq \
#       --argjson measuredCaptureCount 10 \
#       --argjson expectedTrialLabels '["trial-1","trial-2","trial-3","trial-4","trial-5"]' \
#       --argjson expectedCaptureRoles '["AnnotatedPreview","Calibrated","Combined","Metadata","Preview","Raw"]' \
#       --argjson expectedCaptureArtifactRoleCounts '{"AnnotatedPreview":1,"Calibrated":1,"Combined":1,"Metadata":12,"Preview":2,"Raw":1}' \
#       <run>/w6-identity-summary.json
#
# Every standard on the right-hand side of these comparisons arrives as a parameter.
# The summary fields on the left are what the identity program measured. A term that
# compared one document-derived value against another document-derived value would
# be checking the evidence against itself, which is the defect this gate exists to
# refuse.
.identityRecorded == true
and .trialsIdentified == true
and .captureIdSetsDisjoint == true
and .trialCount == ($expectedTrialLabels | length)
and .trialLabels == ($expectedTrialLabels | sort)
and .expectedMeasuredCaptureCount == $measuredCaptureCount
and .recordedCaptureCount == (($expectedTrialLabels | length) * $measuredCaptureCount)
and .distinctCaptureIdCount == .recordedCaptureCount
and .recordedRoleCount == ($expectedCaptureRoles | unique | length)
and .expectedCaptureArtifactRoleCounts == $expectedCaptureArtifactRoleCounts
# Issue #770, correction round 5. captureArtifactRoleCounts is the DISTINCT set of
# per-capture role histograms across every document. Requiring it to equal a
# one-element list holding the declared histogram says two things at once: every
# capture carries the declared multiplicity, and no capture differs from any other.
# The artefact total is then implied rather than asserted loosely; the round-4
# expression asked only that artefacts outnumber captures, which a corpus cut from
# eighteen artefacts per capture to one per role satisfied with six hundred of nine
# hundred digests missing.
and .captureArtifactRoleCounts == [$expectedCaptureArtifactRoleCounts]
and .recordedArtifactCount == (.recordedCaptureCount * .expectedCaptureArtifactCount)
