#!/usr/bin/env bash

deploy_transient_confirm_validate_allowlist() {
    local path="$1" run_id="$2"
    deploy_validate_private_file "$path" || return 1
    jq -e --arg run "$run_id" '
      (keys | sort) == (["events","runId","schemaVersion"] | sort) and .schemaVersion == 1 and .runId == $run and
      (.events | type == "array" and length >= 1 and length <= 32 and
       length == ([.[].centralTransientEventId | ascii_downcase] | unique | length) and
       all(.[]; (keys | sort) == (["agentId","centralTransientEventId"] | sort) and
         (.centralTransientEventId | test("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")) and
         (.agentId | type == "string" and length >= 1 and length <= 128)))' "$path" >/dev/null 2>&1 ||
      { deploy_fail transient-confirm allowlist invalid; return 1; }
}

deploy_transient_confirm_validate_evidence() {
    local path="$1" run_id="$2" hash="$3" revision="$4" allowlist_hash="$5" allowlist="$6"
    deploy_phase_file_safe "$path" || return 1
    jq -e --arg run "$run_id" --arg hash "$hash" --arg revision "$revision" --arg allowlistHash "$allowlist_hash" \
      --argjson expected "$(jq -c '[.events[] | {centralTransientEventId:(.centralTransientEventId | ascii_downcase),agentId}] | sort_by(.centralTransientEventId)' "$allowlist")" '
      (keys | sort) == (["allowlistSha256","completedAt","events","inventorySha256","mode","phaseStatus","runId","schemaVersion","sourceRevision","startedAt"] | sort) and
      .schemaVersion == 1 and .runId == $run and .mode == "isolated" and .inventorySha256 == $hash and
      .sourceRevision == $revision and .allowlistSha256 == $allowlistHash and .phaseStatus == "passed" and
      (.startedAt | type == "string") and (.completedAt | type == "string") and
      (.events | type == "array" and length == ($expected | length) and all(.[];
        (keys | sort) == (["agentId","centralTransientEventId","mailpitMessageId","messageSnippetSha256","notificationId","recipientSha256","requestSha256","reviewId"] | sort) and
        (.centralTransientEventId | test("^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$")) and
        (.reviewId | test("^[0-9a-fA-F-]{36}$")) and (.notificationId | test("^[0-9a-fA-F-]{36}$")) and
        (.mailpitMessageId | type == "string" and length >= 1 and length <= 256) and
        all(.requestSha256,.recipientSha256,.messageSnippetSha256; test("^[0-9a-f]{64}$")))) and
      ([.events[] | {centralTransientEventId,agentId}] | sort_by(.centralTransientEventId)) == $expected' "$path" >/dev/null 2>&1
}

deploy_transient_confirm_detail_eligible() {
    local path="$1" id="$2" agent="$3" marker="$4"
    jq -e --arg id "$id" --arg agent "$agent" --arg marker "$marker" '
      (.summary.centralTransientEventId | ascii_downcase) == $id and .summary.agentId == $agent and
      (.summary.reviewState == 0 or (.summary.reviewState == 1 and .event.reviews[-1].assessmentId == .summary.activeAssessmentId and
        .event.reviews[-1].disposition == 1 and (.event.reviews[-1].reasonCodes | index($marker) != null))) and
      .summary.effectiveClassification == 1 and
      (.summary.effectiveMeteorSeverity == 0 or .summary.effectiveMeteorSeverity == 1) and
      (.summary.activeAssessmentId | test("^[0-9a-fA-F-]{36}$")) and (.summary.eTag | test("^\"[A-Za-z0-9_-]+\"$"))' "$path" >/dev/null 2>&1
}

