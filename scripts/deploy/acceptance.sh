#!/usr/bin/env bash

deploy_acceptance_publish() {
    deploy_phase_publish acceptance-init DEPLOY_ACCEPTANCE_JSON "$DEPLOY_ACCEPTANCE_LEDGER" "$DEPLOY_ACCEPTANCE_MANIFEST" \
      "$DEPLOY_ACCEPTANCE_EVIDENCE" "$DEPLOY_ACCEPTANCE_COMMIT"
}

deploy_acceptance_required_ids() {
    jq -cn '[
      "normal-flow","raw-payload-write-crash","payload-published-before-journal-crash","journal-committed-before-wakeup-crash",
      "logichost-network-outage","minio-failure","sql-failure","worker-before-output-crash",
      "worker-after-output-before-completion-crash","out-of-order-upload","permanent-upload-rejection","disk-pressure",
      "optional-lane-backlog","duplicate-delivery","corrupt-delivery","lease-crash-before-work","lease-crash-after-work","bounded-shutdown",
      "raw-boundary-payload-written","raw-boundary-payload-flushed","raw-boundary-payload-published","raw-boundary-before-journal-commit",
      "raw-boundary-after-journal-commit","raw-boundary-before-wakeup","raw-boundary-sidecar-directory-sync",
      "raw-boundary-migration-transaction-began","raw-boundary-before-migration-commit","raw-boundary-validation-completed",
      "raw-boundary-payload-directory-sync",
      "raw-boundary-sidecar-written","raw-boundary-sidecar-flushed","raw-boundary-sidecar-published",
      "raw-boundary-before-journal-transaction","raw-boundary-journal-transaction-began","raw-boundary-journal-row-inserted",
      "raw-boundary-before-index-projection","raw-boundary-after-index-projection",
      "capture-lane-work-rows-commit","capture-lane-claim-commit","capture-lane-completion-before-commit",
      "capture-lane-completion-after-commit","capture-lane-retry-commit","capture-lane-quarantine-commit",
      "processing-output-before-node-commit","calibration-before-payload-write","calibration-payload-publication",
      "calibration-before-manifest-write","calibration-manifest-publication","calibration-before-profile-write",
      "calibration-profile-publication","calibration-directory-sync","calibration-sqlite-publication","calibration-activation-commit",
      "transient-candidate-stage-before-commit","transient-candidate-stage-after-commit","transient-candidate-reservation-before-commit",
      "transient-candidate-reservation-after-commit","transient-candidate-record-before-commit","transient-candidate-record-after-commit",
      "transient-candidate-finalization-before-commit","transient-candidate-finalization-after-commit",
      "transient-candidate-submission-before-commit","transient-candidate-submission-after-commit",
      "transient-candidate-acknowledgement-before-commit","transient-candidate-acknowledgement-after-commit",
      "transient-candidate-reservation-before-validation","transient-candidate-reservation-after-validation",
      "transient-runtime-frame-history-before-commit","transient-runtime-frame-history-after-commit",
      "transient-runtime-identity-before-commit","transient-runtime-identity-after-commit",
      "transient-runtime-causal-before-commit","transient-runtime-causal-after-commit",
      "transient-runtime-candidate-journal-after-commit","transient-runtime-observation-before-commit",
      "transient-runtime-observation-after-commit","transient-runtime-assessment-before-commit",
      "transient-runtime-assessment-after-commit","transient-runtime-finalization-after-commit","transient-runtime-handoff-after-commit",
      "transient-runtime-completion-before-commit","transient-runtime-completion-after-commit",
      "transient-runtime-retirement-before-commit","transient-runtime-retirement-after-commit",
      "outbox-enqueue-commit","outbox-claim-commit","outbox-retry-commit","outbox-acknowledgement-commit","outbox-quarantine-commit",
      "central-ingest-intent-commit","central-ingest-object-publication","central-ingest-finalization-commit",
      "central-job-intent-commit","central-job-staging-publication","central-job-canonical-publication",
      "central-job-completion-before-commit","central-job-completion-after-commit","central-window-freeze-commit",
      "central-window-timeout-commit","central-window-output-object-publication","central-window-output-finalization-commit",
      "cameraagent-host-failure","logichost-host-failure","network-failure","redis-failure","smtp-failure",
      "external-system-validation","accelerated-soak","stellarium-validation","future-hardware-acceptance"
    ]'
}

