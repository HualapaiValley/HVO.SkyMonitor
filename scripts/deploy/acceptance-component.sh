#!/usr/bin/env bash

readonly ACCEPTANCE_COMPONENT_FAMILY=logichost-dependencies
readonly ACCEPTANCE_COMPONENT_TEST=HVO.SkyMonitor.IntegrationTests.LogicHostDependencyOutageAcceptanceTests.Issue107_DependenciesDegradeWithoutFabricatedDataAndRecoverWithinBound

deploy_acceptance_component_verify_source() {
    local revision="$1" tree="$2" head current_tree status
    head="$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null)" || { deploy_fail acceptance-component source head-unavailable; return 1; }
    current_tree="$(git -C "$REPO_ROOT" rev-parse "${head}^{tree}" 2>/dev/null)" ||
      { deploy_fail acceptance-component source tree-unavailable; return 1; }
    status="$(git -C "$REPO_ROOT" status --porcelain --untracked-files=normal 2>/dev/null)" ||
      { deploy_fail acceptance-component source status-unavailable; return 1; }
    [[ "$head" == "$revision" && "$current_tree" == "$tree" && -z "$status" ]] ||
      { deploy_fail acceptance-component source committed-source-changed; return 1; }
}

deploy_acceptance_component_prepare_directory() {
    local path="$1"
    if [[ -e "$path" || -L "$path" ]]; then
        [[ -d "$path" && ! -L "$path" && "$(stat -c '%u:%a' -- "$path" 2>/dev/null)" == "$(id -u):700" ]] ||
          { deploy_fail acceptance-component directory unsafe; return 1; }
    else
        install -d -m 700 -- "$path" 2>/dev/null || { deploy_fail acceptance-component directory create-failed; return 1; }
    fi
}

deploy_acceptance_component_validate_source() {
    local path="$1" revision="$2"
    [[ -f "$path" && ! -L "$path" && "$(stat -c '%u:%h:%a' -- "$path" 2>/dev/null)" == "$(id -u):1:600" &&
       "$(stat -c %s -- "$path" 2>/dev/null)" -le 262144 ]] ||
      { deploy_fail acceptance-component evidence unsafe-or-too-large; return 1; }
    jq -e --arg revision "$revision" '
      def digest: type == "string" and test("^[0-9A-F]{64}$");
      def timestamp: type == "string" and test("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$");
      .Schema == "hvo-logichost-dependency-outages-v2" and
      (keys | sort) == ["Dependencies","Schema","Source"] and
      (.Source | (keys | sort) == (["RequestedRevision","Head","Branch","Dirty","DirtyDiffSha256","OutputDirectoryName",
        "RunId","Trial","Claimability","Assemblies"] | sort) and
        .RequestedRevision == $revision and .Head == $revision and .Dirty == false and
        (.DirtyDiffSha256 | digest) and .OutputDirectoryName == $revision and .Trial == null and
        .Claimability == "clean-source-attributed-review-required" and
        (.Branch | type == "string" and length <= 255 and (test("[[:cntrl:]]") | not)) and
        (.RunId | type == "string" and test("^run-[0-9]{8}T[0-9]{9}-[0-9]+-[0-9a-f]{32}$")) and
        ([.Assemblies[].Name] == ["HVO.SkyMonitor.IntegrationTests","HVO.SkyMonitor.LogicHost","HVO.SkyMonitor.TestSupport"]) and
        all(.Assemblies[];
          (keys | sort) == (["Name","Sha256","ModuleVersionId","Configuration","InformationalVersion","SourceSha256",
            "AssemblyWrittenUtc","LatestSourceWriteUtc"] | sort) and
          (.Sha256 | digest) and (.SourceSha256 | digest) and .Configuration == "Release" and
          (.ModuleVersionId | type == "string" and test("^[0-9a-fA-F-]{36}$")) and
          (.InformationalVersion | type == "string" and length <= 512 and contains($revision)) and
          (.AssemblyWrittenUtc | type == "string" and length > 0 and length <= 64) and
          (.LatestSourceWriteUtc | type == "string" and length > 0 and length <= 64))) and
      ([.Dependencies[].Dependency] == ["Minio","SqlServer","Redis","Smtp"]) and
      (.Dependencies | length == 4 and all(.[];
        (keys | sort) == (["Dependency","StartedAt","CompletedAt","InitialHealthStatus","OutageStatus","RecoveryStatus",
          "InitialOperationPassed","OutageOperationFailed","RecoveryOperationPassed","OutageMilliseconds","RecoveryMilliseconds",
          "OutageHealthHttpStatus","RecoveryHealthHttpStatus"] | sort) and
        (.StartedAt | timestamp) and (.CompletedAt | timestamp) and (.StartedAt | fromdateiso8601) <= (.CompletedAt | fromdateiso8601) and
        .InitialHealthStatus == "Healthy" and .OutageStatus != "Healthy" and
        (.OutageStatus == "Degraded" or .OutageStatus == "Unhealthy" or .OutageStatus == "Unavailable") and
        .RecoveryStatus == "Healthy" and .InitialOperationPassed == true and .OutageOperationFailed == true and
        .RecoveryOperationPassed == true and (.OutageMilliseconds | numbers) >= 15000 and .OutageMilliseconds <= 480000 and
        (.RecoveryMilliseconds | numbers) >= 0 and .RecoveryMilliseconds <= 60000 and
         (.OutageHealthHttpStatus == null or .OutageHealthHttpStatus == 503) and .RecoveryHealthHttpStatus == 200))
    ' "$path" >/dev/null 2>&1 || { deploy_fail acceptance-component evidence invalid; return 1; }
}

