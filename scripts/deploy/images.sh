#!/usr/bin/env bash

deploy_images_component_contracts() {
    local inventory="$1" logic_platforms camera_platforms
    logic_platforms="$(jq -c '[.logicHost.expectedArchitecture] | unique | sort' "$inventory")"
    camera_platforms="$(jq -c '[.cameraAgents[].expectedArchitecture] | unique | sort' "$inventory")"
    jq -cn --argjson logic "$logic_platforms" --argjson camera "$camera_platforms" '
      [{component:"logicHost",dockerfile:"src/HVO.SkyMonitor.LogicHost/Dockerfile",platforms:$logic},
       {component:"cameraAgent",dockerfile:"src/HVO.SkyMonitor.CameraAgent/Dockerfile",platforms:$camera}] | .[]'
}

deploy_images_assert_source() {
    local revision="$1" tree="$2"
    [[ "$(git -C "$REPO_ROOT" rev-parse HEAD 2>/dev/null)" == "$revision" &&
       "$(git -C "$REPO_ROOT" rev-parse "${revision}^{tree}" 2>/dev/null)" == "$tree" &&
       -z "$(git -C "$REPO_ROOT" status --porcelain 2>/dev/null)" ]] ||
      { deploy_fail images source source-changed-or-dirty; return 1; }
}

deploy_images_validate_builder() {
    local inventory="$1" required_platforms="$2" builder expected_driver expected_endpoint info listing
    local expected_id expected_name expected_arch actual_arch_raw actual_arch
    builder="$(jq -r '.images.builder.name' "$inventory")"
    expected_driver="$(jq -r '.images.builder.driver' "$inventory")"
    expected_endpoint="$(jq -r '.images.builder.endpoint' "$inventory")"
    expected_id="$(jq -r '.images.builder.expectedDaemonIdentity' "$inventory")"
    expected_name="$(jq -r '.images.builder.expectedDaemonName' "$inventory")"
    expected_arch="$(jq -r '.images.builder.expectedArchitecture' "$inventory")"
    listing="$(docker buildx ls --format '{{json .}}' 2>/dev/null)" || { deploy_fail images builder unavailable; return 1; }
    jq -se --arg builder "$builder" --arg driver "$expected_driver" --arg endpoint "$expected_endpoint" --argjson platforms "$required_platforms" '
      [ .[] | select(.Name == $builder) ] as $matches |
      ($matches | length) > 0 and all($matches[]; .Driver == $driver and (.Err // "") == "" and
        (.Nodes | type == "array" and length == 1 and
          (.[0].Endpoint == $endpoint or ($endpoint == "default" and .[0].Endpoint == "unix:///var/run/docker.sock")) and .[0].Status == "running" and
          .[0] as $node | all($platforms[]; . as $platform | $node.Platforms | index("linux/" + $platform) != null)))' <<< "$listing" >/dev/null 2>&1 ||
      { deploy_fail images builder mismatch-or-remote; return 1; }
    info="$(docker info --format '{"ID":{{json .ID}},"Name":{{json .Name}},"Architecture":{{json .Architecture}},"OSType":{{json .OSType}}}' 2>/dev/null)" ||
      { deploy_fail images builder control-daemon-unavailable; return 1; }
    actual_arch_raw="$(jq -er '.Architecture | strings | select(length > 0)' <<< "$info" 2>/dev/null)" ||
      { deploy_fail images builder control-daemon-invalid-response; return 1; }
    actual_arch="$(deploy_normalize_docker_architecture "$actual_arch_raw")" ||
      { deploy_fail images builder control-daemon-unsupported-architecture; return 1; }
    jq -e --arg id "$expected_id" --arg name "$expected_name" '
      .ID == $id and .Name == $name and .OSType == "linux"' <<< "$info" >/dev/null 2>&1 ||
      { deploy_fail images builder control-daemon-mismatch; return 1; }
    [[ "$actual_arch" == "$expected_arch" ]] || { deploy_fail images builder control-daemon-mismatch; return 1; }
}

deploy_images_correlate_target() {
    local target="$1" preflight="$2" name context expected_id expected_name expected_arch inspected info actual_arch_raw actual_arch
    name="$(jq -r '.name' <<< "$target")"; context="$(jq -r '.dockerContext' <<< "$target")"
    expected_id="$(jq -r '.expectedDockerDaemonIdentity' <<< "$target")"
    expected_name="$(jq -r '.expectedHostName' <<< "$target")"
    expected_arch="$(jq -r '.expectedArchitecture' <<< "$target")"
    jq -e --arg name "$name" --arg id "$expected_id" --arg host "$expected_name" --arg arch "$expected_arch" '
      any(.targets[]; .name == $name and .dockerDaemonIdentity == $id and .hostName == $host and .architecture == $arch)' <<< "$preflight" >/dev/null 2>&1 ||
      { deploy_fail images "$name" preflight-correlation-mismatch; return 1; }
    inspected="$(deploy_transport_docker_context "$context")" || { deploy_fail images "$name" docker-context-unavailable; return 1; }
    [[ "$inspected" == "$context" ]] || { deploy_fail images "$name" docker-context-mismatch; return 1; }
    info="$(deploy_transport_docker_info "$context")" || { deploy_fail images "$name" docker-daemon-unavailable; return 1; }
    actual_arch_raw="$(jq -er '.Architecture | strings | select(length > 0)' <<< "$info" 2>/dev/null)" ||
      { deploy_fail images "$name" docker-daemon-invalid-response; return 1; }
    actual_arch="$(deploy_normalize_docker_architecture "$actual_arch_raw")" ||
      { deploy_fail images "$name" docker-daemon-unsupported-architecture; return 1; }
    jq -e --arg id "$expected_id" --arg name "$expected_name" '
      .ID == $id and .Name == $name and .OSType == "linux" and
      (.ServerVersion | type == "string" and length > 0)' <<< "$info" >/dev/null 2>&1 ||
      { deploy_fail images "$name" docker-daemon-correlation-mismatch; return 1; }
    [[ "$actual_arch" == "$expected_arch" ]] || { deploy_fail images "$name" docker-daemon-correlation-mismatch; return 1; }
}

deploy_images_require_prepare() {
    local inventory="$1" run_id="$2" mode="$3" inventory_hash="$4" revision="$5" worktree_state="$6"
    local manifest ledger evidence
    manifest="$(dirname "$DEPLOY_MANIFEST")/prepare-manifest.json"
    ledger="$(dirname "$DEPLOY_MANIFEST")/prepare-ledger.json"
    evidence="$(dirname "$DEPLOY_EVIDENCE")/prepare.json"
    [[ -f "$manifest" && ! -L "$manifest" && -f "$ledger" && ! -L "$ledger" && -f "$evidence" && ! -L "$evidence" ]] ||
      { deploy_fail images prepare-required missing; return 1; }
    [[ "$(stat -c '%u:%h:%a' "$manifest" 2>/dev/null)" == "$(id -u):1:600" &&
       "$(stat -c '%u:%h:%a' "$ledger" 2>/dev/null)" == "$(id -u):1:600" &&
       "$(stat -c '%u:%h:%a' "$evidence" 2>/dev/null)" == "$(id -u):1:600" ]] ||
      { deploy_fail images prepare-required unsafe; return 1; }
    local manifest_json ledger_json
    manifest_json="$(jq -c . "$manifest" 2>/dev/null)" || { deploy_fail images prepare-required invalid-manifest; return 1; }
    # shellcheck disable=SC2034 # Used by target correlation throughout the image phase.
    DEPLOY_IMAGES_PREFLIGHT_JSON="$(jq -c . "$DEPLOY_MANIFEST" 2>/dev/null)" || { deploy_fail images preflight-required invalid-manifest; return 1; }
    [[ "$(jq -r '.phaseStatus' <<< "$DEPLOY_IMAGES_PREFLIGHT_JSON")" == passed ]] || { deploy_fail images preflight-required not-passed; return 1; }
    deploy_require_completed_evidence_match "$DEPLOY_IMAGES_PREFLIGHT_JSON" "$DEPLOY_EVIDENCE" || return 1
    ledger_json="$(jq -c . "$ledger" 2>/dev/null)" || { deploy_fail images prepare-required invalid-ledger; return 1; }
    jq -e --arg run "$run_id" --arg mode "$mode" --arg hash "$inventory_hash" --arg revision "$revision" '
      .schemaVersion == 1 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and
      .sourceRevision == $revision and .phaseStatus == "passed"' <<< "$manifest_json" >/dev/null 2>&1 ||
      { deploy_fail images prepare-required contract-mismatch; return 1; }
    deploy_validate_prepare_ledger "$inventory" "$ledger_json" "$run_id" "$mode" "$inventory_hash" "$revision" || return 1
    jq -e --argjson manifest "$manifest_json" --argjson ledger "$ledger_json" '
      .schemaVersion == $manifest.schemaVersion and .runId == $manifest.runId and .mode == $manifest.mode and
      .inventorySha256 == $manifest.inventorySha256 and .phaseStatus == "passed" and
      $manifest.targets == $ledger.targets and
      .targets == [$manifest.targets[] | {target,status,disposition,controlDirectory,markerDigest,creatingRunId}]' \
      "$evidence" >/dev/null 2>&1 || { deploy_fail images prepare-required evidence-mismatch; return 1; }
    deploy_require_resume_match "$DEPLOY_MANIFEST" "$run_id" "$mode" "$inventory_hash" "$revision" "$worktree_state" || return 1
}

deploy_images_publish() {
    deploy_publish_json "$DEPLOY_IMAGES_LEDGER" "$DEPLOY_IMAGES_LEDGER_JSON" &&
      { [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-after-images-ledger || "$(jq -r '.phaseStatus' <<< "$DEPLOY_IMAGES_LEDGER_JSON")" != passed ]] || exit 78; } &&
      deploy_publish_json "$DEPLOY_IMAGES_EVIDENCE" "$(jq -c '{schemaVersion,runId,mode,inventorySha256,sourceRevision,sourceTree,builder,distributionMode,phaseStatus,startedAt,updatedAt,completedAt,
        images:[.images[] | del(.archivePath)],targets}' <<< "$DEPLOY_IMAGES_LEDGER_JSON")" &&
      { [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-after-images-evidence || "$(jq -r '.phaseStatus' <<< "$DEPLOY_IMAGES_LEDGER_JSON")" != passed ]] || exit 77; } &&
      deploy_publish_json "$DEPLOY_IMAGES_MANIFEST" "$DEPLOY_IMAGES_LEDGER_JSON"
}

