using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The upgrade seam for a target release's release.json (#1312): its notes are shown, and an older target refused,
/// before the lifecycle journal, the backup, the pause or the image change, and the notes travel with the result.
/// </summary>
public sealed partial class LifecycleContractTests
{
    private const string NotesCandidateReference = "ghcr.io/example/cameraagent@sha256:4444444444444444444444444444444444444444444444444444444444444444";
    private const string NotesCandidateImageId = "sha256:5555555555555555555555555555555555555555555555555555555555555555";

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_TargetRelease_ShowsItsNotesBeforeAnyMutationAndReturnsThem()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        ConfigureNotesCandidate(fixture);
        var lifecycle = new FakeLifecycleClient();
        using var output = new ObservingWriter<(bool Journaled, string? ActiveImageId, int Pauses)>(
            () => (File.Exists(fixture.Paths.LifecycleStatePath), fixture.Runner.ActiveImageId, lifecycle.PauseCount));

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            NotesUpgrade(fixture) with { TargetRelease = ReleaseManifest.Parse(ReleaseManifestTests.Fixture()) },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, output: output);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(NotesCandidateImageId, result.Image!.ImageId);
        // The fixture's image was installed by reference and records no release, so every note up to the target shows.
        var notes = result.UpgradeNotes!;
        Assert.IsNull(notes.InstalledVersion);
        Assert.AreEqual("1.2.0", notes.TargetVersion);
        Assert.AreEqual("1.0.0 1.1.0 1.2.0", UpgradeNotesTests.Versions(notes.Notes));
        Assert.AreEqual(UpgradeNotes.Render(notes) + Environment.NewLine, output.ToString());
        Assert.AreEqual((false, fixture.Manifest.Image.ImageId, 0), output.StateAtFirstWrite);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_TargetReleaseDryRun_ShowsTheNotesAfterTheRecordedInstalledVersion()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        await RecordInstalledReleaseAsync(fixture, "1.0.0");
        ConfigureNotesCandidate(fixture);
        using var output = new ObservingWriter<bool>(() => File.Exists(fixture.Paths.LifecycleStatePath));

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            NotesUpgrade(fixture) with
            {
                DryRun = true,
                TargetRelease = ReleaseManifest.Parse(ReleaseManifestTests.Fixture())
            },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, output: output);

        Assert.AreEqual("planned", result.Outcome);
        Assert.AreEqual("1.0.0", result.UpgradeNotes!.InstalledVersion);
        Assert.AreEqual("1.1.0 1.2.0", UpgradeNotesTests.Versions(result.UpgradeNotes.Notes));
        StringAssert.StartsWith(output.ToString(), "Upgrading from 1.0.0 to 1.2.0.", StringComparison.Ordinal);
        Assert.IsFalse(output.StateAtFirstWrite);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_TargetReleaseOlderThanInstalled_IsRefusedBeforeTheJournalAndPointsToRollback()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        await RecordInstalledReleaseAsync(fixture, "1.3.0");
        ConfigureNotesCandidate(fixture);
        var lifecycle = new FakeLifecycleClient();
        var manifestBefore = await File.ReadAllTextAsync(fixture.Paths.ManifestPath);
        using var output = new ObservingWriter<bool>(() => true);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            NotesUpgrade(fixture) with { TargetRelease = ReleaseManifest.Parse(ReleaseManifestTests.Fixture()) },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, output: output));

        Assert.AreEqual(
            "1.3.0 is installed, which is newer than the upgrade target 1.2.0; an upgrade never goes back. " +
            $"To return to the previous image, run 'hvo-skymonitor cameraagent rollback --instance-id {fixture.InstanceId:D}'.",
            exception.Message);
        // Nothing was journaled, shown, paused or changed, so no refused operation is left to settle or resume.
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(string.Empty, output.ToString());
        Assert.AreEqual(0, lifecycle.PauseCount);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(manifestBefore, await File.ReadAllTextAsync(fixture.Paths.ManifestPath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_TargetReleaseWithJson_CarriesTheNotesOnlyInTheResult()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        ConfigureNotesCandidate(fixture);
        using var output = new ObservingWriter<bool>(() => true);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            NotesUpgrade(fixture) with
            {
                Json = true,
                TargetRelease = ReleaseManifestTests.Release("1.2.0", ("1.2.0", "twelve"))
            },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, output: output);

        // Standard output carries exactly the one JSON result, so the notes are written there and nowhere else.
        Assert.AreEqual(string.Empty, output.ToString());
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, DeploymentJsonContext.Default.LifecycleResult));
        var notes = document.RootElement.GetProperty("upgradeNotes");
        Assert.AreEqual(JsonValueKind.Null, notes.GetProperty("installedVersion").ValueKind);
        Assert.AreEqual("1.2.0", notes.GetProperty("targetVersion").GetString());
        Assert.AreEqual("twelve", notes.GetProperty("notes")[0].GetProperty("text").GetString());
        // A result with no release.json behind it keeps its previous shape.
        using var plain = JsonDocument.Parse(JsonSerializer.Serialize(
            result with { UpgradeNotes = null }, DeploymentJsonContext.Default.LifecycleResult));
        Assert.IsFalse(plain.RootElement.TryGetProperty("upgradeNotes", out _));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("1.4.0", true, "1.4.0")]
    [DataRow("1.4.0-dev.2", true, "1.4.0-dev.2")]
    [DataRow("r42", true, null)]
    [DataRow("1.4.0", false, null)]
    public async Task InstalledVersionAsync_IsTheRetainedReleaseRecordsVersionOfTheRunningImage(
        string recorded, bool ofTheRunningImage, string? expected)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        Assert.IsNull(await UpgradeNotes.InstalledVersionAsync(fixture.Paths, fixture.Manifest.Image, CancellationToken.None));
        await RecordInstalledReleaseAsync(
            fixture, recorded, ofTheRunningImage ? null : $"sha256:{new string('9', 64)}");

        Assert.AreEqual(
            expected, await UpgradeNotes.InstalledVersionAsync(fixture.Paths, fixture.Manifest.Image, CancellationToken.None));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task InstalledVersionAsync_InvalidRecord_IsRefused()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        SafeFileSystem.WriteTextAtomic(Path.Combine(fixture.Paths.DeploymentStateRoot, AcquiredImage.EvidenceFileName), "{");

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => UpgradeNotes.InstalledVersionAsync(fixture.Paths, fixture.Manifest.Image, CancellationToken.None));

        Assert.AreEqual("The retained image release record is invalid JSON.", exception.Message);
    }

    [TestMethod]
    [DataRow(LifecycleOperationKind.Rollback)]
    [DataRow(LifecycleOperationKind.Reinstall)]
    public void LifecycleRequest_TargetReleaseOutsideAnUpgrade_IsRejected(LifecycleOperationKind operation)
    {
        var request = new LifecycleRequest(operation, Guid.NewGuid(), InstallRequest.DefaultProductRoot, false, false, false)
        {
            TargetRelease = ReleaseManifestTests.Release("1.2.0")
        };

        var exception = Assert.ThrowsExactly<InstallUsageException>(request.Validate);

        Assert.AreEqual("A release's upgrade notes apply only to an upgrade.", exception.Message);
    }

    private static void ConfigureNotesCandidate(LifecycleFixture fixture)
        => fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            NotesCandidateReference, NotesCandidateImageId, fixture.Uid, fixture.Gid);

    private static LifecycleRequest NotesUpgrade(LifecycleFixture fixture)
        => fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = NotesCandidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        };

    /// <summary>
    /// Keeps the release record an install or upgrade from a release keeps, naming <paramref name="version"/> for
    /// <paramref name="imageId"/> (the running image by default).
    /// </summary>
    private static Task RecordInstalledReleaseAsync(LifecycleFixture fixture, string version, string? imageId = null)
        => SafeFileSystem.WriteJsonAtomicAsync(
            Path.Combine(fixture.Paths.DeploymentStateRoot, AcquiredImage.EvidenceFileName),
            new CameraAgentImageReleaseEvidence(
                DeploymentSchemaVersions.ImageReleaseEvidence,
                new DistributionVerificationEvidence(
                    "ImageRelease", "image", version, $"cameraagent-image-v{version}", new string('1', 64), 1024,
                    "p256-sha256:" + new string('2', 64), "cameraagent-image.tar", new string('3', 64), 2048,
                    new Uri("https://example.test/releases/"), new Uri("https://example.test/releases/cameraagent-image.tar"),
                    "verified", DateTimeOffset.UnixEpoch, null, null),
                "CameraAgent", "ghcr.io/hualapaivalley/skymonitor-cameraagent", $"sha256:{new string('4', 64)}", "linux",
                fixture.Manifest.Image.Architecture, $"sha256:{new string('5', 64)}", imageId ?? fixture.Manifest.Image.ImageId,
                new string('6', 40), new string('7', 40),
                new DistributionImageCompatibility(
                    "cameraagent-state-v2", new string('8', 40), "20260827053715_InitialIdentity", 13, 2,
                    "cameraagent-install-v1", "hyg-v42-production-p3-s2", null),
                "sbom.json", "provenance.json", "scan.json", null, DateTimeOffset.UnixEpoch),
            DeploymentJsonContext.Default.CameraAgentImageReleaseEvidence,
            CancellationToken.None);

    /// <summary>Captures what is written, and what <c>observe</c> reports at the moment the first character is.</summary>
    private sealed class ObservingWriter<TState>(Func<TState> observe) : TextWriter
    {
        private readonly StringBuilder written = new();

        public TState? StateAtFirstWrite { get; private set; }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (written.Length == 0)
            {
                StateAtFirstWrite = observe();
            }
            written.Append(value);
        }

        public override string ToString() => written.ToString();
    }
}
