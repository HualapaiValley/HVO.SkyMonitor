using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The answers file (install contract section 5, #1315): strict JSON with a schema version, the same members
/// <c>--config</c> reads without the run flags, every problem a usage error, and only <c>--cpus</c> and
/// <c>--memory</c> beside it, so a re-run changes the limits. Also <c>--config</c> as its deprecated predecessor,
/// <c>--plan</c> as the name of <c>--dry-run</c>, and the deprecated <c>--migration-backward-compatible</c>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class InstallAnswersTests
{
    private static readonly Guid InstanceId = Guid.Parse("0c1e7a52-6d1f-4b8e-9a3c-2f5d7e9b1a40");

    private static string Answers(string extra = "", int? schemaVersion = InstallAnswers.CurrentSchemaVersion)
        => "{" + (schemaVersion is null ? string.Empty : $"\"schemaVersion\":{schemaVersion},") +
           $"\"instanceId\":\"{InstanceId:D}\",\"friendlyName\":\"North Camera\",\"ownerEmail\":\"admin@example.test\"," +
           "\"catalogBundle\":\"/srv/hvo/catalog.bundle\"," +
           $"\"imageReference\":\"ghcr.io/example/cameraagent@sha256:{new string('a', 64)}\"" + extra + "}";

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Parse_AnswersFile_ProducesTheRequestTheSameFlagsProduce()
    {
        using var file = new TemporaryFile(Answers(",\"port\":5140,\"latitudeDegrees\":35.2,\"timeZoneId\":\"UTC\",\"cpus\":3,\"memory\":\"6g\""));

        var fromAnswers = CommandLine.Parse(["cameraagent", "install", "--answers", file.Path]);
        var fromFlags = CommandLine.Parse(
        [
            "cameraagent", "install", "--instance-id", InstanceId.ToString("D"), "--friendly-name", "North Camera",
            "--owner-email", "admin@example.test", "--catalog-bundle", "/srv/hvo/catalog.bundle",
            "--image-ref", $"ghcr.io/example/cameraagent@sha256:{new string('a', 64)}", "--port", "5140",
            "--latitude", "35.2", "--time-zone", "UTC", "--cpus", "3", "--memory", "6g"
        ]);

        Assert.AreEqual(fromFlags, fromAnswers);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(",\"frendlyName\":\"typo\"", "frendlyName")]
    [DataRow(",\"dryRun\":true", "dryRun")]
    [DataRow(",\"json\":true", "json")]
    [DataRow(",\"resume\":true", "resume")]
    [DataRow(",\"channel\":\"stable\"", "channel")]
    [DataRow(",\"imageManifest\":\"/srv/manifest.json\"", "imageManifest")]
    public void Parse_AnswersWithAMemberThisInstallerDoesNotRead_IsAUsageError(string extra, string member)
    {
        ArgumentNullException.ThrowIfNull(extra);
        ArgumentNullException.ThrowIfNull(member);
        using var file = new TemporaryFile(Answers(extra));

        var exception = Assert.ThrowsExactly<InstallUsageException>(
            () => CommandLine.Parse(["cameraagent", "install", "--answers", file.Path]));

        StringAssert.Contains(exception.Message, file.Path, StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, member, StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Parse_AnswersNamingAMemberTwice_IsAUsageError()
    {
        using var file = new TemporaryFile(Answers(",\"port\":5140,\"port\":5141"));

        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(["cameraagent", "install", "--answers", file.Path]));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(null, "names no schemaVersion")]
    [DataRow(2, "is schema 2; this installer reads schema 1")]
    [DataRow(0, "is schema 0; this installer reads schema 1")]
    public void Parse_AnswersWithoutTheSchemaThisInstallerReads_IsAUsageError(int? schemaVersion, string reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        using var file = new TemporaryFile(Answers(schemaVersion: schemaVersion));

        var exception = Assert.ThrowsExactly<InstallUsageException>(
            () => CommandLine.Parse(["cameraagent", "install", "--answers", file.Path]));

        StringAssert.Contains(exception.Message, reason, StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Parse_AnswersWithoutAnInstanceId_IsAUsageErrorSoARerunCannotInstallASecondInstance()
    {
        using var file = new TemporaryFile(Answers().Replace($"\"instanceId\":\"{InstanceId:D}\",", string.Empty, StringComparison.Ordinal));

        var exception = Assert.ThrowsExactly<InstallUsageException>(
            () => CommandLine.Parse(["cameraagent", "install", "--answers", file.Path]));

        StringAssert.Contains(exception.Message, "must name its instanceId", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("not json")]
    [DataRow("")]
    [DataRow("[]")]
    public void Parse_AnswersThatAreNotAJsonObject_IsAUsageError(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var file = new TemporaryFile(content);

        Assert.ThrowsExactly<InstallUsageException>(() => CommandLine.Parse(["cameraagent", "install", "--answers", file.Path]));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Parse_AnswersFileThatCannotBeRead_IsAUsageError()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"hvo-answers-{Guid.NewGuid():N}.json");

        var exception = Assert.ThrowsExactly<InstallUsageException>(
            () => CommandLine.Parse(["cameraagent", "install", "--answers", missing]));

        StringAssert.Contains(exception.Message, "could not be read", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Parse_AnswersWithLimitsOnTheCommandLine_TheCommandLineLimitsWin()
    {
        using var file = new TemporaryFile(Answers(",\"cpus\":3,\"memory\":\"6g\""));

        var request = CommandLine.Parse(["cameraagent", "install", "--answers", file.Path, "--memory", "8g"]);

        Assert.AreEqual(3m, request.Cpus);
        Assert.AreEqual("8g", request.Memory);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("--port", "5140")]
    [DataRow("--friendly-name", "Other")]
    [DataRow("--instance-id", "0c1e7a52-6d1f-4b8e-9a3c-2f5d7e9b1a41")]
    public void Parse_AnswersWithAnyOtherValueOption_IsAUsageError(string option, string value)
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(value);
        using var file = new TemporaryFile(Answers());

        var exception = Assert.ThrowsExactly<InstallUsageException>(
            () => CommandLine.Parse(["cameraagent", "install", "--answers", file.Path, option, value]));

        StringAssert.Contains(exception.Message, "only with --cpus and --memory", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Parse_AnswersWithConfig_IsAUsageError()
    {
        using var file = new TemporaryFile(Answers());

        var exception = Assert.ThrowsExactly<InstallUsageException>(
            () => CommandLine.Parse(["cameraagent", "install", "--answers", file.Path, "--config", file.Path]));

        StringAssert.Contains(exception.Message, "--answers cannot be combined with --config", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Parse_AnswersWithRunFlags_KeepsTheFlagsOnTheCommandLine()
    {
        using var file = new TemporaryFile(Answers());

        var request = CommandLine.Parse(["cameraagent", "install", "--answers", file.Path, "--plan", "--json"]);

        Assert.IsTrue(request.DryRun);
        Assert.IsTrue(request.Json);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Parse_AnswersWithoutAProductRoot_InstallsIntoTheProductionRootThePolicyResolved()
    {
        using var file = new TemporaryFile(Answers());

        var request = CommandLine.Parse(["cameraagent", "install", "--answers", file.Path], false, "/srv/data/hvo-skymonitor");

        Assert.AreEqual("/srv/data/hvo-skymonitor", request.ProductRoot);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void ParseCommand_Config_StillInstallsAndWarnsThatItIsDeprecated()
    {
        var expected = CommandLine.Parse(
        [
            "cameraagent", "install", "--friendly-name", "North Camera", "--catalog-bundle", "/srv/hvo/catalog.bundle",
            "--image-ref", $"ghcr.io/example/cameraagent@sha256:{new string('a', 64)}"
        ]);
        using var file = new TemporaryFile(JsonSerializer.Serialize(expected, InstallRequestJsonContext.Default.InstallRequest));

        var command = (InstallDeploymentCommand)CommandLine.ParseCommand(["cameraagent", "install", "--config", file.Path], false);

        Assert.AreEqual(expected, command.Request);
        Assert.AreEqual(CommandLine.ConfigDeprecation, command.ParseWarnings.Single());
        Assert.AreEqual("option-deprecated", command.ParseWarnings.Single().Code);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void ParseCommand_Answers_CarriesNoDeprecationWarning()
    {
        using var file = new TemporaryFile(Answers());

        var command = (InstallDeploymentCommand)CommandLine.ParseCommand(["cameraagent", "install", "--answers", file.Path], false);

        Assert.IsEmpty(command.ParseWarnings);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Parse_ConfigFileThatCannotBeRead_IsAUsageErrorLikeAnAnswersFile()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"hvo-config-{Guid.NewGuid():N}.json");

        var exception = Assert.ThrowsExactly<InstallUsageException>(
            () => CommandLine.Parse(["cameraagent", "install", "--config", missing]));

        StringAssert.Contains(exception.Message, "could not be read", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("uninstall")]
    [DataRow("purge")]
    [DataRow("reinstall")]
    [DataRow("rollback")]
    public void ParseCommand_Plan_IsTheDryRunOfEveryInstanceLifecycleCommand(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var id = Guid.NewGuid().ToString("D");
        string[] confirmation = name == "purge" ? ["--confirm-instance-id", id] : [];
        var command = (LifecycleRequest)CommandLine.ParseCommand(
            ["cameraagent", name, "--instance-id", id, .. confirmation, "--plan"], false);

        Assert.IsTrue(command.DryRun);
    }

    [TestMethod]
    public void ParseCommand_Plan_IsTheDryRunOfAnUpgradeAndOfAResetAndAnInstall()
    {
        var id = Guid.NewGuid().ToString("D");
        var upgrade = (LifecycleRequest)CommandLine.ParseCommand(
            ["cameraagent", "upgrade", "--instance-id", id, "--image-ref", $"sha256:{new string('b', 64)}", "--plan"], false);
        var reset = (CameraAgentStateResetRequest)CommandLine.ParseCommand(
            ["cameraagent", "reset-state", "--instance-id", id, "--confirm-instance-id", id, "--plan"], false);
        var install = CommandLine.Parse(
        [
            "cameraagent", "install", "--friendly-name", "North Camera", "--catalog-bundle", "/srv/hvo/catalog.bundle",
            "--image-ref", $"ghcr.io/example/cameraagent@sha256:{new string('a', 64)}", "--plan"
        ]);

        Assert.IsTrue(upgrade.DryRun);
        Assert.IsTrue(reset.DryRun);
        Assert.IsTrue(install.DryRun);
    }

    [TestMethod]
    public void ParseCommand_MigrationAssertion_IsAcceptedKeptInTheRequestIdentityAndWarnedAsDeprecated()
    {
        var id = Guid.NewGuid().ToString("D");
        string[] arguments = ["cameraagent", "upgrade", "--instance-id", id, "--image-ref", $"sha256:{new string('b', 64)}"];

        var asserted = (LifecycleRequest)CommandLine.ParseCommand([.. arguments, "--migration-backward-compatible"], false);
        var plain = (LifecycleRequest)CommandLine.ParseCommand(arguments, false);

        Assert.IsTrue(asserted.MigrationBackwardCompatible);
        Assert.AreEqual(CommandLine.MigrationAssertionDeprecation, asserted.ParseWarnings.Single());
        Assert.IsEmpty(plain.ParseWarnings);
        // A journal written by an upgrade begun with the option still matches a --resume that repeats it.
        Assert.AreNotEqual(plain.ComputeRequestSha256(), asserted.ComputeRequestSha256());
    }

    /// <summary>A file holding <c>content</c>, deleted when disposed.</summary>
    private sealed class TemporaryFile : IDisposable
    {
        public TemporaryFile(string content)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hvo-answers-{Guid.NewGuid():N}.json");
            File.WriteAllText(Path, content);
        }

        public string Path { get; }

        public void Dispose() => File.Delete(Path);
    }
}
