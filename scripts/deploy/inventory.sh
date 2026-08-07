#!/usr/bin/env bash

deploy_validate_inventory() {
    local inventory="$1" mode="$2"
    [[ -f "$inventory" && ! -L "$inventory" ]] || deploy_fail validate inventory "inventory-missing-or-symlink" || return 1
    jq -e --arg mode "$mode" '
      def exact($a): type == "object" and ((keys | sort) == ($a | sort));
      def text: type == "string" and length > 0 and (test("[[:cntrl:]]") | not);
      def name: text and test("^[a-z0-9][a-z0-9-]{0,31}$");
      def token: text and test("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$");
      def root: text and startswith("/") and . != "/" and
        (contains("//") | not) and (split("/") | any(. == "." or . == "..") | not);
       def url: text and test("^https?://[A-Za-z0-9][A-Za-z0-9.-]*:[0-9]{1,5}(/[^[:space:]]*)?$") and
         (contains("@") | not);
       def ipv4: type == "string" and test("^[0-9]{1,3}(?:[.][0-9]{1,3}){3}$") and
         (split(".") | all(tonumber >= 0 and tonumber <= 255));
      def base: exact(["name","sshHost","dockerContext","expectedArchitecture","expectedHostName","expectedHostIdentity","expectedDockerDaemonIdentity","runtimeRoot","runtimeOwner"]) and
        (.name | name) and (.sshHost | text and test("^[A-Za-z0-9][A-Za-z0-9._@-]{0,254}$")) and
        (.dockerContext | text and test("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")) and
        (.expectedArchitecture == "amd64" or .expectedArchitecture == "arm64") and
        (.expectedHostName | text and test("^[A-Za-z0-9][A-Za-z0-9.-]{0,127}$")) and
        (.expectedHostIdentity | token) and (.expectedDockerDaemonIdentity | token) and (.runtimeRoot | root) and
        (.runtimeOwner | text and test("^[a-z_][a-z0-9_-]{0,31}$"));
       def infra: exact(["name","sshHost","dockerContext","expectedArchitecture","expectedHostName","expectedHostIdentity","expectedDockerDaemonIdentity","runtimeRoot","runtimeOwner","ports"]) and
         ({name,sshHost,dockerContext,expectedArchitecture,expectedHostName,expectedHostIdentity,expectedDockerDaemonIdentity,runtimeRoot,runtimeOwner} | base) and
         (.ports | type == "array" and length > 0 and all(type == "number" and floor == . and . >= 1 and . <= 65535) and length == (unique | length));
       def app: exact(["name","sshHost","dockerContext","expectedArchitecture","expectedHostName","expectedHostIdentity","expectedDockerDaemonIdentity","runtimeRoot","runtimeOwner","internalEndpoint","publicEndpoint","trustedProxyAddresses","ports"]) and
         ({name,sshHost,dockerContext,expectedArchitecture,expectedHostName,expectedHostIdentity,expectedDockerDaemonIdentity,runtimeRoot,runtimeOwner} | base) and
         (.publicEndpoint | url) and (.internalEndpoint | url and startswith("http://")) and
         (.trustedProxyAddresses | type == "array" and length == (unique | length) and all(ipv4)) and
         (.ports | type == "array" and length > 0 and all(type == "number" and floor == . and . >= 1 and . <= 65535) and length == (unique | length)) and
         (. as $target | (.internalEndpoint | capture("^http://[^/:]+:(?<port>[0-9]+)").port | tonumber) as $internalPort |
           $internalPort <= 65535 and ($target.ports | index($internalPort) != null));
       def camera: exact(["name","sshHost","dockerContext","expectedArchitecture","expectedHostName","expectedHostIdentity","expectedDockerDaemonIdentity","runtimeRoot","runtimeOwner","internalEndpoint","publicEndpoint","trustedProxyAddresses","ports","moduleConfigPath","ownerPasswordSecretReference"]) and
         ({name,sshHost,dockerContext,expectedArchitecture,expectedHostName,expectedHostIdentity,expectedDockerDaemonIdentity,runtimeRoot,runtimeOwner,
           internalEndpoint,publicEndpoint,trustedProxyAddresses,ports} | app) and
         (.moduleConfigPath | root) and (.ownerPasswordSecretReference | type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$"));
       exact(["schemaVersion","environment","installationId","source","secretSource","logicHost","cameraAgents","sharedServices","serviceEndpoints","catalog","images","deployment"]) and
       .schemaVersion == 5 and (.environment | name) and (.installationId | type == "string" and test("^[a-z0-9][a-z0-9-]{0,63}$")) and
      (.source | exact(["revision","dirtyDisposition"]) and (.revision | type == "string" and test("^[0-9a-f]{40}$")) and
        (.dirtyDisposition == "require-clean" or .dirtyDisposition == "allow-dirty")) and
      (.secretSource | exact(["path","requiredReferences"]) and (.path | text) and
        (.requiredReferences | type == "array" and length > 0 and length == (unique | length) and
          all(type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$")))) and
       (.logicHost | app and (.publicEndpoint | startswith("https://"))) and
       (.cameraAgents | type == "array" and length > 0 and all(camera) and
        ([.[].ownerPasswordSecretReference] | unique | length) == length) and
      (.sharedServices == null or (.sharedServices | infra)) and
      (.serviceEndpoints | type == "array" and all(exact(["name","host","port","fromTargets"]) and (.name | name) and
        (.host | text and test("^[A-Za-z0-9][A-Za-z0-9.-]{0,253}$")) and (.port | type == "number" and floor == . and . >= 1 and . <= 65535) and
        (.fromTargets | type == "array" and length > 0 and length == (unique | length) and all(type == "string")))) and
      (.catalog | exact(["kind","version","sha256","length","rowCount"]) and (.kind == "production" or .kind == "fixture") and
        (.version | text and test("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")) and (.sha256 | type == "string" and test("^[0-9a-fA-F]{64}$")) and
        (.length | type == "number" and floor == . and . > 0) and (.rowCount | type == "number" and floor == . and . > 0)) and
      (.images | exact(["distributionMode","artifactRoot","tag","builder","registryImmutableTags","logicHost","cameraAgent"]) and
        (.distributionMode == "registry" or .distributionMode == "archive") and
        (.artifactRoot == null or (.artifactRoot | root)) and
        (.tag | type == "string" and test("^rev-[0-9a-f]{40}$") and . != "latest") and
        (.builder | exact(["name","driver","endpoint","expectedDaemonIdentity","expectedDaemonName","expectedArchitecture"]) and
          (.name | text and test("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")) and
          (.driver == "docker" or .driver == "docker-container") and
          .endpoint == "default" and
          (.expectedDaemonIdentity | token) and (.expectedDaemonName | text and test("^[A-Za-z0-9][A-Za-z0-9.-]{0,127}$")) and
          (.expectedArchitecture == "amd64" or .expectedArchitecture == "arm64")) and
        (.registryImmutableTags | type == "boolean") and
        (.logicHost | exact(["repository"]) and (.repository | text and
          test("^[a-z0-9]([a-z0-9._-]*[a-z0-9])?/[a-z0-9]([a-z0-9._/-]*[a-z0-9])?$") and
          (split("/") | all(length > 0 and . != "." and . != "..")) and ((contains("//") or contains("@") or contains(":")) | not))) and
        (.cameraAgent | exact(["repository"]) and (.repository | text and
          test("^[a-z0-9]([a-z0-9._-]*[a-z0-9])?/[a-z0-9]([a-z0-9._/-]*[a-z0-9])?$") and
          (split("/") | all(length > 0 and . != "." and . != "..")) and ((contains("//") or contains("@") or contains(":")) | not)))) and
      .images.logicHost.repository != .images.cameraAgent.repository and
      .images.tag == ("rev-" + .source.revision) and
      ((.images.distributionMode == "archive" and .images.artifactRoot != null and .images.registryImmutableTags == false) or
       (.images.distributionMode == "registry" and .images.artifactRoot == null and .images.registryImmutableTags == true)) and
      (.deployment | exact(["catalog","services","resources","certificates","deviceBootstrap","secretMappings","limits"]) and
        (.catalog | exact(["bundlePath","installRoot","allowFixture"]) and (.bundlePath | root) and (.installRoot | root) and (.allowFixture | type == "boolean")) and
        (.services | exact(["mode","sql","redis","minio","smtp","images"]) and (.mode == "existing" or .mode == "deploy") and
          all(.sql,.redis,.minio,.smtp; .host | text and test("^[A-Za-z0-9][A-Za-z0-9.-]{0,253}$")) and
          all(.sql,.redis,.minio,.smtp; .port | type == "number" and floor == . and . >= 1 and . <= 65535) and
          (.sql | exact(["host","port","database","adminUser","adminSecretReference","initializerUser","initializerSecretReference","initializerConnectionReference","runtimeUser","runtimeSecretReference","runtimeConnectionReference"]) and (.database | name) and
            all(.adminUser,.initializerUser,.runtimeUser; type == "string" and test("^[A-Za-z][A-Za-z0-9._-]{0,63}$")) and
            all(.adminSecretReference,.initializerSecretReference,.initializerConnectionReference,.runtimeSecretReference,.runtimeConnectionReference; type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$"))) and
          (.redis | exact(["host","port","prefix","adminSecretReference","user","secretReference"]) and (.prefix | text and test("^[a-z0-9][a-z0-9:-]{0,63}:$")) and
            (.user | type == "string" and test("^[A-Za-z][A-Za-z0-9._-]{0,63}$")) and
            all(.adminSecretReference,.secretReference; type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$"))) and
          (.minio | exact(["host","port","useSsl","artifactBucket","diagnosticsBucket","rootAccessKeyReference","rootSecretKeyReference","accessKeyReference","secretKeyReference"]) and (.useSsl | type == "boolean") and
            (.artifactBucket | name) and (.diagnosticsBucket | name) and .artifactBucket != .diagnosticsBucket and
            all(.rootAccessKeyReference,.rootSecretKeyReference,.accessKeyReference,.secretKeyReference; type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$"))) and
          (.smtp | exact(["kind","host","port","usernameReference","passwordReference"]) and (.kind == "production" or .kind == "mailpit") and
            (.usernameReference == null or (.usernameReference | type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$"))) and
            (.passwordReference == null or (.passwordReference | type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$")))) and
          (.images | exact(["sqlServer","redis","minio","minioClient","mailpit"]) and all(.[]; . == null or (type == "string" and test("^[a-z0-9][a-z0-9._/-]+@sha256:[0-9a-f]{64}$"))))) and
        (.resources | exact(["project","sqlDatabase","redisPrefix","artifactBucket","diagnosticsBucket"]) and
          (.project | name) and (.sqlDatabase | name) and (.redisPrefix | text and test("^[a-z0-9][a-z0-9:-]{0,63}:$")) and
          (.artifactBucket | name) and (.diagnosticsBucket | name)) and
        (.certificates | exact(["signingPath","encryptionPath","signingPasswordReference","encryptionPasswordReference"]) and
          (.signingPath | root) and (.encryptionPath | root) and
          all(.signingPasswordReference,.encryptionPasswordReference; . == null or (type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$")))) and
        (.deviceBootstrap | exact(["clientId","clientSecretReference","scopes"]) and
          (.clientId | text and test("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")) and
          (.clientSecretReference | type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$")) and
          (.scopes | type == "array" and length > 0 and length == (unique | length) and all(type == "string" and test("^[a-z][a-z0-9.:-]{0,127}$")))) and
        (.secretMappings | type == "array" and length > 0 and length == ([.[].key] | unique | length) and all(exact(["reference","key"]) and
          (.reference | type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$")) and (.key | text and test("^[A-Za-z][A-Za-z0-9]*(?:__[A-Za-z0-9][A-Za-z0-9-]*)+$")))) and
        (.limits | exact(["cpus","memory"]) and (.cpus == null or (.cpus | type == "string" and test("^[0-9]+(?:[.][0-9]+)?$"))) and
          (.memory == null or (.memory | type == "string" and test("^[0-9]+[MG]$"))))) and
      .deployment.resources.sqlDatabase == .deployment.services.sql.database and
      .deployment.resources.redisPrefix == .deployment.services.redis.prefix and
      .deployment.resources.artifactBucket == .deployment.services.minio.artifactBucket and
      .deployment.resources.diagnosticsBucket == .deployment.services.minio.diagnosticsBucket and
      (.deployment as $deployment |
        any($deployment.secretMappings[]; .reference == $deployment.services.sql.runtimeConnectionReference and .key == "ConnectionStrings__skymonitordb") and
        any($deployment.secretMappings[]; .reference == $deployment.services.sql.initializerConnectionReference and .key == "ConnectionStrings__skymonitordb-migrations") and
        any($deployment.secretMappings[]; .reference == $deployment.services.minio.accessKeyReference and .key == "Minio__AccessKey") and
        any($deployment.secretMappings[]; .reference == $deployment.services.minio.secretKeyReference and .key == "Minio__SecretKey") and
        ($deployment.services.smtp.usernameReference == null or any($deployment.secretMappings[];
          .reference == $deployment.services.smtp.usernameReference and .key == "Smtp__Username")) and
        ($deployment.services.smtp.passwordReference == null or any($deployment.secretMappings[];
          .reference == $deployment.services.smtp.passwordReference and .key == "Smtp__Password")) and
        ($deployment.certificates.signingPasswordReference == null or any($deployment.secretMappings[];
          .reference == $deployment.certificates.signingPasswordReference and .key == "OpenIddictCertificates__SigningPassword")) and
        ($deployment.certificates.encryptionPasswordReference == null or any($deployment.secretMappings[];
          .reference == $deployment.certificates.encryptionPasswordReference and .key == "OpenIddictCertificates__EncryptionPassword"))) and
      ((.deployment.services.mode == "deploy" and .sharedServices != null and .deployment.services.smtp.kind == "mailpit" and
         ([.deployment.services.sql.port,.deployment.services.redis.port,.deployment.services.minio.port,.deployment.services.smtp.port] | unique | length) == 4 and
         (.sharedServices.ports | sort) == ([.deployment.services.sql.port,.deployment.services.redis.port,.deployment.services.minio.port,.deployment.services.smtp.port] | sort)) or
       .deployment.services.mode == "existing") and
      (($mode == "isolated" and (.catalog.kind == "production" or .deployment.catalog.allowFixture == true)) or $mode == "persistent")
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
    jq -e '(.secretSource.requiredReferences | sort) as $required |
      ([.deployment.secretMappings[].reference,
        .deployment.services.sql.adminSecretReference,.deployment.services.sql.initializerSecretReference,.deployment.services.sql.initializerConnectionReference,
        .deployment.services.sql.runtimeSecretReference,.deployment.services.sql.runtimeConnectionReference,
        .deployment.services.redis.adminSecretReference,.deployment.services.redis.secretReference,
        .deployment.services.minio.rootAccessKeyReference,.deployment.services.minio.rootSecretKeyReference,
        .deployment.services.minio.accessKeyReference,.deployment.services.minio.secretKeyReference,
        .deployment.services.smtp.usernameReference,.deployment.services.smtp.passwordReference,
        .deployment.certificates.signingPasswordReference,.deployment.certificates.encryptionPasswordReference,
        .deployment.deviceBootstrap.clientSecretReference,.cameraAgents[].ownerPasswordSecretReference] |
        map(select(. != null)) | unique | all(. as $reference | $required | index($reference) != null))' "$inventory" >/dev/null ||
      deploy_fail validate inventory "unknown-deployment-secret-reference" || return 1
    if [[ "$mode" == persistent ]]; then
        jq -e '.catalog.kind == "production" and .deployment.catalog.allowFixture == false and
          .deployment.services.mode == "existing" and .deployment.services.smtp.kind == "production" and
          (([.logicHost] + .cameraAgents) | all(.[]; (.publicEndpoint | startswith("https://")) and
            (.internalEndpoint | startswith("http://")) and (.trustedProxyAddresses | length > 0)))' "$inventory" >/dev/null ||
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
    local inventory="$1" secret_path reference count duplicates
    secret_path="$(jq -er '.secretSource.path' "$inventory")"
    if ! deploy_is_safe_absolute_path "$secret_path" || [[ "$(realpath -m -- "$secret_path" 2>/dev/null)" != "$secret_path" ]]; then
        deploy_fail validate secret-source "noncanonical-secret-source"
        return 1
    fi
    deploy_validate_private_file "$secret_path" || return 1
    if LC_ALL=C grep -q '[[:cntrl:]]' "$secret_path" 2>/dev/null; then
        deploy_fail validate secret-source "control-character-rejected"
        return 1
    fi
    if grep -Ev '^[A-Z][A-Z0-9_]{0,127}=.*$' "$secret_path" >/dev/null 2>&1; then
        deploy_fail validate secret-source "invalid-secret-entry"
        return 1
    fi
    duplicates="$(cut -d= -f1 "$secret_path" | sort | uniq -d)" || {
        deploy_fail validate secret-source "secret-reference-scan-failed"
        return 1
    }
    if [[ -n "$duplicates" ]]; then
        deploy_fail validate secret-source "duplicate-secret-reference"
        return 1
    fi
    while IFS= read -r reference; do
        count="$(grep -Ec "^${reference}=" "$secret_path" 2>/dev/null)" || {
            deploy_fail validate secret-reference "required-reference-scan-failed"
            return 1
        }
        case "$count" in
          0) deploy_fail validate secret-reference "required-reference-missing" || return 1 ;;
          1) ;;
          *) deploy_fail validate secret-reference "duplicate-required-reference" || return 1 ;;
        esac
    done < <(jq -r '.secretSource.requiredReferences[]' "$inventory")
}

deploy_inventory_targets() {
    jq -c '([.logicHost] + .cameraAgents + (if .sharedServices then [.sharedServices] else [] end)) | .[]' "$1"
}
