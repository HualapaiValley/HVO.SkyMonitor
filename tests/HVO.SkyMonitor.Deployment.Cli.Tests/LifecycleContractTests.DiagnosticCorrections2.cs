using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DoNotParallelize]
    [DataRow(false, "prefix token=\"head\\\"hidden review-1487-secret tail\" suffix", "prefix token=\"<redacted>\" suffix")]
    [DataRow(true, "prefix token=\"head\\\"hidden review-1487-secret tail\" suffix", "prefix token=\"<redacted>\" suffix")]
    [DataRow(false, "prefix token='head\\'hidden review-1487-secret tail' suffix", "prefix token='<redacted>' suffix")]
    [DataRow(true, "prefix token='head\\'hidden review-1487-secret tail' suffix", "prefix token='<redacted>' suffix")]
    [DataRow(false, "prefix token=\"head\"\"hidden review-1487-secret tail\" suffix", "prefix token=\"<redacted>\" suffix")]
    [DataRow(true, "prefix token=\"head\"\"hidden review-1487-secret tail\" suffix", "prefix token=\"<redacted>\" suffix")]
    [DataRow(false, "prefix token='head''hidden review-1487-secret tail' suffix", "prefix token='<redacted>' suffix")]
    [DataRow(true, "prefix token='head''hidden review-1487-secret tail' suffix", "prefix token='<redacted>' suffix")]
    [DataRow(false, "prefix token=\"head\\\"one\"\"two review-1487-secret tail\" suffix", "prefix token=\"<redacted>\" suffix")]
    [DataRow(true, "prefix token=\"head\\\"one\"\"two review-1487-secret tail\" suffix", "prefix token=\"<redacted>\" suffix")]
    [DataRow(false, "prefix token='head\\'one''two review-1487-secret tail' suffix", "prefix token='<redacted>' suffix")]
    [DataRow(true, "prefix token='head\\'one''two review-1487-secret tail' suffix", "prefix token='<redacted>' suffix")]
    [DataRow(false, "prefix token=\"head review-1487-secret tail", "prefix token=\"<redacted>")]
    [DataRow(true, "prefix token=\"head review-1487-secret tail", "prefix token=\"<redacted>")]
    [DataRow(false, "prefix token=\"head review-1487-secret tail\\", "prefix token=\"<redacted>")]
    [DataRow(true, "prefix token=\"head review-1487-secret tail\\", "prefix token=\"<redacted>")]
    [DataRow(false, "prefix {\"password\":\"head\\\"hidden review-1487-secret tail\",\"user\":\"owner\"} suffix", "prefix {\"password\":\"<redacted>\",\"user\":\"owner\"} suffix")]
    [DataRow(true, "prefix {\"password\":\"head\\\"hidden review-1487-secret tail\",\"user\":\"owner\"} suffix", "prefix {\"password\":\"<redacted>\",\"user\":\"owner\"} suffix")]
    // Ordinary closed values keep the form and suffix the quoted-credential patterns always gave them.
    [DataRow(false, "prefix token=\"head review-1487-secret tail\\\\\" suffix", "prefix token=\"<redacted>\" suffix")]
    [DataRow(true, "prefix token=\"head review-1487-secret tail\\\\\" suffix", "prefix token=\"<redacted>\" suffix")]
    [DataRow(false, "prefix token=\"one review-1487-secret tail\" middle secret='two review-1487-secret tail' suffix", "prefix token=\"<redacted>\" middle secret='<redacted>' suffix")]
    [DataRow(true, "prefix token=\"one review-1487-secret tail\" middle secret='two review-1487-secret tail' suffix", "prefix token=\"<redacted>\" middle secret='<redacted>' suffix")]
    [DataRow(false, "prefix token=\"head review-1487-secret tail\" suffix", "prefix token=\"<redacted>\" suffix")]
    [DataRow(true, "prefix token=\"head review-1487-secret tail\" suffix", "prefix token=\"<redacted>\" suffix")]
    public async Task Diagnostic1476R2_SqlitePlainQuotedVariants(bool json, string diagnostic, string expected)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        ArgumentNullException.ThrowIfNull(expected);
        using var fixture = await MigratingFixtureAsync();
        var target = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, AutomationStore);
        ExecuteSql(target, FixtureDump(AutomationStore));
        SaveDefinition(target, "review-agent", 1);
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
        Console.WriteLine(JsonSerializer.Serialize(output));
        Assert.IsFalse(output.Contains("review-1487-secret", StringComparison.Ordinal), "Credential tail leaked: " + output);
        Assert.IsFalse(output.TrimEnd('\r', '\n').Any(char.IsControl));
        StringAssert.Contains(output, JsonEncodedText.Encode(expected).ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DoNotParallelize]
    [DataRow(false, "token", "review-1487-secret", "claim token=<redacted> of account owner removed again")]
    [DataRow(true, "token", "review-1487-secret", "claim token=<redacted> of account owner removed again")]
    [DataRow(false, "password", "review-1487-secret", "claim password=<redacted> of account owner removed again")]
    [DataRow(true, "password", "review-1487-secret", "claim password=<redacted> of account owner removed again")]
    [DataRow(false, "secret", "review-1487-secret", "claim secret=<redacted> of account owner removed again")]
    [DataRow(true, "secret", "review-1487-secret", "claim secret=<redacted> of account owner removed again")]
    [DataRow(false, "token", "review-1487-secret tail words", "claim token=<redacted> of account owner removed again")]
    [DataRow(true, "token", "review-1487-secret tail words", "claim token=<redacted> of account owner removed again")]
    // An ordinary claim keeps its exact wording, spaces in its value included.
    [DataRow(false, "scope", "camera read", "claim scope=camera read of account owner removed again")]
    [DataRow(true, "scope", "camera read", "claim scope=camera read of account owner removed again")]
    public async Task Diagnostic1476R2_ClaimAssignmentIsRedactedWhole(bool json, string type, string value, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        using var fixture = await BackupFixtureAsync();
        var identity = CameraAgentStateLayout.IdentityDatabasePath(fixture.Paths.StateRoot);
        Execute(identity, $"INSERT INTO AspNetUserClaims(UserId, ClaimType, ClaimValue) VALUES('owner', '{type}', '{value}');");
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        Execute(identity, $"DELETE FROM AspNetUserClaims WHERE ClaimType='{type}';");
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
        Assert.IsFalse(output.Contains("review-1487-secret", StringComparison.Ordinal), "A credential assembled from two database fields was exposed.");
        StringAssert.Contains(output, JsonEncodedText.Encode(expected).ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Diagnostic1476R2_HardLinkRefusalIsFormattedOnce(bool json)
    {
        using var fixture = await BackupFixtureAsync();
        var path = Path.Combine(fixture.Paths.ConfigRoot, HostileDiagnostic1476Name);
        await File.WriteAllTextAsync(path, "ordinary file");
        NativeLinux.CreateHardLink(path, Path.Combine(fixture.Root, "outside-link"));
        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => BackupAsync(fixture, new FakeLifecycleClient()));
        Console.WriteLine(JsonSerializer.Serialize(failure.Message));
        Assert.IsTrue(failure.IsFormattedDiagnostic, "A composed backup diagnostic must retain its formatting marker.");
        Assert.AreEqual("The backup failed, and nothing was kept; the instance is as it was. " +
            CameraAgentStateDiagnostics.Reason($"Input file '{path}' must not have hard links."), failure.Message);
        var output = await RenderDiagnostic1476ErrorAsync(failure.Message, json);
        AssertDiagnostic1476Safe(output);
        Assert.IsFalse(output.Contains('\u001b', StringComparison.Ordinal), "A raw typed refusal was carried under the formatting marker.");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DoNotParallelize]
    [DataRow(false, "backup", false)]
    [DataRow(true, "backup", false)]
    [DataRow(false, "restore-staging", false)]
    [DataRow(true, "restore-staging", false)]
    [DataRow(false, "restore-swap", false)]
    [DataRow(true, "restore-swap", false)]
    // Already formatted typed failures are carried exactly once, never escaped again.
    [DataRow(false, "backup", true)]
    [DataRow(true, "backup", true)]
    [DataRow(false, "restore-staging", true)]
    [DataRow(true, "restore-staging", true)]
    [DataRow(false, "restore-swap", true)]
    [DataRow(true, "restore-swap", true)]
    public async Task Diagnostic1476R2_TypedFailureIsFormattedOnceInComposition(bool json, string site, bool preformatted)
    {
        using var fixture = await BackupFixtureAsync();
        const string raw = "typed failure " + HostileDiagnostic1476Name;
        var formatted = CameraAgentStateDiagnostics.Reason(raw);
        InstallerException Fault() => preformatted ? InstallerException.Formatted(formatted) : new InstallerException(raw);
        string? archive = null;
        if (site != "backup")
        {
            archive = (await BackupAsync(fixture, new FakeLifecycleClient())).Backup!.Archive;
            await UninstallAsync(fixture);
        }
        var snapshotStep = CameraAgentStateSnapshot.StepCompleted;
        var backupStep = CameraAgentLifecycleManager.BackupStepCompleted;
        InstallerException failure;
        try
        {
            switch (site)
            {
                case "backup":
                    CameraAgentStateSnapshot.StepCompleted = step => { if (step.StartsWith("snapshot:", StringComparison.Ordinal)) throw Fault(); };
                    break;
                case "restore-staging":
                    CameraAgentLifecycleManager.BackupStepCompleted = step => { if (step == "restore-staged") throw Fault(); };
                    break;
                default:
                    CameraAgentStateSnapshot.StepCompleted = step => { if (step.StartsWith("restore:", StringComparison.Ordinal)) throw Fault(); };
                    break;
            }
            failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => archive is null
                ? BackupAsync(fixture, new FakeLifecycleClient())
                : RestoreAsync(fixture, archive));
        }
        finally
        {
            CameraAgentStateSnapshot.StepCompleted = snapshotStep;
            CameraAgentLifecycleManager.BackupStepCompleted = backupStep;
        }
        Console.WriteLine(JsonSerializer.Serialize(failure.Message));
        var expected = site switch
        {
            "backup" => $"The backup failed, and nothing was kept; the instance is as it was. {formatted}",
            "restore-staging" => $"The restore failed before it replaced anything, and the instance is as it was. {formatted}",
            _ => $"The restore stopped after it began replacing the instance: {formatted} Rerun the same command with --resume to finish it."
        };
        Assert.IsTrue(failure.IsFormattedDiagnostic, "A composed diagnostic must retain its formatting marker.");
        Assert.AreEqual(expected, failure.Message);
        var output = await RenderDiagnostic1476ErrorAsync(failure.Message, json);
        AssertDiagnostic1476Safe(output);
        Assert.IsFalse(output.Contains('\u001b', StringComparison.Ordinal), "A raw typed failure was carried under the formatting marker.");
    }
}
