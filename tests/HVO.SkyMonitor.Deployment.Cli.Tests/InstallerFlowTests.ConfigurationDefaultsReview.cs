using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Regressions from the formal reviews of Refs #1779. At <c>7fea55f9</c>: an adoption interrupted by an installer whose
/// newest generation was older than this installer's (M1); changed-answer refusals that lost options to the 256-character
/// diagnostic limit (M2); and a record whose rig profile or schedule description no longer matches its configuration,
/// which failed with exit 1 instead of a refusal (M3). At <c>40d7bf81</c>: malformed configuration metadata in a completed
/// instance's records, which failed with exit 1 (M4); and adoption failures whose cause and remedy were cut at the same
/// limit (M-new). At <c>adb401ba</c>: duplicated configuration metadata, which still failed with exit 1 (S1); and an
/// adoption failure of an instance recorded without limits, whose cause was lost to an unexpected error (S2). At
/// <c>12eb2146</c>: duplicated configuration metadata whose values are all text, which the typed reader accepted and the
/// re-run adopted through (S1b).
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

    /// <summary>
    /// M4. Malformed or unreadable camera-configuration metadata in a completed instance's manifest or result is a
    /// configuration the installer cannot explain: the plan and the run are refused with exit 3, naming the record, the
    /// field and the remedy, <c>errorCode</c> <c>refused</c> under <c>--json</c>, and nothing changes. Before the review
    /// the strict record reader's failure ended every one in exit 1.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("manifest", "configurationSha256", "text", "not a lowercase SHA-256")]
    [DataRow("manifest", "configurationSha256", "uppercase", "not a lowercase SHA-256")]
    [DataRow("manifest", "rigProfileName", "empty", "empty")]
    [DataRow("manifest", "scheduleSha256", "number", "not text")]
    [DataRow("result", "configurationSha256", "null", "missing")]
    [DataRow("result", "scheduleState", "empty", "empty")]
    [DataRow("result", "rigProfileSha256", "removed", "missing")]
    public async Task MalformedConfigurationMetadata_IsRefusedNamingTheRecordAndChangesNothing(
        string record, string field, string edit, string problem)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(edit);
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);
            var path = record == "manifest" ? files.Manifest : files.Result;
            var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            switch (edit)
            {
                case "text": node[field] = "not-a-sha256"; break;
                case "uppercase": node[field] = ((string)node[field]!).ToUpperInvariant(); break;
                case "empty": node[field] = string.Empty; break;
                case "number": node[field] = 42; break;
                case "null": node[field] = null; break;
                case "removed": Assert.IsTrue(node.Remove(field)); break;
            }
            await File.WriteAllTextAsync(path, node.ToJsonString());
            var configuration = await File.ReadAllTextAsync(files.Configuration);

            var attempts = new[]
            {
                await RunInstallProgramAsync(request with { DryRun = true }, runner, owner),
                await RunInstallProgramAsync(request with { Cpus = 3m, Memory = "6g" }, runner, owner),
                await RunInstallProgramAsync(request, runner, owner),
                await RunInstallProgramAsync(request with { Json = true }, runner, owner)
            };

            foreach (var attempt in attempts)
            {
                Assert.AreEqual((int)InstallerExitCode.Refused, attempt.ExitCode, attempt.Output + attempt.Error);
                StringAssert.Contains(attempt.Message, path, StringComparison.Ordinal);
                StringAssert.Contains(attempt.Message, files.Configuration, StringComparison.Ordinal);
                StringAssert.Contains(attempt.Message, $"{field} ({problem})", StringComparison.Ordinal);
                StringAssert.Contains(attempt.Message, "Restore the instance's records from a backup", StringComparison.Ordinal);
                attempt.AssertChangedNothing($"malformed {record} {field} changed something");
            }
            StringAssert.StartsWith(attempts[1].Error, "Installation refused: ", StringComparison.Ordinal);
            StringAssert.EndsWith(attempts[1].Error, "Nothing was changed.", StringComparison.Ordinal);
            Assert.AreEqual("refused", attempts[3].ErrorCode);
            Assert.AreEqual(configuration, await File.ReadAllTextAsync(files.Configuration));
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// M-new. The issue's main path, an adoption with new limits, failing before its commit: the failure, exit 1, names
    /// what it changed, the cause and what was restored, whole, human and JSON; when the restore fails too, it names the
    /// restore's cause and the remedy. Before the review the message was cut at 256 characters, losing the cause and the
    /// remedy.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task AdoptionWithNewLimitsThatFails_NamesTheCauseAndTheRemedyWhole(bool restoreAlsoFails, bool json)
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new AdoptionOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);
            var before = await RecordsAsync(files);
            owner.FailOnVerification = owner.VerificationAttempts + 1;
            if (restoreAlsoFails)
            {
                runner.FailComposeUpNumber = runner.ComposeUpCount + 2;
            }

            var failed = await RunInstallProgramAsync(request with { Cpus = 3m, Memory = "6g", Json = json }, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Failed, failed.ExitCode, failed.Output + failed.Error);
            string message;
            if (json)
            {
                Assert.AreEqual("installation-failed", failed.ErrorCode);
                message = failed.Message;
            }
            else
            {
                StringAssert.StartsWith(failed.Error, "Installation failed: ", StringComparison.Ordinal);
                message = failed.Error["Installation failed: ".Length..];
            }
            Assert.IsGreaterThan(256, message.Length, "the whole failure is longer than the diagnostic limit");
            if (restoreAlsoFails)
            {
                StringAssert.StartsWith(message,
                    $"Changing the container limits and adopting the installer's new configuration defaults ({PlainCloudChange}) failed, and restoring the recorded limits and configuration also failed: ",
                    StringComparison.Ordinal);
                StringAssert.Contains(message, "cannot recreate the container", StringComparison.Ordinal);
                StringAssert.EndsWith(message, "Re-run the same install to carry on.", StringComparison.Ordinal);
            }
            else
            {
                Assert.AreEqual(
                    $"Changing the container limits to 3 CPUs, 6 GiB of memory and 256 PIDs and adopting the installer's new configuration defaults ({PlainCloudChange}) failed, so the recorded limits and configuration were restored: injected verification failure",
                    message);
                CollectionAssert.AreEqual(before, await RecordsAsync(files));
            }

            // Re-running the same install carries on, as the failure says.
            var carriedOn = await RunInstallProgramAsync(request with { Cpus = 3m, Memory = "6g" }, runner, owner);
            Assert.AreEqual((int)InstallerExitCode.Done, carriedOn.ExitCode, carriedOn.Error);
            Assert.AreEqual(CameraConfiguration.Generate(request, (await files.ManifestAsync()).ApplicationIdentity).Sha256,
                (await files.ManifestAsync()).ConfigurationSha256);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// S1. A completed instance's manifest or result that names <c>configurationSha256</c> twice, the first time not as
    /// text, is malformed configuration metadata: refused with exit 3, naming the record, the field and the remedy,
    /// <c>errorCode</c> <c>refused</c> under <c>--json</c>, nothing changed, by the plan and the run alike. Before the review
    /// the duplicate escaped the metadata scan and ended in exit 1.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("manifest", "plan")]
    [DataRow("manifest", "run")]
    [DataRow("manifest", "json")]
    [DataRow("result", "plan")]
    [DataRow("result", "run")]
    [DataRow("result", "json")]
    public async Task DuplicatedConfigurationMetadata_IsRefusedNamingTheRecordAndChangesNothing(string record, string mode)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(mode);
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);
            var path = record == "manifest" ? files.Manifest : files.Result;
            var text = await File.ReadAllTextAsync(path);
            var property = System.Text.RegularExpressions.Regex.Matches(
                text, "\"configurationSha256\"\\s*:\\s*\"[0-9a-f]{64}\"", System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromSeconds(5));
            Assert.HasCount(1, property);
            // Every other member stays as it was; the valid identity is kept, after a first one that is not text.
            await File.WriteAllTextAsync(path, text.Replace(
                property[0].Value, "\"configurationSha256\":42," + property[0].Value, StringComparison.Ordinal));
            var configuration = await File.ReadAllTextAsync(files.Configuration);

            var attempt = await RunInstallProgramAsync(mode switch
            {
                "plan" => request with { DryRun = true },
                "json" => request with { Json = true },
                _ => request with { Cpus = 3m, Memory = "6g" }
            }, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Refused, attempt.ExitCode, attempt.Output + attempt.Error);
            StringAssert.Contains(attempt.Message, path, StringComparison.Ordinal);
            StringAssert.Contains(attempt.Message, "configurationSha256 (duplicated)", StringComparison.Ordinal);
            StringAssert.Contains(attempt.Message, "Restore the instance's records from a backup", StringComparison.Ordinal);
            attempt.AssertChangedNothing($"duplicated {record} configuration metadata changed something");
            if (mode == "json")
            {
                Assert.AreEqual("refused", attempt.ErrorCode);
            }
            else
            {
                StringAssert.StartsWith(attempt.Error, "Installation refused: ", StringComparison.Ordinal);
                StringAssert.EndsWith(attempt.Error, "Nothing was changed.", StringComparison.Ordinal);
            }
            Assert.AreEqual(configuration, await File.ReadAllTextAsync(files.Configuration));
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// S2. An instance an earlier installer recorded without limits, re-run with none, keeps none; when its adoption fails
    /// before the commit, the failure names the real cause and what was restored (or, when the restore fails too, the
    /// remedy), exit 1, and a re-run carries on. Before the review it failed with an unexpected null-argument error
    /// instead, after the restore had succeeded.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AdoptionFailureOfAnInstanceRecordedWithoutLimits_NamesTheRealCause(bool restoreAlsoFails)
    {
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        // One fixed rendered model, so the manifest edited to look like an earlier installer's still validates, as the
        // existing control for instances recorded without limits does.
        var runner = new InstallerProcessRunner(request.ImageReference, root, request.InstanceId!.Value)
        {
            Labels = DefaultsImageLabels(CloudDefaults)
        };
        var owner = new AdoptionOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);
            var manifestNode = JsonNode.Parse(await File.ReadAllTextAsync(files.Manifest))!.AsObject();
            Assert.IsTrue(manifestNode.Remove("resources"));
            await File.WriteAllTextAsync(files.Manifest, manifestNode.ToJsonString());
            // The earlier installer's own re-run puts back the original limit lines the records now describe.
            using (CameraConfigurationDefaults.Use([CameraConfigurationDefaults.Shipped[0]]))
            {
                _ = await InstallResourceRequestAsync(request, runner, owner);
            }
            Assert.IsNull((await files.ManifestAsync()).Resources);
            var before = await RecordsAsync(files);
            owner.FailOnVerification = owner.VerificationAttempts + 1;
            if (restoreAlsoFails)
            {
                runner.FailComposeUpNumber = runner.ComposeUpCount + 2;
            }

            var failed = await RunInstallProgramAsync(request, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Failed, failed.ExitCode, failed.Output + failed.Error);
            StringAssert.StartsWith(failed.Error, "Installation failed: ", StringComparison.Ordinal);
            var message = failed.Error["Installation failed: ".Length..];
            Assert.IsFalse(message.Contains("Unexpected", StringComparison.Ordinal), message);
            if (restoreAlsoFails)
            {
                StringAssert.StartsWith(message,
                    $"Adopting the installer's new configuration defaults ({PlainCloudChange}) failed, and restoring the recorded configuration also failed: ",
                    StringComparison.Ordinal);
                StringAssert.Contains(message, "cannot recreate the container", StringComparison.Ordinal);
                StringAssert.EndsWith(message, "Re-run the same install to carry on.", StringComparison.Ordinal);
            }
            else
            {
                Assert.AreEqual(
                    $"Adopting the installer's new configuration defaults ({PlainCloudChange}) failed, so the recorded configuration was restored: injected verification failure",
                    message);
                CollectionAssert.AreEqual(before, await RecordsAsync(files), "the restore put every record back");
                Assert.AreEqual(before[0], runner.LastComposeUpConfiguration, "the container was put back on the recorded configuration");
            }

            var carriedOn = await RunInstallProgramAsync(request, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Done, carriedOn.ExitCode, carriedOn.Error);
            var manifest = await files.ManifestAsync();
            Assert.IsNull(manifest.Resources, "an instance recorded without limits keeps none");
            Assert.AreEqual(CameraConfiguration.Generate(request, manifest.ApplicationIdentity).Sha256, manifest.ConfigurationSha256);
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }

    /// <summary>
    /// S1b. A completed instance's manifest or result that names <c>configurationSha256</c> twice, both times as a valid
    /// lowercase SHA-256, whether the two agree or not, reads as valid to the typed reader, which keeps the last. No
    /// installer writes it, so it is refused like any other malformed configuration metadata: exit 3, naming the record,
    /// the field and the remedy, <c>errorCode</c> <c>refused</c> under <c>--json</c>, and nothing changes, by the plan and
    /// the run alike. Before the review every one exited 0, and each run adopted and recreated the container.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("manifest", "identical", "plan")]
    [DataRow("manifest", "identical", "run")]
    [DataRow("manifest", "identical", "json")]
    [DataRow("manifest", "conflicting", "plan")]
    [DataRow("manifest", "conflicting", "run")]
    [DataRow("manifest", "conflicting", "json")]
    [DataRow("result", "identical", "plan")]
    [DataRow("result", "identical", "run")]
    [DataRow("result", "identical", "json")]
    [DataRow("result", "conflicting", "plan")]
    [DataRow("result", "conflicting", "run")]
    [DataRow("result", "conflicting", "json")]
    public async Task DuplicatedTextConfigurationMetadata_IsRefusedNamingTheRecordAndChangesNothing(
        string record, string duplicate, string mode)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(duplicate);
        ArgumentNullException.ThrowIfNull(mode);
        var bundle = RequireProductionCatalogBundle();
        var (request, root) = ResourceRequest(bundle, Guid.NewGuid());
        var runner = DefaultsDaemon(request, root, CloudDefaults);
        var owner = new InstallerOwnerClient();
        try
        {
            var files = InstalledFiles.Of(await InstallBefore1722Async(request, runner, owner), root);
            var path = record == "manifest" ? files.Manifest : files.Result;
            var text = await File.ReadAllTextAsync(path);
            var property = System.Text.RegularExpressions.Regex.Matches(
                text, "\"configurationSha256\"\\s*:\\s*\"[0-9a-f]{64}\"", System.Text.RegularExpressions.RegexOptions.None,
                TimeSpan.FromSeconds(5));
            Assert.HasCount(1, property);
            // Both values are text and valid SHA-256s; the typed reader keeps the last, the installed identity.
            var first = duplicate == "identical"
                ? property[0].Value
                : "\"configurationSha256\":\"" + new string('e', 64) + "\"";
            await File.WriteAllTextAsync(path, text.Replace(property[0].Value, first + "," + property[0].Value, StringComparison.Ordinal));
            var configuration = await File.ReadAllTextAsync(files.Configuration);

            var attempt = await RunInstallProgramAsync(mode switch
            {
                "plan" => request with { DryRun = true },
                "json" => request with { Json = true },
                _ => request
            }, runner, owner);

            Assert.AreEqual((int)InstallerExitCode.Refused, attempt.ExitCode, attempt.Output + attempt.Error);
            StringAssert.Contains(attempt.Message, path, StringComparison.Ordinal);
            StringAssert.Contains(attempt.Message, files.Configuration, StringComparison.Ordinal);
            StringAssert.Contains(attempt.Message, "configurationSha256 (duplicated)", StringComparison.Ordinal);
            StringAssert.Contains(attempt.Message, "Restore the instance's records from a backup", StringComparison.Ordinal);
            attempt.AssertChangedNothing($"{duplicate} duplicated {record} configuration metadata changed something");
            Assert.AreEqual(configuration, await File.ReadAllTextAsync(files.Configuration), "camera-module.json was rewritten");
            if (mode == "json")
            {
                Assert.AreEqual("refused", attempt.ErrorCode);
            }
            else
            {
                StringAssert.StartsWith(attempt.Error, "Installation refused: ", StringComparison.Ordinal);
                StringAssert.EndsWith(attempt.Error, "Nothing was changed.", StringComparison.Ordinal);
            }
        }
        finally
        {
            DeleteResourceRoot(root);
        }
    }
}
