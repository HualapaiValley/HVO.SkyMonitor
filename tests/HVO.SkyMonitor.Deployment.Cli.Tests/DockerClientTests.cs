using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class DockerClientTests
{
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

        var result = await new DockerClient(runner).PrepareImageAsync(request, allowMutation: true, signedImage: null, CancellationToken.None);

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
            () => new DockerClient(runner).PrepareImageAsync(CreateRequest(imageId), allowMutation: true, signedImage: null, CancellationToken.None));
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

        await new DockerClient(runner).PrepareImageAsync(request, allowMutation: true, signedImage: null, CancellationToken.None);

        Assert.IsFalse(runner.Commands.Any(static command => command.Contains(" image pull ", StringComparison.Ordinal)));
    }

    [TestMethod]
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
                () => new DockerClient(runner).PrepareImageAsync(request, allowMutation: true, signedImage: null, CancellationToken.None));

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
