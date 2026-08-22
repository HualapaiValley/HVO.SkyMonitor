#!/usr/bin/env bash

deploy_fail() {
    local stage="$1" check="$2" reason="$3"
    printf 'stage=%s check=%s status=failed reason=%s\n' "$stage" "$check" "$reason" >&2
    return 1
}

deploy_status() {
    printf 'stage=%s check=%s status=%s reason=%s\n' "$1" "$2" "$3" "$4"
}

deploy_require_commands() {
    local name
    for name in "$@"; do
        command -v "$name" >/dev/null || deploy_fail validate tools "required-tool-unavailable" || return 1
    done
}

deploy_is_safe_name() {
    [[ "$1" =~ ^[a-z0-9][a-z0-9-]{0,31}$ ]]
}

deploy_normalize_docker_architecture() {
    case "$1" in
        amd64|x86_64) printf 'amd64\n' ;;
        arm64|aarch64) printf 'arm64\n' ;;
        *) return 1 ;;
    esac
}

deploy_is_safe_absolute_path() {
    local path="$1"
    [[ "$path" == /* && "$path" != / && "$path" != *//* && "$path" != */./* &&
       "$path" != */../* && "$path" != */. && "$path" != */.. ]]
}

deploy_validate_private_file() {
    local path="$1" mode owner links
    [[ -f "$path" && ! -L "$path" ]] || deploy_fail validate secret-source "unsafe-secret-source" || return 1
    if ! owner="$(stat -c '%u' -- "$path" 2>/dev/null)" ||
       ! mode="$(stat -c '%a' -- "$path" 2>/dev/null)" ||
       ! links="$(stat -c '%h' -- "$path" 2>/dev/null)"; then
        deploy_fail validate secret-source "secret-source-stat-failed"
        return 1
    fi
    [[ "$owner" == "$(id -u)" && "$links" == 1 && ( "$mode" == 600 || "$mode" == 400 ) ]] ||
        deploy_fail validate secret-source "secret-source-must-be-owner-only" || return 1
}

