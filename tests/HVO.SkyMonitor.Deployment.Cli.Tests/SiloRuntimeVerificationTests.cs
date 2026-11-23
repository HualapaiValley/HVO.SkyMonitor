using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The Silo container's identity and private network as a Silo-layout verification sees them (#1966 review). The image a
/// container runs is the pinned release only when it is this daemon's own platform child of the pinned index: a
/// container created from the shared index reports the index as its image whichever child it runs, so only its
/// manifest descriptor (containerd store) or its config ID (classic store) proves it. The private network must be the
/// instance's own Compose network, internal, holding exactly the instance's Silo (under its alias) and its CameraAgent.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloRuntimeVerificationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    // Recorded at install as the loaded platform manifest (offline) or as the pulled index (online).
    [DataRow("manifest", "index", "amd64-manifest", true)]
    [DataRow("manifest", "manifest", "amd64-manifest", true)]
    [DataRow("index", "index", "amd64-manifest", true)]
    [DataRow("index", "manifest", "amd64-manifest", true)]
    [DataRow("config", "config", "none", true)]
    [DataRow("index", "index", "arm64-manifest", false)]
    [DataRow("manifest", "index", "arm64-manifest", false)]
    [DataRow("config", "arm64-config", "none", false)]
    [DataRow("index", "index", "none", false)]
    [DataRow("index", "unknown", "none", false)]
    [DataRow("index", "unknown", "amd64-manifest", false)]
    public async Task ContainerIdentity_IsThisPlatformsChildOfThePinNotTheRecordedImageString(
        string recorded, string running, string descriptor, bool accepted)
    {
        using var harness = new SiloHarness().Defaults();
        harness.SiloImage = Id(running);
        harness.SiloManifest = descriptor switch
        {
            "amd64-manifest" => (SiloHarness.Amd64.ManifestDigest, "amd64"),
            "arm64-manifest" => (SiloHarness.Arm64.ManifestDigest, "arm64"),
            _ => null
        };
        var docker = harness.Docker(out _);
        var image = new SiloImageIdentity(SiloImagePin.Reference, Id(recorded), "amd64", SiloHarness.Amd64.ManifestDigest,
            SiloImagePin.Release, SiloImagePin.SourceRevision);

        var verification = SiloInstallation.VerifyContainerAsync(docker, harness.InstanceId, harness.SiloName, harness.DataRoot, image,
            expectApplication: true, TestContext.CancellationToken);

        if (accepted)
        {
            await verification;
        }
        else
        {
            var refusal = await Assert.ThrowsAsync<InstallerException>(() => verification);
            StringAssert.Contains(refusal.Message, "pinned", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [DataRow("exact", null)]
    [DataRow("detached", "network")]
    [DataRow("wrong-network", "network")]
    [DataRow("second-network", "network")]
    [DataRow("not-internal", "network")]
    [DataRow("foreign-labels", "network")]
    [DataRow("no-alias", "network")]
    [DataRow("foreign-sibling", "network")]
    [DataRow("application-detached", "network")]
    [DataRow("inspect-fails", "network")]
    public async Task PrivateNetwork_IsTheInstancesOwnInternalNetworkHoldingOnlyItsSiloAndCameraAgent(string change, string? failure)
    {
        using var harness = new SiloHarness().Defaults();
        switch (change)
        {
            case "detached": harness.SiloNetworks.Clear(); harness.NetworkMembers.Remove(harness.SiloId); break;
            case "wrong-network":
                harness.SiloNetworks.Clear();
                harness.SiloNetworks["hvo-skymonitor-other_silo"] = ["hvo-silo"];
                harness.NetworkMembers.Remove(harness.SiloId);
                break;
            case "second-network": harness.SiloNetworks["bridge"] = []; break;
            case "not-internal": harness.NetworkInternal = false; break;
            case "foreign-labels": harness.NetworkLabels["com.docker.compose.project"] = "hvo-skymonitor-other"; break;
            case "no-alias": harness.SiloNetworks[harness.NetworkName] = [harness.SiloName]; break;
            case "foreign-sibling": harness.NetworkMembers.Add(new string('f', 64)); break;
            case "application-detached": harness.CameraAgentOnNetwork = false; harness.NetworkMembers.Remove(harness.CameraAgentId); break;
            case "inspect-fails": harness.NetworkInspectFails = true; break;
        }
        var docker = harness.Docker(out _);
        var image = new SiloImageIdentity(SiloImagePin.Reference, SiloImagePin.IndexDigest, "amd64", SiloHarness.Amd64.ManifestDigest,
            SiloImagePin.Release, SiloImagePin.SourceRevision);

        var verification = SiloInstallation.VerifyContainerAsync(docker, harness.InstanceId, harness.SiloName, harness.DataRoot, image,
            expectApplication: true, TestContext.CancellationToken);

        if (failure is null)
        {
            await verification;
        }
        else
        {
            var refusal = await Assert.ThrowsAsync<InstallerException>(() => verification);
            StringAssert.Contains(refusal.Message, failure, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [DataRow("clean", true)]
    [DataRow("listed-then-removed", true)]
    [DataRow("listing-unanswered", false)]
    [DataRow("listed-but-uninspectable", false)]
    public async Task DataRootExclusivity_FailsClosedOnAnyInventoryItCannotConclude(string change, bool concluded)
    {
        using var harness = new SiloHarness().Defaults();
        switch (change)
        {
            case "listed-then-removed": harness.ListedButGone = true; break;
            case "listing-unanswered": harness.ContainerListFails = true; break;
            case "listed-but-uninspectable": harness.ListedButUninspectable = new string('a', 64); break;
        }
        var docker = harness.Docker(out _);

        var check = docker.EnsureSiloDataRootExclusiveAsync(harness.DataRoot, harness.SiloName, TestContext.CancellationToken);

        if (concluded)
            await check;
        else
            await Assert.ThrowsAsync<InstallerException>(() => check);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("unrelated-bind", null)]
    [DataRow("tmpfs-mount", null)]
    [DataRow("volume-docker-manages", null)]
    [DataRow("tmpfs-volume", null)]
    [DataRow("bind-volume-elsewhere", null)]
    [DataRow("data-root", "which is the data root, lies inside it or holds it")]
    [DataRow("path-inside", "which is the data root, lies inside it or holds it")]
    [DataRow("ancestor", "which is the data root, lies inside it or holds it")]
    [DataRow("link-to-the-data-root", "which is the data root, lies inside it or holds it")]
    [DataRow("link-to-a-path-inside", "which is the data root, lies inside it or holds it")]
    [DataRow("path-inside-through-a-link", "which is the data root, lies inside it or holds it")]
    [DataRow("source-that-does-not-resolve", "which does not resolve here, so it is not proved apart from the data root")]
    [DataRow("bind-volume-of-the-data-root", "a bind of")]
    [DataRow("tmpfs-typed-bind-volume-of-the-data-root", "a bind of")]
    [DataRow("rbind-volume-of-another-type", "a bind of")]
    [DataRow("bind-volume-naming-no-device", "is a bind that names no device, so where its data lies is not proved")]
    [DataRow("tmpfs-volume-moved", "is of type tmpfs with options move, so where its data lies is not proved")]
    [DataRow("volume-of-another-driver", "uses the nfs-plugin driver, so where its data lies is not proved")]
    [DataRow("volume-of-another-type", "is of type ext4 with options none, so where its data lies is not proved")]
    [DataRow("volume-docker-cannot-inspect", "Docker could not inspect volume vanished")]
    [DataRow("volume-docker-manages-around-the-data-root", "kept in Docker's data root")]
    [DataRow("mount-of-another-type", "has a mount of type image, so where its data lies is not proved")]
    public async Task DataRootExclusivity_ProvesEveryOtherMountApartFromTheDataRoot(string shape, string? refusal)
    {
        using var harness = new SiloHarness().Defaults();
        var scratch = Directory.CreateTempSubdirectory("hvo-silo-exclusivity-");
        try
        {
            var inside = Path.Combine(harness.DataRoot, ".minio.sys");
            Directory.CreateDirectory(inside);
            Directory.CreateDirectory(Path.Combine(scratch.FullName, "elsewhere"));
            string Link(string name, string target)
            {
                var link = Path.Combine(scratch.FullName, name);
                File.CreateSymbolicLink(link, target);
                return link;
            }
            var mount = shape switch
            {
                "unrelated-bind" => SiloHarness.BindMount(Path.Combine(scratch.FullName, "elsewhere")),
                "tmpfs-mount" => new JsonObject { ["Type"] = "tmpfs", ["Source"] = string.Empty, ["Destination"] = "/tmp" },
                "volume-docker-manages" => harness.LocalVolume("cache"),
                "tmpfs-volume" => harness.LocalVolume("memory", ("type", "tmpfs"), ("o", "size=64m")),
                "bind-volume-elsewhere" => harness.LocalVolume("shared", ("type", "none"), ("o", "bind"), ("device", Path.Combine(scratch.FullName, "elsewhere"))),
                "data-root" => SiloHarness.BindMount(harness.DataRoot),
                "path-inside" => SiloHarness.BindMount(inside),
                "ancestor" => SiloHarness.BindMount(harness.Paths.InstanceRoot),
                "link-to-the-data-root" => SiloHarness.BindMount(Link("data-alias", harness.DataRoot)),
                "link-to-a-path-inside" => SiloHarness.BindMount(Link("minio-alias", inside)),
                "path-inside-through-a-link" => SiloHarness.BindMount(Path.Combine(Link("data-alias", harness.DataRoot), ".minio.sys")),
                "source-that-does-not-resolve" => SiloHarness.BindMount(Path.Combine(scratch.FullName, "renamed-away")),
                "bind-volume-of-the-data-root" => harness.LocalVolume("bound", ("type", "none"), ("o", "bind"), ("device", harness.DataRoot)),
                "tmpfs-typed-bind-volume-of-the-data-root" => harness.LocalVolume("bound", ("type", "tmpfs"), ("o", "bind"), ("device", harness.DataRoot)),
                "rbind-volume-of-another-type" => harness.LocalVolume("bound", ("type", "ext4"), ("o", "rbind,ro"), ("device", Link("data-alias", harness.DataRoot))),
                "bind-volume-naming-no-device" => harness.LocalVolume("bound", ("type", "none"), ("o", "bind")),
                "tmpfs-volume-moved" => harness.LocalVolume("moved", ("type", "tmpfs"), ("o", "move"), ("device", Path.Combine(scratch.FullName, "elsewhere"))),
                "volume-of-another-driver" => RemoteVolume(harness),
                "volume-of-another-type" => harness.LocalVolume("disk", ("type", "ext4"), ("device", "/dev/sdz1")),
                "volume-docker-cannot-inspect" => SiloHarness.VolumeMount("vanished", "/var/lib/docker/volumes/vanished/_data"),
                "volume-docker-manages-around-the-data-root" => ManagedAround(harness),
                "mount-of-another-type" => new JsonObject { ["Type"] = "image", ["Source"] = "sha256:0123", ["Destination"] = "/image" },
                _ => throw new ArgumentOutOfRangeException(nameof(shape))
            };
            harness.AddOtherContainer(SiloHarness.BindMount(Path.Combine(scratch.FullName, "elsewhere")));
            harness.AddOtherContainer(mount);
            var docker = harness.Docker(out _);

            var check = docker.EnsureSiloDataRootExclusiveAsync(harness.DataRoot, harness.SiloName, TestContext.CancellationToken);

            if (refusal is null)
            {
                await check;
            }
            else
            {
                var refused = await Assert.ThrowsAsync<InstallerException>(() => check);
                Assert.IsTrue(refused.IsRefusal, refused.Message);
                if (!refusal.StartsWith("Docker could not", StringComparison.Ordinal))
                    StringAssert.Contains(refused.Message, "Another container references the Silo data root", StringComparison.Ordinal);
                StringAssert.Contains(refused.Message, refusal, StringComparison.Ordinal);
            }
        }
        finally
        {
            scratch.Delete(recursive: true);
        }

        static JsonObject RemoteVolume(SiloHarness harness)
        {
            harness.Volumes["remote"] = new JsonObject { ["Name"] = "remote", ["Driver"] = "nfs-plugin", ["Mountpoint"] = "/mnt/remote", ["Options"] = null };
            return SiloHarness.VolumeMount("remote", "/mnt/remote");
        }

        static JsonObject ManagedAround(SiloHarness harness)
        {
            // A daemon whose own data root holds the Silo data root: a volume it manages lies around the data root.
            harness.DockerRootDirectory = harness.Paths.ProductRoot;
            return harness.LocalVolume("cache");
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("a-path-inside-bound-elsewhere", "which the mount table places on the data root, inside it or holding it")]
    [DataRow("a-path-in-a-folder-bound-to-a-path-inside", "which the mount table places on the data root, inside it or holding it")]
    [DataRow("the-data-root-bound-elsewhere", "which the mount table places on the data root, inside it or holding it")]
    [DataRow("an-ancestor-bound-elsewhere", "which the mount table places on the data root, inside it or holding it")]
    [DataRow("its-filesystem-root-bound-elsewhere", "which the mount table places on the data root, inside it or holding it")]
    [DataRow("a-folder-holding-a-bind-of-the-data-root", "which holds a mount of the data root, of a path inside it or of one holding it")]
    [DataRow("a-folder-holding-a-bind-of-a-path-inside", "which holds a mount of the data root, of a path inside it or of one holding it")]
    [DataRow("another-path-of-its-filesystem-bound-elsewhere", null)]
    [DataRow("a-folder-holding-a-bind-of-another-path", null)]
    public void DataRootExclusivity_JudgesWhereTheMountTablePlacesASourceAndTheMountsBelowIt(string shape, string? reason)
    {
        // Bind mounts no path resolution or device and inode comparison sees (Sol R5-01, Fable r5 note 2), as the mount table
        // lists them: a separate folder whose mount is a part of the data root's filesystem.
        using var harness = new SiloHarness().Defaults();
        var scratch = Directory.CreateTempSubdirectory("hvo-silo-mount-table-");
        try
        {
            var mountInfo = File.ReadAllText(HostProductRootPolicy.MountInfoPath);
            var data = SiloDataRootExclusivity.Locate(NativeLinux.TryResolvePath(harness.DataRoot)!, mountInfo);
            var parent = data.Path[..data.Path.LastIndexOf('/')];
            var source = Path.Combine(scratch.FullName, "source");
            var (target, root) = shape switch
            {
                "a-path-inside-bound-elsewhere" => (source, data.Path + "/.minio.sys"),
                // The source lies inside the bound folder, not at it: only where the mount table places it shows the overlap.
                "a-path-in-a-folder-bound-to-a-path-inside" => (Path.Combine(scratch.FullName, "bound"), data.Path + "/.minio.sys"),
                "the-data-root-bound-elsewhere" => (source, data.Path),
                "an-ancestor-bound-elsewhere" => (source, parent),
                "its-filesystem-root-bound-elsewhere" => (source, "/"),
                "a-folder-holding-a-bind-of-the-data-root" => (Path.Combine(source, "below"), data.Path),
                "a-folder-holding-a-bind-of-a-path-inside" => (Path.Combine(source, "deep", "below"), data.Path + "/.minio.sys/buckets"),
                "another-path-of-its-filesystem-bound-elsewhere" => (source, parent + "/state"),
                "a-folder-holding-a-bind-of-another-path" => (Path.Combine(source, "below"), parent + "/config"),
                _ => throw new ArgumentOutOfRangeException(nameof(shape))
            };
            Directory.CreateDirectory(target);
            if (shape == "a-path-in-a-folder-bound-to-a-path-inside")
            {
                source = Path.Combine(target, "buckets");
                Directory.CreateDirectory(source);
            }
            static string Escaped(string path) => path.Replace("\\", "\\134", StringComparison.Ordinal).Replace(" ", "\\040", StringComparison.Ordinal);
            var table = mountInfo.TrimEnd('\n') + $"\n4095 1 {data.Device} {Escaped(root)} {Escaped(target)} rw,relatime shared:1 - ext4 /dev/hvo-test rw\n";

            var overlap = new SiloDataRootExclusivity(harness.DataRoot, table).Overlap(source);

            if (reason is null)
                Assert.IsNull(overlap, overlap);
            else
                Assert.AreEqual(reason, overlap);
            Assert.IsNull(new SiloDataRootExclusivity(harness.DataRoot, mountInfo).Overlap(source), "Without that mount the folder is apart.");
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    internal static string Id(string name) => name switch
    {
        "index" => SiloImagePin.IndexDigest,
        "manifest" => SiloHarness.Amd64.ManifestDigest,
        "config" => SiloHarness.Amd64.ConfigDigest,
        "arm64-config" => SiloHarness.Arm64.ConfigDigest,
        _ => "sha256:" + new string('0', 64)
    };

    internal static JsonObject Labels() => new()
    {
        ["org.opencontainers.image.version"] = SiloImagePin.Release,
        ["org.opencontainers.image.revision"] = SiloImagePin.SourceRevision,
        ["org.opencontainers.image.licenses"] = SiloImagePin.License
    };
}
