#!/usr/bin/env bash

deploy_validate_inventory() {
    local inventory="$1" mode="$2"
    [[ -f "$inventory" && ! -L "$inventory" ]] || deploy_fail validate inventory "inventory-missing-or-symlink" || return 1
    jq -e '
      def exact($a): type == "object" and ((keys | sort) == ($a | sort));
      def text: type == "string" and length > 0 and (test("[[:cntrl:]]") | not);
      def name: text and test("^[a-z0-9][a-z0-9-]{0,31}$");
      def token: text and test("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$");
      def root: text and startswith("/") and . != "/" and
        (contains("//") | not) and (split("/") | any(. == "." or . == "..") | not);
      def url: text and test("^https?://[A-Za-z0-9][A-Za-z0-9.-]*:[0-9]{1,5}(/[^[:space:]]*)?$") and
        (contains("@") | not);
      def base: exact(["name","sshHost","dockerContext","expectedArchitecture","expectedHostName","expectedHostIdentity","expectedDockerDaemonIdentity","runtimeRoot"]) and
        (.name | name) and (.sshHost | text and test("^[A-Za-z0-9][A-Za-z0-9._@-]{0,254}$")) and
        (.dockerContext | text and test("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")) and
        (.expectedArchitecture == "amd64" or .expectedArchitecture == "arm64") and
        (.expectedHostName | text and test("^[A-Za-z0-9][A-Za-z0-9.-]{0,127}$")) and
        (.expectedHostIdentity | token) and (.expectedDockerDaemonIdentity | token) and (.runtimeRoot | root);
      def app: exact(["name","sshHost","dockerContext","expectedArchitecture","expectedHostName","expectedHostIdentity","expectedDockerDaemonIdentity","runtimeRoot","publicEndpoint","ports"]) and
        ({name,sshHost,dockerContext,expectedArchitecture,expectedHostName,expectedHostIdentity,expectedDockerDaemonIdentity,runtimeRoot} | base) and
        (.publicEndpoint | url) and (.ports | type == "array" and length > 0 and all(type == "number" and floor == . and . >= 1 and . <= 65535) and length == (unique | length)) and
        (. as $target | (.publicEndpoint | capture("^https?://[^/:]+:(?<port>[0-9]+)").port | tonumber) as $publicPort |
          $publicPort <= 65535 and ($target.ports | index($publicPort) != null));
      exact(["schemaVersion","environment","source","secretSource","logicHost","cameraAgents","sharedServices","serviceEndpoints","catalog","images"]) and
      .schemaVersion == 1 and (.environment | name) and
      (.source | exact(["revision","dirtyDisposition"]) and (.revision | type == "string" and test("^[0-9a-f]{40}$")) and
        (.dirtyDisposition == "require-clean" or .dirtyDisposition == "allow-dirty")) and
      (.secretSource | exact(["path","requiredReferences"]) and (.path | text) and
        (.requiredReferences | type == "array" and length > 0 and length == (unique | length) and
          all(type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$")))) and
      (.logicHost | app) and (.cameraAgents | type == "array" and length > 0 and all(app)) and
      (.sharedServices == null or (.sharedServices | app)) and
      (.serviceEndpoints | type == "array" and all(exact(["name","host","port","fromTargets"]) and (.name | name) and
        (.host | text and test("^[A-Za-z0-9][A-Za-z0-9.-]{0,253}$")) and (.port | type == "number" and floor == . and . >= 1 and . <= 65535) and
        (.fromTargets | type == "array" and length > 0 and length == (unique | length) and all(type == "string")))) and
      (.catalog | exact(["kind","version","sha256"]) and (.kind == "production" or .kind == "fixture") and
        (.version | text and test("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")) and (.sha256 | type == "string" and test("^[0-9a-fA-F]{64}$"))) and
      (.images | exact(["logicHost","cameraAgent"]) and all(.[]; exact(["repository","digest"]) and
        (.repository | text and test("^[A-Za-z0-9][A-Za-z0-9./_-]{0,255}$")) and
        (.digest | type == "string" and test("^sha256:[0-9a-f]{64}$"))))
    ' "$inventory" >/dev/null 2>&1 || deploy_fail validate inventory "schema-or-value-invalid" || return 1

    local target_count unique_count collision_count unknown_route
    target_count="$(jq '([.logicHost.name] + [.cameraAgents[].name] + (if .sharedServices then [.sharedServices.name] else [] end)) | length' "$inventory")"
    unique_count="$(jq '([.logicHost.name] + [.cameraAgents[].name] + (if .sharedServices then [.sharedServices.name] else [] end)) | unique | length' "$inventory")"
    [[ "$target_count" == "$unique_count" ]] || deploy_fail validate inventory "duplicate-target-name" || return 1
    collision_count="$(jq '([.logicHost] + .cameraAgents + (if .sharedServices then [.sharedServices] else [] end)) |
      [group_by(.sshHost)[], group_by(.dockerContext)[], group_by(.expectedHostName)[], group_by(.expectedHostIdentity)[], group_by(.expectedDockerDaemonIdentity)[] | select(length > 1)] | length' "$inventory")"
    [[ "$collision_count" == 0 ]] || deploy_fail validate inventory "target-identity-collision" || return 1
    unknown_route="$(jq '([.logicHost.name] + [.cameraAgents[].name] + (if .sharedServices then [.sharedServices.name] else [] end)) as $names |
      [.serviceEndpoints[].fromTargets[] | select(. as $n | $names | index($n) | not)] | length' "$inventory")"
    [[ "$unknown_route" == 0 ]] || deploy_fail validate inventory "unknown-endpoint-route" || return 1
    if [[ "$mode" == persistent ]]; then
        jq -e '.catalog.kind == "production" and
          (([.logicHost] + .cameraAgents + (if .sharedServices then [.sharedServices] else [] end)) |
            all(.publicEndpoint; startswith("https://")))' "$inventory" >/dev/null ||
            deploy_fail validate inventory "persistent-mode-requires-production-https" || return 1
    fi
    local root_duplicates port_duplicates
    root_duplicates="$(jq '([.logicHost] + .cameraAgents + (if .sharedServices then [.sharedServices] else [] end)) |
      [group_by([.expectedHostIdentity,.runtimeRoot])[] | select(length > 1)] | length' "$inventory")"
    port_duplicates="$(jq '([.logicHost] + .cameraAgents) | map(. as $t | .ports[] | [$t.expectedHostIdentity, .]) |
      [group_by(.)[] | select(length > 1)] | length' "$inventory")"
    [[ "$root_duplicates" == 0 && "$port_duplicates" == 0 ]] || deploy_fail validate inventory "target-resource-collision" || return 1
}

deploy_validate_secret_references() {
    local inventory="$1" secret_path reference
    secret_path="$(jq -er '.secretSource.path' "$inventory")"
    if ! deploy_is_safe_absolute_path "$secret_path" || [[ "$(realpath -m -- "$secret_path" 2>/dev/null)" != "$secret_path" ]]; then
        deploy_fail validate secret-source "noncanonical-secret-source"
        return 1
    fi
    deploy_validate_private_file "$secret_path" || return 1
    while IFS= read -r reference; do
        if ! grep -Eq "^${reference}=" "$secret_path" 2>/dev/null; then
            deploy_fail validate secret-reference "required-reference-missing" || return 1
        fi
    done < <(jq -r '.secretSource.requiredReferences[]' "$inventory")
}

deploy_inventory_targets() {
    jq -c '([.logicHost] + .cameraAgents + (if .sharedServices then [.sharedServices] else [] end)) | .[]' "$1"
}