deploy_acceptance_verify_source_snapshot() {
    local revision="$1" tree="$2" head current_tree status
    head="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null)" || { deploy_fail acceptance-init source head-unavailable; return 1; }
    current_tree="$(git -C "$REPO_ROOT" rev-parse "${head}^{tree}" 2>/dev/null)" ||
      { deploy_fail acceptance-init source tree-unavailable; return 1; }
    status="$(git -C "$REPO_ROOT" status --porcelain --untracked-files=normal 2>/dev/null)" ||
      { deploy_fail acceptance-init source status-unavailable; return 1; }
    [[ "$head" == "$revision" && "$current_tree" == "$tree" && -z "$status" ]] ||
      { deploy_fail acceptance-init source committed-source-changed; return 1; }
}

deploy_acceptance_validate_repository_manifest_json() {
    local manifest_json="$1" required
    required="$(deploy_acceptance_required_ids)" || return 1
    jq -e --argjson required "$required" '
      def safe: type == "string" and test("^[a-z0-9][a-z0-9-]{0,63}$");
      def text: type == "string" and length > 0 and (test("[[:cntrl:]]") | not);
      .schemaVersion == 2 and .campaign == "phase-14-virtualsky-two-host" and
      (keys | sort) == (["schemaVersion","campaign","classifications","scenarios"] | sort) and
      [.classifications[].id] == ["normal","fault","external","soak","stellarium","future-hardware"] and
      all(.classifications[]; (keys | sort) == (["id","normalCampaign"] | sort) and (.normalCampaign | type) == "boolean") and
      (.classifications | map(select(.normalCampaign).id)) == ["normal","fault"] and
      ([.scenarios[].id] | sort) == ($required | sort) and
      all(.scenarios[];
        (keys | sort) == (["id","title","classification","executionClass","source","evidenceSource","workloads","artifactPath"] | sort) and
        (.id | safe) and (.title | text) and (.source | text) and
        (.evidenceSource | text) and
        (.evidenceSource | if startswith("test:") then
           test("^test:(?:[A-Za-z_][A-Za-z0-9_]*[.]){2,}[A-Za-z_][A-Za-z0-9_]*(?:;case=[A-Za-z0-9._-]+)?$")
         else test("^(campaign|script|gate):") end) and
        (.executionClass == "component-automated" or .executionClass == "real-campaign" or .executionClass == "separate-gate") and
        (.classification as $classification | ["normal","fault","external","soak","stellarium","future-hardware"] | index($classification) != null) and
        (.workloads | type == "array" and length == (unique | length) and all(. == "W0" or . == "W1" or . == "W2")) and
        .artifactPath == ("acceptance-artifacts/" + .id + ".json") and
        (if (.source | startswith("boundary:")) then (.evidenceSource | startswith("test:")) else true end)) and
      ([.scenarios[] | select(.source | test("^project-plan:phase-14-fault-row-([1-9]|1[0-2])$")) |
        .source | sub("^project-plan:phase-14-fault-row-"; "") | tonumber] | sort) == [range(1; 13)] and
      all(.scenarios[] | select(.executionClass == "separate-gate"); .classification != "normal" and .classification != "fault") and
      all(.scenarios[] | select(.classification == "external" or .classification == "soak" or
        .classification == "stellarium" or .classification == "future-hardware"); .executionClass == "separate-gate")
    ' <<< "$manifest_json" >/dev/null 2>&1 || { deploy_fail acceptance-init campaign-manifest invalid; return 1; }
}

deploy_acceptance_validate_repository_manifest() {
    local path="$1" manifest_json
    [[ -f "$path" && ! -L "$path" ]] || { deploy_fail acceptance-init campaign-manifest missing-or-unsafe; return 1; }
    manifest_json="$(jq -c . "$path" 2>/dev/null)" || { deploy_fail acceptance-init campaign-manifest invalid-json; return 1; }
    deploy_acceptance_validate_repository_manifest_json "$manifest_json"
}

