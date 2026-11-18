using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Re-running a completed install after the installer's configuration defaults changed (Refs #1779; audit items 1706-B1
/// and 1706-B2). Every difference between an instance's recorded configuration and what the installer now generates is
/// classified: a different answer (lighting included) is refused with exit 3 and named; newer defaults are adopted, and
/// named in the plan, only on a running image that declares them; anything the installer cannot explain is refused with
/// exit 3, naming the file or record and the remedy. Nothing ends in the generic exit 1.
/// <para>
/// The <c>F3_*</c> tests are ported from the 2026-11-10 acceptance audit's reproducer (F3, finding B), which asserted the
/// defect; their defect assertions are inverted here and their controls kept. Instead of seeding roots with older
/// installer code in other worktrees, a completed install from before the cloud steps (Refs #1722) is made by this
/// installer under configuration-default generation 1 alone, the generation every installer before them wrote, which
/// <see cref="CameraConfigurationDefaultsTests"/> pins to the audit's retained <c>camera-module.json</c>.
/// </para>
/// </summary>
public sealed partial class InstallerFlowTests
{
    private const string CloudChange = "pipeline: 11 → 13 steps (adds CloudAssessment, CloudPresentation)";

    /// <summary>The cloud change as a refusal or failure message prints it: its fields are escaped as JSON text.</summary>
    private const string PlainCloudChange = "pipeline: 11 to 13 steps (adds CloudAssessment, CloudPresentation)";
    private const string CloudDefaults = "installer-defaults-2";
    private const string NothingToChangeSummary = "Nothing to change: the instance is installed as the answers describe.";
    private static readonly string[] CloudSteps = ["CloudAssessment", "CloudPresentation"];

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task F3_B_Pre1722Install_BaselineReRunWithNewLimits_AdoptsTheCloudStepsAndNamesThemInEveryPlan()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("1779b001-0000-4000-8000-000000000001"));
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var verified = new List<string>();
        var owner = new InstallerOwnerClient { InspectVerification = expectation => verified.Add(expectation.ConfigurationSha256) };
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);
            var retained = await files.ManifestAsync();
            var retainedResult = await files.ResultAsync();
            var retainedConfiguration = await File.ReadAllTextAsync(files.Configuration);
            var generated = CameraConfiguration.Generate(request, retained.ApplicationIdentity);
            var state = await File.ReadAllTextAsync(files.State);
            var operatorSettings = await StageOperatorSettingAsync(files);

            // The premise of the finding: the rig profile version and identity did not move, the configuration identity did.
            AssertFrozenGeneration(retainedConfiguration, retained.ApplicationIdentity, 1);
            Assert.AreEqual("installer-virtualsky-v4", retained.RigProfileVersion);
            Assert.AreEqual(retained.RigProfileVersion, generated.RigProfileVersion);
            Assert.AreEqual(retained.RigProfileSha256, generated.RigProfileSha256);
            Assert.AreEqual(retained.ScheduleSha256, generated.ScheduleSha256);
            Assert.AreNotEqual(retained.ConfigurationSha256, generated.Sha256, "the default configuration identity moved");
            var retainedSteps = CameraConfigurationDefaultsTests.StepIds(retainedConfiguration);
            var generatedSteps = CameraConfigurationDefaultsTests.StepIds(generated.Json);
            Assert.HasCount(11, retainedSteps);
            Assert.HasCount(13, generatedSteps);
            CollectionAssert.AreEqual(CloudSteps, generatedSteps.Except(retainedSteps).ToArray());
            Assert.IsEmpty(retainedSteps.Except(generatedSteps).ToArray());

            var limits = request with { Cpus = 3m, Memory = "6g" };

            // --plan and --plan --json: exit 0, name the change and the container recreate, change nothing, deploy nothing.
            var plan = await RunInstallProgramAsync(limits with { DryRun = true }, runner, owner);
            var jsonPlan = await RunInstallProgramAsync(limits with { DryRun = true, Json = true }, runner, owner);
            foreach (var attempt in new[] { plan, jsonPlan })
            {
                Assert.AreEqual((int)InstallerExitCode.Done, attempt.ExitCode, attempt.Error);
                attempt.AssertChangedNothing("a plan changed the product root or deployed");
            }
            var planned = jsonPlan.Reported.Plan!;
            var configurationItem = planned.Items.Single(item => item.Target == files.Configuration);
            Assert.AreEqual(DeploymentPlanAction.Change, configurationItem.Action);
            Assert.AreEqual("file", configurationItem.Kind);
            StringAssert.Contains(configurationItem.Detail, CloudChange, StringComparison.Ordinal);
            var containerItem = planned.Items.Single(static item => item.Kind == "container");
            Assert.AreEqual(DeploymentPlanAction.Change, containerItem.Action);
            StringAssert.Contains(containerItem.Detail, "recreated", StringComparison.Ordinal);
            StringAssert.Contains(planned.Items.Single(item => item.Target == files.Manifest).Detail, generated.Sha256, StringComparison.Ordinal);
            CollectionAssert.AreEqual(DeploymentPlanText.Lines(planned).ToArray(), plan.PlanLines.ToArray(), "--plan --json carries the human plan's items");
            Assert.IsTrue(plan.PlanLines.Any(line => line.StartsWith("  change ", StringComparison.Ordinal) &&
                line.Contains(files.Configuration, StringComparison.Ordinal) && line.Contains(CloudChange, StringComparison.Ordinal)), plan.Output);
            using (var json = JsonDocument.Parse(jsonPlan.Output))
            {
                var item = json.RootElement.GetProperty("plan").GetProperty("items").EnumerateArray()
                    .Single(element => element.GetProperty("target").GetString() == files.Configuration);
                Assert.AreEqual("Change", item.GetProperty("action").GetString());
                StringAssert.Contains(item.GetProperty("detail").GetString(), CloudChange, StringComparison.Ordinal);
            }

            // The run: exit 0, the 13-step configuration, the new limits, exactly one compose-up, which recreates.
            var run = await RunInstallProgramAsync(limits, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Done, run.ExitCode, run.Error);
            Assert.AreEqual(1, run.ComposeUps);
            CollectionAssert.Contains(runner.ComposeUps[^1].ToArray(), "--force-recreate");
            CollectionAssert.AreEqual(plan.PlanLines.ToArray(), run.PlanLines.ToArray(), "the run changed what both plans said it would");
            var configuration = await File.ReadAllTextAsync(files.Configuration);
            Assert.AreEqual(generated.Json, configuration);
            Assert.AreEqual(configuration, runner.LastComposeUpConfiguration, "the container was started on the adopted configuration");
            Assert.HasCount(13, CameraConfigurationDefaultsTests.StepIds(configuration));
            var manifest = await files.ManifestAsync();
            var result = await files.ResultAsync();
            Assert.AreEqual(new ContainerResourceLimits(3m, 6 * ResourceGibibyte, 256, true, true), manifest.Resources);
            // One proposed identity agrees across the plan, the file, the manifest, the result and the verification.
            var identity = CameraConfigurationDefaultsTests.CanonicalSha256(configuration);
            Assert.AreEqual(generated.Sha256, identity);
            Assert.AreEqual(identity, manifest.ConfigurationSha256);
            Assert.AreEqual(identity, result.ConfigurationSha256);
            Assert.AreEqual(identity, verified[^1]);
            Assert.AreEqual(manifest.ComposeModelSha256, result.ComposeModelSha256);
            AssertUnchangedIdentities(retained, retainedResult, manifest, result);
            Assert.AreEqual(state, await File.ReadAllTextAsync(files.State), "the request identity and completed state are unchanged");
            Assert.AreEqual(operatorSettings, await File.ReadAllTextAsync(files.OperatorSettings), "the staged active-rig setting is unchanged");

            // Verification passes on the adopted instance, and the same re-run now has nothing to change.
            var again = await RunInstallProgramAsync(limits, runner, owner);
            Assert.AreEqual((int)InstallerExitCode.Done, again.ExitCode, again.Error);
            Assert.AreEqual(NothingToChangeSummary, again.PlanLines[^1]);
            again.AssertChangedNothing("a re-run with nothing to change wrote or deployed");
            Assert.AreEqual(identity, verified[^1]);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task F3_B_Pre1722Install_RepairReRunWithTheInstalledAnswers_AdoptsAndASecondReRunChangesNothing()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.Parse("1779b001-0000-4000-8000-000000000002"));
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);
            var retained = await files.ManifestAsync();
            var retainedResult = await files.ResultAsync();
            var compose = await File.ReadAllTextAsync(files.Compose);

            var repaired = await RunInstallProgramAsync(request, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Done, repaired.ExitCode, repaired.Error);
            Assert.AreEqual(1, repaired.ComposeUps);
            Assert.IsTrue(repaired.PlanLines.Any(line => line.StartsWith("  change ", StringComparison.Ordinal) &&
                line.Contains(files.Configuration, StringComparison.Ordinal) && line.Contains(CloudChange, StringComparison.Ordinal)), repaired.Output);
            var manifest = await files.ManifestAsync();
            Assert.AreEqual(CameraConfiguration.Generate(request, retained.ApplicationIdentity).Sha256, manifest.ConfigurationSha256);
            Assert.AreEqual(retained.Resources, manifest.Resources, "a re-run without new limits keeps the recorded ones");
            Assert.AreEqual(compose, await File.ReadAllTextAsync(files.Compose), "the Compose model does not change with the configuration");
            AssertUnchangedIdentities(retained, retainedResult, manifest, await files.ResultAsync());

            var second = await RunInstallProgramAsync(request, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Done, second.ExitCode, second.Error);
            Assert.AreEqual(NothingToChangeSummary, second.PlanLines[^1]);
            StringAssert.Contains(second.Output, NothingToChangeSummary, StringComparison.Ordinal);
            second.AssertChangedNothing("a second identical re-run wrote or deployed");
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// The finding's full scenario: installed before the cloud steps (Refs #1722), upgraded with <c>cameraagent upgrade</c>, which keeps the
    /// configuration, then re-run naming the image the instance now runs, with new limits. The re-run adopts only when the
    /// upgraded image declares the cloud defaults; otherwise it refuses and changes nothing.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(true)]
    [DataRow(false)]
    public async Task F3_B_UpgradedInstall_ReRunWithNewLimits_AdoptsOnlyWhenTheUpgradedImageIncludesTheCloudFix(bool fixedImage)
    {
        var bundle = RequireProductionCatalogBundle();
        var (installed, root) = ResourceRequest(bundle, Guid.Parse(fixedImage ? "1779b001-0000-4000-8000-000000000003" : "1779b001-0000-4000-8000-000000000004"));
        // The lifecycle test double reports the published port 5130; no port is bound.
        installed = installed with { Port = 5130 };
        try
        {
            // The baseline instance, on an image from before the cloud fix.
            var files = InstalledFiles.Of(await InstallBefore1722Async(installed, DefaultsDaemon(installed, root, null), new InstallerOwnerClient()), root);
            var candidate = await LifecycleContractTests.UpgradeInstalledRootAsync(root, installed.InstanceId!.Value, fixedImage ? CloudDefaults : null);
            var retained = await files.ManifestAsync();
            var retainedResult = await files.ResultAsync();
            Assert.AreEqual(candidate.ImageId, retained.Image.ImageId);
            Assert.AreNotEqual(CameraConfiguration.Generate(installed, retained.ApplicationIdentity).Sha256, retained.ConfigurationSha256);
            // "After an upgrade, a re-run names the image the instance now runs" (docs/runbooks/deployment-installer.md).
            var request = installed with { ImageReference = candidate.Reference, Cpus = 3m, Memory = "6g" };
            var runner = new InstallerProcessRunner(candidate.ImageId, root, request.InstanceId!.Value)
            {
                RenderComposeFile = true,
                Labels = candidate.Labels,
                RepoDigests = [candidate.Reference]
            };
            var owner = new InstallerOwnerClient();

            var plan = await RunInstallProgramAsync(request with { DryRun = true }, runner, owner);
            var run = await RunInstallProgramAsync(request, runner, owner);

            var manifest = await files.ManifestAsync();
            if (fixedImage)
            {
                Assert.AreEqual((int)InstallerExitCode.Done, plan.ExitCode, plan.Error);
                plan.AssertChangedNothing("the plan changed something");
                Assert.AreEqual((int)InstallerExitCode.Done, run.ExitCode, run.Error);
                Assert.AreEqual(1, run.ComposeUps);
                CollectionAssert.AreEqual(plan.PlanLines.ToArray(), run.PlanLines.ToArray());
                Assert.IsTrue(run.PlanLines.Any(line => line.Contains(CloudChange, StringComparison.Ordinal)), run.Output);
                Assert.AreEqual(new ContainerResourceLimits(3m, 6 * ResourceGibibyte, 256, true, true), manifest.Resources);
                Assert.AreEqual(CameraConfiguration.Generate(installed, retained.ApplicationIdentity).Sha256, manifest.ConfigurationSha256);
                Assert.AreEqual(candidate.ImageId, manifest.Image.ImageId, "the re-run keeps the upgraded image");
                AssertUnchangedIdentities(retained, retainedResult, manifest, await files.ResultAsync());
            }
            else
            {
                foreach (var attempt in new[] { plan, run })
                {
                    Assert.AreEqual((int)InstallerExitCode.Refused, attempt.ExitCode, attempt.Output);
                    StringAssert.Contains(attempt.Error, CameraConfigurationDefaults.ImageLabel, StringComparison.Ordinal);
                    StringAssert.Contains(attempt.Error, "cameraagent upgrade", StringComparison.Ordinal);
                    attempt.AssertChangedNothing("a refused re-run changed something");
                }
                Assert.AreEqual(retained, manifest);
            }
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// The running-image prerequisite: an instance whose running image does not declare the cloud defaults is refused,
    /// by the plan and the run alike, human and JSON, with nothing rewritten, no compose-up, and no image imported, so the
    /// re-run never becomes an image upgrade. An image that declares an older generation, or a value that is not a
    /// generation, is the same as one that declares none.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow((string?)null)]
    [DataRow("installer-defaults-1")]
    [DataRow("cloud")]
    public async Task F3_B_Pre1722Install_OnARunningImageWithoutTheCloudFix_IsRefusedAndNothingChanges(string? declared)
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, declared);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);
            var retained = await files.ManifestAsync();
            var limits = request with { Cpus = 3m, Memory = "6g" };
            var invocations = runner.Invocations;

            var attempts = new[]
            {
                await RunInstallProgramAsync(limits with { DryRun = true }, runner, owner),
                await RunInstallProgramAsync(limits, runner, owner),
                await RunInstallProgramAsync(request, runner, owner),
                await RunInstallProgramAsync(limits with { Json = true }, runner, owner),
                await RunInstallProgramAsync(limits with { DryRun = true, Json = true }, runner, owner)
            };

            foreach (var attempt in attempts.Take(3))
            {
                Assert.AreEqual((int)InstallerExitCode.Refused, attempt.ExitCode, attempt.Output);
                StringAssert.StartsWith(attempt.Error, "Installation refused: ", StringComparison.Ordinal);
                StringAssert.Contains(attempt.Error, $"({PlainCloudChange})", StringComparison.Ordinal);
                StringAssert.Contains(attempt.Error, $"{CameraConfigurationDefaults.ImageLabel} {CloudDefaults} or later", StringComparison.Ordinal);
                StringAssert.Contains(attempt.Error, "A re-run keeps the image", StringComparison.Ordinal);
                StringAssert.EndsWith(attempt.Error, "Nothing was changed.", StringComparison.Ordinal);
                attempt.AssertChangedNothing("a refused re-run changed something");
            }
            foreach (var attempt in attempts.Skip(3))
            {
                Assert.AreEqual((int)InstallerExitCode.Refused, attempt.ExitCode, attempt.Output);
                Assert.AreEqual("refused", attempt.ErrorCode);
                attempt.AssertChangedNothing("a refused re-run changed something");
            }
            Assert.AreEqual(0, runner.LoadCount + runner.PullCount, "a refused re-run imports no image");
            Assert.IsEmpty(runner.Imports);
            Assert.IsGreaterThan(invocations, runner.Invocations);
            Assert.AreEqual(retained, await files.ManifestAsync());
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// The audit's attribution, inverted: moving only the records' configuration identity to the current generation's,
    /// while camera-module.json keeps the 11-step pipeline, no longer lets a re-run silently rewrite the file. The file no
    /// longer matches its records, so the re-run is refused, naming the file, and changes nothing.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task F3_B_Pre1722Install_WithOnlyTheRecordedConfigurationHashMoved_IsRefusedAsUnexplained()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);
            var retained = await files.ManifestAsync();
            var generated = CameraConfiguration.Generate(request, retained.ApplicationIdentity);
            foreach (var path in new[] { files.Manifest, files.Result })
            {
                var record = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
                Assert.AreEqual(retained.ConfigurationSha256, (string?)record["configurationSha256"], path);
                record["configurationSha256"] = generated.Sha256;
                await File.WriteAllTextAsync(path, record.ToJsonString());
            }
            var limits = request with { Cpus = 3m, Memory = "6g" };

            var plan = await RunInstallProgramAsync(limits with { DryRun = true }, runner, owner);
            var run = await RunInstallProgramAsync(limits, runner, owner);

            foreach (var attempt in new[] { plan, run })
            {
                Assert.AreEqual((int)InstallerExitCode.Refused, attempt.ExitCode, attempt.Output);
                StringAssert.Contains(attempt.Error, files.Configuration, StringComparison.Ordinal);
                StringAssert.Contains(attempt.Error, "has been edited", StringComparison.Ordinal);
                attempt.AssertChangedNothing("an unexplained difference changed something");
            }
            Assert.HasCount(11, CameraConfigurationDefaultsTests.StepIds(await File.ReadAllTextAsync(files.Configuration)));
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>Control, kept from the audit: a limits change of an instance this installer made still works.</summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task F3_Control_ReRunWithNewLimits_SucceedsWithThisInstaller()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, null);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallResourceRequestAsync(request, runner, owner), root);
            var before = await files.ManifestAsync();
            var configuration = await File.ReadAllTextAsync(files.Configuration);

            var changed = await InstallResourceRequestAsync(request with { Cpus = 3m, Memory = "6g" }, runner, owner);

            var after = await files.ManifestAsync();
            Assert.AreEqual(new ContainerResourceLimits(3m, 6 * ResourceGibibyte, 256, true, true), after.Resources);
            Assert.AreEqual(DeploymentPlanAction.Change, changed.Plan!.Items.Single(static item => item.Kind == "container").Action);
            Assert.AreEqual(DeploymentPlanAction.Unchanged, changed.Plan.Items.Single(item => item.Target == files.Configuration).Action);
            Assert.AreEqual(before.ConfigurationSha256, after.ConfigurationSha256);
            Assert.AreEqual(configuration, await File.ReadAllTextAsync(files.Configuration));
            CollectionAssert.DoesNotContain(runner.ComposeUps[^1].ToArray(), "--force-recreate", "a limits change alone recreates through Compose");
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>Control: each installer re-runs its own install with nothing to change, the one before the cloud steps (Refs #1722) included.</summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(true)]
    [DataRow(false)]
    public async Task F3_Control_EachInstallerReRunsItsOwnInstallWithNothingToChange(bool before1722)
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, null);
        var owner = new InstallerOwnerClient();
        try
        {
            using var installer = before1722 ? CameraConfigurationDefaults.Use([CameraConfigurationDefaults.Shipped[0]]) : null;
            _ = await InstallResourceRequestAsync(request, runner, owner);

            var again = await RunInstallProgramAsync(request, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Done, again.ExitCode, again.Error);
            Assert.AreEqual(NothingToChangeSummary, again.PlanLines[^1]);
            again.AssertChangedNothing("a re-run by the installer that made the instance changed something");
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// The downgrade design point: an installer that does not know the generation an instance is on, here one that knows
    /// only the generation before the cloud steps (Refs #1722) re-running an instance a newer installer adopted, refuses and changes nothing.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AnOlderInstaller_ReRunningAnInstanceANewerInstallerAdopted_RefusesAndChangesNothing()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);
            Assert.AreEqual((int)InstallerExitCode.Done, (await RunInstallProgramAsync(request, runner, owner)).ExitCode);

            ProgramRun older;
            using (CameraConfigurationDefaults.Use([CameraConfigurationDefaults.Shipped[0]]))
            {
                older = await RunInstallProgramAsync(request, runner, owner);
            }

            Assert.AreEqual((int)InstallerExitCode.Refused, older.ExitCode, older.Output);
            StringAssert.Contains(older.Error, files.Configuration, StringComparison.Ordinal);
            StringAssert.Contains(older.Error, "A newer installer may have written it", StringComparison.Ordinal);
            older.AssertChangedNothing("an older installer changed an adopted instance");
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// The generic mechanism: a test-only default change registered on top of the current generation is adopted by an
    /// instance installed under the current generation, with the plan naming the entry's description.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task SyntheticDefaultChange_OnTopOfTheCurrentGeneration_IsAdoptedAndNamed()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, null);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallResourceRequestAsync(request, runner, owner), root);
            var retained = await files.ManifestAsync();
            var retainedResult = await files.ResultAsync();
            using var registry = CameraConfigurationDefaults.Use([.. CameraConfigurationDefaults.Shipped, SyntheticGeneration(requiresImage: false)]);
            var synthetic = CameraConfiguration.Generate(request, retained.ApplicationIdentity);

            var plan = await RunInstallProgramAsync(request with { DryRun = true }, runner, owner);
            var run = await RunInstallProgramAsync(request, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Done, plan.ExitCode, plan.Error);
            plan.AssertChangedNothing("the plan changed something");
            Assert.AreEqual((int)InstallerExitCode.Done, run.ExitCode, run.Error);
            foreach (var attempt in new[] { plan, run })
            {
                Assert.IsTrue(attempt.PlanLines.Any(line => line.Contains(files.Configuration, StringComparison.Ordinal) &&
                    line.EndsWith($"adopts the installer's new defaults: {SyntheticChange})", StringComparison.Ordinal)), attempt.Output);
            }
            Assert.AreEqual(synthetic.Json, await File.ReadAllTextAsync(files.Configuration));
            var manifest = await files.ManifestAsync();
            Assert.AreEqual(synthetic.Sha256, manifest.ConfigurationSha256);
            AssertUnchangedIdentities(retained, retainedResult, manifest, await files.ResultAsync());
            Assert.AreEqual(NothingToChangeSummary, (await RunInstallProgramAsync(request, runner, owner)).PlanLines[^1]);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>A default change that needs the image's support is adopted only on an image that declares it.</summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("installer-defaults-2", false)]
    [DataRow("installer-defaults-3", true)]
    [DataRow("installer-defaults-4", true)]
    public async Task SyntheticDefaultChangeNeedingTheImage_IsAdoptedOnlyOnAnImageThatDeclaresIt(string declared, bool adopted)
    {
        ArgumentNullException.ThrowIfNull(declared);
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, declared);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallResourceRequestAsync(request, runner, owner), root);
            using var registry = CameraConfigurationDefaults.Use([.. CameraConfigurationDefaults.Shipped, SyntheticGeneration(requiresImage: true)]);

            var run = await RunInstallProgramAsync(request, runner, owner);

            if (adopted)
            {
                Assert.AreEqual((int)InstallerExitCode.Done, run.ExitCode, run.Error);
                Assert.AreEqual(CameraConfiguration.Generate(request, (await files.ManifestAsync()).ApplicationIdentity).Json,
                    await File.ReadAllTextAsync(files.Configuration));
            }
            else
            {
                Assert.AreEqual((int)InstallerExitCode.Refused, run.ExitCode, run.Output);
                StringAssert.Contains(run.Error, "installer-defaults-3 or later", StringComparison.Ordinal);
                StringAssert.Contains(run.Error, SyntheticChange.Replace("→", "to", StringComparison.Ordinal), StringComparison.Ordinal);
                run.AssertChangedNothing("a refused adoption changed something");
            }
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// A jump over more than one generation adopts each in order and names each, from the frozen output from before the cloud steps (Refs #1722).
    /// Omitted and explicit lighting both go through adoption unchanged.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow((string?)null)]
    [DataRow("LegacyScalarSolar")]
    [DataRow("ControlledNight")]
    public async Task AMultiGenerationJump_AdoptsEveryGenerationInOrderAndKeepsTheInstalledLighting(string? lighting)
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        request = request with { IlluminationMode = lighting };
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);
            var retained = await files.ManifestAsync();
            var retainedResult = await files.ResultAsync();
            if (lighting is null)
            {
                AssertFrozenGeneration(await File.ReadAllTextAsync(files.Configuration), retained.ApplicationIdentity, 1);
            }
            using var registry = CameraConfigurationDefaults.Use([.. CameraConfigurationDefaults.Shipped, SyntheticGeneration(requiresImage: false)]);
            var target = CameraConfiguration.Generate(request, retained.ApplicationIdentity);

            var run = await RunInstallProgramAsync(request, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Done, run.ExitCode, run.Error);
            Assert.IsTrue(run.PlanLines.Any(line => line.EndsWith(
                $"adopts the installer's new defaults: {CloudChange}; {SyntheticChange})", StringComparison.Ordinal)), run.Output);
            var configuration = await File.ReadAllTextAsync(files.Configuration);
            Assert.AreEqual(target.Json, configuration);
            using var document = JsonDocument.Parse(configuration);
            var options = document.RootElement.GetProperty("module").GetProperty("options");
            Assert.AreEqual(lighting, options.TryGetProperty("illuminationMode", out var mode) ? mode.GetString() : null);
            Assert.HasCount(13, CameraConfigurationDefaultsTests.StepIds(configuration));
            AssertUnchangedIdentities(retained, retainedResult, await files.ManifestAsync(), await files.ResultAsync());
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// 1706-B1: a re-run that changes the lighting is refused with exit 3, naming <c>--illumination-mode</c> with both
    /// values, before any adoption. Omitted and explicit <c>LegacyScalarSolar</c> are different answers, as the runbook
    /// says, both ways round; the same answer re-runs.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(null, "ControlledNight", false)]
    [DataRow(null, "LegacyScalarSolar", false)]
    [DataRow("LegacyScalarSolar", null, false)]
    [DataRow("ControlledNight", "SolarDriven", false)]
    [DataRow(null, "ControlledNight", true)]
    [DataRow("LegacyScalarSolar", null, true)]
    [DataRow("LegacyScalarSolar", "LegacyScalarSolar", false)]
    [DataRow(null, null, false)]
    [DataRow("LegacyScalarSolar", "LegacyScalarSolar", true)]
    public async Task IlluminationModeChange_IsRefusedAndNamed_AndTheSameLightingReRuns(string? installed, string? requested, bool before1722)
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        request = request with { IlluminationMode = installed };
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(before1722
                ? await InstallBefore1722Async(request, runner, owner)
                : await InstallResourceRequestAsync(request, runner, owner), root);
            var configuration = await File.ReadAllTextAsync(files.Configuration);
            var asked = request with { IlluminationMode = requested };

            var attempts = new[]
            {
                await RunInstallProgramAsync(asked with { DryRun = true }, runner, owner),
                await RunInstallProgramAsync(asked with { Cpus = 3m }, runner, owner),
                await RunInstallProgramAsync(asked with { Json = true }, runner, owner)
            };

            if (installed == requested)
            {
                Assert.IsTrue(attempts.All(static attempt => attempt.ExitCode == (int)InstallerExitCode.Done), string.Join("; ", attempts.Select(static attempt => attempt.Error)));
                return;
            }
            var named = $"--illumination-mode ({installed ?? "omitted"}, not {requested ?? "omitted"})";
            foreach (var attempt in attempts)
            {
                Assert.AreEqual((int)InstallerExitCode.Refused, attempt.ExitCode, attempt.Output);
                StringAssert.Contains(attempt.Message, named, StringComparison.Ordinal);
                attempt.AssertChangedNothing("a refused lighting change changed something");
            }
            StringAssert.StartsWith(attempts[1].Error, "Installation refused: ", StringComparison.Ordinal);
            Assert.AreEqual("refused", attempts[2].ErrorCode);
            Assert.AreEqual(configuration, await File.ReadAllTextAsync(files.Configuration));
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>Changed immutable answers are refused, and each named, before anything is adopted.</summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ChangedAnswers_AreRefusedBeforeAnyDefaultIsAdopted()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);

            var run = await RunInstallProgramAsync(
                request with { FriendlyName = "Other Camera", IlluminationMode = "ControlledNight", Cpus = 3m }, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Refused, run.ExitCode, run.Output);
            StringAssert.Contains(run.Error, "--friendly-name (Limits Camera, not Other Camera)", StringComparison.Ordinal);
            StringAssert.Contains(run.Error, "--illumination-mode (omitted, not ControlledNight)", StringComparison.Ordinal);
            Assert.IsFalse(run.Error.Contains("CloudAssessment", StringComparison.Ordinal), "a refused answer is refused before any adoption: " + run.Error);
            run.AssertChangedNothing("a refusal adopted something");
            Assert.HasCount(11, CameraConfigurationDefaultsTests.StepIds(await File.ReadAllTextAsync(files.Configuration)));
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// The unexplained class: a recorded identity no known generation writes, a hand-edited or unreadable camera-module.json,
    /// and records that disagree in a way no interrupted re-run leaves behind. Each is refused before any write or
    /// compose-up, exit 3, naming the file or record and the remedy, with <c>errorCode</c> <c>refused</c> under
    /// <c>--json</c>; the plan is refused too. Before this change (Refs #1779) a hand-edited file was silently rewritten (exit 0).
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("unknown-generation", false)]
    [DataRow("unknown-generation", true)]
    [DataRow("hand-edited", false)]
    [DataRow("hand-edited", true)]
    [DataRow("unreadable", false)]
    [DataRow("result-unknown", false)]
    [DataRow("result-ahead-of-manifest", true)]
    [DataRow("result-other-lighting", true)]
    public async Task UnexplainedConfiguration_IsRefusedNamingTheFileOrRecordAndChangesNothing(string fault, bool before1722)
    {
        ArgumentNullException.ThrowIfNull(fault);
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(before1722
                ? await InstallBefore1722Async(request, runner, owner)
                : await InstallResourceRequestAsync(request, runner, owner), root);
            var identity = (await files.ManifestAsync()).ApplicationIdentity;
            var (named, remedy) = fault switch
            {
                "unknown-generation" => (files.Configuration, "A newer installer may have written it"),
                "hand-edited" => (files.Configuration, "Move the edited camera-module.json aside"),
                "unreadable" => (files.Configuration, "Move the edited camera-module.json aside"),
                _ => (files.Result, "Restore the instance's records from a backup")
            };
            switch (fault)
            {
                case "unknown-generation":
                    await SetRecordedConfigurationAsync(files, new string('e', 64), manifest: true, result: true);
                    break;
                case "hand-edited":
                    var edited = JsonNode.Parse(await File.ReadAllTextAsync(files.Configuration))!.AsObject();
                    edited["module"]!["options"]!["seed"] = 2026;
                    await File.WriteAllTextAsync(files.Configuration, edited.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    break;
                case "unreadable":
                    await File.WriteAllTextAsync(files.Configuration, "{ \"agentId\": ");
                    break;
                case "result-unknown":
                    await SetRecordedConfigurationAsync(files, new string('e', 64), manifest: false, result: true);
                    break;
                case "result-ahead-of-manifest":
                    // A result on the newer generation behind a manifest on the older one: the commit order never does that.
                    await SetRecordedConfigurationAsync(files, CameraConfiguration.Generate(request, identity).Sha256, manifest: false, result: true);
                    break;
                case "result-other-lighting":
                    await SetRecordedConfigurationAsync(files, CameraConfigurationDefaults.Shipped[0]
                        .Generate(request with { IlluminationMode = "ControlledNight" }, identity).Sha256, manifest: false, result: true);
                    break;
            }
            var configuration = await File.ReadAllTextAsync(files.Configuration);
            var limits = request with { Cpus = 3m, Memory = "6g" };

            var attempts = new[]
            {
                await RunInstallProgramAsync(limits with { DryRun = true }, runner, owner),
                await RunInstallProgramAsync(limits, runner, owner),
                await RunInstallProgramAsync(request, runner, owner),
                await RunInstallProgramAsync(limits with { Json = true }, runner, owner)
            };

            foreach (var attempt in attempts)
            {
                Assert.AreEqual((int)InstallerExitCode.Refused, attempt.ExitCode, attempt.Output);
                StringAssert.Contains(attempt.Message, named, StringComparison.Ordinal);
                StringAssert.Contains(attempt.Message, remedy, StringComparison.Ordinal);
                attempt.AssertChangedNothing($"an unexplained difference ({fault}) changed something");
            }
            StringAssert.StartsWith(attempts[1].Error, "Installation refused: ", StringComparison.Ordinal);
            StringAssert.EndsWith(attempts[1].Error, "Nothing was changed.", StringComparison.Ordinal);
            Assert.AreEqual("refused", attempts[3].ErrorCode);
            Assert.AreEqual(configuration, await File.ReadAllTextAsync(files.Configuration), "an unexplained configuration is never rewritten");
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// The remedy the hand-edit refusal names works: with the edited file moved aside, the re-run writes the configuration
    /// the records describe, or adopts the new defaults for an instance installed before the cloud steps (Refs #1722).
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HandEditedConfigurationMovedAside_IsWrittenAgainByTheReRun(bool before1722)
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(before1722
                ? await InstallBefore1722Async(request, runner, owner)
                : await InstallResourceRequestAsync(request, runner, owner), root);
            var identity = (await files.ManifestAsync()).ApplicationIdentity;
            // The operator moves the edited file out of the instance.
            File.Delete(files.Configuration);

            var plan = await RunInstallProgramAsync(request with { DryRun = true }, runner, owner);
            var run = await RunInstallProgramAsync(request, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Done, plan.ExitCode, plan.Error);
            Assert.AreEqual((int)InstallerExitCode.Done, run.ExitCode, run.Error);
            var expected = before1722 ? $"adopts the installer's new defaults: {CloudChange}" : "written as its records describe";
            Assert.IsTrue(run.PlanLines.Any(line => line.StartsWith("  change ", StringComparison.Ordinal) &&
                line.Contains(files.Configuration, StringComparison.Ordinal) && line.Contains(expected, StringComparison.Ordinal)), run.Output);
            Assert.AreEqual(CameraConfiguration.Generate(request, identity).Json, await File.ReadAllTextAsync(files.Configuration));
            Assert.AreEqual(CameraConfiguration.Generate(request, identity).Sha256, (await files.ManifestAsync()).ConfigurationSha256);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// Whitespace and property order are not an unknown generation: a reformatted camera-module.json keeps its canonical
    /// identity, so the re-run writes the installer's exact bytes back, lists it, and recreates the container.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ReformattedConfiguration_IsWrittenBackAsRecordedNotRefused()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, null);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallResourceRequestAsync(request, runner, owner), root);
            var recorded = await File.ReadAllTextAsync(files.Configuration);
            var manifest = await files.ManifestAsync();
            var reordered = new JsonObject(JsonNode.Parse(recorded)!.AsObject().Reverse()
                .Select(static property => KeyValuePair.Create(property.Key, property.Value?.DeepClone()))).ToJsonString();
            await File.WriteAllTextAsync(files.Configuration, reordered);

            var plan = await RunInstallProgramAsync(request with { DryRun = true }, runner, owner);
            var run = await RunInstallProgramAsync(request, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Done, plan.ExitCode, plan.Error);
            Assert.AreEqual((int)InstallerExitCode.Done, run.ExitCode, run.Error);
            Assert.IsTrue(plan.PlanLines.Any(line => line.StartsWith("  change ", StringComparison.Ordinal) &&
                line.Contains(files.Configuration, StringComparison.Ordinal) && line.Contains("written as its records describe", StringComparison.Ordinal)), plan.Output);
            Assert.AreEqual(recorded, await File.ReadAllTextAsync(files.Configuration));
            Assert.AreEqual(manifest, await files.ManifestAsync(), "a formatting repair commits nothing");
            CollectionAssert.Contains(runner.ComposeUps[^1].ToArray(), "--force-recreate");
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    // ----- helpers -----

    private const string SyntheticChange = "LocalStorage retention: 7 → 14 days (test only)";

    /// <summary>A test-only generation on top of the shipped ones: local storage keeps frames for 14 days, not 7.</summary>
    private static CameraConfigurationGeneration SyntheticGeneration(bool requiresImage)
        => new(CameraConfigurationDefaults.Shipped.Count + 1, SyntheticChange, requiresImage, static (request, identity) =>
        {
            var basis = CameraConfigurationDefaults.Shipped[^1].Generate(request, identity);
            var document = JsonNode.Parse(basis.Json)!.AsObject();
            var storage = document["pipeline"]!["steps"]!.AsArray().Single(static step => (string?)step!["id"] == "LocalStorage")!;
            Assert.AreEqual(7, (int)storage["options"]!["retentionDays"]!);
            storage["options"]!["retentionDays"] = 14;
            var json = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            return basis with { Json = json, Sha256 = CameraConfigurationDefaultsTests.CanonicalSha256(json) };
        });

    /// <summary>The files a re-run reads and may change.</summary>
    private sealed record InstalledFiles(
        string Root, string Manifest, string Result, string State, string Configuration, string Compose, string OperatorSettings, string Record)
    {
        public static InstalledFiles Of(InstallationResult installed, string root) => new(
            root,
            Path.Combine(installed.InstanceRoot, "instance-manifest.json"),
            Path.Combine(installed.StateRoot, "deployment", "installation-result.json"),
            Path.Combine(installed.StateRoot, "deployment", "installation-state.json"),
            Path.Combine(installed.ConfigRoot, "camera-module.json"),
            Path.Combine(installed.ConfigRoot, "compose", "compose.yml"),
            CameraAgentStateLayout.OperatorSettingsPath(installed.StateRoot),
            Path.Combine(root, "operations", "install.json"));

        public Task<InstanceManifest> ManifestAsync() => CameraAgentLifecycleManager.ReadManifestAsync(Manifest, CancellationToken.None);

        public Task<InstallationResult> ResultAsync() => CameraAgentLifecycleManager.ReadResultAsync(Result, CancellationToken.None);
    }

    /// <summary>The contract labels of a CameraAgent image, declaring <paramref name="configurationDefaults"/> or none.</summary>
    private static Dictionary<string, string> DefaultsImageLabels(string? configurationDefaults)
    {
        var labels = new Dictionary<string, string>(ReleaseFixture.ContractLabels, StringComparer.Ordinal)
        {
            ["org.opencontainers.image.revision"] = new string('a', 40)
        };
        if (configurationDefaults is not null)
        {
            labels[CameraConfigurationDefaults.ImageLabel] = configurationDefaults;
        }
        return labels;
    }

    /// <summary>A Docker double whose CameraAgent image declares <paramref name="configurationDefaults"/>, or none.</summary>
    private static InstallerProcessRunner DefaultsDaemon(InstallRequest request, string root, string? configurationDefaults)
        => new(request.ImageReference, root, request.InstanceId!.Value)
        {
            RenderComposeFile = true,
            Labels = DefaultsImageLabels(configurationDefaults)
        };

    /// <summary>
    /// A completed install made as every installer before the cloud steps (Refs #1722) made it: under configuration-default generation 1 alone.
    /// </summary>
    private static async Task<InstallationResult> InstallBefore1722Async(
        InstallRequest request, InstallerProcessRunner runner, IOwnerBootstrapClient owner)
    {
        using var earlier = CameraConfigurationDefaults.Use([CameraConfigurationDefaults.Shipped[0]]);
        var installed = await InstallResourceRequestAsync(request, runner, owner);
        Assert.HasCount(11, CameraConfigurationDefaultsTests.StepIds(
            await File.ReadAllTextAsync(Path.Combine(installed.ConfigRoot, "camera-module.json"))));
        return installed;
    }

    /// <summary>
    /// Requires <paramref name="configuration"/>, written for <paramref name="identity"/>, to be the frozen output of
    /// generation <paramref name="generation"/>, byte for byte, apart from the application identity it names.
    /// </summary>
    private static void AssertFrozenGeneration(string configuration, Guid identity, int generation)
    {
        var fixture = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "installer-configuration-defaults", $"installer-defaults-{generation}.camera-module.json"));
        Assert.AreEqual(
            fixture,
            configuration.Replace(identity.ToString("D"), CameraConfigurationDefaultsTests.FixtureIdentity.ToString("D"), StringComparison.Ordinal) + "\n",
            $"the seeded configuration is installer-defaults-{generation}'s frozen output");
    }

    /// <summary>Adoption moves only the configuration identity: the rig profile, the schedule and the rest stay.</summary>
    private static void AssertUnchangedIdentities(
        InstanceManifest retained, InstallationResult retainedResult, InstanceManifest manifest, InstallationResult result)
    {
        foreach (var (rig, schedule, name, version) in new[]
                 {
                     (manifest.RigProfileSha256, manifest.ScheduleSha256, manifest.RigProfileName, manifest.RigProfileVersion),
                     (result.RigProfileSha256, result.ScheduleSha256, result.RigProfileName, result.RigProfileVersion)
                 })
        {
            Assert.AreEqual(retained.RigProfileSha256, rig);
            Assert.AreEqual(retained.ScheduleSha256, schedule);
            Assert.AreEqual(retained.RigProfileName, name);
            Assert.AreEqual(retained.RigProfileVersion, version);
        }
        Assert.AreEqual(manifest.ConfigurationSha256, result.ConfigurationSha256);
        Assert.AreEqual(retained with
        {
            ConfigurationSha256 = manifest.ConfigurationSha256,
            Resources = manifest.Resources,
            ComposeModelSha256 = manifest.ComposeModelSha256,
            UpdatedUtc = manifest.UpdatedUtc
        }, manifest, "adoption changes only the configuration identity, and the limits and model a limits change moves");
        Assert.AreEqual(retainedResult with
        {
            ConfigurationSha256 = result.ConfigurationSha256,
            ComposeModelSha256 = result.ComposeModelSha256
        }, result);
    }

    /// <summary>Stages an operator setting, as the active-rig settings do, and returns the file as it then is.</summary>
    private static async Task<string> StageOperatorSettingAsync(InstalledFiles files)
    {
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(files.OperatorSettings))!.AsObject();
        settings["Camera"] = new JsonObject { ["SkyLighting"] = "ControlledNight" };
        var text = settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        await File.WriteAllTextAsync(files.OperatorSettings, text);
        return text;
    }

    /// <summary>Records <paramref name="sha256"/> as the configuration identity in the manifest and/or the result.</summary>
    private static async Task SetRecordedConfigurationAsync(InstalledFiles files, string sha256, bool manifest, bool result)
    {
        foreach (var path in new[] { manifest ? files.Manifest : null, result ? files.Result : null }.OfType<string>())
        {
            var record = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            record["configurationSha256"] = sha256;
            await File.WriteAllTextAsync(path, record.ToJsonString());
        }
    }

    /// <summary>What <c>cameraagent install</c> printed, how it exited, and what it changed.</summary>
    private sealed record ProgramRun(int ExitCode, string Output, string Error, int ComposeUps, string[] Before, string[] After)
    {
        /// <summary>The plan, or the changes a run made, as the human output lists them, summary last.</summary>
        public IReadOnlyList<string> PlanLines =>
        [
            .. Output.Split('\n').Select(static line => line.TrimEnd('\r'))
                .SkipWhile(static line => line is not ("Plan:" or "Changes:")).Skip(1)
                .Where(static line => line.Length > 0)
        ];

        /// <summary>The result <c>--json</c> printed.</summary>
        public InstallationResult Reported => JsonSerializer.Deserialize(Output, DeploymentJsonContext.Default.InstallationResult)!;

        /// <summary>The error message, as printed, or as decoded from the error <c>--json</c> printed.</summary>
        public string Message
        {
            get
            {
                if (!Error.StartsWith('{')) return Error;
                using var document = JsonDocument.Parse(Error);
                return document.RootElement.GetProperty("message").GetString()!;
            }
        }

        /// <summary>The <c>errorCode</c> of the error <c>--json</c> printed.</summary>
        public string ErrorCode
        {
            get
            {
                using var document = JsonDocument.Parse(Error);
                return document.RootElement.GetProperty("errorCode").GetString()!;
            }
        }

        /// <summary>Requires the run to have left every file as it was, mtime and inode included, and deployed nothing.</summary>
        public void AssertChangedNothing(string what)
        {
            FileSystemActivity.AssertUnchanged(Before, After, what);
            Assert.AreEqual(0, ComposeUps, what);
        }
    }

    /// <summary>Runs <c>cameraagent install</c> for <paramref name="request"/> through the real program entry point.</summary>
    private static async Task<ProgramRun> RunInstallProgramAsync(
        InstallRequest request, InstallerProcessRunner runner, IOwnerBootstrapClient owner)
    {
        var before = FileSystemActivity.Snapshot(request.ProductRoot);
        var composeUps = runner.ComposeUpCount;
        var services = ProgramServices.Production with
        {
            // The request is the parsed command line; nothing reads the host policy under /etc.
            Parse = _ => new InstallDeploymentCommand(request),
            Install = (install, token) => CameraAgentInstaller.InstallAsync(
                install, runner, _ => owner, RuntimeUid, RuntimeGid, token,
                portAvailabilityProbe: new RecordingPortProbe().Probe, hostEnvironment: new FakeHostEnvironment()),
            Wizard = null
        };
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        int exitCode;
        try
        {
            exitCode = await Program.RunAsync(["cameraagent", "install"], services, CancellationToken.None);
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
        return new ProgramRun(
            exitCode, output.ToString(), error.ToString().Trim(), runner.ComposeUpCount - composeUps, before,
            FileSystemActivity.Snapshot(request.ProductRoot));
    }
}