deploy_acceptance_component_build_artifact() {
    local ledger="$1" scenario_id="$2" dependency="$3" source="$4" scenario entry
    scenario="$(jq -c --arg id "$scenario_id" '.scenarios[] | select(.id == $id)' <<< "$ledger")"
    entry="$(jq -c --arg dependency "$dependency" '.Dependencies[] | select(.Dependency == $dependency)' "$source")"
    [[ -n "$scenario" && -n "$entry" ]] || return 1
    jq -S -cn --argjson ledger "$ledger" --argjson scenario "$scenario" --argjson entry "$entry" '
      {schemaVersion:1,runId:$ledger.runId,scenarioId:$scenario.id,inventorySha256:$ledger.inventorySha256,
       sourceRevision:$ledger.sourceRevision,sourceTree:$ledger.sourceTree,classification:$scenario.classification,
       executionClass:$scenario.executionClass,evidenceSource:$scenario.evidenceSource,workloads:$scenario.workloads,
       outcome:"passed",startedAt:$entry.StartedAt,completedAt:$entry.CompletedAt,
       assertions:[
         {id:"initial-health-healthy",passed:($entry.InitialHealthStatus == "Healthy")},
         {id:"initial-operation-succeeded",passed:$entry.InitialOperationPassed},
         {id:"outage-health-unavailable",passed:($entry.OutageStatus != "Healthy")},
         {id:"outage-operation-failed",passed:$entry.OutageOperationFailed},
         {id:"outage-window-observed",passed:($entry.OutageMilliseconds >= 15000)},
         {id:"recovery-health-healthy",passed:($entry.RecoveryStatus == "Healthy")},
         {id:"recovery-operation-succeeded",passed:$entry.RecoveryOperationPassed},
         {id:"recovery-within-bound",passed:($entry.RecoveryMilliseconds <= 60000)}],
       outputs:[],measurements:[
         {id:"outage-duration",value:$entry.OutageMilliseconds,unit:"milliseconds"},
         {id:"recovery-duration",value:$entry.RecoveryMilliseconds,unit:"milliseconds"}]}'
}

deploy_acceptance_component_verify_trx() {
    (exec 201>&-; dotnet run --project "$REPO_ROOT/scripts/acceptance-trx/HVO.SkyMonitor.AcceptanceTrx.csproj" --configuration Release -- "$1")
}

deploy_acceptance_component_execute_test() {
    local evidence_dir="$1" trx_dir="$2" revision="$3"
    (exec 201>&-; env -u HVO_ISSUE_107_DEPENDENCY -u HVO_EVIDENCE_TRIAL HVO_EVIDENCE_REVISION="$revision" HVO_EVIDENCE_REPOSITORY_ROOT="$REPO_ROOT" \
      HVO_ISSUE_107_EVIDENCE_ROOT="$evidence_dir" \
      timeout --signal=TERM --kill-after=30s 600s dotnet test "$REPO_ROOT/tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj" \
        --configuration Release --artifacts-path "$evidence_dir/../build" \
        --filter "TestCategory=Manual&FullyQualifiedName=$ACCEPTANCE_COMPONENT_TEST" \
        --results-directory "$trx_dir" --logger 'trx;LogFileName=component.trx')
}

deploy_acceptance_component_cleanup_stage() {
    local stage="${DEPLOY_ACCEPTANCE_COMPONENT_STAGE:-}" runtime="${DEPLOY_ACCEPTANCE_COMPONENT_RUNTIME:-}"
    [[ -n "$stage" && -n "$runtime" && "$stage" == "$runtime"/.bundle.tmp.* ]] || return 0
    [[ ! -L "$stage" && -d "$stage" && "$(stat -c '%u:%a' -- "$stage" 2>/dev/null)" == "$(id -u):700" ]] || return 1
    rm -rf -- "$stage"
    DEPLOY_ACCEPTANCE_COMPONENT_STAGE=
}