deploy_acceptance_contract_json() {
    local inventory_json="$1" campaign_json="$2" workloads_json="$3" campaign_hash="$4" workload_hash="$5"
    jq -cn --arg campaignPath "deploy/split-host/acceptance/phase14-scenarios.json" --arg campaignSha "$campaign_hash" \
      --arg workloadPath "deploy/split-host/workloads/canonical-workloads.json" --arg workloadSha "$workload_hash" \
      --argjson inventory "$inventory_json" --argjson campaign "$campaign_json" --argjson workloads "$workloads_json" '
      {campaignManifest:{schemaVersion:$campaign.schemaVersion,relativePath:$campaignPath,sha256:$campaignSha},
       topology:{environment:$inventory.environment,installationId:$inventory.installationId,
         targets:(([{name:$inventory.logicHost.name,role:"logic-host",hostName:$inventory.logicHost.expectedHostName,architecture:$inventory.logicHost.expectedArchitecture,
           hostIdentity:$inventory.logicHost.expectedHostIdentity,dockerDaemonIdentity:$inventory.logicHost.expectedDockerDaemonIdentity}] +
           [$inventory.cameraAgents[] | {name,role:"camera-agent",hostName:.expectedHostName,architecture:.expectedArchitecture,
             hostIdentity:.expectedHostIdentity,dockerDaemonIdentity:.expectedDockerDaemonIdentity}] +
           (if $inventory.sharedServices then [{name:$inventory.sharedServices.name,role:"shared-services",
             hostName:$inventory.sharedServices.expectedHostName,architecture:$inventory.sharedServices.expectedArchitecture,hostIdentity:$inventory.sharedServices.expectedHostIdentity,
             dockerDaemonIdentity:$inventory.sharedServices.expectedDockerDaemonIdentity}] else [] end)) | sort_by(.role,.name))},
       workloadIdentities:{manifestRelativePath:$workloadPath,manifestSha256:$workloadSha,profiles:$workloads.profiles},
       classifications:[$campaign.classifications[] as $classification |
         {classification:$classification.id,normalCampaign:$classification.normalCampaign,status:"not-run",
          scenarioCount:([$campaign.scenarios[] | select(.classification == $classification.id)] | length)}],
       scenarios:[$campaign.scenarios[] |
         {id,classification,executionClass,sourceReference:.source,evidenceSource,workloads,status:"not-run",
          artifact:{relativePath:.artifactPath,byteLength:null,sha256:null}}]}'
}

deploy_acceptance_validate_smoke_candidate() {
    local path="$1" run_id="$2" mode="$3" hash="$4" revision="$5" inventory_json="$6"
    jq -e --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --argjson inventory "$inventory_json" '
      .schemaVersion == 1 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and .sourceRevision == $revision and
      (.publicationGeneration | numbers | . >= 1 and floor == .) and .phaseStatus == "passed" and .workload == $inventory.deployment.workload and
      (keys | sort) == (["schemaVersion","publicationGeneration","runId","mode","inventorySha256","sourceRevision","workload",
        "phaseStatus","startedAt","updatedAt","completedAt","targets"] | sort) and
      (.startedAt | type == "string" and length > 0) and (.updatedAt | type == "string" and length > 0) and
      (.completedAt | type == "string" and length > 0) and
      (.targets | type == "array" and ([.[].target] | sort) == ([$inventory.cameraAgents[].name] | sort) and
        length == ([.[].target] | unique | length) and all(.[];
          (keys | sort) == (["target","deviceId","status","profile","captureSequence","checks","artifacts","telemetry"] | sort) and
          .status == "passed" and (.deviceId | type == "string" and length > 0) and (.profile | type == "object") and
          (.captureSequence | type == "object") and (.checks | type == "object" and all(.[]; . == true)) and
          (.artifacts | type == "array") and (.telemetry | type == "object")))
    ' "$path" >/dev/null 2>&1 || { deploy_fail acceptance-init smoke invalid-ledger; return 1; }
}

# Unlike phase recovery, prerequisite validation must never repair or rewrite smoke.
deploy_acceptance_require_committed_smoke() {
    local state_dir="$1" evidence_dir="$2" run_id="$3" mode="$4" hash="$5" revision="$6" inventory_json="$7"
    local ledger="$state_dir/smoke-ledger.json" manifest="$state_dir/smoke-manifest.json" evidence="$evidence_dir/smoke.json"
    local commit="$state_dir/smoke-commit.json" ledger_json expected_commit path
    for path in "$ledger" "$manifest" "$evidence" "$commit"; do
        deploy_phase_file_safe "$path" || { deploy_fail acceptance-init smoke unsafe-or-incomplete; return 1; }
    done
    ledger_json="$(jq -c . "$ledger" 2>/dev/null)" || { deploy_fail acceptance-init smoke invalid-ledger-json; return 1; }
    deploy_acceptance_validate_smoke_candidate "$ledger" "$run_id" "$mode" "$hash" "$revision" "$inventory_json" || return 1
    expected_commit="$(deploy_phase_commit_json "$ledger_json")" || { deploy_fail acceptance-init smoke invalid-generation; return 1; }
    jq -e '(keys | sort) == (["schemaVersion","generation","ledgerSha256"] | sort) and .schemaVersion == 1 and
      (.generation | numbers | . >= 1 and floor == .) and (.ledgerSha256 | test("^[0-9a-f]{64}$"))' "$commit" >/dev/null 2>&1 ||
      { deploy_fail acceptance-init smoke invalid-commit; return 1; }
    [[ "$(jq -S -c . "$commit" 2>/dev/null)" == "$(jq -S -c . <<< "$expected_commit")" ]] ||
      { deploy_fail acceptance-init smoke commit-mismatch; return 1; }
    for path in "$manifest" "$evidence"; do
        [[ "$(jq -S -c . "$path" 2>/dev/null)" == "$(jq -S -c . <<< "$ledger_json")" ]] ||
          { deploy_fail acceptance-init smoke mirror-mismatch; return 1; }
    done
}

