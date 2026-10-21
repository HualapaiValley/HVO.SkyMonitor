using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DoNotParallelize]
    // F7: decoration on the key, CSI, OSC and 8-bit CSI alike.
    [DataRow(false, "p \u001b[1mpassword\u001b[0m=review-1491-secret s", "p password=<redacted> s")]
    [DataRow(true, "p \u001b[1mpassword\u001b[0m=review-1491-secret s", "p password=<redacted> s")]
    [DataRow(false, "\u001b[1mtoken\u001b[0m: \"a review-1491-secret b\"", "token: \"<redacted>\"")]
    [DataRow(true, "\u001b[1mtoken\u001b[0m: \"a review-1491-secret b\"", "token: \"<redacted>\"")]
    [DataRow(false, "\u001b]8;;https://example.test\u001b\\token\u001b]8;;\u001b\\=\"a review-1491-secret b\"", "token=\"<redacted>\"")]
    [DataRow(true, "\u001b]8;;https://example.test\u001b\\token\u001b]8;;\u001b\\=\"a review-1491-secret b\"", "token=\"<redacted>\"")]
    [DataRow(false, "\u009b1mpassword\u009b0m=\"a review-1491-secret b\"", "password=\"<redacted>\"")]
    [DataRow(true, "\u009b1mpassword\u009b0m=\"a review-1491-secret b\"", "password=\"<redacted>\"")]
    // F8: compound and camelCase keys, and a colour code directly before the key.
    [DataRow(false, "db_password=\"correct review-1491-secret staple\"", "db_password=\"<redacted>\"")]
    [DataRow(true, "db_password=\"correct review-1491-secret staple\"", "db_password=\"<redacted>\"")]
    [DataRow(false, "{\"client_secret\":\"a\\\"review-1491-secret\"}", "{\"client_secret\":\"<redacted>\"}")]
    [DataRow(true, "{\"client_secret\":\"a\\\"review-1491-secret\"}", "{\"client_secret\":\"<redacted>\"}")]
    [DataRow(false, "accessToken='a,review-1491-secret'", "accessToken='<redacted>")]
    [DataRow(true, "accessToken='a,review-1491-secret'", "accessToken='<redacted>")]
    [DataRow(false, "x-api-key=\"a review-1491-secret b\"", "x-api-key=\"<redacted>\"")]
    [DataRow(true, "x-api-key=\"a review-1491-secret b\"", "x-api-key=\"<redacted>\"")]
    [DataRow(false, "\u001b[31mtoken=\"a review-1491-secret b\"", "token=\"<redacted>\"")]
    [DataRow(true, "\u001b[31mtoken=\"a review-1491-secret b\"", "token=\"<redacted>\"")]
    // Ordinary words keep their values: a word that only starts with a credential word, a key that does not end with
    // one, and a key ending in plain "key".
    [DataRow(false, "tokenid=\"kept value\"", "tokenid=\"kept value\"")]
    [DataRow(true, "tokenid=\"kept value\"", "tokenid=\"kept value\"")]
    [DataRow(false, "secret-santa=\"kept value\"", "secret-santa=\"kept value\"")]
    [DataRow(true, "secret-santa=\"kept value\"", "secret-santa=\"kept value\"")]
    [DataRow(false, "turnkey=\"kept value\"", "turnkey=\"kept value\"")]
    [DataRow(true, "turnkey=\"kept value\"", "turnkey=\"kept value\"")]
    public async Task Diagnostic1491_DecoratedAndCompoundKeysAreRedacted(bool json, string diagnostic, string expected)
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
        Assert.IsFalse(output.Contains("review-1491-secret", StringComparison.Ordinal), "Credential leaked: " + output);
        Assert.IsFalse(output.TrimEnd('\r', '\n').Any(char.IsControl));
        Assert.IsFalse(output.Contains("\\u001B", StringComparison.Ordinal) || output.Contains("\\u009B", StringComparison.Ordinal),
            "Decoration reached the output: " + output);
        StringAssert.Contains(output, JsonEncodedText.Encode(expected).ToString(), StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DoNotParallelize]
    [DataRow(false, "\u001b[1mtoken\u001b[0m", "review-1491-secret", "claim token=<redacted> of account owner removed again")]
    [DataRow(true, "\u001b[1mtoken\u001b[0m", "review-1491-secret", "claim token=<redacted> of account owner removed again")]
    // A decorated ordinary claim keeps its value.
    [DataRow(false, "scope\u001b[31m", "camera read", "claim scope=camera read of account owner removed again")]
    [DataRow(true, "scope\u001b[31m", "camera read", "claim scope=camera read of account owner removed again")]
    public async Task Diagnostic1491_DecoratedClaimTypeIsRedactedWhole(bool json, string type, string value, string expected)
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
        Assert.IsFalse(output.Contains("review-1491-secret", StringComparison.Ordinal), "A decorated credential claim was exposed.");
        StringAssert.Contains(output, JsonEncodedText.Encode(expected).ToString(), StringComparison.Ordinal);
    }

    // F9: the top-level writer carries a formatted refusal as it is, so a hostile entry name cannot redact its fixed wording.
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DoNotParallelize]
    [DataRow(false, "nightly password:")]
    [DataRow(true, "nightly password:")]
    [DataRow(false, "nightly secret=")]
    [DataRow(true, "nightly secret=")]
    [DataRow(false, "nightly token ")]
    [DataRow(true, "nightly token ")]
    // A credential in the name is still hidden, by the formatter that composed the refusal.
    [DataRow(false, "nightly token=\"review-1491-secret tail\"")]
    [DataRow(true, "nightly token=\"review-1491-secret tail\"")]
    public async Task Diagnostic1491_FormattedRefusalKeepsItsFixedWording(bool json, string name)
    {
        using var fixture = await BackupFixtureAsync();
        var link = Path.Combine(fixture.Paths.InstanceRoot, name);
        File.CreateSymbolicLink(link, "absent");
        int exitCode;
        string error;
        try
        {
            (exitCode, error) = await RunDiagnostic1491Async(
                fixture.Request(LifecycleOperationKind.Backup) with { Json = json },
                (request, cancellationToken) => CameraAgentLifecycleManager.ExecuteAsync(
                    request, fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
                    fixture.Uid, fixture.Gid, cancellationToken, error: TextWriter.Null));
        }
        finally
        {
            File.Delete(link);
        }
        Console.WriteLine(JsonSerializer.Serialize(error));
        var expected = $"The instance holds '{CameraAgentStateDiagnostics.Reason(name)}', which is neither a file nor a folder, so it " +
            $"cannot be backed up. Move it out of {CameraAgentStateDiagnostics.Reason(fixture.Paths.InstanceRoot)}, then run the backup again.";
        Assert.AreEqual(3, exitCode);
        Assert.AreEqual(json ? Diagnostic1491JsonError("refused", expected) : $"Installation refused: {expected} Nothing was changed.",
            error.TrimEnd('\r', '\n'));
        Assert.IsFalse(error.Contains("review-1491-secret", StringComparison.Ordinal), error);
    }

    // F9 negative controls: a message the formatter did not compose is still redacted at the top level.
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DoNotParallelize]
    [DataRow(false, "failure")]
    [DataRow(true, "failure")]
    [DataRow(false, "refusal")]
    [DataRow(true, "refusal")]
    [DataRow(false, "unexpected")]
    [DataRow(true, "unexpected")]
    public async Task Diagnostic1491_RawMessageIsStillRedactedAtTheTopLevel(bool json, string kind)
    {
        Exception failure = kind switch
        {
            "failure" => new InstallerException("raw failure token=review-1491-secret tail"),
            "refusal" => InstallerException.Refused("raw refusal password:'review-1491-secret tail'"),
            _ => new IOException("raw io token=review-1491-secret")
        };
        var (exitCode, error) = await RunDiagnostic1491Async(
            new LifecycleRequest(LifecycleOperationKind.Backup, Guid.NewGuid(), "/var/lib/hvo-sky", false, false, json),
            (_, _) => throw failure);
        Console.WriteLine(JsonSerializer.Serialize(error));
        var (code, expected, exit) = kind switch
        {
            "failure" => ("installation-failed", "raw failure token=<redacted> tail", 1),
            "refusal" => ("refused", "raw refusal password:'<redacted>'", 3),
            _ => ("installation-failed", "Unexpected IOException: raw io token=<redacted>", 1)
        };
        Assert.AreEqual(exit, exitCode);
        Assert.AreEqual(json
                ? Diagnostic1491JsonError(code, expected)
                : exit == 3 ? $"Installation refused: {expected} Nothing was changed." : $"Installation failed: {expected}",
            error.TrimEnd('\r', '\n'));
    }

    // F11: every vault refusal redacts and escapes the archive name and the vault root it echoes.
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DoNotParallelize]
    [DataRow(false, "unrecorded")]
    [DataRow(true, "unrecorded")]
    [DataRow(false, "partial")]
    [DataRow(true, "partial")]
    [DataRow(false, "name")]
    [DataRow(true, "name")]
    [DataRow(false, "absent-folder")]
    [DataRow(true, "absent-folder")]
    [DataRow(false, "admit")]
    [DataRow(true, "admit")]
    public async Task Diagnostic1491_VaultRefusalIsRedactedAndEscaped(bool json, string site)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"hvo-1491-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var root = Path.Combine(scratch, site == "name" ? "backups" : HostileDiagnostic1476Name);
            if (site != "absent-folder")
            {
                Directory.CreateDirectory(root);
                File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    (site == "admit" ? UnixFileMode.GroupRead : UnixFileMode.None));
            }
            var files = new BackupVaultFiles(root);
            var name = BackupVaultFiles.NameFor(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid());
            var failure = Assert.ThrowsExactly<InstallerException>(() =>
            {
                switch (site)
                {
                    case "unrecorded": files.OpenRead(name, partial: false).Dispose(); break;
                    case "partial": files.OpenRead(name, partial: true).Dispose(); break;
                    case "name": _ = files.ReadRecord(HostileDiagnostic1476Name); break;
                    case "admit": files.Admit(); break;
                    default: files.OpenRead(name, partial: false).Dispose(); break;
                }
            });
            Assert.IsTrue(failure.IsRefusal);
            var output = await RenderDiagnostic1476ErrorAsync(failure.Message, json, failure.IsFormattedDiagnostic);
            Console.WriteLine(JsonSerializer.Serialize(output));
            AssertDiagnostic1476Safe(output);
            Assert.IsTrue(failure.IsFormattedDiagnostic, "A vault refusal must be formatted by the shared formatter.");
            StringAssert.Contains(output, CameraAgentStateDiagnostics.Reason(site == "name" ? HostileDiagnostic1476Name : files.Root),
                StringComparison.Ordinal);
            StringAssert.Contains(output, site switch
            {
                "unrecorded" => "is not a recorded backup in",
                "partial" => "' is not in ",
                "name" => "is not the name of a backup this installer writes.",
                "admit" => "not 0700, the service account's alone. Make it so (chmod 700 ",
                _ => "There is no backup folder "
            }, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static string Diagnostic1491JsonError(string code, string message)
        => JsonSerializer.Serialize(new { schemaVersion = 1, errorCode = code, message });

    /// <summary>Runs <paramref name="request"/> through the real top-level command runner and returns its exit code and error stream.</summary>
    private static async Task<(int ExitCode, string Error)> RunDiagnostic1491Async(
        LifecycleRequest request, Func<LifecycleRequest, CancellationToken, Task<LifecycleResult>> lifecycle)
    {
        var services = ProgramServices.Production with { Parse = _ => request, Lifecycle = lifecycle, Wizard = null };
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var exitCode = await HVO.SkyMonitor.Deployment.Program.RunAsync(
                request.Json ? ["cameraagent", "backup", "--json"] : ["cameraagent", "backup"], services, CancellationToken.None);
            return (exitCode, error.ToString());
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
        }
    }
}