deploy_transient_confirm_audit_ready() {
    local path="$1" review_id="$2" assessment_id="$3" marker="$4"
    jq -e --arg review "$review_id" --arg assessment "$assessment_id" --arg marker "$marker" '
      ([.event.reviews[]? | select(.reviewId == $review and .assessmentId == $assessment and
        .disposition == 1 and .reviewerIdentity == "redacted" and (.reasonCodes | index($marker) != null))] |
        if length == 1 then .[0] else null end) as $selected |
      .summary.reviewState == 1 and $selected != null and
      any(.event.notifications[]?; .assessmentId == $assessment and .state == 1 and .createdUtc >= $selected.createdUtc)' "$path" >/dev/null 2>&1
}

deploy_transient_confirm_mailpit_id() {
    local path="$1" baseline_ids="$2" id="$3" recipient="$4" started="$5" resumed="$6"
    jq -r --argjson old "$baseline_ids" --argjson resumed "$resumed" --arg id "$id" --arg recipient "$recipient" --arg started "$started" '
      [.messages[] | select(($resumed or (.iD as $candidate | $old | index($candidate) | not)) and .subject == "SkyMonitor meteor review" and
        .created >= $started and any(.to[]?; .address == $recipient) and (.snippet | contains($id)))] |
      if length == 1 then .[0].iD else empty end' "$path"
}

deploy_transient_confirm_get() {
    local target="$1" endpoint="$2" headers="$3" remote_root="$4" private_root="$5" id="$6" suffix="$7"
    local status output
    output="$private_root/$id-$suffix.json"
    status="$(deploy_bootstrap_request "$target" GET "$endpoint/api/v1.0/transient-events/$id" "" "$headers" "" \
      "$remote_root/$id-$suffix.json" "$output")" || return 1
    deploy_transport_remove_private_files "$(jq -r '.sshHost' <<< "$target")" "$remote_root/$id-$suffix.json" || return 1
    [[ "$status" == 200 ]] || { deploy_fail transient-confirm "$id" detail-http-$status; return 1; }
    printf '%s\n' "$output"
}

deploy_transient_confirm_mailpit() {
    local shared="$1" port="$2" remote_root="$3" private_root="$4" suffix="$5" status output
    output="$private_root/mailpit-$suffix.json"
    status="$(deploy_bootstrap_request "$shared" GET "http://127.0.0.1:$port/api/v1/messages" "" "" "" \
      "$remote_root/mailpit-$suffix.json" "$output")" || return 1
    deploy_transport_remove_private_files "$(jq -r '.sshHost' <<< "$shared")" "$remote_root/mailpit-$suffix.json" || return 1
    [[ "$status" == 200 ]] || { deploy_fail transient-confirm mailpit list-http-$status; return 1; }
    printf '%s\n' "$output"
}

deploy_transient_confirm_cleanup() {
    local disposition="${1:-strict}" status=0
    if [[ -n "${DEPLOY_TRANSIENT_LOGIC_SSH:-}" && -n "${DEPLOY_TRANSIENT_LOGIC_REMOTE:-}" ]]; then
        deploy_transport_remove_campaign_directory "$DEPLOY_TRANSIENT_LOGIC_SSH" "$DEPLOY_TRANSIENT_LOGIC_REMOTE" || status=1
    fi
    if [[ -n "${DEPLOY_TRANSIENT_SHARED_SSH:-}" && -n "${DEPLOY_TRANSIENT_SHARED_REMOTE:-}" ]]; then
        deploy_transport_remove_campaign_directory "$DEPLOY_TRANSIENT_SHARED_SSH" "$DEPLOY_TRANSIENT_SHARED_REMOTE" || status=1
    fi
    [[ -z "${DEPLOY_TRANSIENT_RENDER_ROOT:-}" ]] || rm -rf -- "$DEPLOY_TRANSIENT_RENDER_ROOT"
    [[ -z "${DEPLOY_TRANSIENT_PRIVATE_ROOT:-}" ]] || rm -rf -- "$DEPLOY_TRANSIENT_PRIVATE_ROOT"
    deploy_transport_reconcile_private_uploads "$disposition" || status=1
    [[ "$disposition" == best-effort ]] || return "$status"
}

