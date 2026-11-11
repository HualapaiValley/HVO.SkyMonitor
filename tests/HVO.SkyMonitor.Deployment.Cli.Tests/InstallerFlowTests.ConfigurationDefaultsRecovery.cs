using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The durability of adopting newer configuration defaults (Refs #1779), with and without a limits change. The manifest
/// is the commit point. Before it is written, any failure (file publication, compose-up, health, verification or
/// cancellation) puts the instance back on its recorded configuration, of the generation that wrote it, and on its
/// recorded limits and identities, and a re-run carries on. After it, an interruption leaves the result, or the install
/// record, behind the manifest, and the next run settles them. A power loss, where no handler runs, is carried forward by
/// the next run. The rig profile, schedule, request identity and staged operator settings never move.
/// </summary>
public sealed partial class InstallerFlowTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("file-publication", false)]
    [DataRow("file-publication", true)]
    [DataRow("file-publication-cancelled", false)]
    [DataRow("file-publication-cancelled", true)]
    [DataRow("compose-up", false)]
    [DataRow("compose-up", true)]
    [DataRow("health", false)]
    [DataRow("health", true)]
    [DataRow("verification", false)]
    [DataRow("verification", true)]
    [DataRow("verified-cancelled", false)]
    [DataRow("verified-cancelled", true)]
    public async Task AdoptionFailingBeforeItsCommit_RestoresTheRecordedConfigurationAndAReRunCarriesOn(string fault, bool withLimits)
    {
        ArgumentNullException.ThrowIfNull(fault);
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new AdoptionOwnerClient();
        var original = CameraAgentInstaller.StepCompleted;
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);
            var retained = await files.ManifestAsync();
            var retainedResult = await files.ResultAsync();
            await StageOperatorSettingAsync(files);
            var before = await RecordsAsync(files);
            var reRun = withLimits ? request with { Cpus = 3m, Memory = "6g" } : request;
            using var cancellation = new CancellationTokenSource();
            switch (fault)
            {
                case "file-publication":
                    CameraAgentInstaller.StepCompleted = step =>
                    {
                        if (step == "rerun-files-written") throw new InstallerException("injected file publication failure");
                    };
                    break;
                case "file-publication-cancelled":
                    CameraAgentInstaller.StepCompleted = step =>
                    {
                        if (step == "rerun-files-written") cancellation.Cancel();
                    };
                    break;
                case "compose-up":
                    runner.FailComposeUpNumber = runner.ComposeUpCount + 1;
                    break;
                case "health":
                    owner.FailOnHealthCheck = owner.HealthChecks + 1;
                    break;
                case "verification":
                    owner.FailOnVerification = owner.VerificationAttempts + 1;
                    break;
                case "verified-cancelled":
                    CameraAgentInstaller.StepCompleted = step =>
                    {
                        if (step == "limits-verified") cancellation.Cancel();
                    };
                    break;
            }

            var failure = await Assert.ThrowsAsync<Exception>(() => CameraAgentInstaller.InstallAsync(
                reRun, runner, _ => owner, RuntimeUid, RuntimeGid, cancellation.Token,
                portAvailabilityProbe: new RecordingPortProbe().Probe, hostEnvironment: new FakeHostEnvironment()));
            CameraAgentInstaller.StepCompleted = original;

            if (fault.EndsWith("-cancelled", StringComparison.Ordinal))
            {
                Assert.IsInstanceOfType<OperationCanceledException>(failure);
            }
            else
            {
                var installerFailure = Assert.IsInstanceOfType<InstallerException>(failure);
                Assert.IsFalse(installerFailure.IsRefusal, "a step that failed part way is a failure, which re-running continues");
                StringAssert.Contains(failure.Message, withLimits
                    ? $"and adopting the installer's new configuration defaults ({CloudChange}) failed, so the recorded limits and configuration were restored"
                    : $"Adopting the installer's new configuration defaults ({CloudChange}) failed, so the recorded configuration was restored",
                    StringComparison.Ordinal);
            }
            // Every record, the configuration file included, is as it was; the container was put back on the recorded
            // configuration and recreated so that it reads it, and it was verified there at the recorded identity.
            CollectionAssert.AreEqual(before, await RecordsAsync(files), "a failure before the commit left a record changed");
            Assert.AreEqual(before[0], runner.LastComposeUpConfiguration, "the container was not put back on the recorded configuration");
            CollectionAssert.Contains(runner.ComposeUps[^1].ToArray(), "--force-recreate");
            Assert.AreEqual(retained.ConfigurationSha256, owner.Verified[^1]);
            Assert.AreEqual(retained.Resources!.MemoryBytes, CameraAgentContainerResources.TryReadLimits(runner.LastComposeUp!)!.MemoryBytes,
                "the container was not put back on the recorded limits");

            // A re-run carries on and adopts.
            var carriedOn = await InstallResourceRequestAsync(reRun, runner, owner);

            Assert.IsTrue(carriedOn.Plan!.HasChanges);
            var manifest = await files.ManifestAsync();
            var result = await files.ResultAsync();
            var adopted = CameraConfiguration.Generate(request, retained.ApplicationIdentity);
            Assert.AreEqual(adopted.Json, await File.ReadAllTextAsync(files.Configuration));
            Assert.AreEqual(adopted.Sha256, manifest.ConfigurationSha256);
            Assert.AreEqual(adopted.Sha256, owner.Verified[^1]);
            AssertUnchangedIdentities(retained, retainedResult, manifest, result);
            Assert.AreEqual(before[4], await File.ReadAllTextAsync(files.State));
            Assert.AreEqual(before[5], await File.ReadAllTextAsync(files.OperatorSettings));
            Assert.IsFalse((await InstallResourceRequestAsync(reRun, runner, owner)).Plan!.HasChanges);
        }
        finally
        {
            CameraAgentInstaller.StepCompleted = original;
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// The adoption is committed by the manifest, and an interruption right after it leaves the result on the previous
    /// configuration identity: a partial new configuration-hash commit. The next plan names the result, and only it (and
    /// the install record after a limits change); the next run settles them without starting anything.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AdoptionInterruptedAfterItsManifestCommit_IsSettledByTheNextRun(bool withLimits)
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new AdoptionOwnerClient();
        var original = CameraAgentInstaller.StepCompleted;
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);
            var retained = await files.ManifestAsync();
            var retainedResult = await files.ResultAsync();
            var operatorSettings = await StageOperatorSettingAsync(files);
            var state = await File.ReadAllTextAsync(files.State);
            var reRun = withLimits ? request with { Cpus = 3m, Memory = "6g" } : request;
            var adopted = CameraConfiguration.Generate(request, retained.ApplicationIdentity);
            CameraAgentInstaller.StepCompleted = step =>
            {
                if (step == "limits-manifest-written") throw new InstallerException("injected interruption after the commit");
            };

            var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => InstallResourceRequestAsync(reRun, runner, owner));
            CameraAgentInstaller.StepCompleted = original;

            Assert.AreEqual("injected interruption after the commit", failure.Message, "a committed change is not restored");
            var committed = await files.ManifestAsync();
            Assert.AreEqual(adopted.Sha256, committed.ConfigurationSha256);
            Assert.AreEqual(retained.ConfigurationSha256, (await files.ResultAsync()).ConfigurationSha256, "the result is still behind");
            Assert.AreEqual(adopted.Json, await File.ReadAllTextAsync(files.Configuration));
            Assert.AreEqual(adopted.Json, runner.LastComposeUpConfiguration);
            var composeUps = runner.ComposeUpCount;

            var plan = await InstallResourceRequestAsync(reRun with { DryRun = true }, runner, owner);
            var settled = await InstallResourceRequestAsync(reRun, runner, owner);

            foreach (var outcome in new[] { plan, settled })
            {
                var changes = outcome.Plan!.Items.Where(static item => item.Action != DeploymentPlanAction.Unchanged).ToArray();
                var resultItem = changes.Single(item => item.Target == files.Result);
                StringAssert.Contains(resultItem.Detail, "configuration identity", StringComparison.Ordinal);
                Assert.HasCount(withLimits ? 2 : 1, changes, string.Join("; ", outcome.Plan.Items));
                if (withLimits)
                {
                    Assert.AreEqual("record", changes.Single(item => item.Target != files.Result).Kind);
                }
            }
            Assert.AreEqual(composeUps, runner.ComposeUpCount, "settling the records starts nothing");
            var manifest = await files.ManifestAsync();
            var result = await files.ResultAsync();
            Assert.AreEqual(committed, manifest);
            AssertUnchangedIdentities(retained, retainedResult, manifest, result);
            Assert.AreEqual(state, await File.ReadAllTextAsync(files.State));
            Assert.AreEqual(operatorSettings, await File.ReadAllTextAsync(files.OperatorSettings));
            if (withLimits)
            {
                Assert.AreEqual(manifest.Resources, (await InstallRecordStore.ReadAsync(
                    InstallRecordLocation.For(root, request.ProductionProductRoot), CancellationToken.None))!.Instances.Single().Resources);
            }
            Assert.IsFalse((await InstallResourceRequestAsync(reRun, runner, owner)).Plan!.HasChanges);
        }
        finally
        {
            CameraAgentInstaller.StepCompleted = original;
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// An interruption after the result is written leaves only the install record behind (with a limits change); the
    /// next run settles it and nothing else.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AdoptionCancelledAtRecordSettlement_IsCommittedAndTheNextRunSettlesTheRecord(bool withLimits)
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new AdoptionOwnerClient();
        var original = CameraAgentInstaller.StepCompleted;
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);
            var retained = await files.ManifestAsync();
            var record = await File.ReadAllTextAsync(files.Record);
            var reRun = withLimits ? request with { Cpus = 3m, Memory = "6g" } : request;
            using var cancellation = new CancellationTokenSource();
            CameraAgentInstaller.StepCompleted = step =>
            {
                if (step == "limits-result-written") cancellation.Cancel();
            };

            await Assert.ThrowsAsync<OperationCanceledException>(() => CameraAgentInstaller.InstallAsync(
                reRun, runner, _ => owner, RuntimeUid, RuntimeGid, cancellation.Token,
                portAvailabilityProbe: new RecordingPortProbe().Probe, hostEnvironment: new FakeHostEnvironment()));
            CameraAgentInstaller.StepCompleted = original;

            var manifest = await files.ManifestAsync();
            var result = await files.ResultAsync();
            Assert.AreEqual(CameraConfiguration.Generate(request, retained.ApplicationIdentity).Sha256, manifest.ConfigurationSha256);
            Assert.AreEqual(manifest.ConfigurationSha256, result.ConfigurationSha256);
            Assert.AreEqual(record, await File.ReadAllTextAsync(files.Record), "the install record is settled last");
            var composeUps = runner.ComposeUpCount;

            var settled = await InstallResourceRequestAsync(reRun, runner, owner);

            Assert.AreEqual(withLimits ? DeploymentPlanAction.Change : DeploymentPlanAction.Unchanged,
                settled.Plan!.Items.Single(static item => item.Kind == "record").Action);
            Assert.IsTrue(settled.Plan.Items.Where(static item => item.Kind != "record").All(static item => item.Action == DeploymentPlanAction.Unchanged),
                string.Join("; ", settled.Plan.Items));
            Assert.AreEqual(composeUps, runner.ComposeUpCount);
            Assert.AreEqual(manifest.Resources, (await InstallRecordStore.ReadAsync(
                InstallRecordLocation.For(root, request.ProductionProductRoot), CancellationToken.None))!.Instances.Single().Resources);
        }
        finally
        {
            CameraAgentInstaller.StepCompleted = original;
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// A power loss after the adoption published the new camera-module.json and before its commit: no handler ran, so the
    /// file is the adopted one and every record the previous one. The installer wrote that file itself, so the next plan
    /// names the adoption and the next run finishes it.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PowerLossAfterTheAdoptedConfigurationWasPublished_IsCarriedOnByTheNextRun()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new AdoptionOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);
            var retained = await files.ManifestAsync();
            var retainedResult = await files.ResultAsync();
            var adopted = CameraConfiguration.Generate(request, retained.ApplicationIdentity);
            await File.WriteAllTextAsync(files.Configuration, adopted.Json);

            var plan = await RunInstallProgramAsync(request with { DryRun = true }, runner, owner);
            var run = await RunInstallProgramAsync(request, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Done, plan.ExitCode, plan.Error);
            plan.AssertChangedNothing("the plan changed something");
            Assert.AreEqual((int)InstallerExitCode.Done, run.ExitCode, run.Error);
            Assert.IsTrue(run.PlanLines.Any(line => line.Contains(files.Configuration, StringComparison.Ordinal) &&
                line.Contains(CloudChange, StringComparison.Ordinal)), run.Output);
            Assert.AreEqual(1, run.ComposeUps);
            CollectionAssert.Contains(runner.ComposeUps[^1].ToArray(), "--force-recreate");
            Assert.AreEqual(adopted.Json, runner.LastComposeUpConfiguration);
            var manifest = await files.ManifestAsync();
            Assert.AreEqual(adopted.Sha256, manifest.ConfigurationSha256);
            AssertUnchangedIdentities(retained, retainedResult, manifest, await files.ResultAsync());
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// A power loss inside a restore, after the manifest had been replaced and the recorded configuration written back:
    /// the manifest holds the adopted identity, the result and the file the previous one. Both are states the commit order
    /// can leave, so the next run carries the instance forward to the manifest, recreating the container on the adopted
    /// configuration and settling the result.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task PowerLossDuringARestoreAfterTheManifestWasReplaced_IsCarriedForwardByTheNextRun()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new AdoptionOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);
            var retained = await files.ManifestAsync();
            var retainedResult = await files.ResultAsync();
            var adopted = CameraConfiguration.Generate(request, retained.ApplicationIdentity);
            await SetRecordedConfigurationAsync(files, adopted.Sha256, manifest: true, result: false);

            var run = await RunInstallProgramAsync(request, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Done, run.ExitCode, run.Error);
            Assert.AreEqual(1, run.ComposeUps);
            CollectionAssert.Contains(runner.ComposeUps[^1].ToArray(), "--force-recreate");
            Assert.AreEqual(adopted.Json, await File.ReadAllTextAsync(files.Configuration));
            Assert.AreEqual(adopted.Json, runner.LastComposeUpConfiguration);
            Assert.AreEqual(adopted.Sha256, owner.Verified[^1]);
            var manifest = await files.ManifestAsync();
            Assert.AreEqual(adopted.Sha256, manifest.ConfigurationSha256);
            AssertUnchangedIdentities(retained, retainedResult, manifest, await files.ResultAsync());
            Assert.IsFalse((await InstallResourceRequestAsync(request, runner, owner)).Plan!.HasChanges);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// An adoption whose verification fails and whose restore Docker then cannot start leaves the container on the adopted
    /// configuration while the records and the file hold the previous one. The failure says to re-run, and the re-run
    /// carries the adoption on, verifying only on the configuration it recreates the container with.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AdoptionWhoseRestoreAlsoFails_IsCarriedOnByTheNextRun()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new AdoptionOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);
            var retained = await files.ManifestAsync();
            var before = await RecordsAsync(files);
            var adopted = CameraConfiguration.Generate(request, retained.ApplicationIdentity);
            owner.FailOnVerification = owner.VerificationAttempts + 1;
            runner.FailComposeUpNumber = runner.ComposeUpCount + 2;

            var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => InstallResourceRequestAsync(request, runner, owner));

            Assert.IsFalse(failure.IsRefusal);
            StringAssert.Contains(failure.Message, "restoring the recorded configuration also failed", StringComparison.Ordinal);
            StringAssert.Contains(failure.Message, "Re-run the same install to carry on.", StringComparison.Ordinal);
            CollectionAssert.AreEqual(before, await RecordsAsync(files));
            Assert.AreEqual(adopted.Json, runner.LastComposeUpConfiguration, "the container is still on the adopted configuration");

            var carriedOn = await RunInstallProgramAsync(request, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Done, carriedOn.ExitCode, carriedOn.Error);
            Assert.AreEqual(adopted.Sha256, (await files.ManifestAsync()).ConfigurationSha256);
            Assert.AreEqual(adopted.Sha256, owner.Verified[^1]);
            Assert.IsFalse((await InstallResourceRequestAsync(request, runner, owner)).Plan!.HasChanges);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// The exit codes of the three classes and of a genuine step failure, through the program entry point: a request-driven
    /// difference and an unexplained one exit 3 having changed nothing, an adoption exits 0, and only a step that failed
    /// part way exits 1, its message saying what was restored.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task EachClassOfConfigurationDifference_HasItsExitCode_AndOnlyAFailedStepExitsOne()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new AdoptionOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);
            var configuration = await File.ReadAllTextAsync(files.Configuration);

            var requestDriven = await RunInstallProgramAsync(request with { IlluminationMode = "SolarDriven" }, runner, owner);
            var edited = JsonNode.Parse(configuration)!.AsObject();
            edited["module"]!["options"]!["seed"] = 7;
            await File.WriteAllTextAsync(files.Configuration, edited.ToJsonString());
            var unexplained = await RunInstallProgramAsync(request, runner, owner);
            await File.WriteAllTextAsync(files.Configuration, configuration);
            owner.FailOnVerification = owner.VerificationAttempts + 1;
            var failed = await RunInstallProgramAsync(request, runner, owner);
            var adopted = await RunInstallProgramAsync(request, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Refused, requestDriven.ExitCode, requestDriven.Output);
            requestDriven.AssertChangedNothing("a request-driven refusal changed something");
            Assert.AreEqual((int)InstallerExitCode.Refused, unexplained.ExitCode, unexplained.Output);
            unexplained.AssertChangedNothing("an unexplained refusal changed something");
            Assert.AreEqual((int)InstallerExitCode.Failed, failed.ExitCode, failed.Output);
            StringAssert.StartsWith(failed.Error, "Installation failed: Adopting the installer's new configuration defaults", StringComparison.Ordinal);
            StringAssert.Contains(failed.Error, "so the recorded configuration was restored", StringComparison.Ordinal);
            Assert.AreEqual((int)InstallerExitCode.Done, adopted.ExitCode, adopted.Error);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// The camera configuration, manifest, result, Compose model, completed state, staged operator settings and install
    /// record, in that order.
    /// </summary>
    private static async Task<string[]> RecordsAsync(InstalledFiles files)
        => await Task.WhenAll(new[]
        {
            files.Configuration, files.Manifest, files.Result, files.Compose, files.State, files.OperatorSettings, files.Record
        }.Select(static path => File.ReadAllTextAsync(path)));

    /// <summary>
    /// An owner client that records the configuration identity of every verification it passes, and fails a health check
    /// or a verification once, at the call numbered.
    /// </summary>
    private sealed class AdoptionOwnerClient : IOwnerBootstrapClient
    {
        private readonly InstallerOwnerClient inner = new();

        /// <summary>The configuration identity of each verification that passed, in order.</summary>
        public List<string> Verified { get; } = [];

        public int HealthChecks { get; private set; }

        public int VerificationAttempts { get; private set; }

        public int FailOnHealthCheck { get; set; } = -1;

        public int FailOnVerification { get; set; } = -1;

        public Task WaitForHealthAsync(
            ApprovedCatalogContract expectedCatalog,
            CancellationToken cancellationToken,
            TimeSpan? timeout = null,
            bool allowQuarantinedReadiness = false)
            => ++HealthChecks == FailOnHealthCheck
                ? throw new InstallerException("injected health failure")
                : inner.WaitForHealthAsync(expectedCatalog, cancellationToken, timeout, allowQuarantinedReadiness);

        public Task<string> ReadStateAsync(string ownerEmail, string password, CancellationToken cancellationToken)
            => inner.ReadStateAsync(ownerEmail, password, cancellationToken);

        public Task<string> ReadInstallationStateAsync(string verificationToken, CancellationToken cancellationToken)
            => inner.ReadInstallationStateAsync(verificationToken, cancellationToken);

        public async Task<string> VerifyInstallationAsync(
            string verificationToken,
            InstallationVerificationExpectation expectation,
            CancellationToken cancellationToken)
        {
            if (++VerificationAttempts == FailOnVerification)
            {
                throw new InstallerException("injected verification failure");
            }
            var state = await inner.VerifyInstallationAsync(verificationToken, expectation, cancellationToken);
            Verified.Add(expectation.ConfigurationSha256);
            return state;
        }
    }
}