deploy_acceptance_component_file_metadata() {
    local id="$1" relative="$2" root="$3" path length digest
    path="$root/$relative"
    [[ -f "$path" && ! -L "$path" && "$(stat -c '%u:%h:%a' -- "$path" 2>/dev/null)" == "$(id -u):1:600" ]] || return 1
    length="$(stat -c %s -- "$path")" || return 1
    digest="$(sha256sum "$path")"; digest="${digest%% *}"
    jq -cn --arg id "$id" --arg path "$relative" --argjson length "$length" --arg sha "$digest" \
      '{id:$id,relativePath:$path,byteLength:$length,sha256:$sha}'
}

deploy_acceptance_component_validate_bundle() {
    local bundle="$1" run_id="$2" mode="$3" hash="$4" revision="$5" tree="$6" ledger="$7"
    local manifest commit digest file path actual_length actual_sha scenario_id
    [[ -d "$bundle" && ! -L "$bundle" && "$(stat -c '%u:%a' -- "$bundle" 2>/dev/null)" == "$(id -u):700" ]] ||
      { deploy_fail acceptance-component bundle unsafe; return 1; }
    [[ -d "$bundle/artifacts" && ! -L "$bundle/artifacts" && "$(stat -c '%u:%a' -- "$bundle/artifacts" 2>/dev/null)" == "$(id -u):700" ]] ||
      { deploy_fail acceptance-component bundle unsafe; return 1; }
    local -a root_entries artifact_entries
    shopt -s nullglob dotglob
    root_entries=("$bundle"/*); artifact_entries=("$bundle/artifacts"/*)
    shopt -u nullglob dotglob
    [[ "${#root_entries[@]}" == 5 && "${#artifact_entries[@]}" == 4 ]] ||
      { deploy_fail acceptance-component bundle unexpected-files; return 1; }
    for path in "$bundle/bundle.json" "$bundle/bundle-commit.json" "$bundle/dependency-outages.json" "$bundle/component.trx"; do
        [[ -f "$path" && ! -L "$path" && "$(stat -c '%u:%h:%a' -- "$path" 2>/dev/null)" == "$(id -u):1:600" ]] ||
          { deploy_fail acceptance-component bundle unsafe-file; return 1; }
    done
    manifest="$(jq -S -c . "$bundle/bundle.json" 2>/dev/null)" || { deploy_fail acceptance-component bundle invalid-manifest; return 1; }
    commit="$(jq -S -c . "$bundle/bundle-commit.json" 2>/dev/null)" || { deploy_fail acceptance-component bundle invalid-commit; return 1; }
    digest="$(printf '%s\n' "$manifest" | sha256sum)"; digest="${digest%% *}"
    jq -e --arg digest "$digest" '(keys | sort) == ["bundleSha256","schemaVersion"] and .schemaVersion == 1 and .bundleSha256 == $digest' \
      <<< "$commit" >/dev/null 2>&1 || { deploy_fail acceptance-component bundle commit-mismatch; return 1; }
    jq -e --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg tree "$tree" \
      --arg family "$ACCEPTANCE_COMPONENT_FAMILY" --arg test "$ACCEPTANCE_COMPONENT_TEST" '
      .schemaVersion == 1 and .componentFamily == $family and .runId == $run and .mode == $mode and
      .inventorySha256 == $hash and .sourceRevision == $revision and .sourceTree == $tree and .testFullyQualifiedName == $test and
      (keys | sort) == (["schemaVersion","componentFamily","runId","mode","inventorySha256","sourceRevision","sourceTree",
        "testFullyQualifiedName","files"] | sort) and
      ([.files[].id] == ["source-evidence","test-result","minio-failure","sql-failure","redis-failure","smtp-failure"]) and
      ([.files[].relativePath] == ["dependency-outages.json","component.trx","artifacts/minio-failure.json",
        "artifacts/sql-failure.json","artifacts/redis-failure.json","artifacts/smtp-failure.json"]) and
      all(.files[]; (keys | sort) == ["byteLength","id","relativePath","sha256"] and
        (.byteLength | numbers) > 0 and (.byteLength | floor) == .byteLength and (.sha256 | test("^[0-9a-f]{64}$")))
    ' <<< "$manifest" >/dev/null 2>&1 || { deploy_fail acceptance-component bundle invalid-manifest; return 1; }
    while IFS= read -r file; do
        path="$bundle/$(jq -r '.relativePath' <<< "$file")"
        [[ -f "$path" && ! -L "$path" && "$(stat -c '%u:%h:%a' -- "$path" 2>/dev/null)" == "$(id -u):1:600" ]] ||
          { deploy_fail acceptance-component bundle unsafe-file; return 1; }
        actual_length="$(stat -c %s -- "$path")"; actual_sha="$(sha256sum "$path")"; actual_sha="${actual_sha%% *}"
        [[ "$actual_length" == "$(jq -r '.byteLength' <<< "$file")" && "$actual_sha" == "$(jq -r '.sha256' <<< "$file")" ]] ||
          { deploy_fail acceptance-component bundle digest-or-length-mismatch; return 1; }
    done < <(jq -c '.files[]' <<< "$manifest")
    deploy_acceptance_component_validate_source "$bundle/dependency-outages.json" "$revision" || return 1
    deploy_acceptance_component_verify_trx "$bundle" || { deploy_fail acceptance-component trx invalid; return 1; }
    for scenario_id in minio-failure sql-failure redis-failure smtp-failure; do
        deploy_acceptance_validate_artifact_json "$bundle/artifacts/$scenario_id.json" "$ledger" \
          "$(jq -c --arg id "$scenario_id" '.scenarios[] | select(.id == $id)' <<< "$ledger")" ||
          { deploy_fail acceptance-component artifact invalid; return 1; }
    done
}

deploy_acceptance_component_create_bundle() {
    local runtime="$1" bundle="$2" run_id="$3" mode="$4" hash="$5" revision="$6" tree="$7" ledger="$8"
    local stage source trx_dir scenario_id dependency artifact metadata files manifest digest
    stage="$(mktemp -d "$runtime/.bundle.tmp.XXXXXX" 2>/dev/null)" || return 1
    chmod 700 "$stage" || { rm -rf -- "$stage"; return 1; }
    DEPLOY_ACCEPTANCE_COMPONENT_RUNTIME="$runtime"; DEPLOY_ACCEPTANCE_COMPONENT_STAGE="$stage"
    install -d -m 700 "$stage/source" "$stage/trx" "$stage/artifacts" || { rm -rf -- "$stage"; return 1; }
    deploy_acceptance_component_execute_test "$stage/source" "$stage/trx" "$revision" || { rm -rf -- "$stage"; return 1; }
    [[ ! -L "$stage/build" && -d "$stage/build" ]] || { rm -rf -- "$stage"; deploy_fail acceptance-component build unsafe-or-missing; return 1; }
    rm -rf -- "$stage/build" || { rm -rf -- "$stage"; return 1; }
    source="$stage/source/dependency-outages.json"; trx_dir="$stage/trx"
    [[ -f "$source" && ! -L "$source" ]] || { rm -rf -- "$stage"; deploy_fail acceptance-component evidence missing; return 1; }
    local -a trx_files source_files
    shopt -s nullglob dotglob
    trx_files=("$trx_dir"/*); source_files=("$stage/source"/*)
    shopt -u nullglob dotglob
    [[ "${#trx_files[@]}" == 1 && "${trx_files[0]##*/}" == component.trx && "${#source_files[@]}" == 1 ]] ||
      { rm -rf -- "$stage"; deploy_fail acceptance-component output unexpected-files; return 1; }
    chmod 600 "$source" "$trx_dir/component.trx" || { rm -rf -- "$stage"; return 1; }
    deploy_acceptance_component_verify_trx "$trx_dir" || { rm -rf -- "$stage"; deploy_fail acceptance-component trx invalid; return 1; }
    deploy_acceptance_component_validate_source "$source" "$revision" || { rm -rf -- "$stage"; return 1; }
    for scenario_id in minio-failure sql-failure redis-failure smtp-failure; do
        case "$scenario_id" in
            minio-failure) dependency=Minio ;;
            sql-failure) dependency=SqlServer ;;
            redis-failure) dependency=Redis ;;
            smtp-failure) dependency=Smtp ;;
        esac
        artifact="$(deploy_acceptance_component_build_artifact "$ledger" "$scenario_id" "$dependency" "$source")" ||
          { rm -rf -- "$stage"; return 1; }
        deploy_publish_json "$stage/artifacts/$scenario_id.json" "$artifact" || { rm -rf -- "$stage"; return 1; }
    done
    mv -T "$source" "$stage/dependency-outages.json" || { rm -rf -- "$stage"; return 1; }
    mv -T "$trx_dir/component.trx" "$stage/component.trx" || { rm -rf -- "$stage"; return 1; }
    rmdir "$stage/source" "$stage/trx" || { rm -rf -- "$stage"; return 1; }
    files='[]'
    while IFS='|' read -r metadata; do files="$(jq -c --argjson item "$metadata" '. + [$item]' <<< "$files")"; done < <(
      deploy_acceptance_component_file_metadata source-evidence dependency-outages.json "$stage"
      deploy_acceptance_component_file_metadata test-result component.trx "$stage"
      for scenario_id in minio-failure sql-failure redis-failure smtp-failure; do
          deploy_acceptance_component_file_metadata "$scenario_id" "artifacts/$scenario_id.json" "$stage"
      done)
    [[ "$(jq 'length' <<< "$files")" == 6 ]] || { rm -rf -- "$stage"; return 1; }
    manifest="$(jq -S -cn --arg family "$ACCEPTANCE_COMPONENT_FAMILY" --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" \
      --arg revision "$revision" --arg tree "$tree" --arg test "$ACCEPTANCE_COMPONENT_TEST" --argjson files "$files" \
      '{schemaVersion:1,componentFamily:$family,runId:$run,mode:$mode,inventorySha256:$hash,sourceRevision:$revision,
       sourceTree:$tree,testFullyQualifiedName:$test,files:$files}')" || { rm -rf -- "$stage"; return 1; }
    deploy_publish_json "$stage/bundle.json" "$manifest" || { rm -rf -- "$stage"; return 1; }
    digest="$(sha256sum "$stage/bundle.json")"; digest="${digest%% *}"
    deploy_publish_json "$stage/bundle-commit.json" "$(jq -cn --arg digest "$digest" '{schemaVersion:1,bundleSha256:$digest}')" ||
      { rm -rf -- "$stage"; return 1; }
    deploy_acceptance_component_verify_source "$revision" "$tree" || { rm -rf -- "$stage"; return 1; }
    mv -T -- "$stage" "$bundle" 2>/dev/null || { rm -rf -- "$stage"; deploy_fail acceptance-component bundle publish-failed; return 1; }
    DEPLOY_ACCEPTANCE_COMPONENT_STAGE=
}

