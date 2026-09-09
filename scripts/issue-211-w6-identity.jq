# Issue #211 / #770 -- W6 evidence identity gate.
#
# This program is the standard the five W6 evidence documents are held to. It is a
# separate file rather than a string inside scripts/test:cameraagent-standalone-211
# so that it can be run on its own over a directory of finished documents, which is
# how a reader checks a campaign that has already completed:
#
#   jq -s -f scripts/issue-211-w6-identity.jq \
#       --arg expectedSchemaVersion issue-211-w6-evidence-v3 \
#       --argjson measuredCaptureCount 10 \
#       --argjson expectedTrialLabels '["trial-1","trial-2","trial-3","trial-4","trial-5"]' \
#       --argjson expectedCaptureRoles '["AnnotatedPreview","Calibrated","Combined","Metadata","Preview","Raw"]' \
#       --argjson expectedCaptureArtifactRoleCounts '{"AnnotatedPreview":1,"Calibrated":1,"Combined":1,"Metadata":12,"Preview":2,"Raw":1}' \
#       TestResults/issue-211/<fingerprint>/<run>/trial-*/evidence/issue-211-w6.json
#
# The input is the slurped array of documents. The output is the identity summary,
# and `identityRecorded` is true only when the problem list is empty. Every standard
# it checks against arrives as a parameter, because a gate that reads its standard
# out of the documents under test cannot reject a document that misstates it.
  # Issue #770, correction round 2. jq 1.7 lets `$` match before a final newline, so a digest
  # with a trailing "\n" satisfied `^[0-9A-Fa-f]{64}$` and then compared equal to nothing. The
  # length is required outright now and the pattern is anchored to the absolute end of the
  # string. A format check in an evidence gate is not here to catch a slip by a cooperative
  # producer; it is here to refuse a value that is not a digest, whoever wrote it.
  def is_sha256: (type == "string") and (length == 64) and test("^[0-9A-Fa-f]{64}\\z");
  def is_byte_count: (type == "number") and (isnan | not) and (isinfinite | not)
    and (. >= 0) and (. == floor);
  def eqi($other): (type == "string") and ($other | type == "string") and (ascii_downcase == ($other | ascii_downcase));
  def trial_problems($index):
    . as $t |
    # The document names its own trial, which is what a reader has to go and open. The slurp
    # index is only a fallback for a document that did not record one.
    ($t.trial // "index \($index)") as $label |
    [ (if ($t.schemaVersion == $expectedSchemaVersion) then empty
       else "trial \($label): schemaVersion is \($t.schemaVersion // "absent"), expected \($expectedSchemaVersion)" end),
      (if (($t.captureProvenance | type) == "array" and ($t.captureProvenance | length) > 0) then empty
       else "trial \($label): captureProvenance is absent or empty, so no capture identity was recorded" end),
      # Issue #770, correction round 2. Non-emptiness is not completeness. A document truncated
      # to one valid provenance record satisfied every check below and reported clean while the
      # measured digests for nine of every ten captures were simply gone.
      #
      # Issue #770, correction round 3. That completeness check was anchored to a number living
      # inside the document under test, so truncating provenance, captures and the measuredCount
      # field together to one left the identity flag true. The real anchor is the campaign
      # MEASURED_CAPTURE_COUNT constant, passed in here from outside every document. The
      # in-document count is now a claim to be checked rather than the standard to check against.
      (if (($t.workload.measuredCount | is_byte_count)
           and $t.workload.measuredCount == $measuredCaptureCount) then empty
       else "trial \($label): workload measuredCount is \($t.workload.measuredCount | tojson) against the \($measuredCaptureCount) captures this campaign measures" end),
      (if (($t.captureProvenance | length) == $measuredCaptureCount) then empty
       else "trial \($label): captureProvenance holds \($t.captureProvenance | length) records against the \($measuredCaptureCount) captures this campaign measures" end),
      (if (($t.captures | type) == "array" and ($t.captures | length) == $measuredCaptureCount) then empty
       else "trial \($label): captures holds \($t.captures | length) records against the \($measuredCaptureCount) captures this campaign measures" end),
      (if (($t.captures | type) == "array"
           and ([$t.captureProvenance[]?.captureId | tostring | ascii_downcase] | unique)
               == ([$t.captures[]?.captureId | tostring | ascii_downcase] | unique)
           and ([$t.captureProvenance[]?.captureId] | length)
               == ([$t.captureProvenance[]?.captureId | tostring | ascii_downcase] | unique | length)) then empty
       else "trial \($label): the captures named by captureProvenance are not exactly the captures the document records, so provenance is incomplete or duplicated" end),
      ($t.captureProvenance[]? |
        . as $c |
        (if ($c.rigSha256 | is_sha256) then empty else "trial \($label) capture \($c.captureId): rigSha256 is not a digest" end),
        (if ($c.processingSha256 | is_sha256) then empty else "trial \($label) capture \($c.captureId): processingSha256 is not a digest" end),
        (if ($c.localProfileSha256 | is_sha256) then empty else "trial \($label) capture \($c.captureId): localProfileSha256 is not a digest" end),
        (if ($c.scheduleRevisionSha256 | is_sha256) then empty else "trial \($label) capture \($c.captureId): scheduleRevisionSha256 is not a digest" end),
        (if ($c.rigSha256 | eqi($t.workload.rigSha256)) then empty
         else "trial \($label) capture \($c.captureId): observed rigSha256 does not match the pinned workload value" end),
        (if ($c.processingSha256 | eqi($t.workload.processingSha256)) then empty
         else "trial \($label) capture \($c.captureId): observed processingSha256 does not match the pinned workload value" end),
        (if ($c.localProfileSha256 | eqi($t.workload.localProfileSha256)) then empty
         else "trial \($label) capture \($c.captureId): observed localProfileSha256 does not match the pinned workload value" end),
        (if ($c.scheduleRevisionSha256 | eqi($t.workload.scheduleSha256)) then empty
         else "trial \($label) capture \($c.captureId): observed scheduleRevisionSha256 does not match the pinned workload value" end),
        (if (($c.artifacts | type) == "array" and ($c.artifacts | length) > 0) then empty
         else "trial \($label) capture \($c.captureId): artifacts is absent or empty, so no artefact identity was recorded" end),
        ($c.artifacts[]? |
          . as $a |
          (if ($a.declaredSha256 | is_sha256) then empty else "trial \($label) artefact \($a.artifactId): declaredSha256 is not a digest" end),
          (if ($a.computedSha256 | is_sha256) then empty else "trial \($label) artefact \($a.artifactId): computedSha256 is not a digest" end),
          (if ($a.declaredSha256 | eqi($a.computedSha256)) then empty
           else "trial \($label) artefact \($a.artifactId): the recomputed digest does not equal the declared one" end),
          (if (($a.observedByteLength | is_byte_count) and ($a.declaredByteLength | is_byte_count)
               and $a.observedByteLength == $a.declaredByteLength) then empty
           else "trial \($label) artefact \($a.artifactId): observedByteLength and declaredByteLength are not equal non-negative whole byte counts" end),
          (if (($a.relativeArtifactPath | type) == "string" and ($a.relativeArtifactPath | length) > 0) then empty
           else "trial \($label) artefact \($a.artifactId): relativeArtifactPath is absent, so the digest cannot be re-derived" end),
          (if (($a.sourceArtifactIds | type) == "array") then empty
           else "trial \($label) artefact \($a.artifactId): sourceArtifactIds is absent, so lineage was not recorded" end),
          (if (($a.role | type) == "string" and ($a.role | length) > 0) then empty
           else "trial \($label) artefact \($a.artifactId): role is \($a.role | tojson), so it cannot be cross-checked against the roles its capture records" end))),
      # Issue #770, correction round 3. Counting artefacts is the weak fix and does not catch the
      # truncation that motivated it: cutting every capture from three artefacts to one leaves the
      # counts equal to each other, the capture identities untouched, and any count-based
      # assertion satisfied. Uniformity defeats a count. The document already carries the same
      # fact written a second time and by a different path, the role list on each captures[]
      # entry, and a truncation of the provenance artefacts has to be mirrored there to survive
      # this. That is a cross-check rather than a count, and it is what is asserted here.
      ($t.captureProvenance[]? |
        . as $c |
        ([$t.captures[]?
          | select((.captureId | tostring | ascii_downcase) == ($c.captureId | tostring | ascii_downcase))
          | .roles] | first) as $recordedRoles |
        (($c.artifacts // []) | map(.role | tostring) | unique) as $provenanceRoles |
        (if (($recordedRoles | type) == "array" and ($recordedRoles | length) > 0
             and ($recordedRoles | map(tostring) | unique) == $provenanceRoles) then empty
         else "trial \($label) capture \($c.captureId): the artefact roles the capture records, \(($recordedRoles // "absent") | tojson), are not the roles its provenance artefacts carry, \($provenanceRoles | tojson)" end)),
      # Issue #770, correction round 4. The cross-check above is not two paths to the same fact.
      # The producer builds captures[].roles from capture.Artifacts and builds
      # captureProvenance[].artifacts by iterating that same collection, so a capture that
      # under-reports its artefacts shortens both lists together and the cross-check then compares
      # the truncation against itself. Cutting every capture from eighteen artefacts to one, and
      # mirroring the roles list, passed it. The standard has to live outside the document, the
      # way the measured capture count now does: a W6 capture carries a known set of artefact
      # roles, and a capture carrying fewer has lost artefact digests whatever it says about
      # itself.
      #
      # Issue #770, correction round 5. The round-4 comment here said per-role artefact counts
      # were deliberately not asserted because they vary by role and by configuration. That
      # licensed the same hole one level down. Anchoring the role SET and leaving the
      # MULTIPLICITY unanchored accepts a capture cut from eighteen artefacts to one per role:
      # all six roles are still present, so the set check is satisfied, and six hundred of nine
      # hundred artefact digests are gone. Measured on the campaign's own documents, that cut
      # reached identityRecorded true with zero problems.
      #
      # The claim in that comment was also wrong where it mattered. Per-role counts do differ
      # from each other, which is what "vary by role" meant and is true. They do not differ
      # across captures within a configuration: all fifty captures of the five-trial campaign
      # carry one identical histogram, twelve Metadata, two Preview and one each of the other
      # four. That measurement is why the histogram can be declared from outside at all, and it
      # is declared rather than derived because a standard read out of the documents under test
      # is not a standard.
      #
      # Requiring the whole histogram rather than a total is the difference between refusing a
      # cut and refusing a cut plus a redistribution. A declared total of eighteen accepts a
      # capture that moves a Preview digest into Metadata and keeps eighteen; the histogram
      # refuses it. Both were measured.
      ($t.captureProvenance[]? |
        . as $c |
        (($c.artifacts // []) | map(.role | tostring) | unique) as $provenanceRoles |
        (if ($provenanceRoles == ($expectedCaptureRoles | unique)) then empty
         else "trial \($label) capture \($c.captureId): the artefact roles recorded are \($provenanceRoles | tojson) against the \(($expectedCaptureRoles | unique) | tojson) a W6 capture carries" end)),
      ($t.captureProvenance[]? |
        . as $c |
        (($c.artifacts // []) | map(.role | tostring) | group_by(.)
          | map({ key: .[0], value: length }) | from_entries) as $roleCounts |
        (if ($roleCounts == $expectedCaptureArtifactRoleCounts) then empty
         else "trial \($label) capture \($c.captureId): the artefact count per role is \($roleCounts | tojson) against the \($expectedCaptureArtifactRoleCounts | tojson) a W6 capture carries, so artefact digests are missing or redistributed" end)),
      (if ($t.calibrationResiduals.observedCatalogSha256 | is_sha256) then empty
       else "trial \($label): calibrationResiduals.observedCatalogSha256 is not a digest" end),
      (if ($t.calibrationResiduals.observedCatalogSha256 | eqi($t.workload.catalogSha256)) then empty
       else "trial \($label): the observed catalog digest does not match the pinned workload value" end) ];
  ([ range(0; length) as $i | (.[$i] | trial_problems($i)) ] | add // []) as $trial_problems |
  # Issue #770, correction round 4. Everything above reads one document at a time, so five copies
  # of one genuine document satisfied all of it: `trialCount` counted documents and the capture
  # count counted provenance records. Nothing compared the documents to each other, and nothing
  # compared their trial labels to the labels the campaign ran. Two properties are checked here
  # that a relabelled copy cannot satisfy. The labels must be the ones the campaign ran, built
  # outside every document. And the capture identities must be pairwise disjoint across trials,
  # because five real trials never capture the same frame twice; label equality alone would still
  # accept five copies that had been relabelled.
  ([ .[] | .trial ]) as $recorded_trial_labels |
  ([ .[] | { trial: (.trial // "unlabelled"),
             ids: ([ .captureProvenance[]?.captureId | tostring | ascii_downcase ] | unique) } ])
    as $trial_capture_ids |
  ([ $trial_capture_ids[] as $t | $t.ids[] | { id: ., trial: $t.trial } ]
    | group_by(.id) | map(select(length > 1))) as $shared_capture_ids |
  (($recorded_trial_labels | map(select((type == "string") and (length > 0))) | sort)
    == ($expectedTrialLabels | sort)) as $trials_identified |
  (($shared_capture_ids | length) == 0) as $capture_ids_disjoint |
  ($trial_problems
    + (if $trials_identified then [] else
        ["the documents carry the trial labels \($recorded_trial_labels | tojson) against the \($expectedTrialLabels | tojson) this campaign ran, so they are not one document per trial"] end)
    + ($shared_capture_ids
        | map("capture \(.[0].id) is recorded by more than one trial, \([ .[].trial ] | tojson), so these documents are not independent trials"))) as $problems |
  {
    schemaVersion: "issue-211-w6-identity-v1",
    expectedEvidenceSchemaVersion: $expectedSchemaVersion,
    trialCount: length,
    expectedMeasuredCaptureCount: $measuredCaptureCount,
    expectedTrialLabels: ($expectedTrialLabels | sort),
    trialLabels: ($recorded_trial_labels | sort),
    trialsIdentified: $trials_identified,
    expectedCaptureArtifactRoles: ($expectedCaptureRoles | unique),
    expectedCaptureArtifactRoleCounts: $expectedCaptureArtifactRoleCounts,
    expectedCaptureArtifactCount: ([ $expectedCaptureArtifactRoleCounts | to_entries[] | .value ] | add // 0),
    captureArtifactRoleCounts:
      ([ .[] | .captureProvenance[]? | (.artifacts // []) | map(.role | tostring)
         | group_by(.) | map({ key: .[0], value: length }) | from_entries ] | unique),
    captureIdSetsDisjoint: $capture_ids_disjoint,
    sharedCaptureIds: ($shared_capture_ids | map(.[0].id)),
    distinctCaptureIdCount: ([ $trial_capture_ids[].ids[] ] | unique | length),
    recordedArtifactCount: ([ .[].captureProvenance[]?.artifacts[]? ] | length),
    recordedCaptureCount: ([ .[].captureProvenance[]? ] | length),
    recordedRoleCount: ([ .[].captureProvenance[]?.artifacts[]?.role ] | unique | length),
    problems: $problems,
    identityRecorded: (($problems | length) == 0)
  }
