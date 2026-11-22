using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The storage layout a CameraAgent instance is installed with. <see cref="Filesystem"/> is every installation made
/// before #1966 and stays the default; <see cref="Silo"/> is the clean-install layout of the CameraAgent Silo cutover
/// contract, selected only for a new instance and never by conversion.
/// </summary>
internal enum CameraAgentStorageLayout
{
    Filesystem,
    Silo
}

/// <summary>
/// The qualified stock Silo runtime profile (#1966; CameraAgent Silo cutover contract sections 2, 4.3, 5.2 and 7.2).
/// One unmodified upstream image pinned by release and index digest, one private listener on an internal-only network,
/// no console, the measured disk-sync setting, a data root of its own beside the CameraAgent state root, and two
/// credentials: the root credential mounted only into Silo and a server-generated, bucket-scoped writer access key
/// mounted only into CameraAgent.
/// </summary>
internal static partial class SiloRuntimeProfile
{
    /// <summary>The instance manifest component schema of a Silo-layout instance. Every reader that accepts only
    /// <c>cameraagent-install-v1</c>, including every installer released before #1966, refuses such an instance.</summary>
    public const string ComponentSchemaVersion = "cameraagent-silo-install-v1";

    public const string ComposeTemplateVersion = "cameraagent-silo-compose-v1";

    /// <summary>The Silo layout's state contract: distinct from <c>cameraagent-state-v2</c> and never a migration target.</summary>
    public const string StateContract = "cameraagent-silo-state-v1";

    public const string Repository = SiloImagePin.Repository;
    public const string Release = SiloImagePin.Release;

    /// <summary>The multi-platform index digest of <see cref="Release"/>; it names exactly the platform manifests below.</summary>
    public const string IndexDigest = SiloImagePin.IndexDigest;

    /// <summary>The upstream source revision the image's <c>org.opencontainers.image.revision</c> label names.</summary>
    public const string SourceRevision = SiloImagePin.SourceRevision;

    public static string ImageReference => SiloImagePin.Reference;

    /// <summary>The platform manifest the pinned index selects for a Docker architecture, or null when unsupported.</summary>
    public static string? PlatformDigestFor(string? architecture) => SiloImagePin.ForArchitecture(architecture)?.ManifestDigest;

    public const string DataDirectoryName = "silo";
    public const string RootSecretsDirectoryName = "silo-root";
    public const string BootstrapDirectoryName = "silo-bootstrap";
    public const string ServiceName = "silo";
    public const string NetworkName = "silo";
    public const string NetworkAlias = "hvo-silo";
    public const int Port = 9000;
    public const string Endpoint = "http://hvo-silo:9000";
    public const int WriterGeneration = 1;

    /// <summary>The only filesystem the Silo data root may live on (contract section 2).</summary>
    public const string RequiredFileSystem = "ext4";

    /// <summary>
    /// The free space the data-root filesystem must keep: the larger of 10 GiB, which holds the contract's proposed two-hour
    /// W6 spool bound (9.08 GB, section 10.3) beside the store, and 5 % of the filesystem. Install, first resume and
    /// <c>silo-verify</c> treat less as not ready. The measured budgets and the pressure pause belong to #1970 and #1973.
    /// </summary>
    public const long MinimumFreeBytes = 10L * 1024 * 1024 * 1024;

    public const int MinimumFreePercent = 5;

    /// <summary>The headroom of the filesystem that holds <paramref name="path"/> (or would, for a path not created yet).</summary>
    public static SiloHeadroom HeadroomOf(string path)
    {
        var drive = new DriveInfo(LinuxHostEnvironment.NearestExistingAncestor(path));
        var total = drive.TotalSize;
        return new SiloHeadroom(drive.AvailableFreeSpace, total, RequiredFreeBytes(total));
    }

    /// <summary>The free space a data-root filesystem of <paramref name="totalBytes"/> must keep.</summary>
    public static long RequiredFreeBytes(long totalBytes) => Math.Max(MinimumFreeBytes, totalBytes / 100 * MinimumFreePercent);

    public const string MarkerKey = "meta/instance.v1.json";
    public const string FenceKey = "meta/fence.json";
    public const string MarkerFormat = "hvo.cameraagent.silo.instance/1";
    public const string FenceFormat = "hvo.cameraagent.silo.fence/1";

    public static string DataRoot(InstallationPaths paths) => Path.Combine(paths.InstanceRoot, DataDirectoryName);
    public static string RootSecretsDirectory(InstallationPaths paths) => Path.Combine(paths.ConfigRoot, RootSecretsDirectoryName);
    public static string BootstrapDirectory(InstallationPaths paths) => Path.Combine(paths.DeploymentStateRoot, BootstrapDirectoryName);
    public static string ContainerNameFor(Guid instanceId) => ComposeDeployment.ContainerNameFor(instanceId) + "-silo";

