using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The upgrade requires <c>--migration-backward-compatible</c> exactly when the candidate declares a state
/// migration (#1307): an unchanged state contract proceeds without it, every kind of declared migration is refused
/// without it and admitted with it, and supplying it for an unchanged contract changes nothing.
/// </summary>
public sealed partial class LifecycleContractTests
{
    private const string CandidateReference = "ghcr.io/example/cameraagent@sha256:4444444444444444444444444444444444444444444444444444444444444444";
    private const string CandidateImageId = "sha256:5555555555555555555555555555555555555555555555555555555555555555";

    /// <summary>The installed image declares exactly the state boundaries the fake daemon reports for the candidate.</summary>
    private static ImageInstallationIdentity CurrentStateContract(ImageInstallationIdentity image)
        => image with
        {
            UpgradeCompatibility = CameraAgentStateContract.Current,
            MinimumCompatibleRevision = new string('7', 40),
            IdentityMigration = "20260827053715_InitialIdentity",
            RawIngressSchema = "12",
            CatalogManifestVersion = "2",
            RawIngressMigration = null
        };

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UpgradeAsync_UnchangedStateContract_CompletesWithOrWithoutTheMigrationAssertion(bool assertMigration)
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed, installedImage: CurrentStateContract);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(CandidateImageId, result.Image!.ImageId);
        Assert.AreEqual(CandidateImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(CameraAgentStateContract.Current, result.Image.UpgradeCompatibility);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("state-compatibility", "io.hvo.skymonitor.state-compatibility installed backward-compatible, candidate cameraagent-state-v2")]
    [DataRow("minimum-compatible-revision", "io.hvo.skymonitor.minimum-compatible-revision installed 7777777777777777777777777777777777777777, candidate 6666666666666666666666666666666666666666")]
    [DataRow("identity-migration", "io.hvo.skymonitor.identity-migration installed 20260827053715_InitialIdentity, candidate 20261016000000_NextIdentity")]
    [DataRow("raw-ingress-schema", "io.hvo.skymonitor.raw-ingress-schema installed 12, candidate 13")]
    [DataRow("raw-ingress-migration", "io.hvo.skymonitor.raw-ingress-migration installed none, candidate canonical-v13-to-v14")]
    [DataRow("catalog-manifest-version", "io.hvo.skymonitor.catalog-manifest-version installed 1, candidate 2")]
    [DataRow("undeclared-installed-boundary", "io.hvo.skymonitor.identity-migration installed none, candidate 20260827053715_InitialIdentity")]
    public async Task UpgradeAsync_DeclaredStateMigration_IsRefusedWithoutTheAssertionAndAdmittedWithIt(
        string migration,
        string expectedChange)
    {
        // Each case changes exactly one boundary from an otherwise unchanged contract. A change the persisted-state
        // preflight would itself refuse is made on the installed side, so this gate is the one that answers.
        Func<ImageInstallationIdentity, ImageInstallationIdentity> installed = migration switch
        {
            "state-compatibility" => image => CurrentStateContract(image) with { UpgradeCompatibility = CameraAgentStateContract.LegacyUnbounded },
            "catalog-manifest-version" => image => CurrentStateContract(image) with { CatalogManifestVersion = "1" },
            "undeclared-installed-boundary" => image => CurrentStateContract(image) with { IdentityMigration = null },
            _ => CurrentStateContract
        };
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, installedImage: installed);
        switch (migration)
        {
            case "minimum-compatible-revision":
                fixture.Runner.CandidateLabels["io.hvo.skymonitor.minimum-compatible-revision"] = new string('6', 40);
                break;
            case "identity-migration":
                fixture.Runner.CandidateLabels["io.hvo.skymonitor.identity-migration"] = "20261016000000_NextIdentity";
                break;
            case "raw-ingress-schema":
                fixture.Runner.CandidateRawIngressSchema = "13";
                break;
            case "raw-ingress-migration":
                fixture.Runner.CandidateLabels["io.hvo.skymonitor.raw-ingress-migration"] = RawIngressV13Schema.MigrationContract;
                break;
        }
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);

        await AssertMigrationRefusedThenAdmittedAsync(fixture, expectedChange);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_JournalBelowTheDeclaredSchema_RequiresTheAssertionEvenWhenTheLabelsAgree()
    {
        // Both images declare raw-ingress schema 15 and the v13/v14 recovery migration, but the persisted journal is
        // still a canonical v14 one, for example one restored from an earlier backup. The preflight admits it because
        // the candidate declares that migration, and the candidate would perform it on startup.
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            installedImage: image => CurrentStateContract(image) with
            {
                RawIngressSchema = "15",
                RawIngressMigration = RawIngressV13Schema.RecoveryMigrationContract
            });
        fixture.Runner.CandidateRawIngressSchema = "15";
        fixture.Runner.CandidateLabels["io.hvo.skymonitor.raw-ingress-migration"] = RawIngressV13Schema.RecoveryMigrationContract;
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        WriteCanonicalV14RawIngressJournal(fixture.Paths);

        await AssertMigrationRefusedThenAdmittedAsync(fixture, "persisted raw-ingress journal schema 14, candidate schema 15");
    }

    private static async Task AssertMigrationRefusedThenAdmittedAsync(LifecycleFixture fixture, string expectedChange)
    {
        var lifecycle = new FakeLifecycleClient();
        var manifestBefore = await File.ReadAllTextAsync(fixture.Paths.ManifestPath);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(exception.Message, "declares a CameraAgent state migration", StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, expectedChange, StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "--migration-backward-compatible", StringComparison.Ordinal);
        // Refused before the drain, the backup, and any Compose mutation: the installed instance is untouched.
        Assert.AreEqual(0, lifecycle.PauseCount);
        Assert.AreEqual(0, fixture.Runner.BackupCount);
        Assert.AreEqual(0, fixture.Runner.ComposeUpCount);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(manifestBefore, await File.ReadAllTextAsync(fixture.Paths.ManifestPath));
        // A terminal refusal blocks nothing, so the same upgrade with the assertion proceeds without --resume.
        var retained = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(InstallationStatus.Failed, retained!.Status);
        Assert.AreEqual(CameraAgentLifecycleManager.RefusedFailureCode, retained.FailureCode);
        Assert.IsFalse(retained.MutationStarted);
        StringAssert.Contains(retained.FailureMessage!, expectedChange, StringComparison.Ordinal);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: true), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(CandidateImageId, result.Image!.ImageId);
        Assert.AreEqual(CandidateImageId, fixture.Runner.ActiveImageId);
    }

    private static LifecycleRequest UpgradeRequest(LifecycleFixture fixture, bool assertMigration)
        => fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = CandidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = assertMigration
        };

    private static void WriteCanonicalV14RawIngressJournal(InstallationPaths paths)
    {
        var path = CameraAgentStateLayout.RawIngressDatabasePath(paths.StateRoot);
        SafeFileSystem.CreateOwnerDirectory(Path.Combine(paths.StateRoot, CameraAgentStateLayout.RawDirectoryName));
        SafeFileSystem.CreateOwnerDirectory(Path.GetDirectoryName(path)!);
        using var stream = typeof(LifecycleContractTests).Assembly.GetManifestResourceStream(
            "HVO.SkyMonitor.Deployment.Cli.Tests.FrozenRawIngressV14.sql")!;
        using var reader = new StreamReader(stream);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The frozen schema is an embedded test resource, not input.
            command.CommandText = reader.ReadToEnd();
#pragma warning restore CA2100
            command.ExecuteNonQuery();
        }
        Assert.AreEqual(14L, CameraAgentStatePreflight.ReadRawIngressVersion(path));
    }
}
