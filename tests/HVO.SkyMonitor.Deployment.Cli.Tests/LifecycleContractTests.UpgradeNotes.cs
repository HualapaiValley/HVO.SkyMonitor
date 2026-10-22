using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The upgrade seam for a target release's release.json (#1312): its notes are shown, and an older target refused,
/// before the lifecycle journal, the backup, the pause or the image change, and the notes travel with the result. With
/// --json they are shown as one event line on standard error, since standard output carries only the result.
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
        using var output = new ObservingWriter<MutationState>(() => Mutations(fixture, lifecycle));
        using var error = new ObservingWriter<MutationState>(() => Mutations(fixture, lifecycle));

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            NotesUpgrade(fixture) with { TargetRelease = ReleaseManifest.Parse(ReleaseManifestTests.Fixture()) },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, output: output, error: error);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(NotesCandidateImageId, result.Image!.ImageId);
        // The fixture's image was installed by reference and records no release, so every note up to the target shows.
        var notes = result.UpgradeNotes!;
        Assert.IsNull(notes.InstalledVersion);
        Assert.AreEqual("1.2.0", notes.TargetVersion);
        Assert.AreEqual("1.0.0 1.1.0 1.2.0", UpgradeNotesTests.Versions(notes.Notes));
        Assert.AreEqual(UpgradeNotes.Render(notes) + Environment.NewLine, output.ToString());
        Assert.AreEqual(string.Empty, error.ToString());
        Assert.AreEqual(Unmutated(fixture), output.StateAtFirstWrite);
        // The upgrade then journaled, paused, snapshotted the small databases (the fixture's image declares no state
        // boundaries, so the candidate is a declared migration) and changed the image, each of which the probe would
        // have seen. Nothing was archived with tar.
        var completed = Mutations(fixture, lifecycle);
        Assert.IsTrue(completed.Journaled);
        Assert.AreEqual(1, completed.Snapshots);
        Assert.AreEqual(0, fixture.Runner.BackupCount);
        Assert.AreEqual(NotesCandidateImageId, completed.ActiveImageId);
        Assert.AreEqual(1, completed.Pauses);
        Assert.AreEqual(CameraAgentContainerResources.Default, completed.Resources);
        Assert.AreNotEqual(fixture.Manifest.ComposeModelSha256, completed.ComposeSha256);
        Assert.AreEqual(1, completed.StagedModels);
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task UpgradeAsync_TargetReleaseOlderThanInstalled_IsRefusedBeforeTheJournalAndPointsToRollback(bool json)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        await RecordInstalledReleaseAsync(fixture, "1.3.0");
        ConfigureNotesCandidate(fixture);
        var lifecycle = new FakeLifecycleClient();
        var manifestBefore = await File.ReadAllTextAsync(fixture.Paths.ManifestPath);
        using var output = new ObservingWriter<bool>(() => true);
        using var error = new ObservingWriter<bool>(() => true);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            NotesUpgrade(fixture) with { Json = json, TargetRelease = ReleaseManifest.Parse(ReleaseManifestTests.Fixture()) },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, output: output, error: error));

        Assert.AreEqual(
            "1.3.0 is installed, which is newer than the upgrade target 1.2.0; an upgrade never goes back. " +
            $"To return to the previous image, run 'hvo-sky-install cameraagent rollback --instance-id {fixture.InstanceId:D}'.",
            exception.Message);
        // Nothing was journaled, shown, paused or changed, so no refused operation is left to settle or resume.
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(string.Empty, output.ToString());
        Assert.AreEqual(string.Empty, error.ToString());
        Assert.AreEqual(0, lifecycle.PauseCount);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(manifestBefore, await File.ReadAllTextAsync(fixture.Paths.ManifestPath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_TargetReleaseWithJson_WritesTheNotesAsOneEventOnStandardErrorBeforeAnyMutation()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        ConfigureNotesCandidate(fixture);
        var lifecycle = new FakeLifecycleClient();
        using var output = new ObservingWriter<MutationState>(() => Mutations(fixture, lifecycle));
        using var error = new ObservingWriter<MutationState>(() => Mutations(fixture, lifecycle));

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            NotesUpgrade(fixture) with
            {
                Json = true,
                TargetRelease = ReleaseManifestTests.Release("1.2.0", ("1.2.0", "twelve"))
            },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, output: output, error: error);

        // Standard output carries exactly the one JSON result, written once the upgrade returns, so the notes went to
        // standard error as one event line, before the journal, the backup, the pause or the image change.
        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(string.Empty, output.ToString());
        Assert.AreEqual(Unmutated(fixture), error.StateAtFirstWrite);
        Assert.AreEqual(CameraAgentContainerResources.Default, Mutations(fixture, lifecycle).Resources);
        using var notesEvent = JsonDocument.Parse(SingleLine(error.ToString()));
        Assert.AreEqual(
            "schemaVersion event upgradeNotes",
            string.Join(' ', notesEvent.RootElement.EnumerateObject().Select(static member => member.Name)));
        Assert.AreEqual(1, notesEvent.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("upgrade-notes", notesEvent.RootElement.GetProperty("event").GetString());
        var shown = notesEvent.RootElement.GetProperty("upgradeNotes")
            .Deserialize(DeploymentJsonContext.Default.LifecycleUpgradeNotes)!;
        Assert.IsNull(shown.InstalledVersion);
        Assert.AreEqual("1.2.0", shown.TargetVersion);
        Assert.AreEqual("twelve", shown.Notes.Single().Text);
        // The result still carries the same notes.
        Assert.AreEqual(UpgradeNotes.Render(shown), UpgradeNotes.Render(result.UpgradeNotes!));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result, DeploymentJsonContext.Default.LifecycleResult));
        Assert.AreEqual(
            "twelve", document.RootElement.GetProperty("upgradeNotes").GetProperty("notes")[0].GetProperty("text").GetString());
        // A result with no release.json behind it keeps its previous shape.
        using var plain = JsonDocument.Parse(JsonSerializer.Serialize(
            result with { UpgradeNotes = null }, DeploymentJsonContext.Default.LifecycleResult));
        Assert.IsFalse(plain.RootElement.TryGetProperty("upgradeNotes", out _));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_TargetReleaseDryRunWithJson_WritesTheNotesEventAndPlansWithThem()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        await RecordInstalledReleaseAsync(fixture, "1.0.0");
        ConfigureNotesCandidate(fixture);
        var lifecycle = new FakeLifecycleClient();
        using var output = new ObservingWriter<MutationState>(() => Mutations(fixture, lifecycle));
        using var error = new ObservingWriter<MutationState>(() => Mutations(fixture, lifecycle));

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            NotesUpgrade(fixture) with
            {
                DryRun = true,
                Json = true,
                TargetRelease = ReleaseManifest.Parse(ReleaseManifestTests.Fixture())
            },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, output: output, error: error);

        Assert.AreEqual("planned", result.Outcome);
        Assert.AreEqual(string.Empty, output.ToString());
        Assert.AreEqual(Unmutated(fixture), error.StateAtFirstWrite);
        var shown = JsonSerializer.Deserialize(
            SingleLine(error.ToString()), DeploymentJsonContext.Default.LifecycleUpgradeNotesEvent)!;
        Assert.AreEqual(LifecycleUpgradeNotesEvent.Name, shown.Event);
        Assert.AreEqual("1.0.0", shown.UpgradeNotes.InstalledVersion);
        Assert.AreEqual("1.1.0 1.2.0", UpgradeNotesTests.Versions(shown.UpgradeNotes.Notes));
        Assert.AreEqual(UpgradeNotes.Render(shown.UpgradeNotes), UpgradeNotes.Render(result.UpgradeNotes!));
        // A dry run changes nothing at all.
        Assert.AreEqual(Unmutated(fixture), Mutations(fixture, lifecycle));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UpgradeAsync_TargetReleaseThatFailsAfterItsJournal_HasAlreadyShownTheNotes(bool json)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        ConfigureNotesCandidate(fixture);
        fixture.Runner.RejectNextStop = true;
        var lifecycle = new FakeLifecycleClient();
        using var output = new ObservingWriter<MutationState>(() => Mutations(fixture, lifecycle));
        using var error = new ObservingWriter<MutationState>(() => Mutations(fixture, lifecycle));

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            NotesUpgrade(fixture) with
            {
                Json = json,
                TargetRelease = ReleaseManifestTests.Release("1.2.0", ("1.2.0", "twelve"))
            },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, output: output, error: error));

        // The upgrade journaled and paused before its stop failed; the notes were out before any of it, so the
        // caller has them although no result follows (with --json, the error object follows the event line).
        Assert.IsTrue(File.Exists(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(1, lifecycle.PauseCount);
        var (shownOn, silent) = json ? (error, output) : (output, error);
        Assert.AreEqual(Unmutated(fixture), shownOn.StateAtFirstWrite);
        Assert.AreEqual(string.Empty, silent.ToString());
        var expected = new LifecycleUpgradeNotes(null, "1.2.0", [new UpgradeNote("1.2.0", "twelve")]);
        Assert.AreEqual(
            (json ? UpgradeNotes.Event(expected) : UpgradeNotes.Render(expected)) + Environment.NewLine, shownOn.ToString());
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
    /// <summary>
    /// The installed version follows the image the instance runs across a rollback: install 1.2.0, upgrade to 1.3.0, roll
    /// back, and the record of 1.2.0 kept beside the rolled-back image is current again, so an upgrade to 1.1.0 is still
    /// refused as going back. <paramref name="interrupted"/> loses the rollback's settlement after its commit, as a crash
    /// would, and <c>--resume</c> restores the record.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false, DisplayName = "rollback")]
    [DataRow(true, DisplayName = "rollback resumed after its settlement was lost")]
    public async Task UpgradeAsync_OlderTargetAfterARollback_IsStillRefused(bool interrupted)
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner);
        await RecordInstalledReleaseAsync(fixture, "1.2.0");
        using var newer = CandidateRelease(fixture, out var newerImageId, version: "1.3.0");
        fixture.Runner.CandidateRawIngressSchema = "13";
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity);

        var upgraded = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with { Release = newer.Folder, MigrationBackwardCompatible = true },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => owner, fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", upgraded.Outcome);
        Assert.AreEqual("1.3.0", await InstalledVersionAsync(fixture));
        var deployment = fixture.Paths.DeploymentStateRoot;
        var upgradedRecords = await Snapshot(deployment);

        var rollback = fixture.Request(LifecycleOperationKind.Rollback);
        if (interrupted)
        {
            await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                rollback, fixture.Runner, _ => new FakeLifecycleClient { RejectNextResume = true }, _ => owner,
                fixture.Uid, fixture.Gid, CancellationToken.None));
            var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
            Assert.AreEqual(LifecycleOperationPhase.Committed, retained!.Phase);
            // The commit is durable but the records are as the upgrade left them, as if the settlement never ran.
            await Restore(deployment, upgradedRecords);
            Assert.AreEqual("1.3.0", await RecordedVersionAsync(fixture));
            rollback = rollback with { Resume = true };
        }
        var rolledBack = await CameraAgentLifecycleManager.ExecuteAsync(
            rollback, fixture.Runner, _ => new FakeLifecycleClient(), _ => owner, fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", rolledBack.Outcome);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual("1.2.0", await InstalledVersionAsync(fixture), "the rollback did not restore the installed release");

        var platform = $"linux/{fixture.Manifest.DockerDaemon.Architecture}";
        using var older = ReleaseFixture.Create(fixture.Root, version: "1.1.0", platforms: [platform]);
        var journalBefore = await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath);
        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with { Release = older.Folder, MigrationBackwardCompatible = true },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => owner, fixture.Uid, fixture.Gid, CancellationToken.None));

        Assert.AreEqual(
            "1.2.0 is installed, which is newer than the upgrade target 1.1.0; an upgrade never goes back. " +
            $"To return to the previous image, run 'hvo-sky-install cameraagent rollback --instance-id {fixture.InstanceId:D}'.",
            exception.Message);
        Assert.AreEqual(journalBefore, await File.ReadAllTextAsync(fixture.Paths.LifecycleStatePath));
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreNotEqual(newerImageId, fixture.Runner.ActiveImageId);
    }

    /// <summary>The installed version an upgrade would read now: the current record's, for the image the manifest runs.</summary>
    private static async Task<string?> InstalledVersionAsync(LifecycleFixture fixture)
    {
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        return await UpgradeNotes.InstalledVersionAsync(fixture.Paths, manifest.Image, CancellationToken.None);
    }

    /// <summary>The version the current record names, whichever image it describes.</summary>
    private static async Task<string?> RecordedVersionAsync(LifecycleFixture fixture)
    {
        using var record = JsonDocument.Parse(await File.ReadAllBytesAsync(
            Path.Combine(fixture.Paths.DeploymentStateRoot, AcquiredImage.EvidenceFileName)));
        return record.RootElement.GetProperty("releaseVersion").GetString();
    }

    private static async Task<Dictionary<string, byte[]>> Snapshot(string folder)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(folder, "image-distribution*"))
        {
            files[Path.GetFileName(path)] = await File.ReadAllBytesAsync(path);
        }
        return files;
    }

    private static async Task Restore(string folder, Dictionary<string, byte[]> files)
    {
        foreach (var path in Directory.EnumerateFiles(folder, "image-distribution*"))
        {
            File.Delete(path);
        }
        foreach (var (name, bytes) in files)
        {
            var path = Path.Combine(folder, name);
            await File.WriteAllBytesAsync(path, bytes);
            // The installer reads its evidence only as owner-private files.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static Task RecordInstalledReleaseAsync(LifecycleFixture fixture, string version, string? imageId = null)
        => SafeFileSystem.WriteJsonAtomicAsync(
            Path.Combine(fixture.Paths.DeploymentStateRoot, AcquiredImage.EvidenceFileName),
            new CameraAgentImageReleaseEvidence(
                DeploymentSchemaVersions.ImageReleaseEvidence, version, $"v{version}", new string('6', 40),
                new string('1', 64), new string('2', 64),
                $"ghcr.io/hualapaivalley/skymonitor-cameraagent@sha256:{new string('4', 64)}",
                $"linux/{fixture.Manifest.Image.Architecture}", "archive",
                OfflineArchive.NameFor(version, $"linux/{fixture.Manifest.Image.Architecture}"), new string('3', 64),
                imageId ?? fixture.Manifest.Image.ImageId, DateTimeOffset.UnixEpoch),
            DeploymentJsonContext.Default.CameraAgentImageReleaseEvidence,
            CancellationToken.None);

    /// <summary>What an upgrade has changed, including its recorded, applied and staged container limits.</summary>
    private readonly record struct MutationState(
        bool Journaled, int Snapshots, string? ActiveImageId, int Pauses,
        ContainerResourceLimits? Resources, string ComposeSha256, int StagedModels);

    private static MutationState Mutations(LifecycleFixture fixture, FakeLifecycleClient lifecycle)
        => new(File.Exists(fixture.Paths.LifecycleStatePath),
            Directory.Exists(Path.Combine(fixture.Paths.OperationsRoot, "lifecycle"))
                ? Directory.EnumerateFiles(Path.Combine(fixture.Paths.OperationsRoot, "lifecycle"), "manifest.json", SearchOption.AllDirectories).Count()
                : 0,
            fixture.Runner.ActiveImageId,
            lifecycle.PauseCount,
            JsonSerializer.Deserialize(File.ReadAllText(fixture.Paths.ManifestPath), DeploymentJsonContext.Default.InstanceManifest)!.Resources,
            ComposeDeployment.ComputeSha256(File.ReadAllText(Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml"))),
            Directory.EnumerateFiles(fixture.Paths.OperationsRoot, "candidate-compose.yml", SearchOption.AllDirectories).Count());

    private static MutationState Unmutated(LifecycleFixture fixture)
        => new(false, 0, fixture.Manifest.Image.ImageId, 0, fixture.Manifest.Resources, fixture.Manifest.ComposeModelSha256, 0);

    /// <summary>The one line <paramref name="written"/> holds, without its line ending.</summary>
    private static string SingleLine(string written)
    {
        var lines = written.Split(Environment.NewLine);
        Assert.HasCount(2, lines, written);
        Assert.AreEqual(string.Empty, lines[1], written);
        return lines[0];
    }

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