deploy_validate_output_root() {
    local path="$1" label="$2" current mode
    deploy_is_safe_absolute_path "$path" || deploy_fail validate "$label" "unsafe-output-root" || return 1
    [[ "$(realpath -m -- "$path" 2>/dev/null)" == "$path" ]] || deploy_fail validate "$label" "noncanonical-output-root" || return 1
    current="$path"
    while [[ ! -e "$current" && ! -L "$current" ]]; do current="$(dirname -- "$current")"; done
    [[ -d "$current" && ! -L "$current" && "$(stat -c '%u' -- "$current" 2>/dev/null)" == "$(id -u)" ]] ||
        deploy_fail validate "$label" "unsafe-output-ancestor" || return 1
    mode="$(stat -c '%a' -- "$current" 2>/dev/null)" || { deploy_fail validate "$label" "output-ancestor-stat-failed"; return 1; }
    (( (8#$mode & 0022) == 0 )) || deploy_fail validate "$label" "writable-output-ancestor" || return 1
    if [[ -e "$path" || -L "$path" ]]; then
        [[ -d "$path" && ! -L "$path" && "$(stat -c '%u' -- "$path" 2>/dev/null)" == "$(id -u)" &&
           "$(stat -c '%a' -- "$path" 2>/dev/null)" == 700 ]] || deploy_fail validate "$label" "output-root-must-be-owner-only" || return 1
    fi
}

deploy_capture_queues_converged() {
    local operations="$1" end_sequence="$2" device="$3" expected_mode="$4"
    [[ "$end_sequence" =~ ^[0-9]+$ && -n "$device" && ( "$expected_mode" == Off || "$expected_mode" == Hybrid ) ]] || return 1
    jq -e --argjson endSequence "$end_sequence" --arg device "$device" --arg expectedMode "$expected_mode" '
      # Pending identities are the complete active-center projection. Record counts may fan out
      # within the causal bounds only after every unrelated lane and active worker/queue is excluded.
      def integer: type == "number" and . >= 0 and floor == .;
      def queue_shape:
        type == "object" and
        all(.pendingCount,.leasedCount,.retryCount,.quarantineCount,.terminalCount; integer);
      def lane_shape:
        type == "object" and (.name | type) == "string" and (.name | length) > 0 and
        (.required | type) == "boolean" and
        all(.pendingCount,.leasedCount,.retryCount,.quarantineCount,.pressureLevel; integer) and
        (.pendingCaptures | type) == "array" and all(.pendingCaptures[];
          (keys | sort) == (["agentId","captureSequence"] | sort) and
          (.agentId | type) == "string" and (.agentId | length) > 0 and (.captureSequence | integer));
      try ((if $endSequence < 2 then $endSequence else 2 end) as $tail |
      .transientWorker.value.maximumCandidates as $maximumCandidates |
      ($tail * 3) as $maximumRawIngressRecords |
      ($tail * (1 + $maximumCandidates)) as $maximumTransientRecords |
      [.captureLanes.value.lanes[] | select(.name == "transient" and .required == true)] as $transient |
      (.rawIngress.value | queue_shape) and (.captureProcessing.value | queue_shape) and
      (.artifactOutbox.value | queue_shape) and
      (.captureLanes.value | type) == "object" and (.captureLanes.value.lanes | type) == "array" and
      ([.captureLanes.value.lanes[].name] | length == (unique | length)) and
      all(.captureLanes.value.lanes[]; lane_shape) and
      all(.captureLanes.value.pendingCount,.captureLanes.value.leasedCount,
        .captureLanes.value.retryCount,.captureLanes.value.quarantineCount; integer) and
      ($maximumCandidates | integer) and $maximumCandidates >= 1 and $maximumCandidates <= 64 and
      (.transientWorker.value.pendingFrames | integer) and (.transientWorker.value.pendingCandidates | integer) and
      .rawIngress.value.availability == "Accepting" and .captureLanes.value.availability == "Healthy" and
      .captureProcessing.value.availability == "Healthy" and .artifactOutbox.value.availability == "Healthy" and
      .rawIngress.value.leasedCount == 0 and .rawIngress.value.retryCount == 0 and
      .rawIngress.value.quarantineCount == 0 and .rawIngress.value.terminalCount == 0 and
      .captureLanes.value.leasedCount == 0 and .captureLanes.value.retryCount == 0 and
      .captureLanes.value.quarantineCount == 0 and
      .captureProcessing.value.pendingCount == 0 and .captureProcessing.value.leasedCount == 0 and
      .captureProcessing.value.retryCount == 0 and .captureProcessing.value.quarantineCount == 0 and
      .captureProcessing.value.terminalCount == 0 and
      .artifactOutbox.value.pendingCount == 0 and .artifactOutbox.value.leasedCount == 0 and
      .artifactOutbox.value.retryCount == 0 and .artifactOutbox.value.quarantineCount == 0 and
      .artifactOutbox.value.terminalCount == 0 and
      .captureLanes.value.pendingCount == ([.captureLanes.value.lanes[].pendingCount] | add // 0) and
      .captureLanes.value.leasedCount == ([.captureLanes.value.lanes[].leasedCount] | add // 0) and
      .captureLanes.value.retryCount == ([.captureLanes.value.lanes[].retryCount] | add // 0) and
      .captureLanes.value.quarantineCount == ([.captureLanes.value.lanes[].quarantineCount] | add // 0) and
      if $expectedMode == "Off" then
        ($transient | length) == 0 and
        .transientWorker.value.availability == "Disabled" and
        .transientWorker.value.pendingFrames == 0 and .transientWorker.value.pendingCandidates == 0 and
        .rawIngress.value.pendingCount == 0 and .captureLanes.value.pendingCount == 0 and
        all(.captureLanes.value.lanes[];
          .pendingCount == 0 and .leasedCount == 0 and .retryCount == 0 and .quarantineCount == 0 and
          .pressureLevel == 0 and (.pendingCaptures | length) == 0)
      else
        ($transient | length) == 1 and
        .captureLanes.value.pendingCount == $transient[0].pendingCount and
        .rawIngress.value.pendingCount >= $tail and
        .rawIngress.value.pendingCount <= $maximumRawIngressRecords and
        $transient[0].pendingCount >= $tail and
        $transient[0].pendingCount <= $maximumTransientRecords and
        ($transient[0].leasedCount == 0 and
          $transient[0].retryCount == 0 and $transient[0].quarantineCount == 0 and $transient[0].pressureLevel == 0) and
        $transient[0].pendingCaptures == [range($endSequence - $tail + 1; $endSequence + 1) |
          {agentId:$device,captureSequence:.}] and
        all(.captureLanes.value.lanes[] | select(.name != "transient");
          .pendingCount == 0 and .leasedCount == 0 and .retryCount == 0 and .quarantineCount == 0 and
          .pressureLevel == 0 and (.pendingCaptures | length) == 0) and
        .transientWorker.value.availability == "Healthy" and
        .transientWorker.value.pendingFrames == 0 and .transientWorker.value.pendingCandidates == 0
      end) catch false' "$operations" >/dev/null 2>&1
}