deploy_run_transient_confirm() {
    local inventory="$1" run_id="$2" mode="$3" hash="$4" revision="$5" worktree="$6" allowlist="$7"
    local state_dir evidence_dir runtime_root render_root private_root logic logic_root logic_remote endpoint headers shared shared_root shared_remote
    local allowlist_hash baseline baseline_ids now event id expected_agent detail etag assessment review_key body request_hash review_headers status response post
    local deadline after review_id notification_id mailpit_id recipient recipient_hash body_hash completed evidence campaign_state resumed_review events='[]'
    [[ "$mode" == isolated ]] || { deploy_fail transient-confirm mode isolated-required; return 1; }
    [[ "$(jq -r '.deployment.services.mode + ":" + .deployment.services.smtp.kind' "$inventory")" == deploy:mailpit ]] ||
      { deploy_fail transient-confirm mailpit isolated-mailpit-required; return 1; }
    deploy_transient_confirm_validate_allowlist "$allowlist" "$run_id" || return 1
    deploy_require_passed_phase "$(dirname "$DEPLOY_MANIFEST")/bootstrap-manifest.json" transient-confirm "$run_id" "$mode" "$hash" "$revision" || return 1
    deploy_require_resume_match "$DEPLOY_MANIFEST" "$run_id" "$mode" "$hash" "$revision" "$worktree" || return 1
    # shellcheck disable=SC2034 # Shared phase state read by scripts/deploy/bootstrap.sh.
    DEPLOY_IMAGES_PREFLIGHT_JSON="$(jq -c . "$DEPLOY_MANIFEST")"
    state_dir="$(dirname "$DEPLOY_MANIFEST")"; evidence_dir="$(dirname "$DEPLOY_EVIDENCE")"
    runtime_root="$state_dir/transient-confirm"; render_root="$runtime_root/rendered"; private_root="$runtime_root/private"
    install -d -m 700 -- "$runtime_root" "$render_root" "$private_root"
    DEPLOY_TRANSIENT_RENDER_ROOT="$render_root"; DEPLOY_TRANSIENT_PRIVATE_ROOT="$private_root"
    evidence="$evidence_dir/transient-confirm.json"
    allowlist_hash="$(jq -S -c . "$allowlist" | sha256sum)"; allowlist_hash="${allowlist_hash%% *}"
    logic="$(jq -c '.logicHost' "$inventory")"; logic_root="$(jq -r '.runtimeRoot' <<< "$logic")"; logic_remote="$logic_root/.hvo-deploy/campaign-$run_id"
    shared="$(jq -c '.sharedServices' "$inventory")"; shared_root="$(jq -r '.runtimeRoot' <<< "$shared")"; shared_remote="$shared_root/.hvo-deploy/campaign-$run_id"
    DEPLOY_TRANSIENT_LOGIC_SSH="$(jq -r '.sshHost' <<< "$logic")"; DEPLOY_TRANSIENT_LOGIC_REMOTE="$logic_remote"
    DEPLOY_TRANSIENT_SHARED_SSH="$(jq -r '.sshHost' <<< "$shared")"; DEPLOY_TRANSIENT_SHARED_REMOTE="$shared_remote"
    if [[ -e "$evidence" || -L "$evidence" ]]; then
        deploy_transient_confirm_validate_evidence "$evidence" "$run_id" "$hash" "$revision" "$allowlist_hash" "$allowlist" ||
          { deploy_fail transient-confirm evidence invalid-or-conflicting; return 1; }
        deploy_transient_confirm_cleanup strict
        return
    fi
    campaign_state="$runtime_root/state.json"
    if [[ -e "$campaign_state" || -L "$campaign_state" ]]; then
        deploy_phase_file_safe "$campaign_state" && jq -e --arg run "$run_id" --arg allowlist "$allowlist_hash" \
          '.schemaVersion == 1 and .runId == $run and .allowlistSha256 == $allowlist and (.startedAt | type == "string")' "$campaign_state" >/dev/null 2>&1 ||
          { deploy_fail transient-confirm state invalid-or-conflicting; return 1; }
        now="$(jq -r '.startedAt' "$campaign_state")"
    else
        now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        deploy_publish_json "$campaign_state" "$(jq -cn --arg run "$run_id" --arg allowlist "$allowlist_hash" --arg started "$now" \
          '{schemaVersion:1,runId:$run,allowlistSha256:$allowlist,startedAt:$started}')" || return 1
    fi
    endpoint="$(jq -r '.internalEndpoint' <<< "$logic")"; headers="$logic_remote/owner.headers"
    deploy_transport_remote_directories "$(jq -r '.sshHost' <<< "$logic")" "$logic_remote" || return 1
    deploy_transport_remote_directories "$(jq -r '.sshHost' <<< "$shared")" "$shared_remote" || return 1
    deploy_bootstrap_central_headers "$inventory" "$logic" "$render_root" "$headers" || return 1
    baseline="$(deploy_transient_confirm_mailpit "$shared" "$(jq -r '.deployment.services.smtp.ports[1]' "$inventory")" "$shared_remote" "$private_root" baseline)" || return 1
    baseline_ids="$(jq -c '[.messages[]?.iD]' "$baseline")" || return 1

    # Complete every direct-ID eligibility check before the first append-only review mutation.
    while IFS= read -r event; do
        id="$(jq -r '.centralTransientEventId | ascii_downcase' <<< "$event")"; expected_agent="$(jq -r '.agentId' <<< "$event")"
        detail="$(deploy_transient_confirm_get "$logic" "$endpoint" "$headers" "$logic_remote" "$private_root" "$id" preflight)" || return 1
        deploy_transient_confirm_detail_eligible "$detail" "$id" "$expected_agent" "synthetic-campaign-$run_id" ||
          { deploy_fail transient-confirm "$id" ineligible-or-not-synthetic; return 1; }
    done < <(jq -c '.events[]' "$allowlist")

    while IFS= read -r event; do
        id="$(jq -r '.centralTransientEventId | ascii_downcase' <<< "$event")"; detail="$private_root/$id-preflight.json"
        etag="$(jq -r '.summary.eTag' "$detail")"; assessment="$(jq -r '.summary.activeAssessmentId' "$detail")"
        review_key="synthetic.$run_id.${id//-/}"; review_key="${review_key:0:128}"
        body="$render_root/$id-review.json"
        jq -S -n --arg assessment "$assessment" --arg marker "synthetic-campaign-$run_id" \
          '{AssessmentId:$assessment,Disposition:1,Override:null,ReasonCodes:[$marker]}' > "$body"
        chmod 600 "$body"; request_hash="$(jq -S -c . "$body" | sha256sum)"; request_hash="${request_hash%% *}"
        resumed_review=false
        if [[ "$(jq -r '.summary.reviewState' "$detail")" == 0 ]]; then
            deploy_bootstrap_stage_json "$logic" "$body" "$logic_remote/$id-review.json" || return 1
            review_headers="$logic_remote/$id-review-control.headers"
            deploy_bootstrap_register_private_remote "$logic" "$review_headers" || return 1
            deploy_transport_derive_review_headers "$(jq -r '.sshHost' <<< "$logic")" "$headers" "$review_headers" "$review_key" "$etag" || return 1
            response="$private_root/$id-review-response.json"
            status="$(deploy_bootstrap_request "$logic" POST "$endpoint/api/v1.0/transient-events/$id/reviews" "$logic_remote/$id-review.json" \
              "$review_headers" "" "$logic_remote/$id-review-response.json" "$response")" || return 1
            deploy_transport_remove_private_files "$(jq -r '.sshHost' <<< "$logic")" "$logic_remote/$id-review.json" "$logic_remote/$id-review-response.json" || return 1
            [[ "$status" == 200 ]] || { deploy_fail transient-confirm "$id" review-http-$status; return 1; }
            review_id="$(jq -er '.reviewId | select(test("^[0-9a-fA-F-]{36}$"))' "$response")" || return 1
            deploy_transport_remove_private_files "$(jq -r '.sshHost' <<< "$logic")" "$review_headers" || return 1
            deploy_transport_forget_private_path "$review_headers" || return 1
        else
            resumed_review=true
            review_id="$(jq -er '.event.reviews[-1].reviewId | select(test("^[0-9a-fA-F-]{36}$"))' "$detail")" || return 1
        fi
        deadline=$(( $(date +%s) + 120 )); after=
        while (( $(date +%s) <= deadline )); do
            after="$(deploy_transient_confirm_get "$logic" "$endpoint" "$headers" "$logic_remote" "$private_root" "$id" after)" || return 1
            if deploy_transient_confirm_audit_ready "$after" "$review_id" "$assessment" "synthetic-campaign-$run_id"; then break; fi
            sleep "${DEPLOY_TEST_POLL_SECONDS:-2}"
        done
        deploy_transient_confirm_audit_ready "$after" "$review_id" "$assessment" "synthetic-campaign-$run_id" ||
          { deploy_fail transient-confirm "$id" audit-or-notification-timeout; return 1; }
        notification_id="$(jq -r --arg review "$review_id" --arg assessment "$assessment" '
          ([.event.reviews[] | select(.reviewId == $review)][0].createdUtc) as $reviewed |
          [.event.notifications[] | select(.assessmentId == $assessment and .state == 1 and .createdUtc >= $reviewed)][-1].notificationId' "$after")"
        recipient="split-host-owner@hvo.local"; recipient_hash="$(printf '%s' "$recipient" | sha256sum)"; recipient_hash="${recipient_hash%% *}"
        deadline=$(( $(date +%s) + 120 )); mailpit_id=
        while (( $(date +%s) <= deadline )); do
            post="$(deploy_transient_confirm_mailpit "$shared" "$(jq -r '.deployment.services.smtp.ports[1]' "$inventory")" "$shared_remote" "$private_root" post)" || return 1
            mailpit_id="$(deploy_transient_confirm_mailpit_id "$post" "$baseline_ids" "$id" "$recipient" "$now" "$resumed_review")" || return 1
            [[ -z "$mailpit_id" ]] || break
            sleep "${DEPLOY_TEST_POLL_SECONDS:-2}"
        done
        [[ -n "$mailpit_id" ]] || { deploy_fail transient-confirm "$id" mailpit-correlation-timeout; return 1; }
        body_hash="$(jq -r --arg message "$mailpit_id" '.messages[] | select(.iD == $message) | .snippet' "$post" | sha256sum)"; body_hash="${body_hash%% *}"
        events="$(jq -c --arg id "$id" --arg agent "$(jq -r '.agentId' <<< "$event")" --arg review "$review_id" --arg notification "$notification_id" \
          --arg requestHash "$request_hash" --arg mailpit "$mailpit_id" --arg recipientHash "$recipient_hash" --arg bodyHash "$body_hash" \
          '. + [{centralTransientEventId:$id,agentId:$agent,reviewId:$review,notificationId:$notification,requestSha256:$requestHash,
            mailpitMessageId:$mailpit,recipientSha256:$recipientHash,messageSnippetSha256:$bodyHash}]' <<< "$events")"
    done < <(jq -c '.events[]' "$allowlist")
    completed="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    deploy_publish_json "$evidence" "$(jq -cn --arg run "$run_id" --arg hash "$hash" --arg revision "$revision" --arg allowlist "$allowlist_hash" \
      --arg started "$now" --arg completed "$completed" --argjson events "$events" \
      '{schemaVersion:1,runId:$run,mode:"isolated",inventorySha256:$hash,sourceRevision:$revision,allowlistSha256:$allowlist,
        phaseStatus:"passed",startedAt:$started,completedAt:$completed,events:$events}')" || return 1
    deploy_transient_confirm_cleanup strict
}