deploy_images_validate_inspect() {
    local json="$1" architecture="$2" revision="$3" tree="$4" component="$5" expected_id="$6" expected_digest="$7" created="$8" sdk="$9" source_reference="${10}"
    local actual_arch_raw actual_arch
    actual_arch_raw="$(jq -er '.architecture | strings | select(length > 0)' <<< "$json" 2>/dev/null)" || return 1
    actual_arch="$(deploy_normalize_docker_architecture "$actual_arch_raw")" || return 1
    jq -e --arg revision "$revision" --arg component "$component" \
      --arg tree "$tree" --arg id "$expected_id" --arg digest "$expected_digest" --arg created "$created" --arg sdk "$sdk" --arg source "$source_reference" '
      type == "object" and .os == "linux" and
      .labels["org.opencontainers.image.revision"] == $revision and .labels["io.hvoskymonitor.component"] == $component and
      .labels["io.hvoskymonitor.source-tree"] == $tree and
      .labels["org.opencontainers.image.created"] == $created and .labels["io.hvoskymonitor.dotnet-sdk"] == $sdk and
      .labels["org.opencontainers.image.ref.name"] == $source and
      ($id == "" or .id == $id) and ($digest == "" or (.repoDigests | index($digest) != null))' <<< "$json" >/dev/null 2>&1 || return 1
    [[ "$actual_arch" == "$architecture" ]]
}

