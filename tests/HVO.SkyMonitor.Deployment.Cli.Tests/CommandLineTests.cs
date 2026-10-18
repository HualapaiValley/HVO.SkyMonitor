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
    [DataRow(null, null)]
    [DataRow("archive", "Archive")]
    [DataRow("registry", "Registry")]
    public void Parse_Release_NamesTheReleaseFolderInsteadOfAnImage(string? source, string? expected)
    {
        string[] arguments =
        [
            .. ValidArguments.Take(8), "--release", "/srv/hvo/release-v1.2.0-dev.3",
            .. source is null ? Array.Empty<string>() : ["--image-source", source]
        ];

        // No image is prompted for: the release names it.
        var request = CommandLine.Parse(arguments);

        Assert.AreEqual("/srv/hvo/release-v1.2.0-dev.3", request.Release);
        Assert.AreEqual(expected, request.ImageSource?.ToString());
        Assert.AreEqual(string.Empty, request.ImageReference);
    }

    [TestMethod]
    [DataRow("--image-ref", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "--image-ref cannot be combined with --release.")]
    [DataRow("--image-archive", "/srv/hvo/cameraagent.tar", "--image-archive and --image-archive-sha256 cannot be combined with --release.")]
    public void Parse_ReleaseWithAnOperatorImage_IsRejected(string option, string value, string message)
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(
            [.. ValidArguments.Take(8), "--release", "/srv/hvo/release", option, value,
             .. option == "--image-archive" ? new[] { "--image-archive-sha256", new string('b', 64) } : []]));

        Assert.AreEqual(message, exception.Message);
    }

    [TestMethod]
    [DataRow("relative/release", "--release must be an absolute normalized path.")]
    [DataRow("/srv//release", "--release must be an absolute normalized path.")]
    [DataRow("/srv/hvo/../release", "--release must be an absolute normalized path other than root.")]
    [DataRow("/", "--release must be an absolute normalized path other than root.")]
    public void Parse_ReleaseThatIsNotAnAbsoluteFolder_IsRejected(string release, string message)
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(
            () => CommandLine.Parse([.. ValidArguments.Take(8), "--release", release]));

        Assert.AreEqual(message, exception.Message);
    }

    [TestMethod]
    public void Parse_ImageSourceWithoutARelease_IsRejected()
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(
            () => CommandLine.Parse([.. ValidArguments, "--image-source", "registry"]));

        Assert.AreEqual("--image-source requires --release.", exception.Message);
    }

    [TestMethod]
    public void Parse_UnknownImageSource_IsRejected()
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(
            () => CommandLine.Parse([.. ValidArguments.Take(8), "--release", "/srv/hvo/release", "--image-source", "network"]));

        Assert.AreEqual("--image-source must be archive or registry.", exception.Message);
    }

    [TestMethod]
    [DataRow("--image-manifest", "/srv/hvo/image-manifest.json")]
    [DataRow("--image-index", "https://downloads.example/image-index.json")]
    [DataRow("--image-version", "1.2.0")]
    [DataRow("--catalog-manifest", "/srv/hvo/catalog-manifest.json")]
    [DataRow("--catalog-index", "https://downloads.example/catalog-index.json")]
    [DataRow("--catalog-version", "hyg-v4.2-p3-s2-r1")]
    [DataRow("--asset-base-url", "https://mirror.example/releases")]
    [DataRow("--channel", "stable")]
    public void Parse_RetiredSignedReleaseOption_IsUnknown(string option, string value)
    {
        // Signed manifests and indexes are retired (#1309): their options are no longer the installer's.
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse([.. ValidArguments, option, value]));

        Assert.AreEqual($"Unknown option '{option}'.", exception.Message);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("imageManifest", "/srv/hvo/image-manifest.json")]
    [DataRow("catalogIndex", "https://downloads.example/catalog-index.json")]
    [DataRow("channel", "Stable")]
    public void Parse_ConfigurationNamingARetiredSignedReleaseMember_IsRejected(string member, string value)
    {
        var path = Path.GetTempFileName();
        try
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(
                CommandLine.Parse(ValidArguments), InstallRequestJsonContext.Default.InstallRequest))!.AsObject();
            json[member] = value;
            File.WriteAllText(path, json.ToJsonString());

            Assert.ThrowsExactly<JsonException>(() => CommandLine.Parse(["cameraagent", "install", "--config", path]));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Parse_ConfigurationNamingARelease_ReadsIt()
    {
        var path = Path.GetTempFileName();
        try
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(
                CommandLine.Parse(ValidArguments), InstallRequestJsonContext.Default.InstallRequest))!.AsObject();
            json["imageReference"] = string.Empty;
            json["release"] = "/srv/hvo/release-v1.2.0-dev.3";
            json["imageSource"] = "Registry";
            File.WriteAllText(path, json.ToJsonString());

            var request = CommandLine.Parse(["cameraagent", "install", "--config", path]);

            Assert.AreEqual("/srv/hvo/release-v1.2.0-dev.3", request.Release);
            Assert.AreEqual("Registry", request.ImageSource?.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("registry", "Registry")]
    public void ParseCommand_UpgradeFromARelease_NamesTheReleaseFolder(string? source, string? expected)
    {
        var request = (LifecycleRequest)CommandLine.ParseCommand([
            "cameraagent", "upgrade", "--instance-id", Guid.NewGuid().ToString("D"),
            "--release", "/srv/hvo/release-v1.2.0-dev.3",
            .. source is null ? Array.Empty<string>() : ["--image-source", source]
        ]);

        Assert.AreEqual("/srv/hvo/release-v1.2.0-dev.3", request.Release);
        Assert.AreEqual(expected, request.ImageSource?.ToString());
        Assert.IsNull(request.ImageReference);
    }

    [TestMethod]
    [DataRow("--image-ref", "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "--image-ref, --image-archive, and --image-archive-sha256 cannot be combined with --release.")]
    [DataRow("--catalog-bundle", "/srv/hvo/catalog.bundle", "Image upgrade does not accept catalog or purge options.")]
    public void ParseCommand_UpgradeFromAReleaseWithAnotherSource_IsRejected(string option, string value, string message)
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand([
            "cameraagent", "upgrade", "--instance-id", Guid.NewGuid().ToString("D"),
            "--release", "/srv/hvo/release", option, value
        ]));

        Assert.AreEqual(message, exception.Message);
    }

    private const string BackupName = "hvo-sky-5a3f7c1e2b4d4e6f8a9b0c1d2e3f4a5b-20261018T073000Z-11223344.tar.gz";

    /// <summary><c>cameraagent restore</c> names its backup by name, or by its path in the product root's backup folder (#1353).</summary>
    [TestMethod]
    [DataRow(BackupName)]
    [DataRow("/var/lib/hvo-sky/backups/" + BackupName)]
    public void ParseCommand_Restore_NamesTheBackupInTheBackupFolder(string archive)
    {
        var request = (LifecycleRequest)CommandLine.ParseCommand(["cameraagent", "restore", archive, "--instance-id", Guid.NewGuid().ToString("D"), "--plan"]);

        Assert.AreEqual(LifecycleOperationKind.Restore, request.Operation);
        Assert.AreEqual(BackupName, request.Archive);
        Assert.IsTrue(request.DryRun);
    }

    [TestMethod]
    [DataRow("/tmp/" + BackupName, "restore reads a backup only from")]
    [DataRow("backup.tar.gz", "restore requires the name of a backup")]
    [DataRow(null, "restore requires the name of a backup")]
    public void ParseCommand_RestoreOfAnythingButABackupInTheFolder_IsRejected(string? archive, string message)
    {
        string[] arguments = archive is null
            ? ["cameraagent", "restore", "--instance-id", Guid.NewGuid().ToString("D")]
            : ["cameraagent", "restore", archive, "--instance-id", Guid.NewGuid().ToString("D")];

        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(arguments));

        StringAssert.StartsWith(exception.Message, message, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ParseCommand_BackupWithAnArchive_IsRejected()
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(
            ["cameraagent", "backup", BackupName, "--instance-id", Guid.NewGuid().ToString("D")]));

        Assert.AreEqual("Unexpected positional argument.", exception.Message);
    }

    [TestMethod]
    public void ParseCommand_UpgradeImageSourceWithoutARelease_IsRejected()
    {
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand([
            "cameraagent", "upgrade", "--instance-id", Guid.NewGuid().ToString("D"),
            "--image-ref", $"sha256:{new string('a', 64)}", "--image-source", "registry"
        ]));

        Assert.AreEqual("--image-source requires --release.", exception.Message);
    }

    [TestMethod]
    [DataRow("cameraagent", "rollback")]
    [DataRow("catalog", "install")]
    [DataRow("cameraagent", "uninstall")]
    public void ParseCommand_ReleaseOutsideAnUpgrade_IsRejected(string group, string command)
    {
        string[] arguments = group == "catalog"
            ? [group, command, "--catalog-bundle", "/srv/hvo/catalog.bundle", "--release", "/srv/hvo/release"]
            : [group, command, "--instance-id", Guid.NewGuid().ToString("D"), "--release", "/srv/hvo/release"];

        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(arguments));
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
            "signed" => request with { ImageReference = null, Release = "/srv/release" },
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
    public void ParseCommand_CatalogCheck_IsNoLongerACommand()
    {
        // catalog check read only signed catalog releases, which are retired (#1309).
        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(["catalog", "check"]));

        Assert.AreEqual("Unknown command 'catalog check'.", exception.Message);
    }

    [TestMethod]
    [DataRow(null, "catalog install requires --catalog-bundle.")]
    [DataRow("--catalog-version", "Catalog install accepts only --catalog-bundle and common options.")]
    [DataRow("relative", "--catalog-bundle must be an absolute normalized path other than root.")]
    public void ParseCommand_CatalogInstallTakesOnlyABundle(string? fault, string message)
    {
        string[] arguments = fault switch
        {
            null => ["catalog", "install"],
            "relative" => ["catalog", "install", "--catalog-bundle", "relative/catalog.bundle"],
            _ => ["catalog", "install", "--catalog-bundle", "/srv/hvo/catalog.bundle", fault, "hyg-v4.2-p3-s2-r1"]
        };

        var exception = Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.ParseCommand(arguments));

        Assert.AreEqual(message, exception.Message);
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
