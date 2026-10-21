using System.Diagnostics;
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
    // F1 (PR-1496-R1): a malformed or dangling escape before a key keeps the key's first letter, and a string that names a
    // credential, or a lone control before a key, stays where it is while the key is matched.
    [DataRow(false, "\u001b[password=review-1491-secret", "[password=<redacted>")]
    [DataRow(true, "\u001b[password=review-1491-secret", "[password=<redacted>")]
    [DataRow(false, "\u001btoken=\"a review-1491-secret b\"", "token=\"<redacted>\"")]
    [DataRow(true, "\u001btoken=\"a review-1491-secret b\"", "token=\"<redacted>\"")]
    [DataRow(false, "\u009btoken=\"a review-1491-secret b\"", "token=\"<redacted>\"")]
    [DataRow(true, "\u009btoken=\"a review-1491-secret b\"", "token=\"<redacted>\"")]
    [DataRow(false, "\u001b[ token=\"a review-1491-secret b\"", "[ token=\"<redacted>\"")]
    [DataRow(true, "\u001b[ token=\"a review-1491-secret b\"", "[ token=\"<redacted>\"")]
    [DataRow(false, "x \u001b[0;secret: review-1491-secret", "x [0;secret: <redacted>")]
    [DataRow(true, "x \u001b[0;secret: review-1491-secret", "x [0;secret: <redacted>")]
    [DataRow(false, "\u001b(password=review-1491-secret", "(password=<redacted>")]
    [DataRow(true, "\u001b(password=review-1491-secret", "(password=<redacted>")]
    [DataRow(false, "x\u001bpassword=review-1491-secret", "xpassword=<redacted>")]
    [DataRow(true, "x\u001bpassword=review-1491-secret", "xpassword=<redacted>")]
    [DataRow(false, "db_\u001b[password=review-1491-secret", "db_[password=<redacted>")]
    [DataRow(true, "db_\u001b[password=review-1491-secret", "db_[password=<redacted>")]
    [DataRow(false, "\u001bPassword=\u001b\\review-1491-secret", "Password=<redacted>")]
    [DataRow(true, "\u001bPassword=\u001b\\review-1491-secret", "Password=<redacted>")]
    [DataRow(false, "\u001b]0;password=\u001b\\review-1491-secret", "]0;password=<redacted>")]
    [DataRow(true, "\u001b]0;password=\u001b\\review-1491-secret", "]0;password=<redacted>")]
    [DataRow(false, "x\u001btoken review-1491-secret", "xtoken <redacted>")]
    [DataRow(true, "x\u001btoken review-1491-secret", "xtoken <redacted>")]
    [DataRow(false, "x\u0085token review-1491-secret", "xtoken <redacted>")]
    [DataRow(true, "x\u0085token review-1491-secret", "xtoken <redacted>")]
    // F6 (PR-1496-R2): two credentials behind a quote. SQLite puts every message in quotes, so none may skip the first.
    [DataRow(false, "Connection failed: Host=db;Password=review-1491-secret;Token=abc", "Host=db;Password=<redacted>;Token=<redacted>")]
    [DataRow(true, "Connection failed: Host=db;Password=review-1491-secret;Token=abc", "Host=db;Password=<redacted>;Token=<redacted>")]
    [DataRow(false, "'(password:review-1491-secret,Token=x", "'(password:<redacted>,Token=<redacted>")]
    [DataRow(true, "'(password:review-1491-secret,Token=x", "'(password:<redacted>,Token=<redacted>")]
    [DataRow(false, "CHECK constraint failed: 'x' password=review-1491-secret, token=review-1491-secret",
        "'x' password=<redacted>, token=<redacted>")]
    [DataRow(true, "CHECK constraint failed: 'x' password=review-1491-secret, token=review-1491-secret",
        "'x' password=<redacted>, token=<redacted>")]
    [DataRow(false, "\"user=x password=review-1491-secret token=review-1491-secret\"", "\"user=x password=<redacted> token=<redacted>\"")]
    [DataRow(true, "\"user=x password=review-1491-secret token=review-1491-secret\"", "\"user=x password=<redacted> token=<redacted>\"")]
    [DataRow(false, "'--password=review-1491-secret --token=review-1491-secret'", "'--password=<redacted> --token=<redacted>'")]
    [DataRow(true, "'--password=review-1491-secret --token=review-1491-secret'", "'--password=<redacted> --token=<redacted>'")]
    // F8 (PR-1496-R2): removed decoration keeps the word boundary before a key after it, as the string did before.
    [DataRow(false, "see\u001b]8;;\u001b\\token review-1491-secret", "seetoken <redacted>")]
    [DataRow(true, "see\u001b]8;;\u001b\\token review-1491-secret", "seetoken <redacted>")]
    [DataRow(false, "link\u001b]8;;https://example.test\u001b\\docs\u001b]8;;\u001b\\token review-1491-secret", "linkdocstoken <redacted>")]
    [DataRow(true, "link\u001b]8;;https://example.test\u001b\\docs\u001b]8;;\u001b\\token review-1491-secret", "linkdocstoken <redacted>")]
    [DataRow(false, "0\u001b]\u0007Bearer review-1491-secret", "0Bearer <redacted>")]
    [DataRow(true, "0\u001b]\u0007Bearer review-1491-secret", "0Bearer <redacted>")]
    [DataRow(false, "x\u001b]0\u001b\\authorization: Basic review-1491-secret", "xauthorization: <redacted>")]
    [DataRow(true, "x\u001b]0\u001b\\authorization: Basic review-1491-secret", "xauthorization: <redacted>")]
    // F9 (PR-1496-R2): URL punctuation inside a removed string still marks the user information after it.
    [DataRow(false, "https\u001b]://\u001b\\user:review-1491-secret@host", "https]://<redacted>@host")]
    [DataRow(true, "https\u001b]://\u001b\\user:review-1491-secret@host", "https]://<redacted>@host")]
    [DataRow(false, "://review-1491-secret\u001bP@\u001b\\", "://<redacted>@\\")]
    [DataRow(true, "://review-1491-secret\u001bP@\u001b\\", "://<redacted>@\\")]
    // A CSI that is not decoration, between a key and its quoted value, still leaves the whole value redacted.
    [DataRow(false, "token: \u001b[2J\"a review-1491-secret b\"", "token: \"<redacted>\"")]
    [DataRow(true, "token: \u001b[2J\"a review-1491-secret b\"", "token: \"<redacted>\"")]
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
    // F1 (PR-1496-R1): a malformed or dangling escape before the claim's key word keeps its first letter.
    [DataRow(false, "\u001b[password", "correct review-1491-secret staple", "claim [password=<redacted> of account owner removed again")]
    [DataRow(true, "\u001b[password", "correct review-1491-secret staple", "claim [password=<redacted> of account owner removed again")]
    [DataRow(false, "\u001btoken", "correct review-1491-secret staple", "claim token=<redacted> of account owner removed again")]
    [DataRow(true, "\u001btoken", "correct review-1491-secret staple", "claim token=<redacted> of account owner removed again")]
    [DataRow(false, "\u009btoken", "correct review-1491-secret staple", "claim token=<redacted> of account owner removed again")]
    [DataRow(true, "\u009btoken", "correct review-1491-secret staple", "claim token=<redacted> of account owner removed again")]
    [DataRow(false, "\u001b[ token", "correct review-1491-secret staple", "claim [ token=<redacted> of account owner removed again")]
    [DataRow(true, "\u001b[ token", "correct review-1491-secret staple", "claim [ token=<redacted> of account owner removed again")]
    [DataRow(false, "x \u001b[0;secret", "correct review-1491-secret staple", "claim x [0;secret=<redacted> of account owner removed again")]
    [DataRow(true, "x \u001b[0;secret", "correct review-1491-secret staple", "claim x [0;secret=<redacted> of account owner removed again")]
    [DataRow(false, "\u001b(password", "correct review-1491-secret staple", "claim (password=<redacted> of account owner removed again")]
    [DataRow(true, "\u001b(password", "correct review-1491-secret staple", "claim (password=<redacted> of account owner removed again")]
    [DataRow(false, "x\u001bpassword", "correct review-1491-secret staple", "claim xpassword=<redacted> of account owner removed again")]
    [DataRow(true, "x\u001bpassword", "correct review-1491-secret staple", "claim xpassword=<redacted> of account owner removed again")]
    [DataRow(false, "db_\u001b[password", "correct review-1491-secret staple", "claim db_[password=<redacted> of account owner removed again")]
    [DataRow(true, "db_\u001b[password", "correct review-1491-secret staple", "claim db_[password=<redacted> of account owner removed again")]
    [DataRow(false, "\u001b[token", "correct review-1491-secret staple", "claim [token=<redacted> of account owner removed again")]
    [DataRow(true, "\u001b[token", "correct review-1491-secret staple", "claim [token=<redacted> of account owner removed again")]
    [DataRow(false, "\u009bpassword", "correct review-1491-secret staple", "claim password=<redacted> of account owner removed again")]
    [DataRow(true, "\u009bpassword", "correct review-1491-secret staple", "claim password=<redacted> of account owner removed again")]
    [DataRow(false, "\u001bsecret", "correct review-1491-secret staple", "claim secret=<redacted> of account owner removed again")]
    [DataRow(true, "\u001bsecret", "correct review-1491-secret staple", "claim secret=<redacted> of account owner removed again")]
    // F4: the decision rests on the name alone, so a CSI the name ends inside cannot take the value's first letter.
    [DataRow(false, "token\u001b[", "correct review-1491-secret staple", "claim token[=<redacted> of account owner removed again")]
    [DataRow(true, "token\u001b[", "correct review-1491-secret staple", "claim token[=<redacted> of account owner removed again")]
    // F7 (PR-1496-R2): a name that reads as a credential with its other controls in place, as the redactor matches it.
    [DataRow(false, "token\u0085x", "correct review-1491-secret staple", "claim token<redacted> of account owner removed again")]
    [DataRow(true, "token\u0085x", "correct review-1491-secret staple", "claim token<redacted> of account owner removed again")]
    [DataRow(false, "0\u001bToken q", "correct review-1491-secret staple", "claim 0Token <redacted> of account owner removed again")]
    [DataRow(true, "0\u001bToken q", "correct review-1491-secret staple", "claim 0Token <redacted> of account owner removed again")]
    [DataRow(false, "secret\u0085-", "correct review-1491-secret staple", "claim secret<redacted> of account owner removed again")]
    [DataRow(true, "secret\u0085-", "correct review-1491-secret staple", "claim secret<redacted> of account owner removed again")]
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
    // F6 (PR-1496-R2): two credentials behind a quote in a raw message.
    [DataRow(false, "quoted")]
    [DataRow(true, "quoted")]
    public async Task Diagnostic1491_RawMessageIsStillRedactedAtTheTopLevel(bool json, string kind)
    {
        Exception failure = kind switch
        {
            "failure" => new InstallerException("raw failure token=review-1491-secret tail"),
            "quoted" => new InstallerException(
                "Backup failed: Connection failed: \"Host=db;Username=app;Password=review-1491-secret;Token=review-1491-secret\""),
            "refusal" => InstallerException.Refused("raw refusal password:'review-1491-secret tail'"),
            _ => new IOException("raw io token=review-1491-secret")
        };
        var (exitCode, error) = await RunDiagnostic1491Async(
            new LifecycleRequest(LifecycleOperationKind.Backup, Guid.NewGuid(), "/var/lib/hvo-sky", false, false, json),
            (_, _) => throw failure);
        Console.WriteLine(JsonSerializer.Serialize(error));
        Assert.IsFalse(error.Contains("review-1491-secret", StringComparison.Ordinal), error);
        var (code, expected, exit) = kind switch
        {
            "failure" => ("installation-failed", "raw failure token=<redacted> tail", 1),
            "quoted" => ("installation-failed",
                "Backup failed: Connection failed: \"Host=db;Username=app;Password=<redacted>;Token=<redacted>\"", 1),
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

    // F2 (PR-1496-R1): a formatted error is not scanned again, so Describe itself hides a counter key that reads as a
    // credential. An ordinary key keeps every character.
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DoNotParallelize]
    [DataRow(false, "password=review-1491-secret", "password=<redacted>")]
    [DataRow(true, "password=review-1491-secret", "password=<redacted>")]
    [DataRow(false, "agent", "agent")]
    [DataRow(true, "agent", "agent")]
    public async Task Diagnostic1491_LoweredCounterKeyIsRedacted(bool json, string key, string shown)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(shown);
        using var fixture = await MigratingFixtureAsync();
        var target = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, AutomationStore);
        ExecuteSql(target, FixtureDump(AutomationStore));
        SaveDefinition(target, key, 1);
        var ensure = typeof(CameraAgentLifecycleManager).GetMethod("EnsureFloorsHold", BindingFlags.Static | BindingFlags.NonPublic)!;
        var thrown = Assert.ThrowsExactly<TargetInvocationException>(() => ensure.Invoke(
            null, [fixture.Paths, new StateCounterFloor[] { new(AutomationStore, "automation-definition-version", key, 2) }]));
        var failure = (InstallerException)thrown.InnerException!;
        Assert.IsTrue(failure.IsFormattedDiagnostic);
        var output = await RenderDiagnostic1476ErrorAsync(failure.Message, json, failure.IsFormattedDiagnostic);
        Console.WriteLine(JsonSerializer.Serialize(output));
        Assert.IsFalse(output.Contains("review-1491-secret", StringComparison.Ordinal), "A counter key exposed a credential: " + output);
        StringAssert.Contains(output,
            $"the automation-definition-version counter of '{AutomationStore}' with key \"{JsonEncodedText.Encode(shown)}\" is 1, below 2.",
            StringComparison.Ordinal);
    }

    // F3 (PR-1496-R1): a key followed by a long run of spaces is matched in linear time. Before the fix each of these threw
    // RegexMatchTimeoutException after about a second; plain token did so before #1491 as well.
    [TestMethod]
    [DataRow("xtoken", 65536)]
    [DataRow("xtoken", 1048576)]
    [DataRow("db_password", 65536)]
    [DataRow("db_password", 1048576)]
    [DataRow("accessToken", 1048576)]
    [DataRow("x-api-key", 1048576)]
    [DataRow("token", 1048576)]
    public void Diagnostic1491_KeyBeforeLongWhitespaceIsLinear(string key, int spaces)
    {
        var diagnostic = key + new string(' ', spaces) + "x";
        var watch = Stopwatch.StartNew();
        var redacted = Redaction.SafeDiagnostic(diagnostic);
        Console.WriteLine($"{key} + {spaces} spaces: {watch.ElapsedMilliseconds} ms");
        Assert.AreEqual(diagnostic[..256], redacted);
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