deploy_images_build_archive() {
    local component="$1" dockerfile="$2" architecture="$3" repository="$4" tag="$5" revision="$6" tree="$7" created="$8" sdk="$9" artifact_root="${10}" builder="${11}"
    local temporary="$artifact_root/.${component}-${architecture}.${run_id}.tmp.tar" config config_name config_digest archive_digest final
    if [[ -e "$temporary" || -L "$temporary" ]]; then
        [[ -f "$temporary" && ! -L "$temporary" && "$(stat -c '%u:%h' "$temporary" 2>/dev/null)" == "$(id -u):1" ]] ||
          { deploy_fail images "$component-$architecture" unsafe-temporary; return 1; }
        rm -f -- "$temporary" || { deploy_fail images "$component-$architecture" temporary-remove-failed; return 1; }
    fi
    docker buildx build --builder "$builder" --platform "linux/$architecture" --file "$REPO_ROOT/$dockerfile" \
      --label "org.opencontainers.image.revision=$revision" --label "org.opencontainers.image.created=$created" \
      --label "org.opencontainers.image.ref.name=$repository:$tag" --label "io.hvoskymonitor.component=$component" \
      --label "io.hvoskymonitor.source-tree=$tree" --label "io.hvoskymonitor.dotnet-sdk=$sdk" \
      --tag "$repository:$tag" --output "type=docker,dest=$temporary" "$REPO_ROOT" >/dev/null 2>&1 ||
      { rm -f -- "$temporary"; deploy_fail images "$component-$architecture" build-failed; return 1; }
    chmod 600 "$temporary" || return 1
    config_name="$(tar -xOf "$temporary" manifest.json 2>/dev/null | jq -er 'if type == "array" and length == 1 then .[0].Config else empty end')" ||
      { rm -f -- "$temporary"; deploy_fail images "$component-$architecture" invalid-archive; return 1; }
    [[ "$config_name" =~ ^([0-9a-f]{64}\.json|blobs/sha256/[0-9a-f]{64})$ ]] || { rm -f -- "$temporary"; deploy_fail images "$component-$architecture" unsafe-archive-config; return 1; }
    config="$(tar -xOf "$temporary" "$config_name" 2>/dev/null)" || { rm -f -- "$temporary"; deploy_fail images "$component-$architecture" invalid-archive; return 1; }
    config_digest="$(printf '%s' "$config" | sha256sum)"; config_digest="${config_digest%% *}"
    [[ "$config_name" == "$config_digest.json" || "$config_name" == "blobs/sha256/$config_digest" ]] ||
      { rm -f -- "$temporary"; deploy_fail images "$component-$architecture" config-digest-mismatch; return 1; }
    jq -e --arg arch "$architecture" --arg revision "$revision" --arg tree "$tree" --arg component "$component" '
      .architecture == $arch and .os == "linux" and .config.Labels["org.opencontainers.image.revision"] == $revision and
      .config.Labels["io.hvoskymonitor.source-tree"] == $tree and
      .config.Labels["io.hvoskymonitor.component"] == $component' <<< "$config" >/dev/null 2>&1 ||
      { rm -f -- "$temporary"; deploy_fail images "$component-$architecture" config-identity-mismatch; return 1; }
    archive_digest="$(sha256sum "$temporary")"; archive_digest="${archive_digest%% *}"
    final="$artifact_root/${component}-${architecture}-sha256-${archive_digest}.tar"
    if [[ -e "$final" || -L "$final" ]]; then
        [[ -f "$final" && ! -L "$final" && "$(stat -c '%u:%h:%a' "$final" 2>/dev/null)" == "$(id -u):1:600" &&
           "$(sha256sum "$final" | cut -d' ' -f1)" == "$archive_digest" ]] ||
          { rm -f -- "$temporary"; deploy_fail images "$component-$architecture" duplicate-output-mismatch; return 1; }
        rm -f -- "$temporary"
    else
        mv -T -- "$temporary" "$final" || return 1
    fi
    jq -cn --arg component "$component" --arg architecture "$architecture" --arg repository "$repository" --arg tag "$tag" \
      --arg path "$final" --arg archive "$archive_digest" --arg config "$config_digest" \
      --arg created "$created" --arg sdk "$sdk" \
      '{component:$component,architecture:$architecture,repository:$repository,tag:$tag,reference:("sha256:"+$config),archivePath:$path,
        archiveSha256:$archive,configDigest:("sha256:"+$config),createdAt:$created,dotnetSdk:$sdk,status:"built"}'
}

deploy_images_observe_registry() {
    local component="$1" platforms_json="$2" repository="$3" tag="$4" revision="$5" tree="$6" created="$7" sdk="$8" builder="$9"
    local raw manifest_json manifest_digest selected descriptor architecture child image source_reference
    raw="$(mktemp "$(dirname "$DEPLOY_IMAGES_LEDGER")/.manifest.XXXXXX")" || return 1
    chmod 600 "$raw" || { rm -f "$raw"; return 1; }
    if ! docker buildx imagetools inspect --builder "$builder" --raw "$repository:$tag" > "$raw" 2>/dev/null; then
        rm -f "$raw"
        return 2
    fi
    jq -e 'type == "object" and .schemaVersion == 2 and (.manifests | type == "array" and length > 0)' "$raw" >/dev/null 2>&1 ||
      { rm -f "$raw"; deploy_fail images "$component" invalid-registry-manifest; return 1; }
    manifest_json="$(docker buildx imagetools inspect --builder "$builder" --format '{{json .Manifest}}' "$repository:$tag" 2>/dev/null)" ||
      { rm -f "$raw"; deploy_fail images "$component" parent-digest-inspect-failed; return 1; }
    manifest_digest="$(jq -er '.digest | select(test("^sha256:[0-9a-f]{64}$"))' <<< "$manifest_json" 2>/dev/null)" ||
      { rm -f "$raw"; deploy_fail images "$component" parent-digest-invalid; return 1; }
    jq -e --argjson formatted "$manifest_json" '.manifests == $formatted.manifests' "$raw" >/dev/null 2>&1 ||
      { rm -f "$raw"; deploy_fail images "$component" parent-descriptor-mismatch; return 1; }
    jq -e '
      all(.manifests[];
        ((.platform.os == "linux" and (.platform.architecture == "amd64" or .platform.architecture == "arm64") and
          (.digest | test("^sha256:[0-9a-f]{64}$"))) or
         (.platform.os == "unknown" and .platform.architecture == "unknown" and
          .annotations["vnd.docker.reference.type"] == "attestation-manifest" and
          (.annotations["vnd.docker.reference.digest"] | test("^sha256:[0-9a-f]{64}$")))))' "$raw" >/dev/null 2>&1 ||
      { rm -f "$raw"; deploy_fail images "$component" unexpected-registry-descriptor; return 1; }
    selected="$(jq -c '[.manifests[] | select(.platform.os == "linux" and (.platform.architecture == "amd64" or .platform.architecture == "arm64")) |
      {architecture:.platform.architecture,digest:.digest}] | sort_by(.architecture)' "$raw")" || { rm -f "$raw"; return 1; }
    jq -ne --argjson expected "$platforms_json" --argjson selected "$selected" '
      ($selected | length) == ($expected | length) and
      ([$selected[].architecture] | unique | sort) == $expected and
      ([$selected[].digest] | unique | length) == ($selected | length)' >/dev/null 2>&1 ||
      { rm -f "$raw"; deploy_fail images "$component" platform-manifest-mismatch; return 1; }
    jq -e --argjson selected "$selected" '
      all(.manifests[] | select(.platform.os == "unknown");
        .annotations["vnd.docker.reference.digest"] as $parent | any($selected[]; .digest == $parent))' "$raw" >/dev/null 2>&1 ||
      { rm -f "$raw"; deploy_fail images "$component" attestation-parent-mismatch; return 1; }
    rm -f "$raw"
    source_reference="$repository:$tag"
    while IFS= read -r descriptor; do
        architecture="$(jq -r '.architecture' <<< "$descriptor")"; child="$(jq -r '.digest' <<< "$descriptor")"
        image="$(docker buildx imagetools inspect --builder "$builder" --format '{{json .Image}}' "$repository@$child" 2>/dev/null)" ||
          { deploy_fail images "$component-$architecture" registry-config-inspect-failed; return 1; }
        image="$(jq -c '{architecture,os,labels:.config.Labels}' <<< "$image" 2>/dev/null)" ||
          { deploy_fail images "$component-$architecture" registry-config-invalid; return 1; }
        deploy_images_validate_inspect "$image" "$architecture" "$revision" "$tree" "$component" "" "" "$created" "$sdk" "$source_reference" ||
          { deploy_fail images "$component-$architecture" registry-config-mismatch; return 1; }
    done < <(jq -c '.[]' <<< "$selected")
    DEPLOY_REGISTRY_IMAGE_JSON="$(jq -cn --arg component "$component" --arg repository "$repository" --arg tag "$tag" \
      --arg digest "$manifest_digest" --arg created "$created" --arg sdk "$sdk" --argjson platforms "$selected" \
      '{component:$component,repository:$repository,tag:$tag,reference:($repository+"@"+$digest),manifestDigest:$digest,platforms:$platforms,
        createdAt:$created,dotnetSdk:$sdk,status:"built"}')"
}

