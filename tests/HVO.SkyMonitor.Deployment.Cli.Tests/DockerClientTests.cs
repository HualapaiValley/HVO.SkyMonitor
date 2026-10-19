using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class DockerClientTests
{
    /// <summary>
    /// The image store a plan names a release's image for (#1384): <c>docker info</c> reports the containerd image store
    /// as a containerd snapshotter driver type, and the classic store reports none.
    /// </summary>
    [TestMethod]
    [DataRow("""{"Driver":"overlayfs","DriverStatus":[["driver-type","io.containerd.snapshotter.v1"]]}""", true)]
    [DataRow("""{"Driver":"overlay2","DriverStatus":[["Backing Filesystem","extfs"],["Supports d_type","true"]]}""", false)]
    [DataRow("""{"Driver":"overlay2","DriverStatus":null}""", false)]
    [DataRow("""{"Driver":"overlay2"}""", false)]
    [DataRow("""{"DriverStatus":[["driver-type"],"io.containerd.snapshotter.v1",[1,2]]}""", false)]
    public void UsesContainerdImageStore_ReadsTheDriverTypeDockerInfoReports(string info, bool expected)
    {
        using var document = JsonDocument.Parse(info);

        Assert.AreEqual(expected, DockerClient.UsesContainerdImageStore(document.RootElement));
    }

    /// <summary>
    /// The image listing that proves an imported image gone (#1315 PR-1355-R5 R5-1), in the form a real daemon prints it:
    /// the first line was captured from Docker 29.8 with the containerd image store, the second is the classic store's.
    /// </summary>
    [TestMethod]
    public void ParseImageListing_RealDaemonLines_ShowEachImageByIdDigestReferenceAndTag()
    {
        const string containerd = "{\"Containers\":\"1\",\"CreatedAt\":\"2026-08-04 13:49:32 +0000 UTC\",\"CreatedSince\":\"2 months ago\",\"Digest\":\"sha256:28a898719c18a33f4e8000685287fa36fd0dd9560c6440227d3a732d79bb41d8\",\"ID\":\"sha256:28a898719c18a33f4e8000685287fa36fd0dd9560c6440227d3a732d79bb41d8\",\"Repository\":\"moby/buildkit\",\"SharedSize\":\"N/A\",\"Size\":\"355MB\",\"Tag\":\"buildx-stable-1\",\"UniqueSize\":\"N/A\"}";
        var classic = $"{{\"Containers\":\"N/A\",\"Digest\":\"sha256:{new string('4', 64)}\",\"ID\":\"sha256:{new string('5', 64)}\",\"Repository\":\"ghcr.io/example/cameraagent\",\"Tag\":\"<none>\"}}";

        var images = DockerClient.ParseImageListing($"{containerd}\n{classic}\n");

        Assert.HasCount(2, images);
        Assert.IsTrue(images[0].Shows("moby/buildkit:buildx-stable-1"));
        Assert.IsTrue(images[0].Shows("moby/buildkit@sha256:28a898719c18a33f4e8000685287fa36fd0dd9560c6440227d3a732d79bb41d8"));
        Assert.IsTrue(images[0].Shows("sha256:28a898719c18a33f4e8000685287fa36fd0dd9560c6440227d3a732d79bb41d8"));
        Assert.IsTrue(images[1].Shows($"ghcr.io/example/cameraagent@sha256:{new string('4', 64)}"));
        Assert.IsTrue(images[1].Shows($"sha256:{new string('5', 64)}"));
        Assert.IsFalse(images.Any(image => image.Shows($"ghcr.io/example/other@sha256:{new string('4', 64)}")));
        Assert.IsFalse(images.Any(image => image.Shows("moby/buildkit:latest")));
        Assert.IsEmpty(DockerClient.ParseImageListing(string.Empty), "a successful listing of no images shows nothing");
    }

    /// <summary>A listing with any line that is not an image this installer reads proves nothing.</summary>
    [TestMethod]
    [DataRow("not json")]
    [DataRow("[]")]
    [DataRow("{\"ID\":\"sha256:abc\",\"Repository\":\"r\",\"Tag\":\"t\",\"Digest\":\"d\"}")]
    [DataRow("{\"ID\":\"sha256:5555555555555555555555555555555555555555555555555555555555555555\",\"Repository\":\"r\",\"Tag\":\"t\"}")]
    [DataRow("{\"ID\":\"sha256:5555555555555555555555555555555555555555555555555555555555555555\",\"Repository\":7,\"Tag\":\"t\",\"Digest\":\"d\"}")]
    public void ParseImageListing_LineThatIsNotAnImage_RefusesTheWholeListing(string line)
    {
        var valid = $"{{\"ID\":\"sha256:{new string('5', 64)}\",\"Repository\":\"r\",\"Tag\":\"t\",\"Digest\":\"<none>\"}}";

        Assert.ThrowsExactly<InstallerException>(() => DockerClient.ParseImageListing($"{valid}\n{line}\n"));
    }

    [TestMethod]
    public async Task PrepareImageAsync_ValidDigest_BindsDaemonAndImageIdentity()
    {
        var digest = $"ghcr.io/example/cameraagent@sha256:{new string('a', 64)}";
        var runner = new FakeProcessRunner(
            "{\"OSType\":\"linux\",\"Architecture\":\"amd64\",\"ID\":\"daemon-1\",\"Name\":\"host\",\"ServerVersion\":\"29.0\"}",
            "2.40.0",
            "pulled",
            $"[{{\"Id\":\"sha256:{new string('b', 64)}\",\"Architecture\":\"amd64\",\"Os\":\"linux\",\"RepoDigests\":[\"{digest}\"]}}]");
        var request = CreateRequest(digest);

        var result = await new DockerClient(runner).PrepareImageAsync(request, allowMutation: true, release: null, CancellationToken.None);

        Assert.AreEqual("daemon-1", result.Daemon.Id);
        Assert.AreEqual(digest, result.Image.ImmutableReference);
        Assert.IsTrue(runner.Commands.Any(command => command.EndsWith($" image pull {digest}", StringComparison.Ordinal)));
        Assert.IsTrue(runner.Commands.Where(command => !command.Contains(" context inspect ", StringComparison.Ordinal))
            .All(command => command.StartsWith("docker --host unix:///var/run/docker.sock ", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task PrepareImageAsync_WrongArchitecture_IsRejected()
    {
        var imageId = $"sha256:{new string('b', 64)}";
        var runner = new FakeProcessRunner(
            "{\"OSType\":\"linux\",\"Architecture\":\"amd64\",\"ID\":\"daemon-1\",\"Name\":\"host\",\"ServerVersion\":\"29.0\"}",
            "2.40.0",
            $"[{{\"Id\":\"{imageId}\",\"Architecture\":\"arm64\",\"Os\":\"linux\",\"RepoDigests\":[]}}]");

        await Assert.ThrowsExactlyAsync<InstallerException>(
            () => new DockerClient(runner).PrepareImageAsync(CreateRequest(imageId), allowMutation: true, release: null, CancellationToken.None));
    }

    [TestMethod]
    public async Task PrepareImageAsync_NoDownload_InspectsExistingDigestWithoutPulling()
    {
        var digest = $"ghcr.io/example/cameraagent@sha256:{new string('a', 64)}";
        var runner = new FakeProcessRunner(
            "{\"OSType\":\"linux\",\"Architecture\":\"amd64\",\"ID\":\"daemon-1\",\"Name\":\"host\",\"ServerVersion\":\"29.0\"}",
            "2.40.0",
            $"[{{\"Id\":\"sha256:{new string('b', 64)}\",\"Architecture\":\"amd64\",\"Os\":\"linux\",\"RepoDigests\":[\"{digest}\"]}}]");
        var request = CreateRequest(digest) with { NoDownload = true };

        await new DockerClient(runner).PrepareImageAsync(request, allowMutation: true, release: null, CancellationToken.None);

        Assert.IsFalse(runner.Commands.Any(static command => command.Contains(" image pull ", StringComparison.Ordinal)));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PrepareImageAsync_ArchiveDoesNotContainRequestedExistingImage_IsRejected()
    {
        var archive = Path.GetTempFileName();
        var requested = $"sha256:{new string('b', 64)}";
        var loaded = $"sha256:{new string('c', 64)}";
        try
        {
            File.SetUnixFileMode(archive, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var request = CreateRequest(requested) with
            {
                ImageArchive = archive,
                ImageArchiveSha256 = await SafeFileSystem.ComputeSha256Async(archive, CancellationToken.None)
            };
            var runner = new FakeProcessRunner(
                "{\"OSType\":\"linux\",\"Architecture\":\"amd64\",\"ID\":\"daemon-1\",\"Name\":\"host\",\"ServerVersion\":\"29.0\"}",
                "2.40.0",
                "Loaded image: unrelated:latest",
                $"[{{\"Id\":\"{loaded}\"}}]",
                $"[{{\"Id\":\"{requested}\",\"Architecture\":\"amd64\",\"Os\":\"linux\",\"RepoDigests\":[]}}]");

            var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
                () => new DockerClient(runner).PrepareImageAsync(request, allowMutation: true, release: null, CancellationToken.None));

            StringAssert.Contains(exception.Message, "did not contain", StringComparison.Ordinal);
            StringAssert.Contains(runner.Commands.Single(command => command.Contains(" image load ", StringComparison.Ordinal)), "/proc/", StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(archive);
        }
    }

    [TestMethod]
    public async Task InspectContainerAsync_DaemonFailure_IsNotTreatedAsAbsence()
    {
        var runner = new FailingProcessRunner();

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            new DockerClient(runner).InspectContainerAsync("cameraagent", CancellationToken.None));

        StringAssert.Contains(exception.Message, "Docker", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("/srv/catalogs/hyg-v4.2-p3-s2-r1")]
    [DataRow("/srv/catalogs")]
    public async Task EnsureNoPathReferencesAsync_DirectOrAncestorMount_IsRejected(string mountSource)
    {
        const string candidate = "/srv/catalogs/hyg-v4.2-p3-s2-r1";
        var runner = new FakeProcessRunner(
            "container-1\n",
            $"[{{\"Mounts\":[{{\"Source\":\"{mountSource}\"}}]}}]");

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            new DockerClient(runner).EnsureNoPathReferencesAsync(candidate, CancellationToken.None));

        StringAssert.Contains(exception.Message, "referenced", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task EnsureNoPathReferencesAsync_SymlinkAliasToDescendant_IsRejected()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"hvo-docker-alias-{Guid.NewGuid():N}");
        var candidate = Path.Combine(parent, "candidate");
        var descendant = Path.Combine(candidate, "state");
        var alias = Path.Combine(parent, "alias");
        Directory.CreateDirectory(descendant);
        File.SetUnixFileMode(candidate, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(descendant, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateSymbolicLink(alias, descendant);
        try
        {
            var inventory = SafeTreeDeletion.CaptureChildInventory(parent, "candidate", NativeLinux.getuid(), NativeLinux.getgid());
            var runner = new FakeProcessRunner(
                "container-1\n",
                $"[{{\"Mounts\":[{{\"Source\":\"{alias}\"}}]}}]");

            await Assert.ThrowsExactlyAsync<InstallerException>(() =>
                new DockerClient(runner).EnsureNoPathReferencesAsync(candidate, CancellationToken.None, inventory));
        }
        finally
        {
            Directory.Delete(parent, true);
        }
    }

    private static InstallRequest CreateRequest(string imageReference) => new()
    {
        FriendlyName = "Camera",
        OwnerEmail = "admin@example.test",
        CatalogBundle = "/srv/catalog.bundle",
        ImageReference = imageReference
    };

    [TestMethod]
    public async Task ReadHostCapabilitiesAsync_ReadsLimitSupportCgroupsDataRootAndWarnings()
    {
        var runner = new FakeProcessRunner(
            "{\"OSType\":\"linux\",\"Architecture\":\"aarch64\",\"ID\":\"daemon-1\",\"Name\":\"pi\",\"ServerVersion\":\"29.0\"," +
            "\"MemoryLimit\":false,\"CpuCfsQuota\":true,\"PidsLimit\":true,\"CgroupVersion\":\"2\",\"DockerRootDir\":\"/srv/docker\"," +
            "\"NCPU\":4,\"MemTotal\":8442572800,\"Warnings\":[\"WARNING: No memory limit support\"]}",
            "2.40.0");

        var capabilities = await new DockerClient(runner).ReadHostCapabilitiesAsync(CancellationToken.None);

        Assert.AreEqual(new DockerDaemonIdentity("daemon-1", "pi", "arm64", "29.0"), capabilities.Daemon);
        Assert.IsFalse(capabilities.MemoryLimit);
        Assert.IsTrue(capabilities.CpuCfsQuota);
        Assert.IsTrue(capabilities.PidsLimit);
        Assert.AreEqual("2", capabilities.CgroupVersion);
        Assert.AreEqual("/srv/docker", capabilities.DockerRootDir);
        Assert.AreEqual(4, capabilities.CpuCount);
        Assert.AreEqual(8442572800, capabilities.MemoryBytes);
        string[] expectedWarnings = ["WARNING: No memory limit support"];
        CollectionAssert.AreEqual(expectedWarnings, capabilities.Warnings.ToArray());
        Assert.IsTrue(runner.Commands.Any(static command => command.EndsWith(" compose version --short", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ReadHostCapabilitiesAsync_UnreportedCapabilities_AreUnknownRatherThanSupported()
    {
        var runner = new FakeProcessRunner(
            "{\"OSType\":\"linux\",\"Architecture\":\"amd64\",\"ID\":\"daemon-1\",\"Name\":\"host\",\"ServerVersion\":\"29.0\",\"Warnings\":null}",
            "2.40.0");

        var capabilities = await new DockerClient(runner).ReadHostCapabilitiesAsync(CancellationToken.None);

        Assert.IsNull(capabilities.MemoryLimit);
        Assert.IsNull(capabilities.CpuCfsQuota);
        Assert.IsNull(capabilities.PidsLimit);
        Assert.IsNull(capabilities.DockerRootDir);
        Assert.IsNull(capabilities.CpuCount);
        Assert.IsNull(capabilities.MemoryBytes);
        Assert.IsEmpty(capabilities.Warnings);
        Assert.IsFalse(HostPrerequisites.Evaluate(capabilities, "/srv/data/hvo-skymonitor", 1, 1, new FakeHostEnvironment(),
            CameraAgentContainerResources.Default).Satisfied);
    }

    [TestMethod]
    public async Task PrepareImageAsync_ContainerdStoreNamingTheReleaseIndex_ResolvesTheReleaseImage()
    {
        // The release's offline archive is loaded by its configuration digest, the classic store's image ID; Docker's
        // containerd store names the loaded image by its index digest instead, which the release also names.
        var release = ReleaseSelection();
        var runner = new StoreScopedProcessRunner("amd64", release.ImageIds![0], ReleaseLabels(release));

        var result = await new DockerClient(runner).PrepareImageAsync(
            CreateRequest(release.ImageIds[2]) with { NoDownload = true }, allowMutation: false, release, CancellationToken.None);

        Assert.AreEqual(release.ImageIds[0], result.Image.ImageId);
        Assert.AreEqual(release.ImageIds[0], result.Image.ImmutableReference);
    }

    [TestMethod]
    public async Task PrepareImageAsync_DaemonThatKnowsNoReleaseIdentity_IsRefused()
    {
        var release = ReleaseSelection();
        var runner = new StoreScopedProcessRunner("amd64", $"sha256:{new string('f', 64)}", ReleaseLabels(release));

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => new DockerClient(runner).PrepareImageAsync(
            CreateRequest(release.ImageIds![2]) with { NoDownload = true }, allowMutation: false, release, CancellationToken.None));

        StringAssert.StartsWith(exception.Message, "Docker could not inspect the CameraAgent image", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("org.opencontainers.image.version")]
    [DataRow("org.opencontainers.image.revision")]
    [DataRow("io.hvo.skymonitor.component")]
    [DataRow("org.opencontainers.image.created")]
    public async Task PrepareImageAsync_ReleaseImageWithoutTheReleasesLabel_IsRefusedWithoutEchoingTheLabel(string? missing)
    {
        var release = ReleaseSelection();
        var labels = ReleaseLabels(release);
        if (missing is null)
        {
            labels["org.opencontainers.image.version"] = "1.2.0-dev.3\u001b[2J";
        }
        else
        {
            labels.Remove(missing);
        }
        var runner = new StoreScopedProcessRunner("amd64", release.ImageIds![2], labels);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => new DockerClient(runner).PrepareImageAsync(
            CreateRequest(release.ImageIds[2]) with { NoDownload = true }, allowMutation: false, release, CancellationToken.None));

        Assert.AreEqual(
            "The CameraAgent image Docker resolved is not labelled as CameraAgent 1.2.0-dev.3 from commit " +
            "0123456789abcdef0123456789abcdef01234567, created 2026-10-17T18:00:00Z, as release v1.2.0-dev.3 requires.",
            exception.Message);
        Assert.IsTrue(exception.IsRefusal, "an image that is not the release's is refused (exit 3)");
    }

    private static ReleaseImageSelection ReleaseSelection() => new(
        "v1.2.0-dev.3", "1.2.0-dev.3", "0123456789abcdef0123456789abcdef01234567", "2026-10-17T18:00:00Z", "linux/amd64",
        $"ghcr.io/hualapaivalley/skymonitor-cameraagent@sha256:{new string('1', 64)}",
        [$"sha256:{new string('1', 64)}", $"sha256:{new string('2', 64)}", $"sha256:{new string('3', 64)}"]);

    private static Dictionary<string, string> ReleaseLabels(ReleaseImageSelection release)
        => new(ReleaseFixture.ContractLabels, StringComparer.Ordinal)
        {
            ["org.opencontainers.image.version"] = release.Version,
            ["org.opencontainers.image.revision"] = release.Commit,
            ["org.opencontainers.image.created"] = release.Created
        };

    /// <summary>A daemon that knows exactly one image reference, as one image store would name the image.</summary>
    private sealed class StoreScopedProcessRunner(
        string architecture,
        string knownReference,
        IReadOnlyDictionary<string, string> labels) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (arguments is ["context", "inspect", ..])
            {
                return Task.FromResult(new ProcessResult(0, "unix:///var/run/docker.sock", string.Empty));
            }
            var effective = arguments.Count >= 2 && arguments[0] == "--host" ? arguments.Skip(2).ToArray() : arguments;
            return effective switch
            {
                ["info", ..] => Task.FromResult(new ProcessResult(
                    0,
                    $"{{\"OSType\":\"linux\",\"Architecture\":\"{architecture}\",\"ID\":\"daemon-1\",\"Name\":\"host\",\"ServerVersion\":\"29.0\"}}",
                    string.Empty)),
                ["compose", ..] => Task.FromResult(new ProcessResult(0, "2.40.0", string.Empty)),
                ["image", "inspect", var reference] when reference == knownReference => Task.FromResult(new ProcessResult(
                    0,
                    System.Text.Json.JsonSerializer.Serialize(new[]
                    {
                        new
                        {
                            Id = knownReference,
                            Architecture = architecture,
                            Os = "linux",
                            RepoDigests = Array.Empty<string>(),
                            Config = new { Labels = labels }
                        }
                    }),
                    string.Empty)),
                ["image", "inspect", var reference] => Task.FromResult(new ProcessResult(1, string.Empty, $"No such image: {reference}")),
                _ => Task.FromResult(new ProcessResult(1, string.Empty, "unexpected command"))
            };
        }
    }

    private sealed class FakeProcessRunner(params string[] outputs) : IProcessRunner
    {
        private int index;
        public List<string> Commands { get; } = [];

        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add($"{fileName} {string.Join(' ', arguments)}");
            if (arguments is ["context", "inspect", ..])
                return Task.FromResult(new ProcessResult(0, "unix:///var/run/docker.sock", string.Empty));
            return Task.FromResult(new ProcessResult(0, outputs[index++], string.Empty));
        }
    }


    private sealed class FailingProcessRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
            => Task.FromResult(new ProcessResult(1, string.Empty, "permission denied"));
    }
}
