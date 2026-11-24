using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Preparing the pinned Silo image (#1966 review): Docker loads exactly the bytes the installer verified, whatever later
/// happens to the staged path, and only this daemon's own platform child of the pin counts as present, so a cached
/// image of another architecture never stands in for, or suppresses, the verified native archive.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloImagePreparationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("replaced")]
    [DataRow("rewritten-in-place")]
    [DataRow("removed")]
    public async Task Load_ReadsTheVerifiedBytesWhateverHappensToTheStagedPathAfterVerification(string change)
    {
        var directory = Directory.CreateTempSubdirectory("hvo-silo-prepare-");
        try
        {
            var staged = Path.Combine(directory.FullName, "silo-amd64.tar");
            var archive = SiloImageArchiveTests.Build();
            SiloImageArchiveTests.WriteTar(staged, archive.Entries);
            var verifiedSha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(staged, TestContext.CancellationToken)));
            using var snapshot = await SiloImageSnapshot.CreateAsync(staged, archive.Platform, TestContext.CancellationToken);
            Assert.AreEqual(verifiedSha, snapshot.Sha256);
            Assert.IsFalse(Directory.EnumerateFileSystemEntries(Path.GetTempPath(), "hvo-silo-image-*").Any(path =>
                File.Exists(Path.Combine(path, "archive.tar"))), "No path names the snapshot.");
            switch (change)
            {
                case "replaced":
                    var replacement = Path.Combine(directory.FullName, "replacement.tar");
                    await File.WriteAllBytesAsync(replacement, RandomNumberGenerator.GetBytes(4096), TestContext.CancellationToken);
                    File.Move(replacement, staged, overwrite: true);
                    break;
                case "rewritten-in-place":
                    await using (var same = new FileStream(staged, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                        await same.WriteAsync(RandomNumberGenerator.GetBytes(4096), TestContext.CancellationToken);
                    break;
                case "removed":
                    File.Delete(staged);
                    break;
            }
            var runner = new ScriptedDocker(new DaemonState().Answer);
            var docker = new DockerClient(runner);

            var image = await docker.PrepareSiloImageAsync("amd64", allowPull: false, snapshot, TestContext.CancellationToken);

            Assert.AreEqual(verifiedSha, runner.LoadedSha256.Single(), "Docker reads exactly the verified bytes.");
            StringAssert.StartsWith(runner.Calls.Single(call => call.Arguments is ["image", "load", ..]).Arguments[3], "/proc/", StringComparison.Ordinal);
            Assert.AreEqual(SiloHarness.Amd64.ManifestDigest, image.ImageId);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task Snapshot_RefusesAnArchiveThatIsNotThePinnedImageAndKeepsNothing()
    {
        var directory = Directory.CreateTempSubdirectory("hvo-silo-prepare-");
        try
        {
            var staged = Path.Combine(directory.FullName, "silo-amd64.tar");
            var archive = SiloImageArchiveTests.Build();
            SiloImageArchiveTests.WriteTar(staged, archive.Entries);

            var refusal = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
                SiloImageSnapshot.CreateAsync(staged, SiloHarness.Amd64, TestContext.CancellationToken));

            Assert.IsTrue(refusal.IsRefusal);
            StringAssert.Contains(refusal.Message, "does not hold the pinned linux/amd64 manifest", StringComparison.Ordinal);
            Assert.IsFalse(Directory.EnumerateDirectories(Path.GetTempPath(), "hvo-silo-image-*").Any(path =>
                File.Exists(Path.Combine(path, "archive.tar"))));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task Prepare_ACachedForeignChildOfTheSharedIndexIsAbsentSoTheVerifiedNativeArchiveLoads()
    {
        var directory = Directory.CreateTempSubdirectory("hvo-silo-prepare-");
        try
        {
            var staged = Path.Combine(directory.FullName, "silo-amd64.tar");
            var archive = SiloImageArchiveTests.Build();
            SiloImageArchiveTests.WriteTar(staged, archive.Entries);
            using var snapshot = await SiloImageSnapshot.CreateAsync(staged, archive.Platform, TestContext.CancellationToken);
            var runner = new ScriptedDocker(new DaemonState { CachedForeignIndex = true }.Answer);
            var docker = new DockerClient(runner);

            var image = await docker.PrepareSiloImageAsync("amd64", allowPull: false, snapshot, TestContext.CancellationToken);

            Assert.HasCount(1, runner.LoadedSha256, "The verified native archive is loaded.");
            Assert.AreEqual(SiloHarness.Amd64.ManifestDigest, image.PlatformDigest);
            Assert.AreEqual("amd64", image.Architecture);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DataRow("classic", "config")]
    [DataRow("containerd-loaded", "manifest")]
    [DataRow("containerd-pulled", "index")]
    [DataRow("foreign-platform-answer", null)]
    [DataRow("foreign-config-at-config-id", null)]
    [DataRow("absent", null)]
    public async Task Resolve_FindsOnlyThisPlatformsOwnChildOfThePin(string store, string? reference)
    {
        var runner = new ScriptedDocker((args, _) => store switch
        {
            "classic" when args is ["image", "inspect", var id] && id == SiloHarness.Amd64.ConfigDigest =>
                ScriptedDocker.Ok(new JsonArray(Image(SiloHarness.Amd64.ConfigDigest, "amd64", descriptor: null))),
            "containerd-loaded" when args is ["image", "inspect", var id] && id == SiloHarness.Amd64.ManifestDigest =>
                ScriptedDocker.Ok(new JsonArray(Image(SiloHarness.Amd64.ManifestDigest, "amd64", SiloHarness.Amd64.ManifestDigest))),
            "containerd-pulled" when args is ["image", "inspect", "--platform", "linux/amd64", var id] && id == SiloImagePin.IndexDigest =>
                ScriptedDocker.Ok(new JsonArray(Image(SiloHarness.Amd64.ManifestDigest, "amd64", SiloHarness.Amd64.ManifestDigest))),
            "foreign-platform-answer" when args is ["image", "inspect", "--platform", _, _] =>
                ScriptedDocker.Ok(new JsonArray(Image(SiloHarness.Arm64.ManifestDigest, "arm64", SiloHarness.Arm64.ManifestDigest))),
            "foreign-config-at-config-id" when args is ["image", "inspect", var id] && id == SiloHarness.Amd64.ConfigDigest =>
                ScriptedDocker.Ok(new JsonArray(Image(SiloHarness.Amd64.ConfigDigest, "arm64", descriptor: null))),
            _ when args is ["image", "inspect", .., var id] => ScriptedDocker.NoSuch("image", id),
            _ => null
        });
        var docker = new DockerClient(runner);

        var resolved = await docker.ResolvePinnedSiloImageAsync(SiloHarness.Amd64, TestContext.CancellationToken);

        Assert.AreEqual(reference is null ? null : SiloRuntimeVerificationTests.Id(reference), resolved?.Reference);
    }

    [TestMethod]
    public async Task Resolve_ADaemonFailureFailsClosedRatherThanReadingAsAbsent()
    {
        var docker = new DockerClient(new ScriptedDocker(static (args, _) => args is ["image", "inspect", ..] ? ScriptedDocker.DaemonError() : null));

        await Assert.ThrowsExactlyAsync<InstallerException>(() => docker.ResolvePinnedSiloImageAsync(SiloHarness.Amd64, TestContext.CancellationToken));
    }

    /// <summary>
    /// A containerd-store daemon: the pinned index may be cached holding only the arm64 child; loading the staged amd64
    /// archive adds an image named by the amd64 platform manifest digest.
    /// </summary>
    private sealed class DaemonState
    {
        public bool CachedForeignIndex { get; init; }
        public bool Loaded { get; private set; }

        public ProcessResult? Answer(IReadOnlyList<string> args, string? input)
        {
            switch (args)
            {
                case ["image", "load", "--input", _]:
                    Loaded = true;
                    return ScriptedDocker.Ok($"Loaded image ID: {SiloHarness.Amd64.ManifestDigest}\n");
                case ["image", "inspect", "--platform", "linux/amd64", var reference]:
                    return Loaded && reference == SiloImagePin.IndexDigest
                        ? ScriptedDocker.Ok(new JsonArray(Image(SiloHarness.Amd64.ManifestDigest, "amd64", SiloHarness.Amd64.ManifestDigest)))
                        : ScriptedDocker.NoSuch("image", reference);
                case ["image", "inspect", "--platform", var platform, var reference]:
                    return ScriptedDocker.NoSuch("image", $"{reference} ({platform})");
                case ["image", "inspect", var reference]:
                    if (Loaded && reference == SiloHarness.Amd64.ManifestDigest)
                        return ScriptedDocker.Ok(new JsonArray(Image(SiloHarness.Amd64.ManifestDigest, "amd64", SiloHarness.Amd64.ManifestDigest)));
                    if (CachedForeignIndex && (reference == SiloImagePin.IndexDigest || reference == SiloImagePin.Reference))
                        return ScriptedDocker.Ok(new JsonArray(Image(SiloImagePin.IndexDigest, "arm64", SiloImagePin.IndexDigest, repoDigest: true)));
                    return ScriptedDocker.NoSuch("image", reference);
                default:
                    return null;
            }
        }
    }

    private static JsonObject Image(string id, string architecture, string? descriptor, bool repoDigest = false)
    {
        var image = new JsonObject
        {
            ["Id"] = id,
            ["Os"] = "linux",
            ["Architecture"] = architecture,
            ["RepoDigests"] = repoDigest ? new JsonArray(JsonValue.Create($"pgsty/silo@{SiloImagePin.IndexDigest}")) : new JsonArray(),
            ["Config"] = new JsonObject { ["Labels"] = SiloRuntimeVerificationTests.Labels() }
        };
        if (descriptor is not null)
            image["Descriptor"] = new JsonObject
            {
                ["mediaType"] = descriptor == SiloImagePin.IndexDigest ? "application/vnd.oci.image.index.v1+json" : "application/vnd.oci.image.manifest.v1+json",
                ["digest"] = descriptor
            };
        return image;
    }
}
