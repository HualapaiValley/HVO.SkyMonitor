using System.Reflection;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DoNotParallelize]
    [DataRow(false, "double")]
    [DataRow(true, "double")]
    [DataRow(false, "single")]
    [DataRow(true, "single")]
    [DataRow(false, "escaped-double")]
    [DataRow(true, "escaped-double")]
    [DataRow(false, "escaped-single")]
    [DataRow(true, "escaped-single")]
    [DataRow(false, "doubled-single")]
    [DataRow(true, "doubled-single")]
    [DataRow(false, "unterminated-double")]
    [DataRow(true, "unterminated-double")]
    [DataRow(false, "unterminated-single")]
    [DataRow(true, "unterminated-single")]
    [DataRow(false, "unicode")]
    [DataRow(true, "unicode")]
    [DataRow(false, "multiple")]
    [DataRow(true, "multiple")]
    public async Task Diagnostic1476R1_SqliteQuotedVariants(bool json, string variant)
    {
        using var fixture = await MigratingFixtureAsync();
        var target = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, AutomationStore);
        ExecuteSql(target, FixtureDump(AutomationStore));
        SaveDefinition(target, "review-agent", 1);
        var quoted = variant switch
        {
            "single" => "'head review-1487-secret tail'",
            "escaped-double" => "\"head\\\"hidden review-1487-secret tail\"",
            "escaped-single" => "'head\\'hidden review-1487-secret tail'",
            "doubled-single" => "'head''hidden review-1487-secret tail'",
            "unterminated-double" => "\"head review-1487-secret tail",
            "unterminated-single" => "'head review-1487-secret tail",
            "unicode" => "\"密碼🔭 review-1487-secret tail\"",
            "multiple" => "\"head review-1487-secret tail\"\u001b[0m middle token=\u001b[32m'another review-1487-secret tail'",
            _ => "\"head review-1487-secret tail\""
        };
        var diagnostic = "prefix token=\u001b[31m" + quoted;
        if (!variant.StartsWith("unterminated", StringComparison.Ordinal)) diagnostic += "\u001b[0m suffix";
        ExecuteSql(target, "UPDATE automation_definition_revisions SET definition_json='{}';" +
            "CREATE TRIGGER refuse_update BEFORE UPDATE ON automation_definitions BEGIN " +
            $"SELECT RAISE(ABORT, '{diagnostic.Replace("'", "''", StringComparison.Ordinal)}'); END;");
        var plan = CameraAgentStateCounters.PlanCarry(
            [new(AutomationStore, "automation-definition-version", "review-agent", 2)], _ => target,
            Path.Combine(fixture.Root, "review-trial"));
        Assert.HasCount(1, plan.Uncarried);
        using var error = new StringWriter();
        await CameraAgentStateSnapshot.ShowRestoreUncarriedAsync(
            new(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), DateTimeOffset.UtcNow), plan.Uncarried, json, error);
        var output = error.ToString();
        if (json) { using var doc = JsonDocument.Parse(output); output = doc.RootElement.GetProperty("warning").GetString()!; }
        Console.WriteLine(JsonSerializer.Serialize(new { variant, json, output }));
        StringAssert.Contains(output, "prefix", StringComparison.Ordinal);
        if (!variant.StartsWith("unterminated", StringComparison.Ordinal)) StringAssert.Contains(output, "suffix", StringComparison.Ordinal);
        Assert.IsFalse(output.Contains("review-1487-secret", StringComparison.Ordinal), "Credential tail leaked: " + output);
        Assert.IsFalse(output.TrimEnd('\r', '\n').Any(char.IsControl));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Diagnostic1476R1_NormalPasswordDescriptionPreserved(bool json)
    {
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        Execute(CameraAgentStateLayout.IdentityDatabasePath(fixture.Paths.StateRoot),
            "UPDATE AspNetUsers SET PasswordHash = 'hash-after', SecurityStamp = 'stamp-after' WHERE Id = 'owner';");
        await UninstallAsync(fixture);
        using var error = new StringWriter();
        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Restore) with { Archive = backup.Backup!.Archive, Json = json },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None, error: error);
        Assert.AreEqual("completed", result.Outcome);
        var output = error.ToString();
        if (json)
        {
            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Single(l => l.Contains(LifecycleCredentialsRestoredEvent.Name, StringComparison.Ordinal));
            using var doc = JsonDocument.Parse(line); output = doc.RootElement.GetProperty("warning").GetString()!;
        }
        Console.WriteLine(JsonSerializer.Serialize(output));
        Assert.AreEqual("account owner keeps the password and security stamp the live instance had.",
            output[(output.LastIndexOf(": ", StringComparison.Ordinal) + 2)..].TrimEnd('\r', '\n'));
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    [DataRow(false, false, true)]
    [DataRow(true, false, true)]
    [DataRow(false, true, true)]
    [DataRow(true, true, true)]
    public async Task Diagnostic1476R1_BackupRecoveryFormatsEachFragmentOnce(bool json, bool cleanupFails, bool restartFails)
    {
        using var fixture = await BackupFixtureAsync();
        const string entry = "config/file\"review";
        const string rawCleanup = "cleanup\"review token=\u001b[31m\"review-1487-secret tail\"";
        const string rawRestart = "restart\"review token=\u009b31m'review-1487-secret tail'";
        var link = Path.Combine(fixture.Paths.InstanceRoot, entry);
        var original = CameraAgentStateSnapshot.StepCompleted;
        CameraAgentStateSnapshot.StepCompleted = _ =>
        {
            if (new FileInfo(link).LinkTarget is null) File.CreateSymbolicLink(link, "missing");
        };
        if (restartFails) fixture.Runner.OnComposeUp = _ => throw new IOException(rawRestart);
        BackupVault.Override = _ => new FaultingVault(new OwnerBackupVault(new BackupVaultFiles(Path.Combine(fixture.Root, BackupVault.FolderName))))
        {
            DiscardFault = cleanupFails ? static () => throw new IOException(rawCleanup) : null
        };
        InstallerException failure;
        try
        {
            failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => BackupAsync(fixture, new FakeLifecycleClient()));
        }
        finally
        {
            BackupVault.Override = null;
            CameraAgentStateSnapshot.StepCompleted = original;
            File.Delete(link);
        }
        Assert.IsTrue(failure.IsFormattedDiagnostic, "A composed recovery diagnostic must retain its formatting marker.");
        var output = await RenderDiagnostic1476ErrorAsync(failure.Message, json, failure.IsFormattedDiagnostic);
        Console.WriteLine(JsonSerializer.Serialize(output));
        StringAssert.Contains(output, CameraAgentStateDiagnostics.Reason(entry), StringComparison.Ordinal);
        Assert.IsFalse(output.Contains("\\\\u0022", StringComparison.Ordinal), "Existing encoded identity was encoded again.");
        if (cleanupFails) StringAssert.Contains(failure.Message, CameraAgentStateDiagnostics.Reason(rawCleanup), StringComparison.Ordinal);
        if (restartFails) StringAssert.Contains(failure.Message, CameraAgentStateDiagnostics.Reason(rawRestart), StringComparison.Ordinal);
        Assert.IsFalse(output.Contains("review-1487-secret", StringComparison.Ordinal));
        AssertDiagnostic1476Safe(output);
        AssertDiagnostic1476Safe(failure.Message);
        if (cleanupFails || restartFails) StringAssert.Contains(failure.Message, "--resume", StringComparison.Ordinal);
        if (cleanupFails && !restartFails) StringAssert.Contains(failure.Message, "the instance was restarted and resumed", StringComparison.Ordinal);
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false, true, false, true)]
    [DataRow(false, true, false, false)]
    [DataRow(true, true, false, true)]
    [DataRow(true, true, false, false)]
    [DataRow(false, false, true, true)]
    [DataRow(false, false, true, false)]
    [DataRow(true, false, true, true)]
    [DataRow(true, false, true, false)]
    [DataRow(false, true, true, true)]
    [DataRow(false, true, true, false)]
    [DataRow(true, true, true, true)]
    [DataRow(true, true, true, false)]
    public async Task Diagnostic1476R1_ResumeRecoveryPreservesFormattedFailures(bool json, bool cleanupFails, bool restartFails, bool preformatted)
    {
        using var fixture = await BackupFixtureAsync();
        const string entry = "config/file\"review";
        var link = Path.Combine(fixture.Paths.InstanceRoot, entry);
        var original = CameraAgentStateSnapshot.StepCompleted;
        CameraAgentStateSnapshot.StepCompleted = _ =>
        {
            if (new FileInfo(link).LinkTarget is null) File.CreateSymbolicLink(link, "missing");
        };
        var raw = "refused '" + entry + "' token=\u001b[31m\"review-1487-secret tail\"";
        var formatted = CameraAgentStateDiagnostics.Reason(raw);
        InstallerException RecoveryFault() => preformatted ? InstallerException.Formatted(formatted) : new InstallerException(raw);
        BackupVault.Override = _ => new FaultingVault(new OwnerBackupVault(new BackupVaultFiles(Path.Combine(fixture.Root, BackupVault.FolderName))))
        {
            DiscardFault = static () => throw new IOException("cleanup unavailable")
        };
        InstallerException failure;
        try
        {
            await Assert.ThrowsExactlyAsync<InstallerException>(() => BackupAsync(fixture, new FakeLifecycleClient()));
            CameraAgentStateSnapshot.StepCompleted = original;
            File.Delete(link);
            BackupVault.Override = _ => new FaultingVault(new OwnerBackupVault(new BackupVaultFiles(Path.Combine(fixture.Root, BackupVault.FolderName))))
            {
                DiscardFault = cleanupFails ? () => throw RecoveryFault() : null
            };
            if (restartFails) fixture.Runner.OnComposeUp = _ => throw RecoveryFault();
            using var error = new StringWriter();
            failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                fixture.Request(LifecycleOperationKind.Backup) with { Resume = true, Json = json }, fixture.Runner,
                _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
                fixture.Uid, fixture.Gid, CancellationToken.None, error: error));
        }
        finally
        {
            BackupVault.Override = null;
            CameraAgentStateSnapshot.StepCompleted = original;
            File.Delete(link);
        }
        Assert.IsTrue(failure.IsFormattedDiagnostic, "A composed recovery diagnostic must retain its formatting marker.");
        var output = await RenderDiagnostic1476ErrorAsync(failure.Message, json, failure.IsFormattedDiagnostic);
        Console.WriteLine(JsonSerializer.Serialize(output));
        StringAssert.Contains(failure.Message, formatted, StringComparison.Ordinal);
        StringAssert.Contains(output, CameraAgentStateDiagnostics.Reason(entry), StringComparison.Ordinal);
        Assert.IsFalse(output.Contains("\\\\u0022", StringComparison.Ordinal));
        AssertDiagnostic1476Safe(output);
        Assert.IsFalse(output.Contains("review-1487-secret", StringComparison.Ordinal));
        StringAssert.Contains(failure.Message, "--resume", StringComparison.Ordinal);
        Assert.AreEqual(cleanupFails, failure.Message.Contains("discarding its partial archive also failed", StringComparison.Ordinal));
        Assert.AreEqual(restartFails, failure.Message.Contains("restarting the instance also failed", StringComparison.Ordinal));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DoNotParallelize]
    [DataRow("outside.txt", "it holds outside.txt, outside the instance and its snapshot")]
    [DataRow("instance/a//file", "it holds an entry named 'instance/a//file', which is not the canonical spelling of a path")]
    [DataRow("instance/a/../file", "it holds an entry named 'instance/a/../file', which is not the canonical spelling of a path")]
    public void Diagnostic1476R1_NormalArchiveWording(string name, string expected)
    {
        var actual = BackupArchive.NameProblem(name, System.Formats.Tar.TarEntryType.RegularFile);
        Assert.AreEqual(expected, actual);
        Console.WriteLine(JsonSerializer.Serialize(actual));
    }
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DoNotParallelize]
    public void Diagnostic1476R1_NormalRecoveryWording()
    {
        var method = typeof(CameraAgentLifecycleManager).GetMethod("RecoveryFailureMessage", BindingFlags.Static | BindingFlags.NonPublic)!;
        var actual = (string)method.Invoke(null, ["The backup failed (No space left on device)", new IOException("Read-only file system"), new IOException("daemon unavailable"), true])!;
        Assert.AreEqual("The backup failed (No space left on device); discarding its partial archive also failed (Read-only file system); restarting the instance also failed (daemon unavailable). Rerun the same command with --resume to finish.", actual);
        Console.WriteLine(JsonSerializer.Serialize(actual));
    }
}
