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

        var result = await new DockerClient(runner).PrepareImageAsync(request, allowMutation: true, CancellationToken.None);

        Assert.AreEqual("daemon-1", result.Daemon.Id);
        Assert.AreEqual(digest, result.Image.ImmutableReference);
        CollectionAssert.Contains(runner.Commands, $"docker image pull {digest}");
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
            () => new DockerClient(runner).PrepareImageAsync(CreateRequest(imageId), allowMutation: true, CancellationToken.None));
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

        await new DockerClient(runner).PrepareImageAsync(request, allowMutation: true, CancellationToken.None);

        Assert.IsFalse(runner.Commands.Any(static command => command.StartsWith("docker image pull", StringComparison.Ordinal)));
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
                () => new DockerClient(runner).PrepareImageAsync(request, allowMutation: true, CancellationToken.None));

            StringAssert.Contains(exception.Message, "did not contain", StringComparison.Ordinal);
            StringAssert.Contains(runner.Commands.Single(command => command.StartsWith("docker image load", StringComparison.Ordinal)), "/proc/", StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(archive);
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
            return Task.FromResult(new ProcessResult(0, outputs[index++], string.Empty));
        }
    }
}