    /// <summary>The instance's private network as Compose names it: its project's <c>silo</c> network.</summary>
    public static string NetworkNameFor(Guid instanceId) => ComposeDeployment.ContainerNameFor(instanceId) + "_" + NetworkName;
    public static string BucketFor(Guid instanceId) => $"hvo-{instanceId:N}";
    public static string StoreIdFor(Guid instanceId) => $"local-silo:{instanceId:D}";
    public static string WriterNameFor(int generation) => $"hvo-writer-g{generation.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>
    /// <c>storageBindingId</c> (contract 4.2): a hash of the instance ID, the qualified local store identity and the
    /// bucket. It names the store, never an endpoint URL, so relocating the endpoint preserves it.
    /// </summary>
    public static string StorageBindingIdFor(Guid instanceId, string storeId, string bucket)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"hvo.cameraagent.silo.binding/1\n{instanceId:D}\n{storeId}\n{bucket}")));

    /// <summary>The setting files CameraAgent reads to find its store. #1968 consumes them; until then they are inert.</summary>
    public static IReadOnlyDictionary<string, string> CameraAgentSettings(Guid instanceId, Guid firstBootPauseId)
    {
        var bucket = BucketFor(instanceId);
        var storeId = StoreIdFor(instanceId);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CameraAgent__FirstBootPause__Id"] = firstBootPauseId.ToString("D"),
            ["CameraAgent__Silo__StateContract"] = StateContract,
            ["CameraAgent__Silo__Endpoint"] = Endpoint,
            ["CameraAgent__Silo__Bucket"] = bucket,
            ["CameraAgent__Silo__StoreId"] = storeId,
            ["CameraAgent__Silo__StorageBindingId"] = StorageBindingIdFor(instanceId, storeId, bucket),
            ["CameraAgent__Silo__WriterGeneration"] = WriterGeneration.ToString(CultureInfo.InvariantCulture)
        };
    }

    /// <summary>The writer credential's setting files, written only after the server generates it.</summary>
    /// <summary>Where CameraAgent reads its setting files, the instance's <c>config/secrets</c> mounted read-only.</summary>
    public const string CameraAgentSettingsMount = "/run/hvo-secrets";

    public const string WriterAccessKeySetting = "CameraAgent__Silo__AccessKey";
    public const string WriterSecretKeySetting = "CameraAgent__Silo__SecretKey";

    /// <summary>
    /// The Compose service and network of the Silo layout. The listener is on an internal network shared only with this
    /// instance's CameraAgent, so it publishes no host port and has no route off the host; the console is off; the
    /// root credential is read from files mounted only here.
    /// </summary>
    public const string ComposeService = """

  silo:
    container_name: ${HVO_CONTAINER_NAME:?container name required}-silo
    hostname: ${HVO_CONTAINER_NAME}-silo
    image: ${HVO_SILO_IMAGE:?pinned silo image required}
    platform: ${HVO_SILO_PLATFORM:?silo platform required}
    pull_policy: never
    labels:
      io.hvo.skymonitor.product: HVO.SkyMonitor
      io.hvo.skymonitor.component: Silo
      io.hvo.skymonitor.instance-id: ${HVO_INSTANCE_ID:?instance id required}
    user: "${HVO_RUNTIME_UID:?uid required}:${HVO_RUNTIME_GID:?gid required}"
    command: [server, /data, --address, ":9000"]
    environment:
      MINIO_BROWSER: "off"
      MINIO_FS_OSYNC: "on"
      MINIO_UPDATE: "off"
      MINIO_ROOT_USER_FILE: /run/hvo-silo/root-user
      MINIO_ROOT_PASSWORD_FILE: /run/hvo-silo/root-password
      MINIO_ACCESS_KEY_FILE: /run/hvo-silo/root-user
      MINIO_SECRET_KEY_FILE: /run/hvo-silo/root-password
    volumes:
      - ${HVO_SILO_DATA_ROOT:?silo data root required}:/data
      - ${HVO_CONFIG_ROOT:?config root required}/silo-root:/run/hvo-silo:ro
    networks:
      silo:
        aliases: [hvo-silo]
    read_only: true
    cap_drop: [ALL]
    security_opt: [no-new-privileges:true]
    tmpfs:
      - /tmp:rw,nosuid,nodev,noexec,mode=1777,size=64m
    restart: unless-stopped
    stop_grace_period: 30s
    healthcheck:
      test: [CMD, curl, --fail, --silent, http://127.0.0.1:9000/minio/health/live]
      interval: 10s
      timeout: 3s
      retries: 12
      start_period: 10s
    cpus: "1.0"
    mem_limit: 1G
    pids_limit: 256
    logging:
      driver: json-file
      options:
        max-size: 10m
        max-file: "3"

networks:
  default: {}
  silo:
    internal: true
    labels:
      io.hvo.skymonitor.instance-id: ${HVO_INSTANCE_ID:?instance id required}
      io.hvo.skymonitor.network: silo
""";

    /// <summary>The CameraAgent service's network membership under the Silo layout: its published network and the private one.</summary>
    public const string CameraAgentNetworks = """
    networks:
      default: {}
      silo: {}
""";

    /// <summary>
    /// The bucket-scoped writer policy: object reads and writes and exact-version deletes, nothing at bucket or admin
    /// level, and no write or delete of any kind under <c>meta/</c>. The instance marker and the fence are created by the
    /// installer's root credential and advanced only by a takeover under a new generation's credential, so the writer can
    /// never replace or remove the binding it is checked against (a permitted <c>DeleteObjectVersion</c> would otherwise
    /// let it erase them, which the #1966 qualification run observed through <c>rb --force</c>).
    /// </summary>
    public static string WriterPolicy(string bucket)
    {
        var policy = new JsonObject
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new JsonArray(
                new JsonObject
                {
                    ["Effect"] = "Allow",
                    ["Action"] = new JsonArray("s3:GetBucketLocation", "s3:GetBucketVersioning", "s3:ListBucket", "s3:ListBucketVersions"),
                    ["Resource"] = new JsonArray($"arn:aws:s3:::{bucket}")
                },
                new JsonObject
                {
                    ["Effect"] = "Allow",
                    ["Action"] = new JsonArray("s3:GetObject", "s3:GetObjectVersion", "s3:PutObject", "s3:DeleteObjectVersion"),
                    ["Resource"] = new JsonArray($"arn:aws:s3:::{bucket}/*")
                },
                new JsonObject
                {
                    ["Effect"] = "Deny",
                    ["Action"] = new JsonArray("s3:PutObject", "s3:DeleteObject", "s3:DeleteObjectVersion"),
                    ["Resource"] = new JsonArray($"arn:aws:s3:::{bucket}/meta/*")
                })
        };
        return policy.ToJsonString();
    }

    /// <summary>The create-only instance marker (contract 4.2/4.3). Its bytes are fixed before the first PUT.</summary>
    public static byte[] Marker(Guid instanceId, DateTimeOffset createdUtc)
    {
        var bucket = BucketFor(instanceId);
        var storeId = StoreIdFor(instanceId);
        return Canonical(new JsonObject
        {
            ["format"] = MarkerFormat,
            ["instanceId"] = instanceId.ToString("D"),
            ["stateContract"] = StateContract,
            ["storeId"] = storeId,
            ["bucket"] = bucket,
            ["storageBindingId"] = StorageBindingIdFor(instanceId, storeId, bucket),
            ["createdUtc"] = createdUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        });
    }

    /// <summary>The generation-1 writer fence (contract 4.3, 7.2): the current writer generation and its credential identity.</summary>
    public static byte[] Fence(Guid instanceId, string writerAccessKeyId, DateTimeOffset createdUtc)
    {
        var bucket = BucketFor(instanceId);
        return Canonical(new JsonObject
        {
            ["format"] = FenceFormat,
            ["instanceId"] = instanceId.ToString("D"),
            ["storageBindingId"] = StorageBindingIdFor(instanceId, StoreIdFor(instanceId), bucket),
            ["writerGeneration"] = WriterGeneration,
            ["writerAccessKeyId"] = writerAccessKeyId,
            ["createdUtc"] = createdUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
        });
    }

    private static byte[] Canonical(JsonObject value) => Encoding.UTF8.GetBytes(value.ToJsonString() + "\n");

    /// <summary>Reads a marker back and checks it binds exactly this instance, store and bucket.</summary>
    public static bool MarkerBinds(ReadOnlySpan<byte> marker, Guid instanceId)
    {
        try
        {
            using var document = JsonDocument.Parse(marker.ToArray());
            var root = document.RootElement;
            var bucket = BucketFor(instanceId);
            var storeId = StoreIdFor(instanceId);
            return root.ValueKind == JsonValueKind.Object &&
                   Text(root, "format") == MarkerFormat &&
                   Text(root, "instanceId") == instanceId.ToString("D") &&
                   Text(root, "storeId") == storeId &&
                   Text(root, "bucket") == bucket &&
                   Text(root, "storageBindingId") == StorageBindingIdFor(instanceId, storeId, bucket);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// The read-only inventory of the store a bootstrap may create or adopt (#1966 review): the raw machine-readable
    /// answers of <c>mcli</c> for the buckets, this instance's bucket's objects and versioning (when it is listed), and the
    /// root user's access keys, each returned base64-encoded so that the installer, not a shell pattern, parses them.
    /// </summary>
    public static string InventoryStoreScript(string bucket)
    {
        RequireBucket(bucket);
        return ScriptPrelude + $$"""
            # hvo-bootstrap: inventory
            bucket='{{bucket}}'
            {{InventoryReads}}
            b64() { printf '%s' "$1" | base64 -w0; }
            printf '{"outcome":"inventory","buckets":"%s","objects":"%s","versioning":"%s","keys":"%s"}\n' \
              "$(b64 "$buckets")" "$(b64 "$objects")" "$(b64 "$versioning")" "$(b64 "$keys")"
            """;
    }

    /// <summary>The read-only <c>mcli admin accesskey info --json</c> answer for each of <paramref name="keys"/>, base64-encoded.</summary>
    public static string KeyInfoScript(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        RequireCandidateKeys(keys);
        return ScriptPrelude + $$"""
            # hvo-bootstrap: key-info
            infos=""
            for key in {{string.Join(' ', keys.Select(static key => $"'{key}'"))}}; do
              info="$(mcli admin accesskey info root/ "$key" --json 2>/dev/null)" || fail "keys-unreadable"
              infos="$infos${infos:+,}\"$(printf '%s' "$info" | base64 -w0)\""
            done
            printf '{"outcome":"key-info","info":[%s]}\n' "$infos"
            """;
    }

    /// <summary>
    /// The only bootstrap step that changes the store (#1966 review). It reads the inventory again and changes nothing
    /// unless the bucket, object and versioning answers still hash to <paramref name="inventorySha256"/> (those answers are
    /// byte-stable) and the access keys are still exactly <paramref name="inventoryKeys"/> in number, each still answering:
    /// the key and policy answers are not byte-stable (Silo orders a policy's actions differently from call to call), so
    /// the installer has already judged them from their parsed form. It then makes the bucket (or its versioning), removes
    /// exactly <paramref name="removeKeys"/>, and creates the writer with its least-privilege policy.
    /// </summary>
    public static string ApplyBootstrapScript(
        string bucket, int generation, string inventorySha256, IReadOnlyList<string> inventoryKeys, string bucketAction, IReadOnlyList<string> removeKeys)
    {
        RequireBucket(bucket);
        ArgumentNullException.ThrowIfNull(inventoryKeys);
        ArgumentNullException.ThrowIfNull(removeKeys);
        RequireCandidateKeys(inventoryKeys);
        RequireCandidateKeys(removeKeys);
        if (!Sha256Pattern().IsMatch(inventorySha256) || bucketAction is not ("create" or "enable-versioning" or "none") ||
            removeKeys.Any(key => !inventoryKeys.Contains(key, StringComparer.Ordinal)))
            throw new InstallerException("The Silo bootstrap plan is not in the expected form.");
        return ScriptPrelude + $$"""
            # hvo-bootstrap: apply
            bucket='{{bucket}}'
            {{InventoryReads}}
            material="$buckets"$'\n'"$objects"$'\n'"$versioning"
            seen="$(printf '%s' "$material" | sha256sum)"
            [ "${seen%% *}" = '{{inventorySha256}}' ] || fail "store-changed"
            rest="$keys"
            count=0
            while [[ $rest == *'"accessKey":"'* ]]; do rest="${rest#*\"accessKey\":\"}"; count=$((count + 1)); done
            [ "$count" = '{{inventoryKeys.Count.ToString(CultureInfo.InvariantCulture)}}' ] || fail "store-changed"
            for key in {{string.Join(' ', inventoryKeys.Select(static key => $"'{key}'"))}}; do
              mcli admin accesskey info root/ "$key" --json >/dev/null 2>&1 || fail "store-changed"
            done
            # The store is exactly what the installer approved: only now is anything changed.
            case '{{bucketAction}}' in
              create) mcli mb --with-versioning "root/$bucket" >/dev/null 2>&1 || fail "bucket-create-failed" ;;
              enable-versioning) mcli version enable "root/$bucket" >/dev/null 2>&1 || fail "versioning-not-enabled" ;;
            esac
            info="$(mcli version info "root/$bucket" --json 2>/dev/null || true)"
            [[ $info =~ \"status\":\"Enabled\" ]] || fail "versioning-not-enabled"
            for key in {{string.Join(' ', removeKeys.Select(static key => $"'{key}'"))}}; do
              mcli admin accesskey rm root/ "$key" >/dev/null 2>&1 || fail "writer-remove-failed"
            done
            printf '%s' '{{WriterPolicy(bucket)}}' > "$work/policy.json"
            key="$(mcli admin accesskey create root/ --policy "$work/policy.json" --name '{{WriterNameFor(generation)}}' --description 'HVO CameraAgent writer generation {{generation}}' --json 2>/dev/null || true)"
            [[ $key =~ \"accessKey\":\"([A-Za-z0-9]+)\" ]] || fail "writer-create-failed"
            access_key="${BASH_REMATCH[1]}"
            [[ $key =~ \"secretKey\":\"([A-Za-z0-9+/=]+)\" ]] || fail "writer-create-failed"
            printf '{"outcome":"created","versioning":"Enabled","accessKey":"%s","secretKey":"%s"}\n' "$access_key" "${BASH_REMATCH[1]}"
            """;
    }

    /// <summary>The inventory reads shared by <see cref="InventoryStoreScript"/> and <see cref="ApplyBootstrapScript"/>.</summary>
    private const string InventoryReads = """
        buckets="$(mcli ls root --json 2>/dev/null)" || fail "store-unreadable"
            objects=""
            versioning=""
            if [[ $buckets == *"\"key\":\"$bucket/\""* ]]; then
              objects="$(mcli ls --versions --recursive "root/$bucket" --json 2>/dev/null)" || fail "bucket-unreadable"
              versioning="$(mcli version info "root/$bucket" --json 2>/dev/null)" || fail "bucket-unreadable"
            fi
            keys="$(mcli admin accesskey ls root/ --json 2>/dev/null)" || fail "keys-unreadable"
        """;

    /// <summary>The shape of an access key Silo generates for a writer: anything else is not a key this installer created.</summary>
    internal static bool IsGeneratedKey(string key) => GeneratedKeyPattern().IsMatch(key);

    private static void RequireCandidateKeys(IReadOnlyList<string> keys)
    {
        if (keys.Any(static key => !IsGeneratedKey(key)))
            throw new InstallerException("A Silo access key is not in the shape Silo generates.");
    }

    /// <summary>The prelude of a script that holds only the writer credential, which a verification probe runs.</summary>
    private const string WriterPrelude = """
        set -euo pipefail
        umask 077
        MC_CONFIG_DIR="$(mktemp -d /tmp/hvo-mc.XXXXXX)"
        export MC_CONFIG_DIR
        work="$(mktemp -d /tmp/hvo-probe.XXXXXX)"
        trap 'rm -rf "$MC_CONFIG_DIR" "$work"' EXIT
        fail() { printf '{"outcome":"refused","reason":"%s"}\n' "$1"; exit 3; }

        """;

    private const string ScriptPrelude = """
        set -euo pipefail
        umask 077
        MC_CONFIG_DIR="$(mktemp -d /tmp/hvo-mc.XXXXXX)"
        export MC_CONFIG_DIR
        work="$(mktemp -d /tmp/hvo-boot.XXXXXX)"
        trap 'rm -rf "$MC_CONFIG_DIR" "$work"' EXIT
        root_user="$(cat /run/hvo-silo/root-user)"
        root_password="$(cat /run/hvo-silo/root-password)"
        export MC_HOST_root="http://${root_user}:${root_password}@127.0.0.1:9000"
        unset root_user root_password
        fail() { printf '{"outcome":"refused","reason":"%s"}\n' "$1"; exit 3; }
        for _ in $(seq 1 60); do mcli ready root >/dev/null 2>&1 && break; sleep 1; done
        mcli ready root >/dev/null 2>&1 || fail "silo-not-ready"

        """;


    /// <summary>
    /// The resume of an interrupted bucket and writer creation (#1966 review), run only under this instance's own durable
    /// bootstrap intent and only when no writer credential was recorded: the attempt it resumes may have created the
    /// bucket, its versioning or the writer key without its acknowledgement reaching the installer. The store may hold
    /// nothing but that bucket, empty (the marker and fence are written only once a credential is recorded), and no access
    /// key but earlier writer keys of this generation for this bucket, which are removed because their secret was never
    /// recorded. Anything else is refused and left untouched. It then makes the bucket versioned and creates the writer.
    /// </summary>


    /// <summary>
    /// The second bootstrap step: create the marker and the generation-1 fence create-only from their fixed bytes with the
    /// root credential, which alone may write under <c>meta/</c>, then read each back as the writer by exact version and
    /// SHA-256, which also proves the writer credential works. A create-only conflict is resolved by reading the
    /// existing latest version: equal bytes mean an earlier attempt succeeded; different bytes are refused and nothing
    /// is replaced (contract 4.5, exact-byte replay). Carries the writer credential only in the script text it reads from standard input, never in argv.
    /// </summary>
    public static string WriteBindingScript(string bucket, string writerAccessKey, string writerSecretKey, byte[] marker, byte[] fence)
    {
        RequireBucket(bucket);
        RequireWriter(writerAccessKey, writerSecretKey);
        return ScriptPrelude + $$"""
            bucket='{{bucket}}'
            writer_access='{{writerAccessKey}}'
            writer_secret='{{writerSecretKey}}'
            export MC_HOST_writer="http://${writer_access}:${writer_secret}@127.0.0.1:9000"
            unset writer_secret
            mcli stat "writer/$bucket" >/dev/null 2>&1 || fail "writer-cannot-read-bucket"
            info="$(mcli version info "writer/$bucket" --json 2>/dev/null || true)"
            [[ $info =~ \"status\":\"Enabled\" ]] || fail "versioning-not-enabled"
            create_only() {
              local name="$1" key="$2" b64="$3" file
              file="$work/$name"
              printf '%s' "$b64" | base64 -d > "$file"
              local expected
              expected="$(sha256sum "$file")"; expected="${expected%% *}"
              # Create-only: an existing object refuses the write with PreconditionFailed, which only mcli's JSON output names,
              # and must then hold exactly these bytes (a resume after a completed binding).
              if ! mcli put --json --disable-multipart --checksum SHA256 -H 'If-None-Match: *' "$file" "root/$bucket/$key" >"$work/put.out" 2>&1; then
                [[ "$(cat "$work/put.out")" == *'"Code":"PreconditionFailed"'* ]] || fail "$name-create-failed"
              fi
              local stat version actual
              stat="$(mcli stat "writer/$bucket/$key" --json 2>/dev/null || true)"
              [[ $stat =~ \"versionID\":\"([A-Za-z0-9-]+)\" ]] || fail "$name-version-missing"
              version="${BASH_REMATCH[1]}"
              actual="$(mcli cat --version-id "$version" "writer/$bucket/$key" 2>/dev/null | sha256sum)"; actual="${actual%% *}"
              [ "$actual" = "$expected" ] || fail "$name-conflict"
              printf '"%sVersionId":"%s","%sSha256":"%s"' "$name" "$version" "$name" "$actual" > "$work/$name.result"
            }
            # Run in this shell, not a command substitution, so a refusal is the script's readable last line.
            create_only marker '{{MarkerKey}}' '{{Convert.ToBase64String(marker)}}'
            create_only fence '{{FenceKey}}' '{{Convert.ToBase64String(fence)}}'
            printf '{"outcome":"bound",%s,%s}\n' "$(cat "$work/marker.result")" "$(cat "$work/fence.result")"
            """;
    }

    /// <summary>
    /// A read-only binding check as the writer: the bucket is versioned and its marker's latest version is exactly the
    /// recorded one with the recorded SHA-256 and binds this instance. Prints the marker bytes base64-encoded.
    /// </summary>
    /// <summary>What the host expects of CameraAgent's settings mount: the exact endpoint and each entry's identity.</summary>
    internal sealed record SettingsExpectation(string Endpoint, string EndpointSha256, string Directory, string EndpointFile, string AccessFile, string SecretFile);

    /// <summary>The identities of the instance's settings directory and files on the host, as <c>stat -c %d:%i</c> prints them.</summary>
    public static SettingsExpectation ExpectSettings(string settingsDirectory)
    {
        static string Identity(string path)
        {
            var node = NativeLinux.GetNodeIdentity(path);
            ulong major = node.DeviceMajor, minor = node.DeviceMinor;
            var device = ((major & 0xfffff000UL) << 32) | ((major & 0x00000fffUL) << 8) | ((minor & 0xffffff00UL) << 12) | (minor & 0x000000ffUL);
            return $"{device.ToString(CultureInfo.InvariantCulture)}:{node.Inode.ToString(CultureInfo.InvariantCulture)}";
        }
        return new SettingsExpectation(
            Endpoint,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Endpoint))),
            Identity(settingsDirectory),
            Identity(Path.Combine(settingsDirectory, "CameraAgent__Silo__Endpoint")),
            Identity(Path.Combine(settingsDirectory, WriterAccessKeySetting)),
            Identity(Path.Combine(settingsDirectory, WriterSecretKeySetting)));
    }

    public static string VerifyBindingScript(string bucket, string markerVersionId, SettingsExpectation expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        RequireBucket(bucket);
        if (!VersionIdPattern().IsMatch(markerVersionId)) throw new InstallerException("The recorded Silo marker version is invalid.");
        if (expectation.Endpoint != Endpoint || !Sha256Pattern().IsMatch(expectation.EndpointSha256) ||
            new[] { expectation.Directory, expectation.EndpointFile, expectation.AccessFile, expectation.SecretFile }.Any(static identity => !IdentityPattern().IsMatch(identity)))
            throw new InstallerException("The expected CameraAgent settings are not in the expected form.");
        // Run beside CameraAgent with its network namespace and its live mounts. Before any credential is read or anything
        // is sent, the settings mount must be this instance's own directory and files (by device and inode, no symbolic
        // link) and the endpoint file must hold exactly this instance's endpoint; the store is then reached only at that
        // expected address. No credential is sent in.
        return WriterPrelude + $$"""
            bucket='{{bucket}}'
            settings='{{CameraAgentSettingsMount}}'
            identity() { stat -c '%d:%i' "$1" 2>/dev/null; }
            own() { # path expected-identity
              [[ -e $1 && ! -L $1 ]] || fail "cameraagent-settings-not-the-instances"
              [ "$(identity "$1")" = "$2" ] || fail "cameraagent-settings-not-the-instances"
            }
            [[ -d $settings ]] || fail "cameraagent-settings-not-the-instances"
            own "$settings" '{{expectation.Directory}}'
            own "$settings/CameraAgent__Silo__Endpoint" '{{expectation.EndpointFile}}'
            [[ -f "$settings/CameraAgent__Silo__Endpoint" ]] || fail "cameraagent-settings-not-the-instances"
            endpoint_sha="$(sha256sum < "$settings/CameraAgent__Silo__Endpoint")" || fail "cameraagent-settings-unreadable"
            [ "${endpoint_sha%% *}" = '{{expectation.EndpointSha256}}' ] || fail "cameraagent-endpoint-not-the-instances"
            own "$settings/{{WriterAccessKeySetting}}" '{{expectation.AccessFile}}'
            own "$settings/{{WriterSecretKeySetting}}" '{{expectation.SecretFile}}'
            [[ -f "$settings/{{WriterAccessKeySetting}}" && -f "$settings/{{WriterSecretKeySetting}}" ]] || fail "cameraagent-settings-not-the-instances"
            writer_access="$(cat "$settings/{{WriterAccessKeySetting}}" 2>/dev/null)" || fail "cameraagent-settings-unreadable"
            writer_secret="$(cat "$settings/{{WriterSecretKeySetting}}" 2>/dev/null)" || fail "cameraagent-settings-unreadable"
            [[ $writer_access =~ ^[A-Za-z0-9]{16,128}$ && $writer_secret =~ ^[A-Za-z0-9+/=]{16,128}$ ]] || fail "cameraagent-credential-unusable"
            secret_sha="$(printf '%s' "$writer_secret" | sha256sum)"; secret_sha="${secret_sha%% *}"
            export MC_HOST_writer="http://${writer_access}:${writer_secret}@{{NetworkAlias}}:{{Port}}"
            unset writer_secret
            for _ in $(seq 1 20); do mcli ready writer >/dev/null 2>&1 && break; sleep 1; done
            mcli ready writer >/dev/null 2>&1 || fail "silo-unreachable-from-cameraagent"
            mcli stat "writer/$bucket" >/dev/null 2>&1 || fail "writer-cannot-read-bucket"
            info="$(mcli version info "writer/$bucket" --json 2>/dev/null || true)"
            [[ $info =~ \"status\":\"Enabled\" ]] || fail "versioning-not-enabled"
            stat="$(mcli stat "writer/$bucket/{{MarkerKey}}" --json 2>/dev/null || true)"
            [[ $stat =~ \"versionID\":\"([A-Za-z0-9-]+)\" ]] || fail "marker-missing"
            [ "${BASH_REMATCH[1]}" = '{{markerVersionId}}' ] || fail "marker-version-changed"
            mcli cat --version-id '{{markerVersionId}}' "writer/$bucket/{{MarkerKey}}" > "$work/marker" 2>/dev/null || fail "marker-unreadable"
            sha="$(sha256sum "$work/marker")"; sha="${sha%% *}"
            fence_stat="$(mcli stat "writer/$bucket/{{FenceKey}}" --json 2>/dev/null || true)"
            [[ $fence_stat =~ \"versionID\":\"([A-Za-z0-9-]+)\" ]] || fail "fence-missing"
            fence_version="${BASH_REMATCH[1]}"
            mcli cat --version-id "$fence_version" "writer/$bucket/{{FenceKey}}" > "$work/fence" 2>/dev/null || fail "fence-unreadable"
            printf '{"outcome":"verified","markerSha256":"%s","marker":"%s","fenceVersionId":"%s","fence":"%s","endpoint":"%s","writerAccessKeyId":"%s","writerSecretSha256":"%s"}\n' \
              "$sha" "$(base64 -w0 "$work/marker")" "$fence_version" "$(base64 -w0 "$work/fence")" '{{Endpoint}}' "$writer_access" "$secret_sha"
            """;
    }

    private static void RequireWriter(string accessKey, string secretKey)
    {
        // The credential is embedded in the script the container reads from standard input, never in an argument, so
        // it must not be able to end the quoted assignment that carries it.
        if (!AccessKeyPattern().IsMatch(accessKey) || !SecretKeyPattern().IsMatch(secretKey))
            throw new InstallerException("The Silo writer credential is not in the expected form.");
    }

    private static void RequireBucket(string bucket)
    {
        if (!BucketPattern().IsMatch(bucket)) throw new InstallerException("The Silo bucket name is invalid.");
    }

    [GeneratedRegex("^hvo-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex BucketPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^[0-9]{1,20}:[0-9]{1,20}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityPattern();

    [GeneratedRegex("^[A-Z0-9]{20}$", RegexOptions.CultureInvariant)]
    private static partial Regex GeneratedKeyPattern();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    internal static partial Regex VersionIdPattern();

    [GeneratedRegex("^[A-Za-z0-9]{16,128}$", RegexOptions.CultureInvariant)]
    internal static partial Regex AccessKeyPattern();

    [GeneratedRegex("^[A-Za-z0-9+/=]{16,128}$", RegexOptions.CultureInvariant)]
    internal static partial Regex SecretKeyPattern();

    /// <summary>
    /// Parses one bootstrap or verification script's single JSON line. A refusal surfaces only its fixed reason code;
    /// anything else is reported without echoing output, which can hold the generated writer secret.
    /// </summary>
    public static JsonElement ParseScriptResult(ProcessResult result, string expectedOutcome, string step)
    {
        ArgumentNullException.ThrowIfNull(result);
        var line = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(line ?? string.Empty);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InstallerException($"The Silo {step} step returned no readable result (exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}).");
        }
        var outcome = root.TryGetProperty("outcome", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (outcome == "refused")
        {
            var reason = root.TryGetProperty("reason", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
            reason = reason is not null && ReasonPattern().IsMatch(reason) ? reason : "unknown";
            throw InstallerException.Refused($"The Silo {step} step was refused: {reason}.");
        }
        if (result.ExitCode != 0 || outcome != expectedOutcome)
            throw new InstallerException($"The Silo {step} step did not complete (exit {result.ExitCode.ToString(CultureInfo.InvariantCulture)}).");
        return root;
    }

    [GeneratedRegex("^[a-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ReasonPattern();

    /// <summary>
    /// The filesystem type, source and device of the mount that holds <paramref name="path"/>, from mountinfo text: the
    /// longest mount point that contains the path's nearest existing ancestor.
    /// </summary>
    public static (string FileSystem, string Source, string Device) MountOf(string path, string mountInfo)
    {
        ArgumentNullException.ThrowIfNull(mountInfo);
        var target = Path.GetFullPath(path);
        (string MountPoint, string FileSystem, string Source, string Device)? best = null;
        foreach (var line in mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0) continue;
            var left = line[..separator].Split(' ');
            var right = line[(separator + 3)..].Split(' ');
            if (left.Length < 5 || right.Length < 2) continue;
            var mountPoint = HostProductRootPolicy.UnescapeMountPath(left[4]);
            var contains = mountPoint == "/" || target == mountPoint ||
                           target.StartsWith(mountPoint.TrimEnd('/') + "/", StringComparison.Ordinal);
            if (contains && (best is null || mountPoint.Length >= best.Value.MountPoint.Length))
                best = (mountPoint, right[0], right[1], left[2]);
        }
        return best is { } found
            ? (found.FileSystem, found.Source, found.Device)
            : throw new InstallerException("The filesystem that would hold the Silo data root could not be identified.");
    }

    /// <summary>
    /// The read-only admission of a new instance's Silo data root (contract 8.1 "clean roots"): it must not exist yet,
    /// and the filesystem that will hold it must be local ext4. Returns the filesystem and device it will land on.
    /// </summary>
    public static (string FileSystem, string Device) AdmitFreshDataRoot(string dataRoot, string mountInfo)
    {
        if (File.Exists(dataRoot) || Directory.Exists(dataRoot) || new FileInfo(dataRoot).LinkTarget is not null)
            throw InstallerException.Refused("The Silo data root already exists; a Silo-layout instance starts only on an empty, new data root.");
        var ancestor = LinuxHostEnvironment.NearestExistingAncestor(dataRoot);
        var (fileSystem, _, device) = MountOf(ancestor, mountInfo);
        if (!string.Equals(fileSystem, RequiredFileSystem, StringComparison.Ordinal))
            throw InstallerException.Refused(
                $"The Silo data root must be on a local {RequiredFileSystem} filesystem; it would be on {Printable(fileSystem)}.");
        return (fileSystem, device);
    }

    /// <summary>
    /// The data root a resumed install created before it was interrupted: a directory of the runtime identity, not a link,
    /// still on local ext4. Nothing else is adopted.
    /// </summary>
    public static (string FileSystem, string Device) AdmitRetainedDataRoot(string dataRoot, string mountInfo, uint uid, uint gid)
    {
        if (new DirectoryInfo(dataRoot).LinkTarget is not null)
            throw InstallerException.Refused("The retained Silo data root is a link; it is not adopted.");
        var identity = NativeLinux.GetDirectoryIdentity(dataRoot);
        if (identity.Uid != uid || identity.Gid != gid)
            throw InstallerException.Refused("The retained Silo data root does not belong to the runtime identity.");
        var (fileSystem, _, device) = MountOf(dataRoot, mountInfo);
        if (!string.Equals(fileSystem, RequiredFileSystem, StringComparison.Ordinal))
            throw InstallerException.Refused($"The Silo data root must be on a local {RequiredFileSystem} filesystem.");
        return (fileSystem, device);
    }

    /// <summary>
    /// Whether fence bytes are a writer fence of this instance's binding at <paramref name="generation"/>, and the writer
    /// key ID they name.
    /// </summary>
    public static string? FenceWriter(ReadOnlySpan<byte> fence, Guid instanceId, int generation)
    {
        try
        {
            using var document = JsonDocument.Parse(fence.ToArray());
            var root = document.RootElement;
            var bucket = BucketFor(instanceId);
            return root.ValueKind == JsonValueKind.Object &&
                   Text(root, "format") == FenceFormat &&
                   Text(root, "instanceId") == instanceId.ToString("D") &&
                   Text(root, "storageBindingId") == StorageBindingIdFor(instanceId, StoreIdFor(instanceId), bucket) &&
                   root.TryGetProperty("writerGeneration", out var value) && value.TryGetInt32(out var number) && number == generation &&
                   Text(root, "writerAccessKeyId") is { } access && AccessKeyPattern().IsMatch(access)
                ? access
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Printable(string value)
        => value.Length <= 32 && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')
            ? value
            : "an unsupported filesystem";
}

/// <summary>Free space on the Silo data-root filesystem against the profile's reserve (#1966).</summary>
internal sealed record SiloHeadroom(long AvailableBytes, long TotalBytes, long RequiredBytes)
{
    public bool Ready => AvailableBytes >= RequiredBytes;
}