deploy_images_resolve_registry() {
    local component="$1" dockerfile="$2" platforms_json="$3" repository="$4" tag="$5" revision="$6" tree="$7" created="$8" sdk="$9" builder="${10}"
    local platforms status
    if deploy_images_observe_registry "$component" "$platforms_json" "$repository" "$tag" "$revision" "$tree" "$created" "$sdk" "$builder"; then
        return 0
    else
        status=$?
    fi
    [[ "$status" == 2 ]] || return "$status"
    platforms="$(jq -r 'map("linux/" + .) | join(",")' <<< "$platforms_json")"
    if ! docker buildx build --builder "$builder" --platform "$platforms" --file "$REPO_ROOT/$dockerfile" \
      --label "org.opencontainers.image.revision=$revision" --label "org.opencontainers.image.created=$created" \
      --label "org.opencontainers.image.ref.name=$repository:$tag" --label "io.hvoskymonitor.component=$component" \
      --label "io.hvoskymonitor.source-tree=$tree" --label "io.hvoskymonitor.dotnet-sdk=$sdk" \
      --tag "$repository:$tag" --push "$REPO_ROOT" >/dev/null 2>&1; then
        if deploy_images_observe_registry "$component" "$platforms_json" "$repository" "$tag" "$revision" "$tree" "$created" "$sdk" "$builder"; then
            return 0
        fi
        deploy_fail images "$component" build-push-failed
        return 1
    fi
    if [[ "${DEPLOY_TEST_FAILPOINT:-}" == abrupt-after-registry-push ]]; then exit 76; fi
    deploy_images_observe_registry "$component" "$platforms_json" "$repository" "$tag" "$revision" "$tree" "$created" "$sdk" "$builder" || {
        status=$?; [[ "$status" != 2 ]] || deploy_fail images "$component" pushed-tag-missing
        return 1
    }
}

