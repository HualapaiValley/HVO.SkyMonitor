#!/usr/bin/env bash

deploy_is_canonical_uuid() {
    [[ "$1" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$ ]]
}

deploy_is_catalog_id() {
    [[ "$1" =~ ^[a-z0-9][a-z0-9-]{0,31}$ ]]
}

deploy_component_directory() {
    case "$1" in
      logicHost) printf 'logichosts\n' ;;
      cameraAgent) printf 'cameraagents\n' ;;
      *) return 1 ;;
    esac
}

deploy_target_component() {
    local inventory="$1" target_name="$2"
    if [[ "$(jq -r '.logicHost.name' "$inventory")" == "$target_name" ]]; then
        printf 'logicHost\n'
    elif jq -e --arg name "$target_name" 'any(.cameraAgents[]; .name == $name)' "$inventory" >/dev/null; then
        printf 'cameraAgent\n'
    else
        return 1
    fi
}

deploy_catalog_for_target() {
    local inventory="$1" target="$2" catalog_id
    catalog_id="$(jq -r '.catalogId' <<< "$target")"
    jq -ec --arg id "$catalog_id" '.catalogs[] | select(.catalogId == $id)' "$inventory"
}

deploy_catalog_root() {
    local inventory="$1" target="$2" catalog
    catalog="$(deploy_catalog_for_target "$inventory" "$target")" || return 1
    printf '%s/catalogs/%s\n' "$(jq -r '.productRoot' "$inventory")" "$(jq -r '.catalogId' <<< "$catalog")"
}

deploy_instance_manifest_json() {
    local inventory="$1" target="$2" component="$3"
    jq -cn --arg component "$component" --arg instance "$(jq -r '.instanceId' <<< "$target")" \
      --arg installation "$(jq -r '.installationId' "$inventory")" \
      '{schemaVersion:1,product:"HVO.SkyMonitor",component:$component,instanceId:$instance,
        creationProvenance:{installationId:$installation},applicationIdentityFile:"application-identity.json"}'
}

deploy_application_binding_json() {
    local target="$1"
    jq -cn --arg configured "$(jq -r '.applicationIdentity' <<< "$target")" \
      '{schemaVersion:1,state:"pre-provisioning",configuredIdentity:$configured,boundIdentity:null}'
}

deploy_compose_project() {
    local inventory="$1" target="$2" compact
    compact="$(jq -r '.instanceId | gsub("-"; "")' <<< "$target")"
    printf '%s-%s\n' "$(jq -r '.deployment.resources.project' "$inventory")" "$compact"
}

deploy_validate_product_layout() {
    local inventory="$1" mode="$2" product_root target component component_directory expected_root catalog catalog_id expected_catalog_root
    product_root="$(jq -r '.productRoot' "$inventory")"
    deploy_is_safe_absolute_path "$product_root" || deploy_fail validate product-layout unsafe-product-root || return 1
    [[ "$(realpath -m -- "$product_root" 2>/dev/null)" == "$product_root" ]] || deploy_fail validate product-layout noncanonical-product-root || return 1
    [[ "$mode" != persistent || "$product_root" == /var/lib/hvo/skymonitor || "$product_root" == /tmp/hvo-deploy-test.* ]] ||
      deploy_fail validate product-layout persistent-product-root-mismatch || return 1

    while IFS= read -r target; do
        component="$(deploy_target_component "$inventory" "$(jq -r '.name' <<< "$target")")" || return 1
        component_directory="$(deploy_component_directory "$component")" || return 1
        deploy_is_canonical_uuid "$(jq -r '.instanceId' <<< "$target")" ||
          { deploy_fail validate product-layout invalid-instance-uuid; return 1; }
        expected_root="$product_root/$component_directory/$(jq -r '.instanceId' <<< "$target")"
        [[ "$(jq -r '.runtimeRoot' <<< "$target")" == "$expected_root" ]] ||
          { deploy_fail validate product-layout instance-root-mismatch; return 1; }
        deploy_is_catalog_id "$(jq -r '.catalogId' <<< "$target")" ||
          { deploy_fail validate product-layout invalid-catalog-id; return 1; }
        deploy_catalog_for_target "$inventory" "$target" >/dev/null ||
          { deploy_fail validate product-layout unknown-catalog-reference; return 1; }
    done < <(jq -c '([.logicHost] + .cameraAgents)[]' "$inventory")

    while IFS= read -r catalog; do
        catalog_id="$(jq -r '.catalogId' <<< "$catalog")"
        deploy_is_catalog_id "$catalog_id" || { deploy_fail validate product-layout invalid-catalog-id; return 1; }
        expected_catalog_root="$product_root/catalogs/$catalog_id"
        [[ "$(jq -r '.installRoot' <<< "$catalog")" == "$expected_catalog_root" ]] ||
          { deploy_fail validate product-layout catalog-root-mismatch; return 1; }
    done < <(jq -c '.catalogs[]' "$inventory")

    jq -e '
      . as $inventory | ([.logicHost] + .cameraAgents) as $apps |
      ([$apps[].instanceId] | length == (unique | length)) and
      ([$apps[].applicationIdentity] | length == (unique | length)) and
      ([$apps[].cookieName] | length == (unique | length)) and
      ([.catalogs[].catalogId] | length == (unique | length)) and
      ([.catalogs[].installRoot] | length == (unique | length)) and
      all($apps[]; .cookieName == ("hvo.skymonitor." + (.instanceId | gsub("-"; "")))) and
      all($apps[]; .runtimeRoot as $root |
        all($apps[]; .runtimeRoot as $other | $other == $root or (($other | startswith($root + "/") | not) and ($root | startswith($other + "/") | not))) and
        all($inventory.catalogs[]; .installRoot as $catalog | (($catalog | startswith($root + "/") | not) and ($root | startswith($catalog + "/") | not))))
    ' "$inventory" >/dev/null || deploy_fail validate product-layout identity-or-path-collision || return 1
}
