using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Regressions from the formal review of Refs #1779 at <c>7fea55f9</c>: an adoption interrupted by an installer whose
/// newest generation was older than this installer's (M1); changed-answer refusals that lost options to the 256-character
/// diagnostic limit (M2); and a record whose rig profile or schedule description no longer matches its configuration,
/// which failed with exit 1 instead of a refusal (M3).
/// </summary>
public sealed partial class InstallerFlowTests
{
    /// <summary>
    /// M1. An adoption of generation 2 interrupted after it published camera-module.json, re-run once generation 3 is
    /// registered, is carried on to generation 3 instead of being refused as a hand edit. A file of a generation older than
    /// the recorded one, or of a newer generation with other lighting, is still refused: no interrupted adoption writes it.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("newer-generation")]
    [DataRow("older-generation")]
    [DataRow("newer-generation-other-lighting")]
    public async Task AnAdoptionInterruptedBeforeANewerGenerationWasRegistered_IsCarriedOnToTheNewest(string written)
    {
        ArgumentNullException.ThrowIfNull(written);
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(written == "older-generation"
                ? await InstallResourceRequestAsync(request, runner, owner)
                : await InstallBefore1722Async(request, runner, owner), root);
            var retained = await files.ManifestAsync();
            var retainedResult = await files.ResultAsync();
            // The interrupted run wrote the file and lost power before its commit; no handler ran.
            var interrupted = written switch
            {
                "newer-generation" => CameraConfigurationDefaults.Shipped[1].Generate(request, retained.ApplicationIdentity),
                "older-generation" => CameraConfigurationDefaults.Shipped[0].Generate(request, retained.ApplicationIdentity),
                _ => CameraConfigurationDefaults.Shipped[1].Generate(request with { IlluminationMode = "ControlledNight" }, retained.ApplicationIdentity)
            };
            await File.WriteAllTextAsync(files.Configuration, interrupted.Json);
            using var registry = CameraConfigurationDefaults.Use([.. CameraConfigurationDefaults.Shipped, SyntheticGeneration(requiresImage: false)]);
            var newest = CameraConfiguration.Generate(request, retained.ApplicationIdentity);

            var plan = await RunInstallProgramAsync(request with { DryRun = true }, runner, owner);
            var run = await RunInstallProgramAsync(request, runner, owner);

            if (written != "newer-generation")
            {
                foreach (var attempt in new[] { plan, run })
                {
                    Assert.AreEqual((int)InstallerExitCode.Refused, attempt.ExitCode, attempt.Output);
                    StringAssert.Contains(attempt.Message, files.Configuration, StringComparison.Ordinal);
                    StringAssert.Contains(attempt.Message, "has been edited", StringComparison.Ordinal);
                    attempt.AssertChangedNothing("an unexplained file changed something");
                }
                Assert.AreEqual(interrupted.Json, await File.ReadAllTextAsync(files.Configuration));
                return;
            }
            Assert.AreEqual((int)InstallerExitCode.Done, plan.ExitCode, plan.Error);
            plan.AssertChangedNothing("the plan changed something");
            Assert.AreEqual((int)InstallerExitCode.Done, run.ExitCode, run.Error);
            foreach (var attempt in new[] { plan, run })
            {
                Assert.IsTrue(attempt.PlanLines.Any(line => line.Contains(files.Configuration, StringComparison.Ordinal) &&
                    line.EndsWith($"adopts the installer's new defaults: {CloudChange}; {SyntheticChange})", StringComparison.Ordinal)), attempt.Output);
            }
            Assert.AreEqual(1, run.ComposeUps);
            CollectionAssert.Contains(runner.ComposeUps[^1].ToArray(), "--force-recreate");
            Assert.AreEqual(newest.Json, await File.ReadAllTextAsync(files.Configuration));
            Assert.AreEqual(newest.Json, runner.LastComposeUpConfiguration);
            var manifest = await files.ManifestAsync();
            Assert.AreEqual(newest.Sha256, manifest.ConfigurationSha256);
            AssertUnchangedIdentities(retained, retainedResult, manifest, await files.ResultAsync());
            Assert.IsFalse((await InstallResourceRequestAsync(request, runner, owner)).Plan!.HasChanges);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// M2. A re-run whose answers differ in many options names every one of them, each with its installed and requested
    /// value, and the remedy, however long the list: the refusal is not cut at the diagnostic limit. Human and JSON alike.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task ChangedAnswers_AreAllNamedWithBothValues_HoweverLongTheList()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new InstallerOwnerClient();
        try
        {
            _ = await InstallResourceRequestAsync(request, runner, owner);
            var changed = request with
            {
                FriendlyName = "North Dome All-Sky Camera Above The Eastern Ridge",
                OwnerEmail = "observatory-operations@example.test",
                Port = request.Port == 5131 ? 5132 : 5131,
                LatitudeDegrees = 35.1875,
                LongitudeDegrees = -114.0525,
                ElevationMeters = 1234.5,
                TimeZoneId = "America/Phoenix",
                IlluminationMode = "ControlledNight"
            };
            string[] named =
            [
                "--friendly-name (Limits Camera, not North Dome All-Sky Camera Above The Eastern Ridge)",
                "--owner-email (owner@example.test, not observatory-operations@example.test)",
                $"--port ({request.Port}, not {changed.Port})",
                "--latitude (0, not 35.1875)",
                "--longitude (0, not -114.0525)",
                "--elevation (0, not 1234.5)",
                "--time-zone (UTC, not America/Phoenix)",
                "--illumination-mode (omitted, not ControlledNight)"
            ];

            var human = await RunInstallProgramAsync(changed, runner, owner);
            var json = await RunInstallProgramAsync(changed with { Json = true }, runner, owner);
            var plan = await RunInstallProgramAsync(changed with { DryRun = true }, runner, owner);

            foreach (var attempt in new[] { human, json, plan })
            {
                Assert.AreEqual((int)InstallerExitCode.Refused, attempt.ExitCode, attempt.Output);
                Assert.IsGreaterThan(256, attempt.Message.Length, "the list is longer than the diagnostic limit");
                foreach (var option in named)
                {
                    StringAssert.Contains(attempt.Message, option, StringComparison.Ordinal);
                }
                StringAssert.Contains(attempt.Message,
                    "A re-run may change only --cpus and --memory; give the answers it was installed with.", StringComparison.Ordinal);
                attempt.AssertChangedNothing("a refused re-run changed something");
            }
            StringAssert.StartsWith(human.Error, "Installation refused: Instance ", StringComparison.Ordinal);
            StringAssert.EndsWith(human.Error, "Nothing was changed.", StringComparison.Ordinal);
            Assert.AreEqual("refused", json.ErrorCode);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// M3. A manifest or result whose rig profile or schedule description no longer matches the configuration it records
    /// is refused cleanly, exit 3, naming the record, the field and both values, with nothing changed, whether or not an
    /// adoption is pending. Before the review the manifest case failed with exit 1 (an index out of range) and the result
    /// case with the generic "differs from its retained result".
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("manifest", "rigProfileName", false)]
    [DataRow("manifest", "rigProfileName", true)]
    [DataRow("manifest", "scheduleState", false)]
    [DataRow("manifest", "scheduleSchemaVersion", true)]
    [DataRow("result", "rigProfileName", false)]
    [DataRow("result", "rigProfileVersion", true)]
    public async Task ARecordedRigOrScheduleDescriptionThatDisagrees_IsRefusedAndChangesNothing(string record, string field, bool before1722)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(field);
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(before1722
                ? await InstallBefore1722Async(request, runner, owner)
                : await InstallResourceRequestAsync(request, runner, owner), root);
            var path = record == "manifest" ? files.Manifest : files.Result;
            var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            var recorded = (string)node[field]!;
            node[field] = "edited-" + recorded;
            await File.WriteAllTextAsync(path, node.ToJsonString());
            var configuration = await File.ReadAllTextAsync(files.Configuration);

            var attempts = new[]
            {
                await RunInstallProgramAsync(request with { DryRun = true }, runner, owner),
                await RunInstallProgramAsync(request with { Cpus = 3m }, runner, owner),
                await RunInstallProgramAsync(request with { Json = true }, runner, owner)
            };

            foreach (var attempt in attempts)
            {
                Assert.AreEqual((int)InstallerExitCode.Refused, attempt.ExitCode, attempt.Output + attempt.Error);
                StringAssert.Contains(attempt.Message, path, StringComparison.Ordinal);
                StringAssert.Contains(attempt.Message, $"{field} edited-{recorded}, not {recorded}", StringComparison.Ordinal);
                StringAssert.Contains(attempt.Message, "Restore the instance's records from a backup", StringComparison.Ordinal);
                attempt.AssertChangedNothing($"a disagreeing {record} {field} changed something");
            }
            Assert.AreEqual("refused", attempts[2].ErrorCode);
            Assert.AreEqual(configuration, await File.ReadAllTextAsync(files.Configuration));
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// M3, the adoption side: a registered generation that would move the rig profile is refused by name, exit 3, with
    /// nothing changed; the instance stays on the generation it is on.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task AGenerationThatWouldMoveTheRigProfile_IsRefusedByName()
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallResourceRequestAsync(request, runner, owner), root);
            var moving = new CameraConfigurationGeneration(CameraConfigurationDefaults.Shipped.Count + 1, "rig profile: v4 → v5 (test only)", false,
                static (installed, identity) =>
                {
                    var basis = CameraConfigurationDefaults.Shipped[^1].Generate(installed, identity);
                    var document = JsonNode.Parse(basis.Json)!.AsObject();
                    document["rig"]!["profileVersion"] = "installer-virtualsky-v5";
                    var json = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    return basis with
                    {
                        Json = json,
                        Sha256 = CameraConfigurationDefaultsTests.CanonicalSha256(json),
                        RigProfileVersion = "installer-virtualsky-v5"
                    };
                });
            using var registry = CameraConfigurationDefaults.Use([.. CameraConfigurationDefaults.Shipped, moving]);

            var run = await RunInstallProgramAsync(request, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Refused, run.ExitCode, run.Output);
            StringAssert.Contains(run.Message, "adopting the installer's configuration defaults installer-defaults-3 would change its rig profile or schedule identity", StringComparison.Ordinal);
            run.AssertChangedNothing("a refused rig move changed something");
            Assert.AreEqual(CameraConfigurationDefaults.Shipped[^1].Id,
                CameraConfigurationDefaults.Recognize(request, (await files.ManifestAsync()).ApplicationIdentity,
                    (await files.ManifestAsync()).ConfigurationSha256, (await files.ManifestAsync()).RigProfileSha256,
                    (await files.ManifestAsync()).ScheduleSha256)!.Generation.Id);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }
}