deploy_validate_images_ledger() {
    local inventory="$1" json="$2" run="$3" mode="$4" hash="$5" revision="$6" tree="$7" distribution="$8"
    local expected_components expected_targets entry component architecture repository tag artifact_root expected_platforms reference image_id expected_sdk expected_builder
    expected_builder="$(jq -c '.images.builder' "$inventory")"
    jq -e --arg run "$run" --arg mode "$mode" --arg hash "$hash" --arg revision "$revision" --arg tree "$tree" \
      --arg distribution "$distribution" --argjson builder "$expected_builder" '
      type == "object" and (((keys - ["completedAt"]) | sort) ==
        (["schemaVersion","runId","mode","inventorySha256","sourceRevision","sourceTree","builder","distributionMode","phaseStatus","startedAt","updatedAt","images","targets"] | sort)) and
      .schemaVersion == 1 and .runId == $run and .mode == $mode and .inventorySha256 == $hash and
      .sourceRevision == $revision and .sourceTree == $tree and .builder == $builder and .distributionMode == $distribution and
      (.phaseStatus == "running" or .phaseStatus == "failed" or .phaseStatus == "passed") and
      (.startedAt | type == "string" and length > 0) and (.updatedAt | type == "string" and length > 0) and
      ((.phaseStatus == "passed" and (.completedAt | type == "string" and length > 0)) or (.phaseStatus != "passed" and (has("completedAt") | not))) and
      (.images | type == "array") and ([.images[] | [.component, (.architecture // "manifest")]] | unique | length) == (.images | length) and
      all(.images[]; .component == "logicHost" or .component == "cameraAgent") and
      (.targets | type == "array") and ([.targets[].target] | unique | length) == (.targets | length) and
      all(.targets[]; (keys | sort) == (["target","component","architecture","reference","imageId","status"] | sort) and
        .status == "verified" and (.target | type == "string") and (.component == "logicHost" or .component == "cameraAgent") and
        (.architecture == "amd64" or .architecture == "arm64") and (.reference | type == "string"))' <<< "$json" >/dev/null 2>&1 ||
      { deploy_fail images ledger invalid; return 1; }
    tag="$(jq -r '.images.tag' "$inventory")"; artifact_root="$(jq -r '.images.artifactRoot // empty' "$inventory")"
    expected_sdk="$(jq -r '.sdk.version' "$REPO_ROOT/global.json")"
    while IFS= read -r entry; do
        component="$(jq -r '.component' <<< "$entry")"; repository="$(jq -r --arg component "$component" '.images[$component].repository // empty' "$inventory")"
        expected_platforms="$(deploy_images_component_contracts "$inventory" | jq -c --arg component "$component" 'select(.component == $component).platforms')"
        [[ -n "$repository" && -n "$expected_platforms" ]] || { deploy_fail images ledger image-mismatch; return 1; }
        if [[ "$distribution" == registry ]]; then
            jq -e --arg repository "$repository" --arg tag "$tag" --arg sdk "$expected_sdk" --argjson expected "$expected_platforms" '
              .repository == $repository and .tag == $tag and (.createdAt | type == "string" and length > 0) and .dotnetSdk == $sdk and
              (if .status == "push-intended" then
                 (keys | sort) == (["component","repository","tag","platforms","createdAt","dotnetSdk","status"] | sort) and .platforms == $expected
               elif .status == "built" then
                 (keys | sort) == (["component","repository","tag","reference","manifestDigest","platforms","createdAt","dotnetSdk","status"] | sort) and
                 (.manifestDigest | test("^sha256:[0-9a-f]{64}$")) and .reference == ($repository + "@" + .manifestDigest) and
                 ([.platforms[].architecture] | unique | sort) == $expected and
                 all(.platforms[]; (keys | sort) == (["architecture","digest"] | sort) and (.digest | test("^sha256:[0-9a-f]{64}$")))
               else false end)' <<< "$entry" >/dev/null 2>&1 ||
              { deploy_fail images ledger image-mismatch; return 1; }
        else
            architecture="$(jq -r '.architecture // empty' <<< "$entry")"
            jq -e --arg repository "$repository" --arg tag "$tag" --arg architecture "$architecture" --arg root "$artifact_root" --arg sdk "$expected_sdk" '
              (keys | sort) == (["component","architecture","repository","tag","reference","archivePath","archiveSha256","configDigest","createdAt","dotnetSdk","status"] | sort) and
              .repository == $repository and .tag == $tag and .architecture == $architecture and .status == "built" and
              (.createdAt | type == "string" and length > 0) and .dotnetSdk == $sdk and
              (.archiveSha256 | test("^[0-9a-f]{64}$")) and (.configDigest | test("^sha256:[0-9a-f]{64}$")) and .reference == .configDigest and
              .archivePath == ($root + "/" + .component + "-" + $architecture + "-sha256-" + .archiveSha256 + ".tar")' <<< "$entry" >/dev/null 2>&1 ||
              { deploy_fail images ledger image-mismatch; return 1; }
            jq -e --arg architecture "$architecture" 'index($architecture) != null' <<< "$expected_platforms" >/dev/null 2>&1 ||
              { deploy_fail images ledger platform-mismatch; return 1; }
            reference="$(jq -r '.archivePath' <<< "$entry")"
            [[ -f "$reference" && ! -L "$reference" && "$(stat -c '%u:%h:%a' "$reference" 2>/dev/null)" == "$(id -u):1:600" &&
               "$(sha256sum "$reference" | cut -d' ' -f1)" == "$(jq -r '.archiveSha256' <<< "$entry")" ]] ||
              { deploy_fail images ledger archive-drift; return 1; }
        fi
    done < <(jq -c '.images[]' <<< "$json")
    expected_components="$(deploy_images_component_contracts "$inventory" | jq -sc '[.[] | if $distribution == "registry" then [.component] else [.component as $c | .platforms[] | [$c,.]] end] | flatten(1)' --arg distribution "$distribution")"
    if [[ "$distribution" == registry ]]; then
        [[ "$(jq -c '[.images[] | select(.status == "built") | .component] | sort' <<< "$json")" == "$(jq -c 'sort' <<< "$expected_components")" || "$(jq -r '.phaseStatus' <<< "$json")" != passed ]] ||
          { deploy_fail images ledger incomplete-images; return 1; }
    elif [[ "$(jq -r '.phaseStatus' <<< "$json")" == passed ]]; then
        [[ "$(jq -c '[.images[] | [.component,.architecture]] | sort' <<< "$json")" == "$(jq -c 'sort' <<< "$expected_components")" ]] ||
          { deploy_fail images ledger incomplete-images; return 1; }
    fi
    expected_targets="$(jq -c '[.logicHost.name] + [.cameraAgents[].name] | sort' "$inventory")"
    if [[ "$(jq -r '.phaseStatus' <<< "$json")" == passed ]]; then
        [[ "$(jq -c '[.targets[].target] | sort' <<< "$json")" == "$expected_targets" ]] || { deploy_fail images ledger incomplete-targets; return 1; }
    fi
    while IFS= read -r entry; do
        target_name="$(jq -r '.target' <<< "$entry")"
        target="$(jq -c --arg name "$target_name" '([.logicHost] + .cameraAgents) | map(select(.name == $name))[0] // empty' "$inventory")"
        [[ -n "$target" ]] || { deploy_fail images ledger target-mismatch; return 1; }
        architecture="$(jq -r '.expectedArchitecture' <<< "$target")"
        if [[ "$target_name" == "$(jq -r '.logicHost.name' "$inventory")" ]]; then component=logicHost; else component=cameraAgent; fi
        reference="$(jq -r '.reference' <<< "$entry")"; image_id="$(jq -r '.imageId' <<< "$entry")"
        [[ "$(jq -r '.component' <<< "$entry")" == "$component" && "$(jq -r '.architecture' <<< "$entry")" == "$architecture" &&
           "$image_id" =~ ^sha256:[0-9a-f]{64}$ ]] || { deploy_fail images ledger target-mismatch; return 1; }
        jq -e --arg component "$component" --arg architecture "$architecture" --arg reference "$reference" --arg imageId "$image_id" --arg distribution "$distribution" '
          if $distribution == "registry" then
            any(.images[]; .component == $component and .reference == $reference)
          else
            $reference == $imageId and any(.images[]; .component == $component and .architecture == $architecture)
          end' <<< "$json" >/dev/null 2>&1 ||
          { deploy_fail images ledger target-reference-mismatch; return 1; }
    done < <(jq -c '.targets[]' <<< "$json")
}

deploy_require_committed_images() {
    local inventory="$1" run="$2" mode="$3" hash="$4" revision="$5"
    local state_dir evidence_dir ledger_path manifest_path evidence_path ledger manifest evidence expected tree distribution path
    state_dir="$(dirname "$DEPLOY_MANIFEST")"; evidence_dir="$(dirname "$DEPLOY_EVIDENCE")"
    ledger_path="$state_dir/images-ledger.json"; manifest_path="$state_dir/images-manifest.json"; evidence_path="$evidence_dir/images.json"
    for path in "$ledger_path" "$manifest_path" "$evidence_path"; do
        [[ -f "$path" && ! -L "$path" && "$(stat -c '%u:%h:%a' "$path" 2>/dev/null)" == "$(id -u):1:600" ]] ||
          { deploy_fail up images-authority missing-or-unsafe; return 1; }
    done
    if ! ledger="$(jq -c . "$ledger_path" 2>/dev/null)" || ! manifest="$(jq -c . "$manifest_path" 2>/dev/null)" ||
      ! evidence="$(jq -c . "$evidence_path" 2>/dev/null)"; then
        deploy_fail up images-authority invalid-json
        return 1
    fi
    tree="$(git -C "$REPO_ROOT" rev-parse "${revision}^{tree}")"; distribution="$(jq -r '.images.distributionMode' "$inventory")"
    deploy_validate_images_ledger "$inventory" "$ledger" "$run" "$mode" "$hash" "$revision" "$tree" "$distribution" || return 1
    deploy_validate_images_ledger "$inventory" "$manifest" "$run" "$mode" "$hash" "$revision" "$tree" "$distribution" || return 1
    [[ "$(jq -r '.phaseStatus' <<< "$ledger")" == passed && "$ledger" == "$manifest" ]] ||
      { deploy_fail up images-authority committed-state-mismatch; return 1; }
    expected="$(jq -c '{schemaVersion,runId,mode,inventorySha256,sourceRevision,sourceTree,builder,distributionMode,phaseStatus,startedAt,updatedAt,completedAt,
      images:[.images[] | del(.archivePath)],targets}' <<< "$ledger")"
    [[ "$evidence" == "$expected" ]] || { deploy_fail up images-authority committed-evidence-mismatch; return 1; }
    # shellcheck disable=SC2034 # Consumed by the sourced up phase.
    declare -g DEPLOY_COMMITTED_IMAGES_JSON="$ledger"
}

deploy_run_images() {
    local inventory="$1" run_id="$2" mode="$3" inventory_hash="$4" revision="$5" worktree_state="$6" tree="$7"
    local distribution artifact_root tag sdk now build_created component_contract component dockerfile platforms repository architecture image image_id archive_path
    local target target_name context inspect expected_digest reference target_entry image_entry created source_reference expected_evidence builder required_platforms intent inspect_id inspect_digest manifest_json manifest_status ledger_status
    deploy_images_require_prepare "$inventory" "$run_id" "$mode" "$inventory_hash" "$revision" "$worktree_state" || return 1
    deploy_images_assert_source "$revision" "$tree" || return 1
    distribution="$(jq -r '.images.distributionMode' "$inventory")"; artifact_root="$(jq -r '.images.artifactRoot // empty' "$inventory")"
    tag="$(jq -r '.images.tag' "$inventory")"; sdk="$(jq -r '.sdk.version' "$REPO_ROOT/global.json")"; builder="$(jq -r '.images.builder.name' "$inventory")"
    build_created="$(git -C "$REPO_ROOT" show -s --format=%cI "$revision")"
    [[ "$build_created" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T ]] || { deploy_fail images source commit-timestamp-invalid; return 1; }
    required_platforms="$(jq -c '[.logicHost.expectedArchitecture,.cameraAgents[].expectedArchitecture] | unique | sort' "$inventory")"
    deploy_images_validate_builder "$inventory" "$required_platforms" || return 1
    if [[ "$distribution" == archive ]]; then
        deploy_validate_output_root "$artifact_root" artifact-root || return 1
        [[ "$artifact_root" != "$REPO_ROOT" && "$artifact_root" != "$REPO_ROOT/"* &&
           "$REPO_ROOT" != "$artifact_root/"* &&
           "$artifact_root" != "$(dirname "$DEPLOY_MANIFEST")" && "$artifact_root" != "$(dirname "$DEPLOY_MANIFEST")/"* &&
           "$(dirname "$DEPLOY_MANIFEST")" != "$artifact_root/"* &&
           "$artifact_root" != "$(dirname "$DEPLOY_EVIDENCE")" && "$artifact_root" != "$(dirname "$DEPLOY_EVIDENCE")/"* &&
           "$(dirname "$DEPLOY_EVIDENCE")" != "$artifact_root/"* ]] ||
          { deploy_fail images artifact-root output-overlap; return 1; }
        install -d -m 700 -- "$artifact_root" || { deploy_fail images artifact-root create-failed; return 1; }
    fi
    DEPLOY_IMAGES_MANIFEST="$(dirname "$DEPLOY_MANIFEST")/images-manifest.json"
    DEPLOY_IMAGES_LEDGER="$(dirname "$DEPLOY_MANIFEST")/images-ledger.json"
    DEPLOY_IMAGES_EVIDENCE="$(dirname "$DEPLOY_EVIDENCE")/images.json"
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    if [[ -e "$DEPLOY_IMAGES_LEDGER" || -L "$DEPLOY_IMAGES_LEDGER" ]]; then
        [[ -f "$DEPLOY_IMAGES_LEDGER" && ! -L "$DEPLOY_IMAGES_LEDGER" && "$(stat -c '%u:%h:%a' "$DEPLOY_IMAGES_LEDGER" 2>/dev/null)" == "$(id -u):1:600" ]] ||
          { deploy_fail images ledger unsafe; return 1; }
        DEPLOY_IMAGES_LEDGER_JSON="$(jq -c . "$DEPLOY_IMAGES_LEDGER" 2>/dev/null)" || { deploy_fail images ledger invalid; return 1; }
        deploy_validate_images_ledger "$inventory" "$DEPLOY_IMAGES_LEDGER_JSON" "$run_id" "$mode" "$inventory_hash" "$revision" "$tree" "$distribution" || return 1
        [[ -f "$DEPLOY_IMAGES_MANIFEST" && ! -L "$DEPLOY_IMAGES_MANIFEST" &&
           "$(stat -c '%u:%h:%a' "$DEPLOY_IMAGES_MANIFEST" 2>/dev/null)" == "$(id -u):1:600" ]] ||
          { deploy_fail images resume manifest-missing-or-unsafe; return 1; }
        manifest_json="$(jq -c . "$DEPLOY_IMAGES_MANIFEST" 2>/dev/null)" || { deploy_fail images resume invalid-manifest; return 1; }
        deploy_validate_images_ledger "$inventory" "$manifest_json" "$run_id" "$mode" "$inventory_hash" "$revision" "$tree" "$distribution" || return 1
        manifest_status="$(jq -r '.phaseStatus' <<< "$manifest_json")"; ledger_status="$(jq -r '.phaseStatus' <<< "$DEPLOY_IMAGES_LEDGER_JSON")"
        if [[ "$manifest_status" == passed ]]; then
            [[ "$manifest_json" == "$DEPLOY_IMAGES_LEDGER_JSON" ]] || { deploy_fail images resume committed-state-mismatch; return 1; }
        fi
        if [[ "$ledger_status" == passed && "$manifest_status" == passed ]]; then
            [[ -f "$DEPLOY_IMAGES_EVIDENCE" && ! -L "$DEPLOY_IMAGES_EVIDENCE" &&
               "$(stat -c '%u:%h:%a' "$DEPLOY_IMAGES_EVIDENCE" 2>/dev/null)" == "$(id -u):1:600" ]] ||
              { deploy_fail images resume committed-evidence-missing; return 1; }
            expected_evidence="$(jq -c '{schemaVersion,runId,mode,inventorySha256,sourceRevision,sourceTree,builder,distributionMode,phaseStatus,startedAt,updatedAt,completedAt,
              images:[.images[] | del(.archivePath)],targets}' <<< "$DEPLOY_IMAGES_LEDGER_JSON")"
            jq -e --argjson expected "$expected_evidence" '. == $expected' "$DEPLOY_IMAGES_EVIDENCE" >/dev/null 2>&1 ||
              { deploy_fail images resume committed-evidence-mismatch; return 1; }
        elif [[ "$ledger_status" == passed && ( -e "$DEPLOY_IMAGES_EVIDENCE" || -L "$DEPLOY_IMAGES_EVIDENCE" ) ]]; then
            [[ -f "$DEPLOY_IMAGES_EVIDENCE" && ! -L "$DEPLOY_IMAGES_EVIDENCE" &&
               "$(stat -c '%u:%h:%a' "$DEPLOY_IMAGES_EVIDENCE" 2>/dev/null)" == "$(id -u):1:600" ]] ||
              { deploy_fail images resume unsafe-staged-evidence; return 1; }
            jq -e . "$DEPLOY_IMAGES_EVIDENCE" >/dev/null 2>&1 || { deploy_fail images resume invalid-staged-evidence; return 1; }
        fi
        DEPLOY_IMAGES_LEDGER_JSON="$(jq -c --arg now "$now" '.phaseStatus="running" | .updatedAt=$now | del(.completedAt)' <<< "$DEPLOY_IMAGES_LEDGER_JSON")"
    else
        DEPLOY_IMAGES_LEDGER_JSON="$(jq -cn --arg run "$run_id" --arg mode "$mode" --arg hash "$inventory_hash" --arg revision "$revision" \
          --arg tree "$tree" --arg distribution "$distribution" --arg now "$now" --argjson builder "$(jq -c '.images.builder' "$inventory")" \
          '{schemaVersion:1,runId:$run,mode:$mode,inventorySha256:$hash,sourceRevision:$revision,sourceTree:$tree,builder:$builder,
          distributionMode:$distribution,phaseStatus:"running",startedAt:$now,updatedAt:$now,images:[],targets:[]}')"
    fi
    DEPLOY_IMAGES_MANIFEST_JSON="$DEPLOY_IMAGES_LEDGER_JSON"
    deploy_publish_json "$DEPLOY_IMAGES_LEDGER" "$DEPLOY_IMAGES_LEDGER_JSON" || return 1
    deploy_publish_json "$DEPLOY_IMAGES_MANIFEST" "$DEPLOY_IMAGES_MANIFEST_JSON" || return 1
    # shellcheck disable=SC2034 # Read by the entrypoint's failure trap.
    DEPLOY_IMAGES_STATE_ACCEPTED=true
    if [[ -e "$DEPLOY_IMAGES_EVIDENCE" || -L "$DEPLOY_IMAGES_EVIDENCE" ]]; then rm -f -- "$DEPLOY_IMAGES_EVIDENCE" || return 1; fi

    while IFS= read -r component_contract; do
        deploy_images_assert_source "$revision" "$tree" || return 1
        component="$(jq -r '.component' <<< "$component_contract")"; dockerfile="$(jq -r '.dockerfile' <<< "$component_contract")"
        platforms="$(jq -c '.platforms' <<< "$component_contract")"; repository="$(jq -r --arg component "$component" '.images[$component].repository' "$inventory")"
        image_entry="$(jq -c --arg component "$component" '.images[]? | select(.component == $component)' <<< "$DEPLOY_IMAGES_LEDGER_JSON")"
        if [[ "$distribution" == registry ]]; then
            if [[ -z "$image_entry" ]]; then
                intent="$(jq -cn --arg component "$component" --arg repository "$repository" --arg tag "$tag" --arg created "$build_created" --arg sdk "$sdk" \
                  --argjson platforms "$platforms" '{component:$component,repository:$repository,tag:$tag,platforms:$platforms,createdAt:$created,dotnetSdk:$sdk,status:"push-intended"}')"
                DEPLOY_IMAGES_LEDGER_JSON="$(jq -c --argjson image "$intent" '.images += [$image]' <<< "$DEPLOY_IMAGES_LEDGER_JSON")"
                deploy_images_publish || return 1
                image_entry="$intent"
            fi
            if [[ "$(jq -r '.status' <<< "$image_entry")" == push-intended ]]; then
                created="$(jq -r '.createdAt' <<< "$image_entry")"
                deploy_images_resolve_registry "$component" "$dockerfile" "$platforms" "$repository" "$tag" "$revision" "$tree" "$created" "$sdk" "$builder" || return 1
                DEPLOY_IMAGES_LEDGER_JSON="$(jq -c --arg component "$component" --argjson image "$DEPLOY_REGISTRY_IMAGE_JSON" \
                  '.images = [.images[] | if .component == $component then $image else . end]' <<< "$DEPLOY_IMAGES_LEDGER_JSON")"
                deploy_images_publish || return 1
                [[ "${DEPLOY_TEST_FAILPOINT:-}" != after-image-build ]] || return 75
            else
                created="$(jq -r '.createdAt' <<< "$image_entry")"
                deploy_images_observe_registry "$component" "$platforms" "$repository" "$tag" "$revision" "$tree" "$created" "$sdk" "$builder" || {
                    [[ "$?" != 2 ]] || deploy_fail images "$component" completed-registry-tag-missing
                    return 1
                }
                jq -ne --argjson expected "$image_entry" --argjson observed "$DEPLOY_REGISTRY_IMAGE_JSON" '$expected == $observed' >/dev/null ||
                  { deploy_fail images "$component" completed-registry-drift; return 1; }
            fi
        else
            while IFS= read -r architecture; do
                image_entry="$(jq -c --arg component "$component" --arg architecture "$architecture" '.images[]? | select(.component == $component and .architecture == $architecture)' <<< "$DEPLOY_IMAGES_LEDGER_JSON")"
                if [[ -z "$image_entry" ]]; then
                    image="$(deploy_images_build_archive "$component" "$dockerfile" "$architecture" "$repository" "$tag" "$revision" "$tree" "$build_created" "$sdk" "$artifact_root" "$builder")" || return 1
                    DEPLOY_IMAGES_LEDGER_JSON="$(jq -c --argjson image "$image" '.images += [$image]' <<< "$DEPLOY_IMAGES_LEDGER_JSON")"
                    deploy_images_publish || return 1
                    [[ "${DEPLOY_TEST_FAILPOINT:-}" != after-image-build ]] || return 75
                fi
            done < <(jq -r '.[]' <<< "$platforms")
        fi
        deploy_images_assert_source "$revision" "$tree" || return 1
    done < <(deploy_images_component_contracts "$inventory")

    while IFS= read -r target; do
        deploy_images_assert_source "$revision" "$tree" || return 1
        target_name="$(jq -r '.name' <<< "$target")"; context="$(jq -r '.dockerContext' <<< "$target")"; architecture="$(jq -r '.expectedArchitecture' <<< "$target")"
        if [[ "$target_name" == "$(jq -r '.logicHost.name' "$inventory")" ]]; then component=logicHost; else component=cameraAgent; fi
        target_entry="$(jq -c --arg target "$target_name" '.targets[]? | select(.target == $target)' <<< "$DEPLOY_IMAGES_LEDGER_JSON")"
        image_entry="$(jq -c --arg component "$component" --arg architecture "$architecture" --arg distribution "$distribution" \
          '.images[] | select(.component == $component and ($distribution == "registry" or .architecture == $architecture))' <<< "$DEPLOY_IMAGES_LEDGER_JSON")"
        created="$(jq -r '.createdAt' <<< "$image_entry")"; source_reference="$(jq -r '.repository + ":" + .tag' <<< "$image_entry")"
        if [[ -n "$target_entry" ]]; then
            reference="$(jq -r '.reference' <<< "$target_entry")"; image_id="$(jq -r '.imageId' <<< "$target_entry")"
            deploy_images_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
            inspect="$(deploy_transport_image_inspect "$context" "$reference")" || { deploy_fail images "$target_name" completed-image-missing; return 1; }
            expected_digest=""; [[ "$distribution" != registry ]] || expected_digest="$reference"
            deploy_images_validate_inspect "$inspect" "$architecture" "$revision" "$tree" "$component" "$image_id" "$expected_digest" "$created" "$sdk" "$source_reference" ||
              { deploy_fail images "$target_name" completed-image-drift; return 1; }
            deploy_images_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
            deploy_status images "$target_name" passed resumed
            continue
        fi
        deploy_images_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
        if [[ "$distribution" == registry ]]; then
            reference="$(jq -r '.reference' <<< "$image_entry")"; expected_digest="$reference"
            deploy_transport_registry_pull "$context" "$reference" || { deploy_fail images "$target_name" pull-failed; return 1; }
        else
            [[ -n "$image_entry" ]] || { deploy_fail images "$target_name" platform-artifact-missing; return 1; }
            archive_path="$(jq -r '.archivePath' <<< "$image_entry")"; expected_digest="$(jq -r '.archiveSha256' <<< "$image_entry")"
            [[ -f "$archive_path" && ! -L "$archive_path" && "$(sha256sum "$archive_path" | cut -d' ' -f1)" == "$expected_digest" ]] ||
              { deploy_fail images "$target_name" archive-drift; return 1; }
            deploy_transport_archive_load "$context" "$archive_path" || { deploy_fail images "$target_name" load-failed; return 1; }
            reference="$source_reference"
        fi
        deploy_images_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
        [[ "${DEPLOY_TEST_FAILPOINT:-}" != after-image-transfer ]] || return 75
        inspect="$(deploy_transport_image_inspect "$context" "$reference")" || { deploy_fail images "$target_name" inspect-failed; return 1; }
        inspect_id=""; inspect_digest=""
        if [[ "$distribution" != archive ]]; then inspect_digest="$expected_digest"; fi
        deploy_images_validate_inspect "$inspect" "$architecture" "$revision" "$tree" "$component" "$inspect_id" "$inspect_digest" \
          "$created" "$sdk" "$source_reference" || { deploy_fail images "$target_name" image-identity-mismatch; return 1; }
        deploy_images_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
        image_id="$(jq -r '.id' <<< "$inspect")"
        if [[ "$distribution" == archive ]]; then reference="$image_id"; fi
        DEPLOY_IMAGES_LEDGER_JSON="$(jq -c --arg target "$target_name" --arg component "$component" --arg architecture "$architecture" \
          --arg reference "$reference" --arg imageId "$image_id" '.targets += [{target:$target,component:$component,architecture:$architecture,reference:$reference,imageId:$imageId,status:"verified"}]' <<< "$DEPLOY_IMAGES_LEDGER_JSON")"
        deploy_images_publish || return 1
        deploy_status images "$target_name" passed verified
    done < <(jq -c '([.logicHost] + .cameraAgents)[]' "$inventory")
    deploy_images_assert_source "$revision" "$tree" || return 1
    now="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
    DEPLOY_IMAGES_LEDGER_JSON="$(jq -c --arg now "$now" '.phaseStatus="passed" | .updatedAt=$now | .completedAt=$now' <<< "$DEPLOY_IMAGES_LEDGER_JSON")"
    DEPLOY_IMAGES_MANIFEST_JSON="$DEPLOY_IMAGES_LEDGER_JSON"
    deploy_images_publish
}
