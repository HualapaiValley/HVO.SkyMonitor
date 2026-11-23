using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// One installed Silo-layout instance on a real temporary product root, and the Docker state it should see: the Silo
/// and CameraAgent containers, the instance's private network, and the store's marker and fence. Each test edits the
/// state to the case it proves; <see cref="Answer"/> scripts the Docker CLI from it.
/// </summary>
internal sealed class SiloHarness : IDisposable
{
    public const string Access = "AKIAEXAMPLEWRITER001";
    public const string Secret = "exampleWriterSecret0123456789ABCDEFGHIJ";
    public const string CameraAgentImage = "sha256:8861c41531d0b696086688ca1fa11c1bd5acb05f54965b6bf436391a6760cb77";
    public static readonly SiloImagePin.PlatformImage Amd64 = SiloImagePin.ForArchitecture("amd64")!;
    public static readonly SiloImagePin.PlatformImage Arm64 = SiloImagePin.ForArchitecture("arm64")!;

    private readonly DirectoryInfo? root;

    /// <summary>A harness on its own temporary product root.</summary>
    public SiloHarness()
        : this(null, Guid.NewGuid())
    {
    }

    /// <summary>
    /// A harness on an existing instance's paths (a lifecycle fixture's), which it does not own. Unless
    /// <paramref name="seed"/>, it writes nothing there: an installer flow creates the instance itself and the harness
    /// only answers Docker.
    /// </summary>
    public SiloHarness(InstallationPaths? paths, Guid instanceId, bool seed = true)
    {
        InstanceId = instanceId;
        if (paths is null)
        {
            root = Directory.CreateTempSubdirectory("hvo-silo-harness-");
            paths = InstallationPaths.Create(Path.Combine(root.FullName, "product"), InstanceId, ProductionCatalog.DefaultCatalogId);
        }
        Paths = paths;
        var secrets = Path.Combine(Paths.ConfigRoot, "secrets");
        if (seed)
        {
            Directory.CreateDirectory(Paths.InstanceRoot);
            SafeFileSystem.CreateOwnerDirectory(secrets);
            SafeFileSystem.WriteTextAtomic(Path.Combine(secrets, SiloRuntimeProfile.WriterAccessKeySetting), Access);
            SafeFileSystem.WriteTextAtomic(Path.Combine(secrets, SiloRuntimeProfile.WriterSecretKeySetting), Secret);
            SafeFileSystem.WriteTextAtomic(Path.Combine(secrets, "CameraAgent__Silo__Endpoint"), SiloRuntimeProfile.Endpoint);
            SafeFileSystem.WriteTextAtomic(Path.Combine(SiloRuntimeProfile.RootSecretsDirectory(Paths), "root-user"), "hvoroot");
            SafeFileSystem.WriteTextAtomic(Path.Combine(SiloRuntimeProfile.RootSecretsDirectory(Paths), "root-password"), "hvorootpassword0123456789");
            Directory.CreateDirectory(DataRoot);
        }
        var created = DateTimeOffset.Parse("2026-11-22T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        Marker = SiloRuntimeProfile.Marker(InstanceId, created);
        Fence = SiloRuntimeProfile.Fence(InstanceId, Access, created);
        var (fileSystem, _, device) = SiloRuntimeProfile.MountOf(DataRoot, File.ReadAllText(HostProductRootPolicy.MountInfoPath));
        var storeId = SiloRuntimeProfile.StoreIdFor(InstanceId);
        Identity = new SiloInstallationIdentity(
            SiloRuntimeProfile.StateContract, SiloImagePin.Reference, SiloImagePin.Release, SiloImagePin.IndexDigest,
            Amd64.ManifestDigest, Amd64.ManifestDigest, SiloImagePin.SourceRevision, DataRoot, fileSystem, device, storeId, Bucket,
            SiloRuntimeProfile.StorageBindingIdFor(InstanceId, storeId, Bucket), SiloRuntimeProfile.WriterGeneration, Access,
            "3a70979c-3128-47c3-8653-7cdc283f4f05", Sha(Marker), "4b81a8ad-4239-48d4-9764-8ded394f5f16", Sha(Fence),
            Guid.NewGuid(), DiskSyncOnWrite: true);
        // The bucket and binding CameraAgent is configured with, as the installer writes them.
        if (seed)
        {
            foreach (var (name, value) in SiloRuntimeProfile.BindingSettings(Identity))
                SafeFileSystem.WriteTextAtomic(Path.Combine(secrets, name), value);
        }
        CameraAgentStarted = DateTimeOffset.UtcNow.AddMinutes(1);
    }

    public Guid InstanceId { get; }
    public InstallationPaths Paths { get; }
    public SiloInstallationIdentity Identity { get; set; }
    public byte[] Marker { get; }
    public byte[] Fence { get; set; }
    public string DataRoot => SiloRuntimeProfile.DataRoot(Paths);
    public string Bucket => SiloRuntimeProfile.BucketFor(InstanceId);
    public string Project => ComposeDeployment.ContainerNameFor(InstanceId);
    public string SiloName => SiloRuntimeProfile.ContainerNameFor(InstanceId);
    public string NetworkName => Project + "_silo";
    public string SiloId { get; } = Hex('5');
    public string CameraAgentId { get; } = Hex('c');
    public string NetworkId { get; } = Hex('9');

    // --- the state a test edits ---------------------------------------------------------------------------------------
    public string SiloImage { get; set; } = SiloImagePin.IndexDigest;
    public (string Digest, string Architecture)? SiloManifest { get; set; } = (Amd64.ManifestDigest, "amd64");
    public Dictionary<string, string[]> SiloNetworks { get; } = [];
    public bool CameraAgentExists { get; set; } = true;
    public bool CameraAgentRunning { get; set; } = true;
    public string CameraAgentHealth { get; set; } = "healthy";
    public string CameraAgentImageId { get; set; } = CameraAgentImage;
    public DateTimeOffset CameraAgentStarted { get; set; }
    public bool CameraAgentOnNetwork { get; set; } = true;
    public JsonObject NetworkLabels { get; set; } = null!;
    public bool NetworkInternal { get; set; } = true;
    public List<string> NetworkMembers { get; } = [];
    public bool NetworkInspectFails { get; set; }
    public bool ProbeFails { get; set; }

    /// <summary>CameraAgent's live settings mount: its host source, whether it is read-only, or absent.</summary>
    public string? SecretsMountSource { get; set; }
    public bool SecretsMountReadOnly { get; set; } = true;
    public bool SecretsMountPresent { get; set; } = true;
    public List<string> CameraAgentEnvironment { get; } = ["HVO_KEY_PER_FILE_DIRECTORY=/run/hvo-secrets"];

    /// <summary>What the probe reads from CameraAgent's live settings mount: the endpoint and writer credential it consumes.</summary>
    public string LiveEndpoint { get; set; } = SiloRuntimeProfile.Endpoint;
    public string LiveAccess { get; set; } = Access;
    public string LiveSecret { get; set; } = Secret;

    /// <summary>Data-root exclusivity inventory: a listing that fails, or a listed container whose inspection fails.</summary>
    public bool ContainerListFails { get; set; }
    public string? ListedButUninspectable { get; set; }
    public bool ListedButGone { get; set; }

    /// <summary>Other containers on the daemon, listed and inspected as Docker answers; each needs an <c>Id</c>.</summary>
    public List<JsonObject> OtherContainers { get; } = [];

    /// <summary>Volumes as <c>docker volume inspect</c> answers them, by name.</summary>
    public Dictionary<string, JsonObject> Volumes { get; } = [];

    /// <summary>The daemon's data root, which holds the volumes it manages.</summary>
    public string DockerRootDirectory { get; set; } = "/var/lib/docker";

    /// <summary>Another container, running, with the given mounts (each a Docker <c>Mounts</c> entry).</summary>
    public JsonObject AddOtherContainer(params JsonObject[] mounts)
    {
        var container = new JsonObject
        {
            ["Id"] = Hex((char)('1' + OtherContainers.Count)),
            ["Name"] = "/other-" + OtherContainers.Count,
            ["State"] = new JsonObject { ["Running"] = true },
            ["Mounts"] = new JsonArray([.. mounts])
        };
        OtherContainers.Add(container);
        return container;
    }

    public static JsonObject BindMount(string source) => new() { ["Type"] = "bind", ["Source"] = source, ["Destination"] = "/other" };

    public static JsonObject VolumeMount(string name, string source) =>
        new() { ["Type"] = "volume", ["Name"] = name, ["Source"] = source, ["Destination"] = "/other" };

    /// <summary>A local volume with the given options (none for one Docker manages), and the mount that uses it.</summary>
    public JsonObject LocalVolume(string name, params (string Key, string Value)[] options)
    {
        var mountpoint = $"{DockerRootDirectory}/volumes/{name}/_data";
        var answer = new JsonObject { ["Name"] = name, ["Driver"] = "local", ["Mountpoint"] = mountpoint };
        if (options.Length > 0)
        {
            var values = new JsonObject();
            foreach (var (key, value) in options) values[key] = value;
            answer["Options"] = values;
        }
        else
        {
            answer["Options"] = null;
        }
        Volumes[name] = answer;
        return VolumeMount(name, mountpoint);
    }

    /// <summary>Answers the bootstrap's store scripts (bucket and writer creation, binding), when a test scripts the store.</summary>
    public Func<string, ProcessResult>? Store { get; set; }

    public SiloHarness Defaults()
    {
        SiloNetworks[NetworkName] = ["hvo-silo", SiloName];
        NetworkLabels = new JsonObject
        {
            ["com.docker.compose.project"] = Project,
            ["com.docker.compose.network"] = "silo",
            ["io.hvo.skymonitor.instance-id"] = InstanceId.ToString("D"),
            ["io.hvo.skymonitor.network"] = "silo"
        };
        NetworkMembers.Clear();
        NetworkMembers.AddRange([SiloId, CameraAgentId]);
        return this;
    }

    public DockerClient Docker(out ScriptedDocker runner)
    {
        runner = new ScriptedDocker(Answer);
        return new DockerClient(runner);
    }

    /// <summary>The scripted Docker CLI: every query the Silo runtime's verification may issue, answered from the state.</summary>
    public ProcessResult? Answer(IReadOnlyList<string> args, string? input)
    {
        switch (args)
        {
            case ["version", "--format", _]:
                return ScriptedDocker.Ok("29.0.0");
            case ["compose", ..]:
                return ScriptedDocker.Ok(string.Empty);
            case ["container", "inspect", var name] when name == SiloName || name == SiloId:
                return ScriptedDocker.Ok(new JsonArray(SiloContainer()));
            case ["container", "inspect", var name] when name == Project || name == CameraAgentId:
                return CameraAgentExists ? ScriptedDocker.Ok(new JsonArray(CameraAgentContainer())) : ScriptedDocker.NoSuch("container", name);
            case ["container", "ls", "--all", "--quiet", "--no-trunc"]:
                if (ContainerListFails) return ScriptedDocker.DaemonError();
                return ScriptedDocker.Ok((CameraAgentExists ? $"{SiloId}\n{CameraAgentId}\n" : $"{SiloId}\n") +
                                         (ListedButUninspectable is { } listed ? listed + "\n" : string.Empty) +
                                         (ListedButGone ? new string('d', 64) + "\n" : string.Empty) +
                                         string.Concat(OtherContainers.Select(static other => other["Id"]!.GetValue<string>() + "\n")));
            case ["container", "inspect", var name] when OtherContainers.Find(other => other["Id"]!.GetValue<string>() == name) is { } other:
                return ScriptedDocker.Ok(new JsonArray(other.DeepClone()));
            case ["volume", "inspect", var name]:
                return Volumes.TryGetValue(name, out var volume) ? ScriptedDocker.Ok(new JsonArray(volume.DeepClone())) : ScriptedDocker.NoSuch("volume", name);
            case ["info", "--format", "{{.DockerRootDir}}"]:
                return ScriptedDocker.Ok(DockerRootDirectory + "\n");
            case ["container", "inspect", var name] when name == ListedButUninspectable:
                return ScriptedDocker.DaemonError();
            case ["container", "inspect", var name] when ListedButGone && name == new string('d', 64):
                return ScriptedDocker.NoSuch("container", name);
            case ["network", "inspect", "--format", "{{.Internal}}", var network]:
                return NetworkInspectFails ? ScriptedDocker.DaemonError()
                    : SiloNetworks.ContainsKey(network) ? ScriptedDocker.Ok(network == NetworkName ? (NetworkInternal ? "true" : "false") : "true")
                    : ScriptedDocker.NoSuch("network", network);
            case ["network", "inspect", var network]:
                return NetworkInspectFails ? ScriptedDocker.DaemonError()
                    : network == NetworkName || network == NetworkId ? ScriptedDocker.Ok(new JsonArray(Network()))
                    : ScriptedDocker.NoSuch("network", network);
            case ["exec", "--interactive", _, "bash", "-s"] or ["run", ..] when input is not null && Store is not null &&
                                                                              (input.Contains("mcli mb", StringComparison.Ordinal) || input.Contains("create_only", StringComparison.Ordinal) ||
                                                                               input.Contains("hvo-bootstrap:", StringComparison.Ordinal)):
                return Store(input);
            case ["exec", "--interactive", _, "bash", "-s"] or ["run", ..] when input is not null:
                return ProbeFails ? new ProcessResult(3, "{\"outcome\":\"refused\",\"reason\":\"writer-cannot-read-bucket\"}\n", string.Empty)
                    : ScriptedDocker.Ok(Verified());
            default:
                return null;
        }
    }

    public JsonObject SiloContainer()
    {
        var networks = new JsonObject();
        foreach (var (network, aliases) in SiloNetworks)
            networks[network] = new JsonObject
            {
                ["NetworkID"] = network == NetworkName ? NetworkId : Hex('7'),
                ["Aliases"] = new JsonArray([.. aliases.Select(static alias => (JsonNode)JsonValue.Create(alias))])
            };
        var container = new JsonObject
        {
            ["Id"] = SiloId,
            ["Name"] = "/" + SiloName,
            ["Image"] = SiloImage,
            ["State"] = new JsonObject { ["Running"] = true, ["Pid"] = 4242, ["Health"] = new JsonObject { ["Status"] = "healthy" } },
            ["HostConfig"] = new JsonObject { ["PortBindings"] = new JsonObject(), ["PublishAllPorts"] = false, ["ReadonlyRootfs"] = true },
            ["Config"] = new JsonObject
            {
                ["User"] = "1000:1000",
                ["Env"] = new JsonArray("MINIO_BROWSER=off", "MINIO_FS_OSYNC=on"),
                ["Labels"] = new JsonObject
                {
                    ["com.docker.compose.project"] = Project,
                    ["com.docker.compose.service"] = "silo",
                    ["io.hvo.skymonitor.instance-id"] = InstanceId.ToString("D")
                }
            },
            ["NetworkSettings"] = new JsonObject { ["Networks"] = networks },
            ["Mounts"] = new JsonArray(new JsonObject { ["Source"] = DataRoot, ["Destination"] = "/data" })
        };
        if (SiloManifest is { } manifest)
            container["ImageManifestDescriptor"] = new JsonObject
            {
                ["mediaType"] = "application/vnd.oci.image.manifest.v1+json",
                ["digest"] = manifest.Digest,
                ["size"] = 2574,
                ["platform"] = new JsonObject { ["architecture"] = manifest.Architecture, ["os"] = "linux" }
            };
        return container;
    }

    public JsonObject CameraAgentContainer() => new()
    {
        ["Id"] = CameraAgentId,
        ["Name"] = "/" + Project,
        ["Image"] = CameraAgentImageId,
        ["State"] = new JsonObject
        {
            ["Running"] = CameraAgentRunning,
            ["StartedAt"] = CameraAgentStarted.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", System.Globalization.CultureInfo.InvariantCulture),
            ["Health"] = new JsonObject { ["Status"] = CameraAgentHealth }
        },
        ["Config"] = new JsonObject
        {
            ["Labels"] = new JsonObject
            {
                ["com.docker.compose.project"] = Project,
                ["io.hvo.skymonitor.instance-id"] = InstanceId.ToString("D")
            },
            ["Env"] = new JsonArray([.. CameraAgentEnvironment.Select(static variable => (JsonNode)JsonValue.Create(variable))])
        },
        ["HostConfig"] = new JsonObject(),
        ["Mounts"] = SecretsMountPresent
            ? new JsonArray(new JsonObject
            {
                ["Type"] = "bind",
                ["Source"] = SecretsMountSource ?? Path.Combine(Paths.ConfigRoot, "secrets"),
                ["Destination"] = "/run/hvo-secrets",
                ["RW"] = !SecretsMountReadOnly
            })
            : new JsonArray(),
        ["NetworkSettings"] = new JsonObject
        {
            ["Networks"] = CameraAgentOnNetwork
                ? new JsonObject { [Project + "_default"] = new JsonObject(), [NetworkName] = new JsonObject { ["NetworkID"] = NetworkId } }
                : new JsonObject { [Project + "_default"] = new JsonObject() }
        }
    };

    public JsonObject Network()
    {
        var members = new JsonObject();
        foreach (var member in NetworkMembers) members[member] = new JsonObject { ["Name"] = member };
        return new JsonObject
        {
            ["Name"] = NetworkName,
            ["Id"] = NetworkId,
            ["Driver"] = "bridge",
            ["Internal"] = NetworkInternal,
            ["Labels"] = NetworkLabels.DeepClone(),
            ["Containers"] = members
        };
    }

    /// <summary>The binding verification's answer: the marker and the fence as the store holds them.</summary>
    public string Verified() =>
        $"{{\"outcome\":\"verified\",\"markerSha256\":\"{Sha(Marker)}\",\"marker\":\"{Convert.ToBase64String(Marker)}\"," +
        $"\"fenceVersionId\":\"{Identity.FenceVersionId}\",\"fence\":\"{Convert.ToBase64String(Fence)}\"," +
        $"\"endpoint\":\"{LiveEndpoint}\",\"writerAccessKeyId\":\"{LiveAccess}\",\"writerSecretSha256\":\"{Sha(Encoding.UTF8.GetBytes(LiveSecret))}\"}}\n";

    public static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string Hex(char digit) => new(digit, 64);

    public static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    public void Dispose() => root?.Delete(recursive: true);
}
