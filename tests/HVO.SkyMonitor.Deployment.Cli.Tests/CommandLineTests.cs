using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class CommandLineTests
{
    private static readonly string[] ValidArguments =
    [
        "cameraagent", "install",
        "--friendly-name", "North Camera",
        "--owner-email", "admin@example.test",
        "--catalog-bundle", "/srv/hvo/catalog.bundle",
        "--image-ref", $"ghcr.io/example/cameraagent@sha256:{new string('a', 64)}"
    ];

    [TestMethod]
    public void Parse_ValidArguments_ReturnsCanonicalRequest()
    {
        var request = CommandLine.Parse(ValidArguments);

        Assert.AreEqual("North Camera", request.FriendlyName);
        Assert.AreEqual("127.0.0.1", request.BindAddress);
        Assert.AreEqual(5130, request.Port);
        Assert.AreEqual(InstallRequest.DefaultProductRoot, request.ProductRoot);
        Assert.AreEqual(CameraAgentReplayProfile.InProcess, request.ReplayProfile);
    }

    [TestMethod]
    public void Parse_FriendlyNameLongerThanTheCameraAccepts_IsRejected()
    {
        var atBound = ValidArguments.ToArray();
        atBound[3] = new string('n', InstallRequest.MaximumFriendlyNameLength);
        var overBound = ValidArguments.ToArray();
        overBound[3] = new string('n', InstallRequest.MaximumFriendlyNameLength + 1);

        Assert.AreEqual(atBound[3], CommandLine.Parse(atBound).FriendlyName);
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(overBound));
        StringAssert.Contains(exception.Message, "at most 80 characters", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Parse_LocalReplayRunnerProfile_IsExplicitlySelectable()
    {
        var request = CommandLine.Parse(ValidArguments.Concat([
            "--replay-profile", "local-runner"
        ]).ToArray());

        Assert.AreEqual(CameraAgentReplayProfile.LocalRunner, request.ReplayProfile);
    }

    [TestMethod]
    public void Parse_UnknownReplayProfile_IsRejected()
    {
        var arguments = ValidArguments.Concat(["--replay-profile", "remote"]).ToArray();

        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Parse_ConfigAndArguments_ProduceEquivalentRequests()
    {
        var expected = CommandLine.Parse(ValidArguments);
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(expected, InstallRequestJsonContext.Default.InstallRequest));

            var actual = CommandLine.Parse(["cameraagent", "install", "--config", path]);

            Assert.AreEqual(expected, actual);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void Parse_WithoutOwnerEmail_UsesTheNonRoutableDefaultAndTheDefaultPassword()
    {
        var arguments = ValidArguments.Where((_, index) => index is not (4 or 5)).ToArray();
        CollectionAssert.DoesNotContain(arguments, "--owner-email");

        var request = CommandLine.Parse(arguments);

        Assert.AreEqual("owner@cameraagent.local", request.OwnerEmail);
        Assert.IsNull(request.PasswordFile);
        Assert.IsFalse(request.GeneratePassword);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Parse_ConfigWithoutOwnerEmail_UsesTheNonRoutableDefault()
    {
        var path = Path.GetTempFileName();
        try
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(
                CommandLine.Parse(ValidArguments),
                InstallRequestJsonContext.Default.InstallRequest))!.AsObject();
            Assert.IsTrue(json.Remove("ownerEmail"));
            File.WriteAllText(path, json.ToJsonString());

            var request = CommandLine.Parse(["cameraagent", "install", "--config", path]);

            Assert.AreEqual("owner@cameraagent.local", request.OwnerEmail);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    [DataRow("owner")]
    [DataRow("owner@localhost")]
    [DataRow("")]
    public void Parse_SuppliedOwnerEmail_MustStillBeAnEmailAddress(string ownerEmail)
    {
        var arguments = ValidArguments.ToArray();
        arguments[5] = ownerEmail;

        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));

        StringAssert.Contains(exception.Message, "--owner-email", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Parse_UnknownOption_FailsClosed()
    {
        var arguments = ValidArguments.Concat(["--password", "secret"]).ToArray();

        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));

        StringAssert.Contains(exception.Message, "Unknown option", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Parse_NonLoopbackWithoutAcknowledgement_IsRejected()
    {
        var arguments = ValidArguments.Concat(["--bind-address", "0.0.0.0"]).ToArray();

        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));

        StringAssert.Contains(exception.Message, "--acknowledge-plaintext-http", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Parse_MutableImageTag_IsRejected()
    {
        var arguments = ValidArguments.ToArray();
        arguments[^1] = "ghcr.io/example/cameraagent:latest";

        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));

        StringAssert.Contains(exception.Message, "immutable", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Parse_ResumeWithoutInstanceId_IsRejected()
    {
        var arguments = ValidArguments.Concat(["--resume"]).ToArray();

        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));

        StringAssert.Contains(exception.Message, "--instance-id", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Parse_ShortImageDigest_IsRejected()
    {
        var arguments = ValidArguments.ToArray();
        arguments[^1] = "sha256:abc";

        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));
    }

    [TestMethod]
    public void Parse_GenerateAndPasswordFile_AreMutuallyExclusive()
    {
        var arguments = ValidArguments.Concat([
            "--password-file", "/srv/hvo/password",
            "--generate-password"
        ]).ToArray();

        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));

        StringAssert.Contains(exception.Message, "cannot be combined", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Parse_SignedLocalCatalog_UsesLocalChannelWithoutNetwork()
    {
        var arguments = ValidArguments
            .Where(static value => value != "--catalog-bundle" && value != "/srv/hvo/catalog.bundle")
            .Concat(["--catalog-manifest", "/srv/hvo/catalog-manifest.json", "--no-download"])
            .ToArray();

        var request = CommandLine.Parse(arguments);

        Assert.AreEqual(DistributionChannel.Local, request.Channel);
        Assert.IsTrue(request.NoDownload);
        Assert.IsNull(request.CatalogBundle);
    }

    [TestMethod]
    public void Parse_StableCatalogManifest_RequiresHttpsLocator()
    {
        var arguments = ValidArguments
            .Where(static value => value != "--catalog-bundle" && value != "/srv/hvo/catalog.bundle")
            .Concat(["--channel", "stable", "--catalog-manifest", "https://downloads.example/catalog/catalog-manifest.json"])
            .ToArray();

        var request = CommandLine.Parse(arguments);

        Assert.AreEqual(DistributionChannel.Stable, request.Channel);
    }

    [TestMethod]
    public void Parse_NonLocalChannelWithLocalManifest_IsRejected()
    {
        var arguments = ValidArguments
            .Where(static value => value != "--catalog-bundle" && value != "/srv/hvo/catalog.bundle")
            .Concat(["--channel", "stable", "--catalog-manifest", "/srv/hvo/catalog-manifest.json"])
            .ToArray();

        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));
    }

    [TestMethod]
    public void Parse_UnknownChannel_IsRejected()
    {
        var arguments = ValidArguments.Concat(["--channel", "latest"]).ToArray();

        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(arguments));
    }

    [TestMethod]
    public void Parse_SignedIndex_AllowsExplicitOrDefaultCatalogVersion()
    {
        var arguments = ValidArguments
            .Where(static value => value != "--catalog-bundle" && value != "/srv/hvo/catalog.bundle")
            .Concat([
                "--channel", "prerelease",
                "--catalog-index", "https://downloads.example/catalog-index.json",
                "--catalog-version", "hyg-v4.2-p3-s2-r1",
                "--asset-base-url", "https://mirror.example/releases"
            ])
            .ToArray();

        var request = CommandLine.Parse(arguments);

        Assert.AreEqual("hyg-v4.2-p3-s2-r1", request.CatalogVersion);
        Assert.AreEqual(DistributionChannel.Prerelease, request.Channel);
    }

    [TestMethod]
    public void ParseCommand_Status_RequiresExactInstanceIdentity()
    {
        var instanceId = Guid.NewGuid();

        var request = (LifecycleRequest)CommandLine.ParseCommand([
            "status", "--instance-id", instanceId.ToString("D"), "--json"
        ]);

        Assert.IsNull(request.Operation);
        Assert.AreEqual(instanceId, request.InstanceId);
        Assert.IsTrue(request.Json);
    }

    [TestMethod]
    public void ParseCommand_Upgrade_RequiresImmutableImageAndCompatibilityAcknowledgement()
    {
        var request = (LifecycleRequest)CommandLine.ParseCommand([
            "cameraagent", "upgrade",
            "--instance-id", Guid.NewGuid().ToString("D"),
            "--image-ref", $"cameraagent@sha256:{new string('a', 64)}",
            "--migration-backward-compatible",
            "--no-download"
        ]);

        Assert.AreEqual(LifecycleOperationKind.Upgrade, request.Operation);
        Assert.IsTrue(request.MigrationBackwardCompatible);
        Assert.IsTrue(request.NoDownload);
    }

    [TestMethod]
    public void ParseCommand_UpgradeWithMutableTag_IsRejected()
    {
        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand([
            "cameraagent", "upgrade", "--instance-id", Guid.NewGuid().ToString("D"),
            "--image-ref", "cameraagent:latest"
        ]));
    }

    [TestMethod]
    public void ParseCommand_RestoreOnlyBindsRecoveryWithoutChangingOriginalRequestHash()
    {
        string[] original = ["cameraagent", "upgrade", "--instance-id", Guid.NewGuid().ToString("D"),
            "--image-ref", $"sha256:{new string('a', 64)}", "--no-download", "--migration-backward-compatible"];
        var operationId = Guid.NewGuid();
        var request = (LifecycleRequest)CommandLine.ParseCommand(original);
        var recovery = (LifecycleRequest)CommandLine.ParseCommand([.. original, "--resume", "--restore-only", "--operation-id", operationId.ToString("D")]);

        Assert.IsTrue(recovery.RestoreOnly);
        Assert.IsTrue(recovery.Resume);
        Assert.AreEqual(operationId, recovery.RecoveryOperationId);
        Assert.AreEqual(request.ComputeRequestSha256(), recovery.ComputeRequestSha256());
    }

    [TestMethod]
    [DataRow("missing-resume")]
    [DataRow("missing-operation")]
    [DataRow("missing-restore")]
    [DataRow("dry-run")]
    [DataRow("rollback")]
    [DataRow("signed")]
    public void ParseCommand_RestoreOnlyRejectsUnsupportedOptions(string fault)
    {
        var request = new LifecycleRequest(LifecycleOperationKind.Upgrade, Guid.NewGuid(), InstallRequest.DefaultProductRoot,
            dryRun: false, resume: true, json: false)
        {
            ImageReference = $"sha256:{new string('a', 64)}",
            RestoreOnly = true,
            RecoveryOperationId = Guid.NewGuid()
        };
        request = fault switch
        {
            "missing-resume" => request with { Resume = false },
            "missing-operation" => request with { RecoveryOperationId = null },
            "missing-restore" => request with { RestoreOnly = false },
            "dry-run" => request with { DryRun = true },
            "rollback" => request with { Operation = LifecycleOperationKind.Rollback, ImageReference = null },
            "signed" => request with { ImageReference = null, ImageManifest = "/srv/release/image-manifest.json" },
            _ => throw new InvalidOperationException()
        };
        Assert.ThrowsExactly<InstallUsageException>(request.Validate);
    }

    [TestMethod]
    public void ParseCommand_OwnerPasswordFile_IsRejected()
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand([
            "cameraagent", "rollback", "--instance-id", Guid.NewGuid().ToString("D"),
            "--owner-password-file", "/owner-private/password"
        ]));

        StringAssert.Contains(exception.Message, "Unknown option", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ParseCommand_OwnerRecoveryRequiresOneSafePasswordSource()
    {
        var instanceId = Guid.NewGuid();
        var generated = (OwnerRecoveryRequest)CommandLine.ParseCommand([
            "cameraagent", "recover-owner",
            "--instance-id", instanceId.ToString("D"),
            "--generate-password",
            "--json"
        ]);

        Assert.AreEqual(instanceId, generated.InstanceId);
        Assert.IsTrue(generated.GeneratePassword);
        Assert.IsTrue(generated.Json);
        Assert.IsNull(generated.PasswordFile);
        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand([
            "cameraagent", "recover-owner",
            "--instance-id", instanceId.ToString("D")
        ]));
        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand([
            "cameraagent", "recover-owner",
            "--instance-id", instanceId.ToString("D"),
            "--generate-password",
            "--password-file", "/owner-private/password"
        ]));
    }

    [TestMethod]
    public void ParseCommand_OwnerRecoveryRejectsPasswordOnCommandLine()
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand([
            "cameraagent", "recover-owner",
            "--instance-id", Guid.NewGuid().ToString("D"),
            "--password", "must-not-be-accepted"
        ]));

        StringAssert.Contains(exception.Message, "Unknown option", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ParseCommand_OwnerRecoveryDoesNotReflectUnexpectedPositionalInput()
    {
        const string secret = "must-not-be-reflected";

        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand([
            "cameraagent", "recover-owner", secret
        ]));

        Assert.IsFalse(exception.Message.Contains(secret, StringComparison.Ordinal));
    }

    [TestMethod]
    public void ParseCommand_PurgeRequiresMatchingConfirmation()
    {
        var instanceId = Guid.NewGuid();

        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand([
            "cameraagent", "purge", "--instance-id", instanceId.ToString("D"),
            "--confirm-instance-id", Guid.NewGuid().ToString("D")
        ]));
    }

    [TestMethod]
    public void ParseCommand_PurgeResumeBindsRemovalDispositionWithoutChangingRequestHash()
    {
        var instanceId = Guid.NewGuid().ToString("D");
        var operationId = Guid.NewGuid();
        string[] resume = ["cameraagent", "purge", "--instance-id", instanceId, "--confirm-instance-id", instanceId, "--resume"];
        var request = (LifecycleRequest)CommandLine.ParseCommand(resume);
        var confirmed = (LifecycleRequest)CommandLine.ParseCommand(
            [.. resume, "--confirm-removed-operation-id", operationId.ToString("D")]);

        Assert.IsNull(request.ConfirmRemovedOperationId);
        Assert.AreEqual(operationId, confirmed.ConfirmRemovedOperationId);
        Assert.AreEqual(request.ComputeRequestSha256(), confirmed.ComputeRequestSha256());
    }

    [TestMethod]
    [DataRow("without-resume")]
    [DataRow("other-operation")]
    [DataRow("empty")]
    [DataRow("non-canonical")]
    public void ParseCommand_RemovalDispositionRequiresPurgeResumeAndCanonicalOperation(string fault)
    {
        var instanceId = Guid.NewGuid().ToString("D");
        var operationId = Guid.NewGuid();
        string[] arguments = fault switch
        {
            "without-resume" => ["cameraagent", "purge", "--instance-id", instanceId, "--confirm-instance-id", instanceId,
                "--confirm-removed-operation-id", operationId.ToString("D")],
            "other-operation" => ["cameraagent", "uninstall", "--instance-id", instanceId, "--resume",
                "--confirm-removed-operation-id", operationId.ToString("D")],
            "empty" => ["cameraagent", "purge", "--instance-id", instanceId, "--confirm-instance-id", instanceId, "--resume",
                "--confirm-removed-operation-id", Guid.Empty.ToString("D")],
            "non-canonical" => ["cameraagent", "purge", "--instance-id", instanceId, "--confirm-instance-id", instanceId, "--resume",
                "--confirm-removed-operation-id", operationId.ToString("N")],
            _ => throw new InvalidOperationException()
        };

        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(arguments));

        StringAssert.Contains(exception.Message, "--confirm-removed-operation-id", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ParseCommand_CatalogCommandsHaveDistinctScope()
    {
        var install = (LifecycleRequest)CommandLine.ParseCommand([
            "catalog", "install", "--catalog-bundle", "/srv/hvo/catalog.bundle", "--dry-run"
        ]);
        var select = (LifecycleRequest)CommandLine.ParseCommand([
            "catalog", "select", "--instance-id", Guid.NewGuid().ToString("D"),
            "--catalog-version", "hyg-v4.2-p3-s2-r2"
        ]);

        Assert.AreEqual(LifecycleOperationKind.CatalogInstall, install.Operation);
        Assert.AreEqual(LifecycleOperationKind.CatalogSelect, select.Operation);
    }

    [TestMethod]
    public void ParseCommand_CatalogCheckIsInstanceFreeAndNamesASignedLocator()
    {
        var check = (LifecycleRequest)CommandLine.ParseCommand([
            "catalog", "check", "--catalog-index", "https://mirror.example/indexes/catalog-stable-index.json"
        ]);

        Assert.AreEqual(LifecycleOperationKind.CatalogCheck, check.Operation);
        Assert.IsNull(check.InstanceId);
        Assert.IsFalse(check.DryRun);
    }

    [TestMethod]
    [DataRow("--instance-id", "00000000-0000-0000-0000-000000000001", DisplayName = "instance")]
    [DataRow("--dry-run", null, DisplayName = "dry-run")]
    [DataRow("--resume", null, DisplayName = "resume")]
    [DataRow("--image-reference", "sha256:0000000000000000000000000000000000000000000000000000000000000000", DisplayName = "image")]
    [DataRow(null, null, DisplayName = "no-locator")]
    public void ParseCommand_CatalogCheckRefusesMutatingOrUnscopedOptions(string? option, string? value)
    {
        string[] arguments = option is null
            ? ["catalog", "check"]
            : ["catalog", "check", "--catalog-manifest", "/srv/hvo/catalog-manifest.json", option, .. value is null ? Array.Empty<string>() : [value]];

        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(arguments));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Parse_TestRootAdmission_IsDecidedOnceAndCarriedOnTheRequest()
    {
        // Admission to a non-production product root is read from the environment once, at the command-line
        // boundary, and then travels on the request. This test states the ambient value rather than assuming
        // it, because every assertion below is only meaningful while nothing in the test process sets it.
        Assert.IsNull(
            Environment.GetEnvironmentVariable("HVO_INSTALLER_ALLOW_TEST_ROOT"),
            "no test may set the admission variable; admission belongs on the request");

        var testRoot = Path.Combine(Path.GetTempPath(), $"hvo-admission-{Guid.NewGuid():N}");
        var arguments = ValidArguments.Concat(["--product-root", testRoot]).ToArray();

        // Admitted at the boundary. Revalidating the same request succeeds even though the environment that
        // admitted it says nothing, which is the whole point: validation never rereads process state.
        var admitted = CommandLine.Parse(arguments, allowTestProductRoot: true);
        Assert.AreEqual(testRoot, admitted.ProductRoot);
        Assert.IsTrue(admitted.AllowTestProductRoot);
        admitted.Validate();

        // Deny by default.
        var denied = Assert.ThrowsExactly<InstallUsageException>(
            () => CommandLine.Parse(arguments, allowTestProductRoot: false));
        StringAssert.Contains(denied.Message, "--product-root", StringComparison.Ordinal);

        var configPath = Path.GetTempFileName();
        try
        {
            var config = JsonSerializer.Serialize(
                CommandLine.Parse(arguments, allowTestProductRoot: true) with { AllowTestProductRoot = false },
                InstallRequestJsonContext.Default.InstallRequest);

            // An installer configuration file carries the non-production root but cannot admit it.
            File.WriteAllText(configPath, config);
            var fromConfig = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(
                ["cameraagent", "install", "--config", configPath], allowTestProductRoot: false));
            StringAssert.Contains(fromConfig.Message, "--product-root", StringComparison.Ordinal);

            // Naming the flag in the file changes nothing. The member is not deserialized, and the boundary
            // decision overwrites it either way, so the file cannot admit a root the boundary refused.
            File.WriteAllText(
                configPath,
                config.Insert(config.IndexOf('{', StringComparison.Ordinal) + 1, "\"allowTestProductRoot\":true,"));
            var forged = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(
                ["cameraagent", "install", "--config", configPath], allowTestProductRoot: false));
            StringAssert.Contains(forged.Message, "--product-root", StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(configPath);
        }

        // Opposite explicit decisions in flight together do not interfere, because neither reads shared state.
        Parallel.For(0, 64, index =>
        {
            if (index % 2 == 0)
            {
                Assert.IsTrue(CommandLine.Parse(arguments, allowTestProductRoot: true).AllowTestProductRoot);
            }
            else
            {
                Assert.ThrowsExactly<InstallUsageException>(
                    () => CommandLine.Parse(arguments, allowTestProductRoot: false));
            }
        });
    }

    [TestMethod]
    public void ParseCommand_EveryListedCommand_ReachesAParserThatReportsMistakesWithItsUsage()
    {
        foreach (var command in CommandLine.Commands)
        {
            string[] arguments = [.. command.Path, .. (command.Positional is null ? [] : new[] { "/srv/hvo" }), "--unlisted", "value"];

            var mistake = Assert.ThrowsExactly<InstallUsageException>(
                () => CommandLine.ParseCommand(arguments, allowTestProductRoot: false), command.Name);

            Assert.AreEqual("Unknown option '--unlisted'.", mistake.Message, command.Name);
            Assert.AreEqual(CommandLine.CommandUsage(command), mistake.Usage, command.Name);
            var help = (HelpCommand)CommandLine.ParseCommand([.. command.Path, "--help"], allowTestProductRoot: false);
            Assert.AreEqual(CommandLine.CommandUsage(command), help.Usage, command.Name);
            Assert.IsFalse(help.Json, command.Name);
        }
    }

    [TestMethod]
    public void ParseCommand_HelpOption_IsRecognisedOnlyWhereAnOptionIsRead()
    {
        var status = CommandLine.CommandUsage(CommandLine.Commands.Single(command => command.Name == "status"));
        var host = CommandLine.CommandUsage(CommandLine.Commands.Single(command => command.Name == "host set-product-root"));
        var instance = Guid.NewGuid().ToString("D");

        Assert.AreEqual(status, Help(["status", "-h"]));
        Assert.AreEqual(status, Help(["status", "--json", "--help"]));
        Assert.AreEqual(status, Help(["status", "--instance-id", instance, "-h"]));
        Assert.AreEqual(host, Help(["host", "set-product-root", "--help"]));
        Assert.AreEqual(host, Help(["host", "set-product-root", "/srv/hvo", "-h"]));
        Assert.AreEqual(CommandLine.GeneralUsage(), Help(["cameraagent", "--help"]));
        Assert.AreEqual(CommandLine.GeneralUsage(), Help(["catalog", "-h"]));

        // A value that happens to be -h is still that option's value, and is validated as one.
        var value = Assert.ThrowsExactly<InstallUsageException>(
            () => CommandLine.ParseCommand(["status", "--product-root", "-h"], allowTestProductRoot: false));
        StringAssert.Contains(value.Message, "--product-root", StringComparison.Ordinal);
        Assert.AreEqual(status, value.Usage);

        // Help does not make an unknown command known.
        var unknown = Assert.ThrowsExactly<InstallUsageException>(
            () => CommandLine.ParseCommand(["bogus", "--help"], allowTestProductRoot: false));
        Assert.AreEqual("Unknown command 'bogus'.", unknown.Message);
        Assert.AreEqual(CommandLine.GeneralUsage(), unknown.Usage);

        static string Help(string[] arguments)
            => ((HelpCommand)CommandLine.ParseCommand(arguments, allowTestProductRoot: false)).Usage;
    }
}