deploy_run_acceptance_component() {
    local inventory="$1" run_id="$2" mode="$3" hash="$4" revision="$5" worktree="$6" tree="$7" component="$8"
    local state_dir runtime bundle ledger scenario_id
    [[ "$component" == "$ACCEPTANCE_COMPONENT_FAMILY" ]] || { deploy_fail acceptance-component component unsupported; return 1; }
    [[ "$worktree" == clean ]] || { deploy_fail acceptance-component source dirty-worktree-rejected; return 1; }
    deploy_acceptance_component_verify_source "$revision" "$tree" || return 1
    deploy_run_acceptance "$inventory" "$run_id" "$mode" "$hash" "$revision" "$worktree" "$tree" || return 1
    state_dir="$(dirname "$DEPLOY_MANIFEST")"; runtime="$state_dir/acceptance-component-$component"; bundle="$runtime/bundle"
    ledger="$(jq -c . "$state_dir/acceptance-ledger.json")" || return 1
    deploy_acceptance_component_prepare_directory "$runtime" || return 1
    DEPLOY_ACCEPTANCE_COMPONENT_RUNTIME="$runtime"
    local stale
    for stale in "$runtime"/.bundle.tmp.*; do
        [[ -e "$stale" || -L "$stale" ]] || continue
        DEPLOY_ACCEPTANCE_COMPONENT_STAGE="$stale"
        deploy_acceptance_component_cleanup_stage || { deploy_fail acceptance-component bundle stale-stage-unsafe; return 1; }
    done
    if [[ -e "$bundle" || -L "$bundle" ]]; then
        deploy_acceptance_component_validate_bundle "$bundle" "$run_id" "$mode" "$hash" "$revision" "$tree" "$ledger" || return 1
    else
        deploy_acceptance_component_create_bundle "$runtime" "$bundle" "$run_id" "$mode" "$hash" "$revision" "$tree" "$ledger" || return 1
        deploy_acceptance_component_validate_bundle "$bundle" "$run_id" "$mode" "$hash" "$revision" "$tree" "$ledger" || return 1
    fi
    deploy_acceptance_component_verify_source "$revision" "$tree" || return 1
    for scenario_id in minio-failure sql-failure redis-failure smtp-failure; do
        deploy_run_acceptance_record "$inventory" "$run_id" "$mode" "$hash" "$revision" "$worktree" "$tree" "$scenario_id" \
          "$bundle/artifacts/$scenario_id.json" || return 1
    done
    deploy_acceptance_component_verify_source "$revision" "$tree"
}
