using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class UpgradePreflightTests
{
    private const string CurrentIdentityMigration = "20260827053715_InitialIdentity";
    private const string LegacyIdentityMigration = "20251125021552_CreateLocalIdentity";
    private const string MinimumCompatibleRevision = "70ecdd3a0d02a5288aaa6438e3a5cfc8e395545f";

    private static readonly uint RuntimeUid = NativeLinux.getuid();
    private static readonly uint RuntimeGid = NativeLinux.getgid();

    private static readonly CameraAgentStateRequirements CurrentRequirements = new(
        CameraAgentStateContract.Current, MinimumCompatibleRevision, CurrentIdentityMigration, 13, 2);

    private static readonly string[] ExpectedLegacyFindingCodes =
    [
        "catalog-manifest-version", "identity-migration-lineage", "raw-ingress-schema",
        "bind-source-missing", "bind-source-mode"
    ];

    private static readonly string[] ExpectedTightenedModes = ["0500", "0600"];

    private static readonly string[] ExpectedBindSourceNames =
    [
        "identity", "data-protection", "provisioning", "raw", "archive", "replay-runner"
    ];

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Evaluate_CapturedPre70Ecdd3State_ReportsEveryIncompatibleBoundaryWithoutStartingCameraAgent()
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 1);
        fixture.WriteIdentityDatabase(LegacyIdentityMigration);
        fixture.WriteRawIngressDatabase(schemaVersion: 11);
        fixture.CreateBindSources();
        fixture.RelaxBindSourceMode(CameraAgentStateLayout.DataProtectionDirectoryName);
        fixture.RemoveBindSource(CameraAgentStateLayout.ProvisioningDirectoryName);

        var report = Evaluate(fixture);

        Assert.IsFalse(report.Compatible);
        Assert.AreEqual("incompatible", report.Outcome);
        Assert.AreEqual(MinimumCompatibleRevision, report.MinimumCompatibleRevision);
        var codes = report.Findings.Select(static finding => finding.Code).ToArray();
        CollectionAssert.AreEquivalent(ExpectedLegacyFindingCodes, codes);
        Assert.IsTrue(report.Findings.All(static finding => finding.Blocking));

        var catalog = report.Findings.Single(static finding => finding.Code == "catalog-manifest-version");
        Assert.AreEqual("1", catalog.Observed);
        Assert.AreEqual("2", catalog.Expected);
        var identity = report.Findings.Single(static finding => finding.Code == "identity-migration-lineage");
        Assert.AreEqual(LegacyIdentityMigration, identity.Observed);
        Assert.AreEqual(CurrentIdentityMigration, identity.Expected);
        var rawIngress = report.Findings.Single(static finding => finding.Code == "raw-ingress-schema");
        Assert.AreEqual("11", rawIngress.Observed);
        Assert.AreEqual("13", rawIngress.Expected);
        var mode = report.Findings.Single(static finding => finding.Code == "bind-source-mode");
        Assert.AreEqual("0755", mode.Observed);
        Assert.AreEqual("0700", mode.Expected);

        // One rendered report carries every boundary, so the operator never discovers them through restart loops.
        var rendered = CameraAgentStatePreflight.Render(report);
        foreach (var code in codes)
        {
            StringAssert.Contains(rendered, code, StringComparison.Ordinal);
        }
        StringAssert.Contains(rendered, MinimumCompatibleRevision, StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Evaluate_CleanStateProducedByTheCandidateContract_IsCompatible()
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 2);
        fixture.WriteIdentityDatabase(CurrentIdentityMigration);
        fixture.WriteRawIngressDatabase(schemaVersion: 13);
        fixture.CreateBindSources();

        var report = Evaluate(fixture);

        Assert.IsTrue(report.Compatible, CameraAgentStatePreflight.Render(report));
        Assert.AreEqual(0, report.Findings.Count);
        Assert.AreEqual("compatible", report.Outcome);
        Assert.AreEqual(CameraAgentStateContract.Current, report.CandidateStateContract);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Evaluate_FreshInstanceWithoutPersistedDatabases_IsCompatible()
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 2);
        fixture.CreateBindSources();

        var report = Evaluate(fixture);

        Assert.IsTrue(report.Compatible, CameraAgentStatePreflight.Render(report));
        Assert.AreEqual(0, report.Findings.Count);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Evaluate_Schema12IsRejectedBeforeUpgradeAndExact13IsAccepted()
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 2);
        fixture.WriteIdentityDatabase(CurrentIdentityMigration);
        fixture.CreateBindSources();
        fixture.WriteRawIngressDatabase(schemaVersion: 12);
        var legacy = Evaluate(fixture);
        Assert.IsFalse(legacy.Compatible);
        var finding = legacy.Findings.Single(static value => value.Blocking);
        Assert.AreEqual("raw-ingress-schema", finding.Code);
        Assert.AreEqual("12", finding.Observed);
        Assert.AreEqual("13", finding.Expected);
        Assert.AreEqual(12L, CameraAgentStatePreflight.ReadRawIngressVersion(
            CameraAgentStateLayout.RawIngressDatabasePath(fixture.Paths.StateRoot)));
        var otherForward = Evaluate(fixture, CurrentRequirements with { RawIngressSchema = 14 });
        Assert.IsFalse(otherForward.Compatible);
        Assert.AreEqual("raw-ingress-schema", otherForward.Findings.Single(static finding => finding.Blocking).Code);
        var oldImage = CurrentRequirements with { RawIngressSchema = 12 };
        fixture.SetRawIngressVersion(13);
        Assert.IsTrue(Evaluate(fixture).Compatible);
        var rollback = Evaluate(fixture, oldImage, CameraAgentStateContractPolicy.AllowLegacy);
        Assert.IsFalse(rollback.Compatible);
        Assert.AreEqual("raw-ingress-schema", rollback.Findings.Single(static finding => finding.Blocking).Code);
        fixture.SetRawIngressVersion(11);
        Assert.IsFalse(Evaluate(fixture).Compatible);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task EnsureCompatibleAsync_Schema12BlocksDeploymentWithoutChangingTheJournal()
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 2);
        fixture.WriteIdentityDatabase(CurrentIdentityMigration);
        fixture.WriteRawIngressDatabase(schemaVersion: 12);
        fixture.CreateBindSources();
        var candidate = new ImageInstallationIdentity(
            "registry", $"cameraagent@sha256:{new string('b', 64)}",
            $"sha256:{new string('c', 64)}", "amd64", null,
            UpgradeCompatibility: CameraAgentStateContract.Current,
            MinimumCompatibleRevision: MinimumCompatibleRevision,
            IdentityMigration: CurrentIdentityMigration,
            RawIngressSchema: "13",
            CatalogManifestVersion: "2");

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            CameraAgentStatePreflight.EnsureCompatibleAsync(
                fixture.Paths, fixture.InstanceId, candidate, CameraAgentStateContract.Current,
                RuntimeUid, RuntimeGid, HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.InProcess,
                persist: true, renderToStandardError: false, CancellationToken.None));

        StringAssert.Contains(exception.Message, "raw-ingress-schema", StringComparison.Ordinal);
        Assert.AreEqual(12L, CameraAgentStatePreflight.ReadRawIngressVersion(
            CameraAgentStateLayout.RawIngressDatabasePath(fixture.Paths.StateRoot)));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Evaluate_SupersededUnboundedLabel_IsRejectedForUpgradeAndAcceptedForRollback()
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 2);
        fixture.WriteIdentityDatabase(CurrentIdentityMigration);
        fixture.WriteRawIngressDatabase(schemaVersion: 12);
        fixture.CreateBindSources();
        var legacy = new CameraAgentStateRequirements(
            CameraAgentStateContract.LegacyUnbounded, null, null, null, null);

        var upgrade = Evaluate(fixture, legacy);
        Assert.IsFalse(upgrade.Compatible);
        Assert.AreEqual(1, upgrade.Findings.Count, CameraAgentStatePreflight.Render(upgrade));
        var blocking = upgrade.Findings.Single(static finding => finding.Blocking);
        Assert.AreEqual("candidate-state-contract-unsupported", blocking.Code);
        Assert.AreEqual(CameraAgentStateContract.LegacyUnbounded, blocking.Observed);

        var rollback = Evaluate(fixture, legacy, CameraAgentStateContractPolicy.AllowLegacy);
        Assert.IsTrue(rollback.Compatible, CameraAgentStatePreflight.Render(rollback));
        var advisory = rollback.Findings.Single();
        Assert.AreEqual("candidate-boundaries-undeclared", advisory.Code);
        Assert.IsFalse(advisory.Blocking, "a rollback target may not be blocked for predating the correction");
        foreach (var label in new[]
                 {
                     "minimum-compatible-revision", "identity-migration", "raw-ingress-schema",
                     "catalog-manifest-version"
                 })
        {
            StringAssert.Contains(advisory.Path, label, StringComparison.Ordinal);
        }
        StringAssert.Contains(
            CameraAgentStatePreflight.Render(rollback), "candidate-boundaries-undeclared", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Evaluate_UninitializedPersistedDatabases_MatchTheRuntimeFreshInitialization()
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 2);
        fixture.WriteEmptyIdentityDatabase();
        fixture.WriteRawIngressDatabase(schemaVersion: 0, createTable: false);
        fixture.CreateBindSources();

        var report = Evaluate(fixture);

        // SqliteRawCaptureJournal initializes a user_version 0 database with no schema objects, and EF migrates an
        // Identity database that carries neither a lineage row nor an AspNet table. Neither is an incompatible state.
        Assert.IsTrue(report.Compatible, CameraAgentStatePreflight.Render(report));
        Assert.AreEqual(0, report.Findings.Count);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task EnsureCompatibleAsync_LegacyState_FailsOnceWithTheCompleteBoundaryReport()
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 1);
        fixture.WriteIdentityDatabase(LegacyIdentityMigration);
        fixture.WriteRawIngressDatabase(schemaVersion: 11);
        fixture.CreateBindSources();
        var candidate = new ImageInstallationIdentity(
            "registry", $"cameraagent@sha256:{new string('b', 64)}", $"sha256:{new string('c', 64)}", "amd64", null,
            UpgradeCompatibility: CameraAgentStateContract.Current,
            MinimumCompatibleRevision: MinimumCompatibleRevision,
            IdentityMigration: CurrentIdentityMigration,
            RawIngressSchema: "13",
            CatalogManifestVersion: "2");

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => CameraAgentStatePreflight.EnsureCompatibleAsync(
                fixture.Paths, fixture.InstanceId, candidate, CameraAgentStateContract.LegacyUnbounded,
                RuntimeUid, RuntimeGid,
                HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.InProcess,
                persist: true, renderToStandardError: false, CancellationToken.None));

        foreach (var code in new[] { "catalog-manifest-version", "identity-migration-lineage", "raw-ingress-schema" })
        {
            StringAssert.Contains(exception.Message, code, StringComparison.Ordinal);
        }
        StringAssert.Contains(exception.Message, MinimumCompatibleRevision, StringComparison.Ordinal);

        var reportPath = Path.Combine(fixture.Paths.DeploymentStateRoot, "state-preflight.json");
        Assert.IsTrue(File.Exists(reportPath), "the consolidated report must be retained for the operator");
        await using var stream = SafeFileSystem.OpenOwnerFileRead(reportPath);
        var persisted = await JsonSerializer.DeserializeAsync(
            stream, DeploymentJsonContext.Default.CameraAgentStatePreflightReport, CancellationToken.None);
        Assert.AreEqual("incompatible", persisted!.Outcome);
        Assert.IsFalse(persisted.Compatible);
        Assert.AreEqual(3, persisted.Findings.Count);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task EnsureCompatibleAsync_CompatibleState_RetainsTheReportWithoutThrowing()
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 2);
        fixture.WriteIdentityDatabase(CurrentIdentityMigration);
        fixture.WriteRawIngressDatabase(schemaVersion: 13);
        fixture.CreateBindSources();
        var candidate = new ImageInstallationIdentity(
            "registry", $"cameraagent@sha256:{new string('b', 64)}", $"sha256:{new string('c', 64)}", "amd64", null,
            UpgradeCompatibility: CameraAgentStateContract.Current,
            MinimumCompatibleRevision: MinimumCompatibleRevision,
            IdentityMigration: CurrentIdentityMigration,
            RawIngressSchema: "13",
            CatalogManifestVersion: "2");

        var report = await CameraAgentStatePreflight.EnsureCompatibleAsync(
            fixture.Paths, fixture.InstanceId, candidate, CameraAgentStateContract.LegacyUnbounded,
            RuntimeUid, RuntimeGid,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.InProcess,
            persist: true, renderToStandardError: false, CancellationToken.None);

        Assert.IsTrue(report.Compatible);
        Assert.AreEqual(
            DeploymentSchemaVersions.StatePreflightReport,
            report.SchemaVersion);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Paths.DeploymentStateRoot, "state-preflight.json")));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Evaluate_UndeclaredCandidateBoundaries_AreSkippedRatherThanCompared()
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 2);
        fixture.WriteIdentityDatabase(LegacyIdentityMigration);
        fixture.WriteRawIngressDatabase(schemaVersion: 11);
        fixture.CreateBindSources();
        var undeclared = new CameraAgentStateRequirements(CameraAgentStateContract.Current, null, null, null, null);

        var report = Evaluate(fixture, undeclared);

        // A boundary the image does not declare cannot be compared, so it is skipped rather than treated as met,
        // and the report names every skipped boundary. The catalog manifest version is still compared against the
        // resolver constant the runtime enforces, which this instance satisfies.
        Assert.IsTrue(report.Compatible, CameraAgentStatePreflight.Render(report));
        var advisory = report.Findings.Single();
        Assert.AreEqual("candidate-boundaries-undeclared", advisory.Code);
        Assert.IsFalse(advisory.Blocking);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Evaluate_IdentityDatabaseWithoutItsHistoryTable_IsAnInterruptedFreshStart()
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 2);
        fixture.WriteUnmigratedIdentityDatabase(createIdentityTable: false);
        fixture.WriteRawIngressDatabase(schemaVersion: 13);
        fixture.CreateBindSources();

        // The runtime materializes the file on its first connection and creates the history table only during
        // MigrateAsync, so a table-less database is an interrupted fresh start, not an unreadable lineage.
        var report = Evaluate(fixture);

        Assert.IsTrue(report.Compatible, CameraAgentStatePreflight.Render(report));
        Assert.AreEqual(0, report.Findings.Count);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Evaluate_IdentitySchemaWithoutAnyRecordedMigration_IsAnIncompatibleLineage()
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 2);
        fixture.WriteUnmigratedIdentityDatabase(createIdentityTable: true);
        fixture.WriteRawIngressDatabase(schemaVersion: 13);
        fixture.CreateBindSources();

        var report = Evaluate(fixture);

        Assert.IsFalse(report.Compatible);
        var finding = report.Findings.Single();
        Assert.AreEqual("identity-migration-lineage", finding.Code);
        Assert.AreEqual("no recorded migration", finding.Observed);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Evaluate_PartiallyDeclaredCandidate_NamesOnlyTheOmittedBoundaries()
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 2);
        fixture.WriteIdentityDatabase(CurrentIdentityMigration);
        fixture.WriteRawIngressDatabase(schemaVersion: 13);
        fixture.CreateBindSources();
        var partial = new CameraAgentStateRequirements(
            CameraAgentStateContract.Current, MinimumCompatibleRevision, CurrentIdentityMigration, 13, null);

        var report = Evaluate(fixture, partial);

        Assert.IsTrue(report.Compatible, CameraAgentStatePreflight.Render(report));
        var advisory = report.Findings.Single();
        Assert.AreEqual("candidate-boundaries-undeclared", advisory.Code);
        StringAssert.Contains(advisory.Path, "catalog-manifest-version", StringComparison.Ordinal);
        foreach (var declared in new[] { "minimum-compatible-revision", "identity-migration", "raw-ingress-schema" })
        {
            Assert.IsFalse(
                advisory.Path.Contains(declared, StringComparison.Ordinal),
                $"a declared boundary must not be reported as omitted: {declared}");
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Evaluate_BindSourceTighterThanOwnerOnly_IsReportedAsIncompatible()
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 2);
        fixture.CreateBindSources();
        // A read-only or non-traversable source blocks the container's writes just as surely as a loose one
        // exposes its state, so neither direction may pass as compatible.
        fixture.SetBindSourceMode(
            CameraAgentStateLayout.RawDirectoryName, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        fixture.SetBindSourceMode(
            CameraAgentStateLayout.ArchiveDirectoryName, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var report = Evaluate(fixture);

        Assert.IsFalse(report.Compatible);
        var modes = report.Findings.Where(static finding => finding.Code == "bind-source-mode").ToArray();
        Assert.AreEqual(2, modes.Length, CameraAgentStatePreflight.Render(report));
        CollectionAssert.AreEquivalent(
            ExpectedTightenedModes, modes.Select(static finding => finding.Observed).ToArray());
        foreach (var finding in modes)
        {
            Assert.AreEqual("0700", finding.Expected);
            Assert.IsTrue(finding.Blocking);
            StringAssert.Contains(finding.Remediation, "tighter one blocks", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task EnsureCompatibleAsync_JsonInvocation_LeavesStandardErrorToTheSingleJsonObject()
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 1);
        fixture.CreateBindSources();
        var candidate = IncompatibleCandidate();
        var original = Console.Error;
        try
        {
            using var suppressed = new StringWriter();
            Console.SetError(suppressed);
            await Assert.ThrowsExactlyAsync<InstallerException>(
                () => CameraAgentStatePreflight.EnsureCompatibleAsync(
                    fixture.Paths, fixture.InstanceId, candidate, CameraAgentStateContract.LegacyUnbounded,
                    RuntimeUid, RuntimeGid,
                    HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.InProcess,
                    persist: true, renderToStandardError: false, CancellationToken.None));
            Assert.AreEqual(
                string.Empty,
                suppressed.ToString(),
                "a --json invocation reserves standard error for exactly one JSON object");

            using var rendered = new StringWriter();
            Console.SetError(rendered);
            await Assert.ThrowsExactlyAsync<InstallerException>(
                () => CameraAgentStatePreflight.EnsureCompatibleAsync(
                    fixture.Paths, fixture.InstanceId, candidate, CameraAgentStateContract.LegacyUnbounded,
                    RuntimeUid, RuntimeGid,
                    HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.InProcess,
                    persist: true, renderToStandardError: true, CancellationToken.None));
            StringAssert.Contains(rendered.ToString(), "catalog-manifest-version", StringComparison.Ordinal);
        }
        finally
        {
            Console.SetError(original);
        }

        // With persistence enabled the complete report stays available whether or not it was rendered.
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Paths.DeploymentStateRoot, "state-preflight.json")));
    }

    private static ImageInstallationIdentity IncompatibleCandidate() => new(
        "registry", $"cameraagent@sha256:{new string('b', 64)}", $"sha256:{new string('c', 64)}", "amd64", null,
        UpgradeCompatibility: CameraAgentStateContract.Current,
        MinimumCompatibleRevision: MinimumCompatibleRevision,
        IdentityMigration: CurrentIdentityMigration,
        RawIngressSchema: "12",
        CatalogManifestVersion: "2");

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void CreateRuntimeDirectory_PreCreatesNestedBindSourcesAndRestrictsAnAdoptedMode()
    {
        using var fixture = new PreflightFixture();
        var sources = ComposeDeployment.WritableStateDirectories(fixture.Paths.StateRoot);
        CollectionAssert.AreEquivalent(ExpectedBindSourceNames, sources.Select(Path.GetFileName).ToArray());

        var adopted = sources[0];
        Directory.CreateDirectory(adopted);
        File.SetUnixFileMode(adopted, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                      UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        foreach (var source in sources)
        {
            SafeFileSystem.CreateRuntimeDirectory(source, RuntimeUid, RuntimeGid);
        }

        foreach (var source in sources)
        {
            var identity = NativeLinux.GetDirectoryIdentity(source);
            Assert.AreEqual(RuntimeUid, identity.Uid);
            Assert.AreEqual(RuntimeGid, identity.Gid);
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                identity.Mode,
                source);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void CreateRuntimeDirectory_RejectsABindSourceOwnedByAnotherIdentity()
    {
        using var fixture = new PreflightFixture();
        var source = Path.Combine(fixture.Paths.StateRoot, CameraAgentStateLayout.DataProtectionDirectoryName);
        Directory.CreateDirectory(source);

        var exception = Assert.ThrowsExactly<InstallerException>(
            () => SafeFileSystem.CreateRuntimeDirectory(source, RuntimeUid + 1, RuntimeGid + 1));
        StringAssert.Contains(exception.Message, "instead of the configured runtime", StringComparison.Ordinal);

        var absent = Path.Combine(fixture.Paths.StateRoot, CameraAgentStateLayout.ProvisioningDirectoryName);
        Assert.ThrowsExactly<InstallerException>(
            () => SafeFileSystem.CreateRuntimeDirectory(absent, RuntimeUid + 1, RuntimeGid + 1));
        Assert.IsFalse(
            Directory.Exists(absent),
            "a refused bind source must not be left behind for the next run to reject again");
    }

    [TestMethod]
    public void CommandLine_ParsesPreflightAndConfirmedStateReset()
    {
        var instanceId = Guid.NewGuid();
        var preflight = (CameraAgentStatePreflightRequest)CommandLine.ParseCommand(
        [
            "cameraagent", "preflight", "--instance-id", instanceId.ToString("D"),
            "--product-root", "/tmp/hvo-preflight", "--image-ref", $"sha256:{new string('a', 64)}",
            "--json"
        ], allowTestProductRoot: true);
        Assert.AreEqual(instanceId, preflight.InstanceId);
        Assert.AreEqual($"sha256:{new string('a', 64)}", preflight.ImageReference);
        Assert.IsTrue(preflight.Json);

        var reset = (CameraAgentStateResetRequest)CommandLine.ParseCommand(
        [
            "cameraagent", "reset-state", "--instance-id", instanceId.ToString("D"),
            "--confirm-instance-id", instanceId.ToString("D"),
            "--product-root", "/tmp/hvo-preflight", "--dry-run"
        ], allowTestProductRoot: true);
        Assert.AreEqual(instanceId, reset.InstanceId);
        Assert.IsTrue(reset.DryRun);

        var usage = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(
        [
            "cameraagent", "reset-state", "--instance-id", instanceId.ToString("D"),
            "--product-root", "/tmp/hvo-preflight"
        ], allowTestProductRoot: true));
        StringAssert.Contains(usage.Message, "--confirm-instance-id", StringComparison.Ordinal);

        var mismatched = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(
        [
            "cameraagent", "reset-state", "--instance-id", instanceId.ToString("D"),
            "--confirm-instance-id", Guid.NewGuid().ToString("D"),
            "--product-root", "/tmp/hvo-preflight"
        ], allowTestProductRoot: true));
        StringAssert.Contains(mismatched.Message, "--confirm-instance-id", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ResetStateAsync_DeletesOnlyCameraAgentRuntimeStateAndPreservesDeploymentConfiguration()
    {
        using var fixture = new PreflightFixture();
        fixture.WriteCatalogManifest(manifestVersion: 1);
        fixture.WriteIdentityDatabase(LegacyIdentityMigration);
        fixture.WriteRawIngressDatabase(schemaVersion: 11);
        fixture.CreateBindSources();
        await fixture.WriteInstalledStateAsync(InstanceLifecycleCondition.Uninstalled);
        var secret = Path.Combine(fixture.Paths.ConfigRoot, "secrets", "LocalIdentity__DatabasePath");
        var temporaryPassword = Path.Combine(fixture.Paths.ConfigRoot, "owner-bootstrap", "temporary-password");
        var request = new CameraAgentStateResetRequest(
            fixture.InstanceId, fixture.InstanceId, fixture.Root, DryRun: true, Json: false)
        {
            AllowTestProductRoot = true
        };

        var planned = await CameraAgentStateResetManager.ExecuteAsync(
            request, new AbsentContainerRunner(), RuntimeUid, RuntimeGid, CancellationToken.None);

        Assert.AreEqual("planned", planned.Outcome);
        Assert.IsTrue(File.Exists(planned.EvidencePath));
        Assert.IsTrue(File.Exists(CameraAgentStateLayout.IdentityDatabasePath(fixture.Paths.StateRoot)));
        CollectionAssert.Contains(planned.PreservedPaths.ToArray(), fixture.Paths.ManifestPath);

        var completed = await CameraAgentStateResetManager.ExecuteAsync(
            request with { DryRun = false }, new AbsentContainerRunner(), RuntimeUid, RuntimeGid,
            CancellationToken.None);

        Assert.AreEqual("completed", completed.Outcome);
        foreach (var directory in ComposeDeployment.WritableStateDirectories(fixture.Paths.StateRoot))
        {
            Assert.IsTrue(Directory.Exists(directory), directory);
            Assert.AreEqual(0, Directory.EnumerateFileSystemEntries(directory).Count(), directory);
            var identity = NativeLinux.GetDirectoryIdentity(directory);
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, identity.Mode);
        }

        Assert.IsTrue(File.Exists(secret), "deployment secrets must survive a CameraAgent state reset");
        Assert.IsTrue(File.Exists(temporaryPassword), "the owner bootstrap credential must survive the reset");
        Assert.IsTrue(File.Exists(fixture.Paths.ManifestPath));
        Assert.IsTrue(File.Exists(fixture.Paths.ApplicationIdentityPath));
        Assert.IsTrue(Directory.Exists(fixture.Paths.CatalogRoot), "shared catalogs stay outside the boundary");
        Assert.IsFalse(File.Exists(fixture.Paths.ResultPath), "the completed result is withdrawn for re-seeding");

        await using var stateStream = SafeFileSystem.OpenOwnerFileRead(fixture.Paths.StatePath);
        var state = await JsonSerializer.DeserializeAsync(
            stateStream, DeploymentJsonContext.Default.InstallationState, CancellationToken.None);
        Assert.AreEqual(InstallationPhase.Preflight, state!.Phase);
        Assert.AreEqual(InstallationStatus.Pending, state.Status);
        Assert.AreEqual(fixture.InstallationId, state.InstallationId);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ResetStateAsync_RequiresAPreserveByDefaultUninstall()
    {
        using var fixture = new PreflightFixture();
        fixture.CreateBindSources();
        await fixture.WriteInstalledStateAsync(InstanceLifecycleCondition.Installed);
        var request = new CameraAgentStateResetRequest(
            fixture.InstanceId, fixture.InstanceId, fixture.Root, DryRun: false, Json: false)
        {
            AllowTestProductRoot = true
        };

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(
            () => CameraAgentStateResetManager.ExecuteAsync(
                request, new AbsentContainerRunner(), RuntimeUid, RuntimeGid, CancellationToken.None));

        StringAssert.Contains(exception.Message, "cameraagent uninstall", StringComparison.Ordinal);
        Assert.IsTrue(Directory.Exists(fixture.Paths.StateRoot));
    }

    private static CameraAgentStatePreflightReport Evaluate(
        PreflightFixture fixture,
        CameraAgentStateRequirements? requirements = null,
        CameraAgentStateContractPolicy policy = CameraAgentStateContractPolicy.RequireCurrent)
        => CameraAgentStatePreflight.Evaluate(
            fixture.Paths,
            fixture.InstanceId,
            $"sha256:{new string('c', 64)}",
            candidateRelease: null,
            requirements ?? CurrentRequirements,
            CameraAgentStateContract.LegacyUnbounded,
            RuntimeUid,
            RuntimeGid,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.InProcess,
            policy);

    private sealed class PreflightFixture : IDisposable
    {
        public PreflightFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), $"hvo-preflight-{Guid.NewGuid():N}");
            InstanceId = Guid.NewGuid();
            InstallationId = Guid.NewGuid();
            Paths = InstallationPaths.Create(Root, InstanceId, ProductionCatalog.CatalogId);
            foreach (var directory in new[]
            {
                Paths.ProductRoot, Path.Combine(Paths.ProductRoot, "cameraagents"), Paths.InstanceRoot,
                Paths.ConfigRoot, Path.Combine(Paths.ConfigRoot, "secrets"),
                Path.Combine(Paths.ConfigRoot, "owner-bootstrap"), Path.Combine(Paths.ConfigRoot, "compose"),
                Paths.StateRoot, Paths.DeploymentStateRoot, Paths.OperationsRoot
            })
            {
                SafeFileSystem.CreateOwnerDirectory(directory);
            }
        }

        public string Root { get; }
        public Guid InstanceId { get; }
        public Guid InstallationId { get; }
        public InstallationPaths Paths { get; }

        public void WriteCatalogManifest(int manifestVersion)
        {
            var versionRoot = Path.Combine(Paths.CatalogRoot, "versions", ProductionCatalog.PackageVersion);
            SafeFileSystem.CreateOwnerDirectory(versionRoot);
            File.WriteAllText(
                Path.Combine(versionRoot, "manifest.json"),
                "{\"manifestVersion\":" + manifestVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"catalog\":{\"id\":\"" + ProductionCatalog.CatalogId + "\"}}");
            Directory.CreateSymbolicLink(
                Path.Combine(Paths.CatalogRoot, "current"), $"versions/{ProductionCatalog.PackageVersion}");
        }

        public void CreateBindSources()
        {
            foreach (var directory in ComposeDeployment.WritableStateDirectories(Paths.StateRoot))
            {
                SafeFileSystem.CreateRuntimeDirectory(directory, RuntimeUid, RuntimeGid);
            }
        }

        public void RelaxBindSourceMode(string name)
            => File.SetUnixFileMode(
                Path.Combine(Paths.StateRoot, name),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        public void SetBindSourceMode(string name, UnixFileMode mode)
            => File.SetUnixFileMode(Path.Combine(Paths.StateRoot, name), mode);

        public void RemoveBindSource(string name) => Directory.Delete(Path.Combine(Paths.StateRoot, name), true);

        public void WriteIdentityDatabase(string migrationId)
        {
            var path = CameraAgentStateLayout.IdentityDatabasePath(Paths.StateRoot);
            SafeFileSystem.CreateOwnerDirectory(Path.GetDirectoryName(path)!);
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The fixture composes a fixed lineage row for a private disposable database.
            command.CommandText =
                "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL);" +
                $"INSERT INTO __EFMigrationsHistory VALUES ('{migrationId}', '10.0.0');";
#pragma warning restore CA2100
            command.ExecuteNonQuery();
        }

        public void WriteEmptyIdentityDatabase()
        {
            var path = CameraAgentStateLayout.IdentityDatabasePath(Paths.StateRoot);
            SafeFileSystem.CreateOwnerDirectory(Path.GetDirectoryName(path)!);
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL);";
            command.ExecuteNonQuery();
        }

        /// <summary>A database EF materialized but never migrated, optionally already carrying Identity tables.</summary>
        public void WriteUnmigratedIdentityDatabase(bool createIdentityTable)
        {
            var path = CameraAgentStateLayout.IdentityDatabasePath(Paths.StateRoot);
            SafeFileSystem.CreateOwnerDirectory(Path.GetDirectoryName(path)!);
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            if (!createIdentityTable)
            {
                return;
            }
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE AspNetUsers (Id TEXT NOT NULL PRIMARY KEY);";
            command.ExecuteNonQuery();
        }

        public void WriteRawIngressDatabase(int schemaVersion, bool createTable = true)
        {
            var path = CameraAgentStateLayout.RawIngressDatabasePath(Paths.StateRoot);
            SafeFileSystem.CreateOwnerDirectory(Path.GetDirectoryName(path)!);
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            using var command = connection.CreateCommand();
#pragma warning disable CA2100 // PRAGMA user_version cannot be parameterized; the value is a test constant.
            command.CommandText =
                (createTable ? "CREATE TABLE raw_capture (id INTEGER PRIMARY KEY);" : string.Empty) +
                $"PRAGMA user_version = {schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)};";
#pragma warning restore CA2100
            command.ExecuteNonQuery();
        }

        public void SetRawIngressVersion(int schemaVersion)
        {
            using var connection = new SqliteConnection($"Data Source={CameraAgentStateLayout.RawIngressDatabasePath(Paths.StateRoot)}");
            connection.Open();
            using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Fixed test schema versions cannot be parameterized in PRAGMA.
            command.CommandText = $"PRAGMA user_version = {schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)};";
#pragma warning restore CA2100
            command.ExecuteNonQuery();
        }

        public async Task WriteInstalledStateAsync(InstanceLifecycleCondition condition)
        {
            var applicationIdentity = Guid.NewGuid();
            var daemon = new DockerDaemonIdentity("daemon", "host", "amd64", "29.7.2");
            var catalog = new CatalogInstallationIdentity(
                ProductionCatalog.CatalogId, ProductionCatalog.PackageVersion, "2", "3",
                ProductionCatalog.DatabaseSha256, ProductionCatalog.DatabaseLength, ProductionCatalog.RowCount,
                Paths.CatalogRoot, new string('a', 64), "local-offline");
            var image = new ImageInstallationIdentity(
                "registry", $"cameraagent@sha256:{new string('b', 64)}", $"sha256:{new string('c', 64)}", "amd64", null,
                UpgradeCompatibility: CameraAgentStateContract.LegacyUnbounded, SourceRevision: new string('8', 40),
                Component: "CameraAgent", ConfigurationContract: "cameraagent-install-v1",
                CatalogContract: "hyg-v42-production-p3-s2");
            var manifest = new InstanceManifest(
                1, "HVO.SkyMonitor", "cameraagent-install-v1", DeploymentComponent.CameraAgent, InstanceId,
                "Preflight Camera", applicationIdentity, $"installer-{InstanceId:D}", 1, new string('d', 64),
                "owner@example.test", 0, 0, 0, "UTC", InstallationId, RuntimeUid, RuntimeGid, Paths.ProductRoot,
                Paths.ConfigRoot, Paths.StateRoot, ComposeDeployment.TemplateVersion, new string('e', 64),
                new string('f', 64), new string('1', 64), "default", "1", "1", "active", new string('2', 64),
                new string('3', 64), catalog, image, null, daemon, CameraAgentStateContract.LegacyUnbounded,
                DateTimeOffset.UtcNow, LifecycleCondition: condition);
            await SafeFileSystem.WriteJsonAtomicAsync(
                Paths.ManifestPath, manifest, DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
            await SafeFileSystem.WriteJsonAtomicAsync(
                Paths.ApplicationIdentityPath,
                new ApplicationIdentityBinding(1, "pre-provisioning", applicationIdentity, null),
                DeploymentJsonContext.Default.ApplicationIdentityBinding, CancellationToken.None);
            await SafeFileSystem.WriteJsonAtomicAsync(
                Paths.StatePath,
                new InstallationState(
                    1, InstallationId, InstanceId, new string('4', 64), InstallationPhase.Completed,
                    InstallationStatus.Completed, DateTimeOffset.UtcNow),
                DeploymentJsonContext.Default.InstallationState, CancellationToken.None);
            await File.WriteAllTextAsync(Paths.ResultPath, "{}", CancellationToken.None);
            File.SetUnixFileMode(Paths.ResultPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await File.WriteAllTextAsync(
                Path.Combine(Paths.ConfigRoot, "secrets", "LocalIdentity__DatabasePath"),
                "/app/App_Data/cameraagent_identity.db",
                CancellationToken.None);
            await File.WriteAllTextAsync(
                Path.Combine(Paths.ConfigRoot, "owner-bootstrap", "temporary-password"), "secret",
                CancellationToken.None);
        }

        public void Dispose()
        {
            if (!Directory.Exists(Root))
            {
                return;
            }
            SafeFileSystem.MakeTreeOwnerWritable(Root);
            Directory.Delete(Root, recursive: true);
        }
    }

    /// <summary>A Docker daemon whose CameraAgent container was already removed by a preserve-by-default uninstall.</summary>
    private sealed class AbsentContainerRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Assert.AreEqual("docker", fileName);
            if (arguments is ["context", "inspect", ..])
            {
                return Task.FromResult(new ProcessResult(0, "unix:///var/run/docker.sock", string.Empty));
            }
            if (arguments.Count >= 2 && arguments[0] == "--host")
            {
                arguments = arguments.Skip(2).ToArray();
            }
            if (arguments is ["info", ..])
            {
                return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new
                {
                    OSType = "linux",
                    ID = "daemon",
                    Name = "host",
                    Architecture = "amd64",
                    ServerVersion = "29.7.2"
                }), string.Empty));
            }
            if (arguments is ["compose", "version", ..])
            {
                return Task.FromResult(new ProcessResult(0, "v2", string.Empty));
            }
            if (arguments is ["container", "inspect", var name])
            {
                return Task.FromResult(new ProcessResult(
                    1, string.Empty, $"Error response from daemon: No such container: {name}"));
            }
            if (arguments is ["container", "ls", ..])
            {
                return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
            }
            throw new InvalidOperationException($"Unexpected docker invocation: {string.Join(' ', arguments)}");
        }
    }
}