deploy_acceptance_validate_candidate() {
    local path="$1" run_id="$2" mode="$3" hash="$4" revision="$5" tree="$6" expected="$7"
    jq -e --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg tree "$tree" \
      --argjson expected "$expected" '
      .schemaVersion == 2 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and
      .sourceRevision == $revision and .sourceTree == $tree and (.publicationGeneration | numbers) >= 1 and
      (.phaseStatus == "running" or .phaseStatus == "passed") and .campaignStatus == "not-run" and
      ((keys - ["completedAt"] | sort) == (["schemaVersion","publicationGeneration","runId","mode","inventorySha256",
        "sourceRevision","sourceTree","campaignManifest","topology","workloadIdentities","campaignStatus",
        "classifications","scenarios","phaseStatus","startedAt","updatedAt"] | sort)) and
      {campaignManifest,topology,workloadIdentities,classifications,scenarios} == $expected and
      all(.classifications[]; .status == "not-run") and
      all(.scenarios[]; .status == "not-run" and .artifact.byteLength == null and .artifact.sha256 == null)
    ' "$path" >/dev/null 2>&1 || { deploy_fail acceptance-init ledger invalid-or-tampered; return 1; }
}

deploy_run_acceptance() {
    local inventory="$1" run_id="$2" mode="$3" hash="$4" revision="$5" worktree="$6" tree="$7"
    local state_dir evidence_dir inventory_json inventory_hash campaign_path workloads_path campaign_json workloads_json campaign_hash workload_hash expected now profile source actual
    state_dir="$(dirname "$DEPLOY_MANIFEST")"; evidence_dir="$(dirname "$DEPLOY_EVIDENCE")"
    [[ "$worktree" == clean ]] || { deploy_fail acceptance-init source dirty-worktree-rejected; return 1; }
    deploy_require_resume_match "$DEPLOY_MANIFEST" "$run_id" "$mode" "$hash" "$revision" clean || return 1
    inventory_json="$(jq -S -c . "$inventory" 2>/dev/null)" || { deploy_fail acceptance-init inventory invalid-json; return 1; }
    inventory_hash="$(printf '%s\n' "$inventory_json" | sha256sum)"; inventory_hash="${inventory_hash%% *}"
    [[ "$inventory_hash" == "$hash" ]] || { deploy_fail acceptance-init inventory hash-mismatch; return 1; }
    deploy_acceptance_require_committed_smoke "$state_dir" "$evidence_dir" "$run_id" "$mode" "$hash" "$revision" "$inventory_json" || return 1

    deploy_acceptance_verify_source_snapshot "$revision" "$tree" || return 1
    campaign_path="deploy/split-host/acceptance/phase14-scenarios.json"
    workloads_path="deploy/split-host/workloads/canonical-workloads.json"
    campaign_json="$(git -C "$REPO_ROOT" show "${revision}:${campaign_path}" 2>/dev/null | jq -c . 2>/dev/null)" ||
      { deploy_fail acceptance-init campaign-manifest committed-blob-unavailable; return 1; }
    workloads_json="$(git -C "$REPO_ROOT" show "${revision}:${workloads_path}" 2>/dev/null | jq -c . 2>/dev/null)" ||
      { deploy_fail acceptance-init workloads committed-blob-unavailable; return 1; }
    deploy_acceptance_validate_repository_manifest_json "$campaign_json" || return 1
    jq -e '.schemaVersion == 1 and (.profiles | keys | sort) == ["W0","W1","W2"]' <<< "$workloads_json" >/dev/null 2>&1 ||
      { deploy_fail acceptance-init workloads invalid; return 1; }
    while IFS= read -r profile; do
        source="$(jq -r --arg profile "$profile" '.profiles[$profile].sourcePath' <<< "$workloads_json")"
        [[ "$source" =~ ^[A-Za-z0-9._/-]+$ && "$source" != /* && "$source" != *../* &&
           "$(git -C "$REPO_ROOT" cat-file -t "${revision}:${source}" 2>/dev/null)" == blob ]] ||
          { deploy_fail acceptance-init workloads unsafe-source; return 1; }
        actual="$(git -C "$REPO_ROOT" show "${revision}:${source}" | sha256sum)"; actual="${actual%% *}"
        [[ "$actual" == "$(jq -r --arg profile "$profile" '.profiles[$profile].sourceSha256' <<< "$workloads_json")" ]] ||
          { deploy_fail acceptance-init workloads source-sha256-mismatch; return 1; }
    done < <(jq -r '.profiles | keys[]' <<< "$workloads_json")

    campaign_hash="$(jq -S -c . <<< "$campaign_json" | sha256sum)"; campaign_hash="${campaign_hash%% *}"
    workload_hash="$(jq -S -c . <<< "$workloads_json" | sha256sum)"; workload_hash="${workload_hash%% *}"
    expected="$(deploy_acceptance_contract_json "$inventory_json" "$campaign_json" "$workloads_json" "$campaign_hash" "$workload_hash")" || return 1
    DEPLOY_ACCEPTANCE_LEDGER="$state_dir/acceptance-ledger.json"
    DEPLOY_ACCEPTANCE_MANIFEST="$state_dir/acceptance-manifest.json"
    DEPLOY_ACCEPTANCE_EVIDENCE="$evidence_dir/acceptance-index.json"
    DEPLOY_ACCEPTANCE_COMMIT="$state_dir/acceptance-commit.json"
    deploy_require_no_orphan_phase_files "$DEPLOY_ACCEPTANCE_LEDGER" "$DEPLOY_ACCEPTANCE_MANIFEST" \
      "$DEPLOY_ACCEPTANCE_EVIDENCE" "$DEPLOY_ACCEPTANCE_COMMIT" || return 1

    if [[ -e "$DEPLOY_ACCEPTANCE_LEDGER" || -L "$DEPLOY_ACCEPTANCE_LEDGER" ]]; then
        deploy_acceptance_verify_source_snapshot "$revision" "$tree" || return 1
        deploy_require_phase_files_match "$DEPLOY_ACCEPTANCE_LEDGER" "$DEPLOY_ACCEPTANCE_MANIFEST" "$DEPLOY_ACCEPTANCE_EVIDENCE" \
          "$DEPLOY_ACCEPTANCE_COMMIT" deploy_acceptance_validate_candidate "$run_id" "$mode" "$hash" "$revision" "$tree" "$expected" || return 1
        deploy_acceptance_verify_source_snapshot "$revision" "$tree" || return 1
        if [[ "$(jq -r '.phaseStatus' "$DEPLOY_ACCEPTANCE_LEDGER")" == passed ]]; then return 0; fi
        DEPLOY_ACCEPTANCE_JSON="$(jq -c --arg now "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
          '.phaseStatus="passed" | .updatedAt=$now | .completedAt=$now' "$DEPLOY_ACCEPTANCE_LEDGER")"
        deploy_acceptance_verify_source_snapshot "$revision" "$tree" || return 1
        deploy_acceptance_publish || return 1
        deploy_acceptance_verify_source_snapshot "$revision" "$tree"
        return
    fi

    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    DEPLOY_ACCEPTANCE_JSON="$(jq -cn --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" \
      --arg tree "$tree" --arg now "$now" --argjson expected "$expected" '
      {schemaVersion:2,publicationGeneration:0,runId:$run,mode:$mode,inventorySha256:$hash,sourceRevision:$revision,
       sourceTree:$tree} + $expected + {campaignStatus:"not-run",phaseStatus:"running",startedAt:$now,updatedAt:$now}')"
    deploy_acceptance_verify_source_snapshot "$revision" "$tree" || return 1
    deploy_acceptance_publish || return 1
    deploy_acceptance_verify_source_snapshot "$revision" "$tree" || return 1
    DEPLOY_ACCEPTANCE_JSON="$(jq -c --arg now "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
      '.phaseStatus="passed" | .updatedAt=$now | .completedAt=$now' <<< "$DEPLOY_ACCEPTANCE_JSON")"
    deploy_acceptance_verify_source_snapshot "$revision" "$tree" || return 1
    deploy_acceptance_publish || return 1
    deploy_acceptance_verify_source_snapshot "$revision" "$tree"
}
