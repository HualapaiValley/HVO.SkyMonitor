def exact($a): type == "object" and ((keys | sort) == ($a | sort));
      def text: type == "string" and length > 0 and (test("[[:cntrl:]]") | not);
      def name: text and test("^[a-z0-9][a-z0-9-]{0,31}$");
      def token: text and test("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$");
      def root: text and startswith("/") and . != "/" and
        (contains("//") | not) and (split("/") | any(. == "." or . == "..") | not);
      def runtime_root: root and ((contains("\\") or contains(",") or contains("\"")) | not);
      def url: text and test("^https?://[A-Za-z0-9][A-Za-z0-9.-]*:[0-9]{1,5}(/[^[:space:]]*)?$") and (contains("@") | not);
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
      def app_v7: exact(["name","sshHost","dockerContext","expectedArchitecture","expectedHostName","expectedHostIdentity","expectedDockerDaemonIdentity","runtimeRoot","runtimeOwner","internalEndpoint","publicEndpoint","trustedProxyAddresses","ports"]) and
        ({name,sshHost,dockerContext,expectedArchitecture,expectedHostName,expectedHostIdentity,expectedDockerDaemonIdentity,runtimeRoot,runtimeOwner} | base) and
        (.publicEndpoint | url) and (.internalEndpoint | url and startswith("http://")) and
        (.trustedProxyAddresses | type == "array" and length == (unique | length) and all(ipv4)) and
        (.ports | type == "array" and length > 0 and all(type == "number" and floor == . and . >= 1 and . <= 65535) and length == (unique | length)) and
        (. as $target | (.internalEndpoint | capture("^http://[^/:]+:(?<port>[0-9]+)").port | tonumber) as $internalPort |
          $internalPort <= 65535 and ($target.ports | index($internalPort) != null));
      def camera_v7: exact(["name","friendlyName","ownerEmail","sshHost","dockerContext","expectedArchitecture","expectedHostName","expectedHostIdentity","expectedDockerDaemonIdentity","runtimeRoot","runtimeOwner","internalEndpoint","publicEndpoint","trustedProxyAddresses","ports","moduleConfigPath","ownerPasswordSecretReference"]) and
        ({name,sshHost,dockerContext,expectedArchitecture,expectedHostName,expectedHostIdentity,expectedDockerDaemonIdentity,runtimeRoot,runtimeOwner,
          internalEndpoint,publicEndpoint,trustedProxyAddresses,ports} | app_v7) and
        (.friendlyName | text and length <= 200) and (.ownerEmail | text and length <= 254 and test("^[^@[:space:]]+@[^@[:space:]]+$")) and
        (.moduleConfigPath | root) and (.ownerPasswordSecretReference | type == "string" and test("^[A-Z][A-Z0-9_]{0,127}$"));
      exact(["schemaVersion","environment","installationId","source","secretSource","observatory","logicHost","cameraAgents","sharedServices","serviceEndpoints","catalog","images","deployment"]) and
      .schemaVersion == 7 and (.logicHost | app_v7) and
      (.cameraAgents | type == "array" and length > 0 and all(camera_v7)) and
      (.sharedServices == null or (.sharedServices | infra)) and
      (.catalog | exact(["kind","version","sha256","length","rowCount"]) and (.kind == "production" or .kind == "fixture") and
        (.version | text and test("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")) and (.sha256 | type == "string" and test("^[0-9a-fA-F]{64}$")) and
        (.length | type == "number" and floor == . and . > 0) and (.rowCount | type == "number" and floor == . and . > 0)) and
      (.deployment | exact(["catalog","services","resources","certificates","automation","deviceBootstrap","transient","workload","secretMappings","limits"])) and
      (.deployment.catalog | exact(["bundlePath","installRoot","allowFixture"]) and
        (.bundlePath | root) and (.installRoot | root) and (.allowFixture | type == "boolean"))
