#!/usr/bin/env bash

deploy_require_passed_phase() {
    local path="$1" phase="$2" run="$3" mode="$4" hash="$5" revision="$6"
    [[ -f "$path" && ! -L "$path" && "$(stat -c '%u:%h:%a' "$path" 2>/dev/null)" == "$(id -u):1:600" ]] ||
      { deploy_fail "$phase" prerequisite missing-or-unsafe; return 1; }
    jq -e --arg run "$run" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" '
      .runId == $run and .mode == $mode and .inventorySha256 == $hash and
      (.sourceRevision // .source.revision) == $revision and .phaseStatus == "passed"' "$path" >/dev/null 2>&1 ||
      { deploy_fail "$phase" prerequisite mismatch-or-not-passed; return 1; }
}

deploy_phase_correlate_target() {
    local target="$1" preflight="$2" name probe host_id host_name architecture _rest
    name="$(jq -r '.name' <<< "$target")"
    probe="$(deploy_transport_ssh_probe "$(jq -r '.sshHost' <<< "$target")" "$(jq -r '.runtimeRoot' <<< "$target")" "$(jq -r '.ports | join(",")' <<< "$target")")" ||
      { deploy_fail deploy "$name" ssh-correlation-failed; return 1; }
    IFS=$'\t' read -r host_id host_name architecture _rest <<< "$probe"
    [[ "$host_id" == "$(jq -r '.expectedHostIdentity' <<< "$target")" &&
       "$host_name" == "$(jq -r '.expectedHostName' <<< "$target")" &&
       "$architecture" == "$(jq -r '.expectedArchitecture' <<< "$target")" ]] ||
      { deploy_fail deploy "$name" ssh-correlation-mismatch; return 1; }
    deploy_images_correlate_target "$target" "$preflight"
}

deploy_validate_local_catalog() {
    local inventory="$1" bundle database manifest kind version sha length rows expected file
    local -a bundle_entries expected_entries
    bundle="$(jq -r '.deployment.catalog.bundlePath' "$inventory")"; kind="$(jq -r '.catalog.kind' "$inventory")"
    version="$(jq -r '.catalog.version' "$inventory")"; sha="$(jq -r '.catalog.sha256' "$inventory")"
    length="$(jq -r '.catalog.length' "$inventory")"; rows="$(jq -r '.catalog.rowCount' "$inventory")"
    database="$bundle/hyg_v42.sqlite"; manifest="$bundle/manifest.json"
    [[ -d "$bundle" && ! -L "$bundle" && -f "$database" && ! -L "$database" && -f "$manifest" && ! -L "$manifest" ]] ||
      { deploy_fail catalog bundle missing-or-unsafe; return 1; }
    shopt -s nullglob dotglob
    bundle_entries=("$bundle"/*)
    shopt -u nullglob dotglob
    if [[ "$kind" == production ]]; then
        expected_entries=(manifest.json hyg_v42.sqlite LICENSE-HYG.md ATTRIBUTION-HYG.md)
    else
        expected_entries=(manifest.json hyg_v42.sqlite)
    fi
    [[ "${#bundle_entries[@]}" == "${#expected_entries[@]}" ]] || { deploy_fail catalog bundle shape-mismatch; return 1; }
    for expected in "${expected_entries[@]}"; do
        file="$bundle/$expected"
        [[ -f "$file" && ! -L "$file" && "$(stat -c %h "$file" 2>/dev/null)" == 1 ]] ||
          { deploy_fail catalog bundle shape-mismatch; return 1; }
    done
    jq -e --arg kind "$kind" --arg version "$version" --arg sha "$sha" --argjson length "$length" --argjson rows "$rows" '
      .package.kind == $kind and .package.version == $version and
      .database.relativePath == "hyg_v42.sqlite" and .database.sha256 == $sha and
      .database.length == $length and .database.rowCount == $rows' "$manifest" >/dev/null 2>&1 ||
      { deploy_fail catalog bundle manifest-inventory-mismatch; return 1; }
    if [[ "$kind" == fixture ]]; then
        jq -e '
          (keys | sort) == (["catalog","database","manifestVersion","package","preprocessingVersion","schemaVersion"] | sort) and
          (.package | keys | sort) == (["kind","version"] | sort) and
          (.catalog | keys | sort) == (["name","version"] | sort) and
          (.database | keys | sort) == (["length","relativePath","rowCount","sha256"] | sort) and
          .manifestVersion == 1 and (.catalog.name | type == "string" and length > 0) and
          (.catalog.version | type == "string" and length > 0) and
          (.schemaVersion | type == "string" and length > 0) and
          (.preprocessingVersion | type == "string" and length > 0)' "$manifest" >/dev/null 2>&1 ||
          { deploy_fail catalog bundle fixture-manifest-invalid; return 1; }
    fi
    [[ "$(sha256sum "$database" | cut -d' ' -f1)" == "$sha" && "$(wc -c < "$database")" == "$length" ]] ||
      { deploy_fail catalog bundle hash-or-length-mismatch; return 1; }
    [[ "$(sqlite3 -batch -noheader -readonly "$database" 'PRAGMA integrity_check;')" == ok &&
       "$(sqlite3 -batch -noheader -readonly "$database" 'SELECT count(*) FROM celestial_objects;')" == "$rows" ]] ||
      { deploy_fail catalog bundle integrity-or-row-mismatch; return 1; }
    if [[ "$kind" == production ]]; then
        # shellcheck source=scripts/catalog/catalog-common.sh
        # shellcheck disable=SC1091
        . "$REPO_ROOT/scripts/catalog/catalog-common.sh"
        hyg_validate_bundle "$bundle" >/dev/null || { deploy_fail catalog bundle production-manifest-invalid; return 1; }
    fi
}

deploy_catalog_mark_failed() {
    local status="${1:-1}" now
    trap - ERR
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    if [[ -n "${DEPLOY_CATALOG_JSON:-}" ]]; then
        DEPLOY_CATALOG_JSON="$(jq -c --arg now "$now" '.phaseStatus="failed" | .updatedAt=$now | del(.completedAt)' <<< "$DEPLOY_CATALOG_JSON")"
        [[ -z "${DEPLOY_CATALOG_LEDGER:-}" ]] || deploy_publish_json "$DEPLOY_CATALOG_LEDGER" "$DEPLOY_CATALOG_JSON" >/dev/null 2>&1 || true
        [[ -z "${DEPLOY_CATALOG_EVIDENCE:-}" ]] || deploy_publish_json "$DEPLOY_CATALOG_EVIDENCE" "$DEPLOY_CATALOG_JSON" >/dev/null 2>&1 || true
        [[ -z "${DEPLOY_CATALOG_MANIFEST:-}" ]] || deploy_publish_json "$DEPLOY_CATALOG_MANIFEST" "$DEPLOY_CATALOG_JSON" >/dev/null 2>&1 || true
    fi
    return "$status"
}

deploy_run_catalog() {
    local inventory="$1" run_id="$2" mode="$3" hash="$4" revision="$5" worktree="$6"
    local state_dir evidence_dir target name root ssh stage result now bundle install_root kind version sha length rows entry
    deploy_require_passed_phase "$(dirname "$DEPLOY_MANIFEST")/prepare-manifest.json" catalog "$run_id" "$mode" "$hash" "$revision" || return 1
    deploy_require_passed_phase "$(dirname "$DEPLOY_MANIFEST")/images-manifest.json" catalog "$run_id" "$mode" "$hash" "$revision" || return 1
    deploy_require_resume_match "$DEPLOY_MANIFEST" "$run_id" "$mode" "$hash" "$revision" "$worktree" || return 1
    deploy_validate_local_catalog "$inventory" || return 1
    DEPLOY_IMAGES_PREFLIGHT_JSON="$(jq -c . "$DEPLOY_MANIFEST")"
    state_dir="$(dirname "$DEPLOY_MANIFEST")"; evidence_dir="$(dirname "$DEPLOY_EVIDENCE")"
    DEPLOY_CATALOG_MANIFEST="$state_dir/catalog-manifest.json"; DEPLOY_CATALOG_LEDGER="$state_dir/catalog-ledger.json"; DEPLOY_CATALOG_EVIDENCE="$evidence_dir/catalog.json"
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    if [[ -f "$DEPLOY_CATALOG_LEDGER" && ! -L "$DEPLOY_CATALOG_LEDGER" ]]; then
        DEPLOY_CATALOG_JSON="$(jq -c . "$DEPLOY_CATALOG_LEDGER")"
        jq -e --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --argjson inventory "$(jq -c . "$inventory")" '
          .schemaVersion == 1 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and .sourceRevision == $revision and
          (.phaseStatus == "running" or .phaseStatus == "failed" or .phaseStatus == "passed") and
          (.targets | type == "array" and length == ([.[].target] | unique | length) and all(.[];
            (keys | sort) == (["sha256","status","target","version"] | sort) and .status == "installed" and
            .version == $inventory.catalog.version and .sha256 == $inventory.catalog.sha256 and
            (.target as $target | ([$inventory.logicHost.name] + [$inventory.cameraAgents[].name] +
              (if $inventory.sharedServices then [$inventory.sharedServices.name] else [] end)) | index($target) != null))) and
          (if .phaseStatus == "passed" then
             ([.targets[].target] | sort) == ([$inventory.logicHost.name] + [$inventory.cameraAgents[].name] +
               (if $inventory.sharedServices then [$inventory.sharedServices.name] else [] end) | sort)
           else true end)' <<< "$DEPLOY_CATALOG_JSON" >/dev/null ||
          { deploy_fail catalog ledger invalid; return 1; }
        [[ -f "$DEPLOY_CATALOG_MANIFEST" && ! -L "$DEPLOY_CATALOG_MANIFEST" && "$(stat -c '%u:%h:%a' "$DEPLOY_CATALOG_MANIFEST" 2>/dev/null)" == "$(id -u):1:600" ]] ||
          { deploy_fail catalog manifest missing-or-unsafe; return 1; }
        if [[ "$(jq -r '.phaseStatus' <<< "$DEPLOY_CATALOG_JSON")" != running ]]; then
            [[ "$(jq -c . "$DEPLOY_CATALOG_MANIFEST" 2>/dev/null)" == "$DEPLOY_CATALOG_JSON" && -f "$DEPLOY_CATALOG_EVIDENCE" && ! -L "$DEPLOY_CATALOG_EVIDENCE" &&
               "$(stat -c '%u:%h:%a' "$DEPLOY_CATALOG_EVIDENCE" 2>/dev/null)" == "$(id -u):1:600" && "$(jq -c . "$DEPLOY_CATALOG_EVIDENCE" 2>/dev/null)" == "$DEPLOY_CATALOG_JSON" ]] ||
              { deploy_fail catalog resume committed-state-mismatch; return 1; }
        fi
        DEPLOY_CATALOG_JSON="$(jq -c --arg now "$now" '.phaseStatus="running" | .updatedAt=$now | del(.completedAt)' <<< "$DEPLOY_CATALOG_JSON")"
    else
        DEPLOY_CATALOG_JSON="$(jq -cn --arg run "$run_id" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg now "$now" \
          '{schemaVersion:1,runId:$run,mode:$mode,inventorySha256:$hash,sourceRevision:$revision,phaseStatus:"running",startedAt:$now,updatedAt:$now,targets:[]}')"
    fi
    deploy_publish_json "$DEPLOY_CATALOG_LEDGER" "$DEPLOY_CATALOG_JSON" || return 1
    deploy_publish_json "$DEPLOY_CATALOG_MANIFEST" "$DEPLOY_CATALOG_JSON" || return 1
    bundle="$(jq -r '.deployment.catalog.bundlePath' "$inventory")"; install_root="$(jq -r '.deployment.catalog.installRoot' "$inventory")"
    kind="$(jq -r '.catalog.kind' "$inventory")"; version="$(jq -r '.catalog.version' "$inventory")"; sha="$(jq -r '.catalog.sha256' "$inventory")"
    length="$(jq -r '.catalog.length' "$inventory")"; rows="$(jq -r '.catalog.rowCount' "$inventory")"
    while IFS= read -r target; do
        name="$(jq -r '.name' <<< "$target")"; root="$(jq -r '.runtimeRoot' <<< "$target")"; ssh="$(jq -r '.sshHost' <<< "$target")"
        entry="$(jq -c --arg name "$name" '.targets[]? | select(.target == $name)' <<< "$DEPLOY_CATALOG_JSON")"
        stage="$root/.hvo-deploy/catalog-$run_id"
        deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
        if [[ -z "$entry" ]]; then
            deploy_transport_remote_directories "$ssh" "$stage" "$stage/bundle" "$stage/scripts" "$stage/scripts/catalog" || { deploy_fail catalog "$name" stage-create-failed; return 1; }
            deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
            deploy_transport_copy "$bundle/." "$ssh" "$stage/bundle/" || { deploy_fail catalog "$name" bundle-transfer-failed; return 1; }
            deploy_transport_copy "$REPO_ROOT/scripts/catalog/catalog-common.sh" "$ssh" "$stage/scripts/catalog/catalog-common.sh" || return 1
            deploy_transport_copy "$REPO_ROOT/scripts/catalog/install-hyg-v42.sh" "$ssh" "$stage/scripts/catalog/install-hyg-v42.sh" || return 1
            deploy_transport_copy "$REPO_ROOT/scripts/infra:operation-lock" "$ssh" "$stage/scripts/infra:operation-lock" || return 1
            deploy_transport_catalog_prepare_scripts "$ssh" "$stage" || { deploy_fail catalog "$name" script-stage-invalid; return 1; }
            deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
            result="$(deploy_transport_catalog_install "$ssh" "$stage" "$install_root" "$kind" "$version" "$sha" "$length" "$rows")" || { deploy_fail catalog "$name" install-or-verify-failed; return 1; }
            [[ "$result" == installed$'\t'"versions/$version"$'\t'"$sha" ]] || { deploy_fail catalog "$name" invalid-install-response; return 1; }
            deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
            DEPLOY_CATALOG_JSON="$(jq -c --arg target "$name" --arg version "$version" --arg sha "$sha" '.targets += [{target:$target,version:$version,sha256:$sha,status:"installed"}]' <<< "$DEPLOY_CATALOG_JSON")"
            deploy_publish_json "$DEPLOY_CATALOG_LEDGER" "$DEPLOY_CATALOG_JSON" || return 1
            deploy_publish_json "$DEPLOY_CATALOG_MANIFEST" "$DEPLOY_CATALOG_JSON" || return 1
        else
            result="$(deploy_transport_catalog_verify "$ssh" "$install_root" "$version" "$sha" "$length" "$rows")" || { deploy_fail catalog "$name" installed-catalog-drift; return 1; }
            [[ "$result" == verified$'\t'"versions/$version"$'\t'"$sha" ]] || { deploy_fail catalog "$name" invalid-verify-response; return 1; }
            deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
        fi
    done < <(deploy_inventory_targets "$inventory")
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"; DEPLOY_CATALOG_JSON="$(jq -c --arg now "$now" '.phaseStatus="passed" | .updatedAt=$now | .completedAt=$now' <<< "$DEPLOY_CATALOG_JSON")"
    deploy_publish_json "$DEPLOY_CATALOG_LEDGER" "$DEPLOY_CATALOG_JSON" &&
      deploy_publish_json "$DEPLOY_CATALOG_EVIDENCE" "$(jq -c 'del(.targets[].runtimeRoot)' <<< "$DEPLOY_CATALOG_JSON")" &&
      deploy_publish_json "$DEPLOY_CATALOG_MANIFEST" "$DEPLOY_CATALOG_JSON"
}
