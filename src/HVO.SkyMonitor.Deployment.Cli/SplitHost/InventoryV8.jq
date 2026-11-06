def exact($a): type == "object" and ((keys | sort) == ($a | sort));
      def text: type == "string" and length > 0 and (test("[[:cntrl:]]") | not);
      def name: text and test("^[a-z0-9][a-z0-9-]{0,31}$");
      def token: text and test("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$");
      def root: text and startswith("/") and . != "/" and
        (contains("//") | not) and (split("/") | any(. == "." or . == "..") | not);
      def runtime_root: root and ((contains("\\") or contains(",") or contains("\"")) | not);
       def memory_mib: capture("^(?<value>[1-9][0-9]*)(?<unit>[MG])$") |
         (.value | tonumber) * (if .unit == "G" then 1024 else 1 end);
       def url: text and test("^https?://[A-Za-z0-9][A-Za-z0-9.-]*:[0-9]{1,5}(/[^[:space:]]*)?$") and
         (contains("@") | not);
       def ipv4: type == "string" and test("^[0-9]{1,3}(?:[.][0-9]{1,3}){3}$") and
         (split(".") | all(tonumber >= 0 and tonumber <= 255));
      def base: exact(["name","sshHost","dockerContext","expectedArchitecture","expectedHostName","expectedHostIdentity","expectedDockerDaemonIdentity","runtimeRoot","runtimeOwner"]) and
        (.name | name) and (.sshHost | text and test("^[A-Za-z0-9][A-Za-z0-9._@-]{0,254}$")) and
        (.dockerContext | text and test("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")) and
        (.expectedArchitecture == "amd64" or .expectedArchitecture == "arm64") and
        (.expectedHostName | text and test("^[A-Za-z0-9][A-Za-z0-9.-]{0,127}$")) and
        (.expectedHostIdentity | token) and (.expectedDockerDaemonIdentity | token) and (.runtimeRoot | runtime_root) and
        (.runtimeOwner | text and test("^[a-z_][a-z0-9_-]{0,31}$"));
       def infra: exact(["name","sshHost","dockerContext","expectedArchitecture","expectedHostName","expectedHostIdentity","expectedDockerDaemonIdentity","runtimeRoot","runtimeOwner","ports"]) and
         ({name,sshHost,dockerContext,expectedArchitecture,expectedHostName,expectedHostIdentity,expectedDockerDaemonIdentity,runtimeRoot,runtimeOwner} | base) and
         (.ports | type == "array" and length > 0 and all(type == "number" and floor == . and . >= 1 and . <= 65535) and length == (unique | length));
       def app: exact(["name","friendlyName","instanceId","applicationIdentity","catalogId","cookieName","sshHost","dockerContext","expectedArchitecture","expectedHostName","expectedHostIdentity","expectedDockerDaemonIdentity","runtimeRoot","runtimeOwner","internalEndpoint","publicEndpoint","trustedProxyAddresses","ports"]) and
          ({name,sshHost,dockerContext,expectedArchitecture,expectedHostName,expectedHostIdentity,expectedDockerDaemonIdentity,runtimeRoot,runtimeOwner} | base) and
          (.friendlyName | text and length <= 200) and
          (.instanceId | type == "string" and test("^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")) and
          (.applicationIdentity | text and test("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")) and
          (.catalogId | name) and (.cookieName | text and test("^hvo[.]skymonitor[.][0-9a-f]{32}$")) and
         (.publicEndpoint | url) and (.internalEndpoint | url and startswith("http://")) and
         (.trustedProxyAddresses | type == "array" and length == (unique | length) and all(ipv4)) and
         (.ports | type == "array" and length > 0 and all(type == "number" and floor == . and . >= 1 and . <= 65535) and length == (unique | length)) and
         (. as $target | (.internalEndpoint | capture("^http://[^/:]+:(?<port>[0-9]+)").port | tonumber) as $internalPort |
           $internalPort <= 65535 and ($target.ports | index($internalPort) != null));
         def camera: exact(["name","friendlyName","instanceId","applicationIdentity","catalogId","cookieName","ownerEmail","sshHost","dockerContext","expectedArchitecture","expectedHostName","expectedHostIdentity","expectedDockerDaemonIdentity","runtimeRoot","runtimeOwner","internalEndpoint","publicEndpoint","trustedProxyAddresses","ports","moduleConfigPath","ownerPasswordSecretReference"]) and
           ({name,friendlyName,instanceId,applicationIdentity,catalogId,cookieName,sshHost,dockerContext,expectedArchitecture,expectedHostName,expectedHostIdentity,expectedDockerDaemonIdentity,runtimeRoot,runtimeOwner,
             internalEndpoint,publicEndpoint,trustedProxyAddresses,ports} | app) and
            (.ownerEmail | text and length <= 254 and test("^[^@[:space:]]+@[^@[:space:]]+$")) and
           (.moduleConfigPath | root) and (.ownerPasswordSecretReference | type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$"));
         exact(["schemaVersion","environment","installationId","productRoot","source","secretSource","observatory","logicHost","cameraAgents","sharedServices","serviceEndpoints","catalogs","images","deployment"]) and
          .schemaVersion == 8 and (.environment | name) and (.installationId | type == "string" and test("^[a-z0-9][a-z0-9-]{0,63}$")) and
       (.productRoot | runtime_root) and
      (.source | exact(["revision","dirtyDisposition"]) and (.revision | type == "string" and test("^[0-9a-f]{40}$")) and
        (.dirtyDisposition == "require-clean" or .dirtyDisposition == "allow-dirty")) and
       (.secretSource | exact(["path","requiredReferences"]) and (.path | text) and
         (.requiredReferences | type == "array" and length > 0 and length == (unique | length) and
           all(type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$")))) and
       (.observatory | exact(["name","latitudeDegrees","longitudeDegrees","elevationMeters","timeZoneId"]) and
         (.name | text and length <= 200) and (.latitudeDegrees | type == "number" and . >= -90 and . <= 90) and
         (.longitudeDegrees | type == "number" and . >= -180 and . <= 180) and
         (.elevationMeters | type == "number" and . >= -1000 and . <= 10000) and (.timeZoneId | text and length <= 128)) and
       (.logicHost | app) and
       (.cameraAgents | type == "array" and length > 0 and all(camera) and
        ([.[].ownerPasswordSecretReference] | unique | length) == length) and
      (.sharedServices == null or (.sharedServices | infra)) and
      (.serviceEndpoints | type == "array" and all(exact(["name","host","port","fromTargets"]) and (.name | name) and
        (.host | text and test("^[A-Za-z0-9][A-Za-z0-9.-]{0,253}$")) and (.port | type == "number" and floor == . and . >= 1 and . <= 65535) and
        (.fromTargets | type == "array" and length > 0 and length == (unique | length) and all(type == "string")))) and
       (.catalogs | type == "array" and length > 0 and all(
          exact(["catalogId","displayName","kind","version","schemaVersion","preprocessingVersion","sha256","length","rowCount","bundlePath","installRoot","allowFixture"]) and
         (.catalogId | name) and (.displayName | text and length <= 200) and (.kind == "production" or .kind == "fixture") and
          all(.version,.schemaVersion,.preprocessingVersion; text and test("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")) and (.sha256 | type == "string" and test("^[0-9a-fA-F]{64}$")) and
         (.length | type == "number" and floor == . and . > 0) and (.rowCount | type == "number" and floor == . and . > 0) and
         (.bundlePath | root) and (.installRoot | runtime_root) and (.allowFixture | type == "boolean"))) and
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
       (.deployment | exact(["services","resources","certificates","automation","deviceBootstrap","transient","workload","secretMappings","limits"]) and
        (.services | exact(["mode","sql","redis","objectStore","smtp","images"]) and (.mode == "existing" or .mode == "deploy") and
          all(.sql,.redis,.smtp; .host | text and test("^[A-Za-z0-9][A-Za-z0-9.-]{0,253}$")) and
          all(.sql,.redis; .port | type == "number" and floor == . and . >= 1 and . <= 65535) and
          (.sql | exact(["host","port","database","adminUser","adminSecretReference","initializerUser","initializerSecretReference","initializerConnectionReference","runtimeUser","runtimeSecretReference","runtimeConnectionReference"]) and (.database | name) and
            all(.adminUser,.initializerUser,.runtimeUser; type == "string" and test("^[A-Za-z][A-Za-z0-9._-]{0,63}$")) and
            all(.adminSecretReference,.initializerSecretReference,.initializerConnectionReference,.runtimeSecretReference,.runtimeConnectionReference; type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$"))) and
          (.redis | exact(["host","port","prefix","adminSecretReference","user","secretReference"]) and (.prefix | text and test("^[a-z0-9][a-z0-9:-]{0,63}:$")) and
            (.user | type == "string" and test("^[A-Za-z][A-Za-z0-9._-]{0,63}$")) and
            all(.adminSecretReference,.secretReference; type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$"))) and
          (.objectStore | exact(["root","uid","gid","filesystemUuid","minimumFreeBytes","minimumFreeInodes","artifactBucket","diagnosticsBucket"]) and
            (.root | root) and
            all(.uid,.gid; type == "number" and floor == . and . >= 1 and . <= 4294967294) and
            (.filesystemUuid | type == "string" and test("^[0-9a-fA-F-]{36}$")) and
            all(.minimumFreeBytes,.minimumFreeInodes; type == "number" and floor == . and . >= 1) and
            (.artifactBucket | type == "string" and test("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$")) and
            (.diagnosticsBucket | type == "string" and test("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$")) and
            .artifactBucket != .diagnosticsBucket) and
          (.smtp | exact(["kind","host","ports","usernameReference","passwordReference"]) and (.kind == "production" or .kind == "mailpit") and
            (.ports | type == "array" and length == 2) and
            (.ports[0] | type == "number" and floor == . and . >= 1 and . <= 65535) and
            ((.kind == "mailpit" and (.ports[1] | type == "number" and floor == . and . >= 1 and . <= 65535) and .ports[0] != .ports[1]) or
             (.kind == "production" and .ports[1] == null)) and
            (.usernameReference == null or (.usernameReference | type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$"))) and
            (.passwordReference == null or (.passwordReference | type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$")))) and
          (.images | exact(["sqlServer","redis","mailpit"]) and all(.[]; . == null or (type == "string" and test("^[a-z0-9][a-z0-9._/-]+@sha256:[0-9a-f]{64}$"))))) and
        (.resources | exact(["project","sqlDatabase","redisPrefix","artifactBucket","diagnosticsBucket"]) and
          (.project | name) and (.sqlDatabase | name) and (.redisPrefix | text and test("^[a-z0-9][a-z0-9:-]{0,63}:$")) and
          (.artifactBucket | type == "string" and test("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$")) and
          (.diagnosticsBucket | type == "string" and test("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$"))) and
         (.certificates | exact(["signingPath","encryptionPath","signingPasswordReference","encryptionPasswordReference"]) and
          (.signingPath | root) and (.encryptionPath | root) and
           all(.signingPasswordReference,.encryptionPasswordReference; . == null or (type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$")))) and
        (.automation | exact(["ownerApiKeySecretReference"]) and
          (.ownerApiKeySecretReference | type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$"))) and
         (.deviceBootstrap | exact(["clientId","clientSecretReference","scopes"]) and
          (.clientId | text and test("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")) and
          (.clientSecretReference | type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$")) and
            (.scopes | type == "array" and length > 0 and length == (unique | length) and all(type == "string" and test("^[a-z][a-z0-9.:-]{0,127}$")))) and
         (.transient | exact(["mode"]) and (.mode == "Off" or .mode == "Hybrid")) and
         (.workload | exact(["kind","durationSeconds","sustainedArmOptIn"]) and
           (.kind == "W0" or .kind == "W1" or .kind == "W2") and
           (.durationSeconds | type == "number" and floor == . and . >= 1 and . <= 86400) and
            (.sustainedArmOptIn | type == "boolean") and
           (.kind == "W0" or .sustainedArmOptIn == true)) and
        (.secretMappings | type == "array" and length > 0 and length == ([.[].key] | unique | length) and all(exact(["reference","key"]) and
          (.reference | type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$")) and (.key | text and test("^[A-Za-z][A-Za-z0-9]*(?:__[A-Za-z0-9][A-Za-z0-9-]*)+$")))) and
         (.limits | exact(["cpus","memory","sqlMemory","sqlMemoryLimitMb"]) and
           (.cpus == null or (.cpus | type == "string" and test("^[0-9]+(?:[.][0-9]+)?$"))) and
           (.memory == null or (.memory | type == "string" and test("^[0-9]+[MG]$"))) and
           (.sqlMemory == null or (.sqlMemory | type == "string" and test("^(?:[1-9][0-9]{0,5}M|[1-9][0-9]{0,2}G)$"))) and
           (.sqlMemoryLimitMb == null or (.sqlMemoryLimitMb | type == "number" and floor == . and . > 0 and . <= 1022975))) and
         ((.services.mode == "existing") or
           (.limits.sqlMemory != null and .limits.sqlMemoryLimitMb != null and
             .limits.sqlMemoryLimitMb < (.limits.sqlMemory | memory_mib)))) and
      .deployment.resources.sqlDatabase == .deployment.services.sql.database and
      .deployment.resources.redisPrefix == .deployment.services.redis.prefix and
      .deployment.resources.artifactBucket == .deployment.services.objectStore.artifactBucket and
      .deployment.resources.diagnosticsBucket == .deployment.services.objectStore.diagnosticsBucket and
      (.deployment as $deployment |
        any($deployment.secretMappings[]; .reference == $deployment.services.sql.runtimeConnectionReference and .key == "ConnectionStrings__skymonitordb") and
        any($deployment.secretMappings[]; .reference == $deployment.services.sql.initializerConnectionReference and .key == "ConnectionStrings__skymonitordb-migrations") and
        (all($deployment.secretMappings[]; .key != "ObjectStorage__AccessKey" and .key != "ObjectStorage__SecretKey")) and
        ($deployment.services.smtp.usernameReference == null or any($deployment.secretMappings[];
          .reference == $deployment.services.smtp.usernameReference and .key == "Smtp__Username")) and
        ($deployment.services.smtp.passwordReference == null or any($deployment.secretMappings[];
          .reference == $deployment.services.smtp.passwordReference and .key == "Smtp__Password")) and
        ($deployment.certificates.signingPasswordReference == null or any($deployment.secretMappings[];
          .reference == $deployment.certificates.signingPasswordReference and .key == "OpenIddictCertificates__SigningPassword")) and
        ($deployment.certificates.encryptionPasswordReference == null or any($deployment.secretMappings[];
          .reference == $deployment.certificates.encryptionPasswordReference and .key == "OpenIddictCertificates__EncryptionPassword"))) and
      ((.deployment.services.mode == "deploy" and .sharedServices != null and .deployment.services.smtp.kind == "mailpit" and
          ([.deployment.services.sql.port,.deployment.services.redis.port,.deployment.services.smtp.ports[]] | unique | length) == 4 and
          (.sharedServices.ports | sort) == ([.deployment.services.sql.port,.deployment.services.redis.port,.deployment.services.smtp.ports[]] | sort)) or
       .deployment.services.mode == "existing") and
       (($mode == "isolated" and all(.catalogs[]; .kind == "production" or .allowFixture == true)) or $mode == "persistent")
