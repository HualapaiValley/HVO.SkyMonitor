using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    // The marker the shared SQLite path asserts is absent, so a leak on that path fails the row as well as a changed text.
    private const string Diagnostic1509Secret = "review-1491-secret";

    // Read the statically discoverable rows again for the SQLite path and the differential corpus; do not keep a second list.
    private static IEnumerable<object[]> Diagnostic1509Cases()
        => typeof(LifecycleContractTests).GetMethod(nameof(Diagnostic1509_KeysAreRedactedAtTheTopLevel))!
            .GetCustomAttributes(typeof(DataRowAttribute), inherit: false).Cast<DataRowAttribute>()
            .Select(row => row.Data.Select(value => value!).ToArray());

    // Each expected value is the redacted text before the writer escapes the controls redaction leaves, such as a tab. A quoted
    // value that keeps its quotes is followed by a space and more text, because SQLite quotes the whole message: a single quote
    // would be doubled, and text joined to a closing quote is hidden with the value.
    [TestMethod]
    [DoNotParallelize]
    // PR-1508-R1 F1: the words of a secret key may be joined by -, _ or nothing, and may name an access key.
    [DataRow(false, "SecretKey=" + Diagnostic1509Secret, "SecretKey=<redacted>")]
    [DataRow(true, "SecretKey=" + Diagnostic1509Secret, "SecretKey=<redacted>")]
    [DataRow(false, "ObjectStorage__SecretKey=" + Diagnostic1509Secret, "ObjectStorage__SecretKey=<redacted>")]
    [DataRow(true, "ObjectStorage__SecretKey=" + Diagnostic1509Secret, "ObjectStorage__SecretKey=<redacted>")]
    [DataRow(false, "{\"SecretKey\":\"" + Diagnostic1509Secret + "\"}", "{\"SecretKey\":\"<redacted>\"}")]
    [DataRow(true, "{\"SecretKey\":\"" + Diagnostic1509Secret + "\"}", "{\"SecretKey\":\"<redacted>\"}")]
    [DataRow(false, "SecretAccessKey=" + Diagnostic1509Secret, "SecretAccessKey=<redacted>")]
    [DataRow(true, "SecretAccessKey=" + Diagnostic1509Secret, "SecretAccessKey=<redacted>")]
    [DataRow(false, "secret-access-key=" + Diagnostic1509Secret, "secret-access-key=<redacted>")]
    [DataRow(true, "secret-access-key=" + Diagnostic1509Secret, "secret-access-key=<redacted>")]
    [DataRow(false, "ObjectStorage__SecretKey: \"a " + Diagnostic1509Secret + " b\"", "ObjectStorage__SecretKey: \"<redacted>\"")]
    [DataRow(true, "ObjectStorage__SecretKey: \"a " + Diagnostic1509Secret + " b\"", "ObjectStorage__SecretKey: \"<redacted>\"")]
    [DataRow(false, "SecretKey " + Diagnostic1509Secret, "SecretKey <redacted>")]
    [DataRow(true, "SecretKey " + Diagnostic1509Secret, "SecretKey <redacted>")]
    // A word that only starts with a secret key's words stays ordinary.
    [DataRow(false, "secretkeeper=kept-value", "secretkeeper=kept-value")]
    [DataRow(true, "secretkeeper=kept-value", "secretkeeper=kept-value")]
    [DataRow(false, "secretkeeper kept value", "secretkeeper kept value")]
    [DataRow(true, "secretkeeper kept value", "secretkeeper kept value")]
    // PR-1508-R1 F3: a quoted value after a space-only key keeps its quotes.
    [DataRow(false, "db_password \"a " + Diagnostic1509Secret + " b\" tail", "db_password \"<redacted>\" tail")]
    [DataRow(true, "db_password \"a " + Diagnostic1509Secret + " b\" tail", "db_password \"<redacted>\" tail")]
    // Text joined to the closing quote may be part of the value, so the space-only pass still hides it with the value.
    [DataRow(false, "db_password \"a b\"" + Diagnostic1509Secret, "db_password <redacted>")]
    [DataRow(true, "db_password \"a b\"" + Diagnostic1509Secret, "db_password <redacted>")]
    [DataRow(false, "x-api-key 'a " + Diagnostic1509Secret + " b' tail", "x-api-key '<redacted>' tail")]
    [DataRow(true, "x-api-key 'a " + Diagnostic1509Secret + " b' tail", "x-api-key '<redacted>' tail")]
    // A quoted value after a tab, a no-break space or a NEL is hidden whole; before, every word after the first was shown.
    [DataRow(false, "db_password\t\"a " + Diagnostic1509Secret + " b\" tail", "db_password\t\"<redacted>\" tail")]
    [DataRow(true, "db_password\t\"a " + Diagnostic1509Secret + " b\" tail", "db_password\t\"<redacted>\" tail")]
    [DataRow(false, "token\t'a " + Diagnostic1509Secret + " b' tail", "token\t'<redacted>' tail")]
    [DataRow(true, "token\t'a " + Diagnostic1509Secret + " b' tail", "token\t'<redacted>' tail")]
    [DataRow(false, "xtoken\t\"a " + Diagnostic1509Secret + " b\"", "xtoken\t\"<redacted>\"")]
    [DataRow(true, "xtoken\t\"a " + Diagnostic1509Secret + " b\"", "xtoken\t\"<redacted>\"")]
    [DataRow(false, "db_password\u00a0\"a " + Diagnostic1509Secret + " b\" tail", "db_password\u00a0\"<redacted>\" tail")]
    [DataRow(true, "db_password\u00a0\"a " + Diagnostic1509Secret + " b\" tail", "db_password\u00a0\"<redacted>\" tail")]
    [DataRow(false, "password\u0085\"a " + Diagnostic1509Secret + " b\" tail", "password\"<redacted>\" tail")]
    [DataRow(true, "password\u0085\"a " + Diagnostic1509Secret + " b\" tail", "password\"<redacted>\" tail")]
    [DataRow(false, "api_key=db_password\t\"a " + Diagnostic1509Secret + " b\"", "api_key=<redacted>\t\"<redacted>\"")]
    [DataRow(true, "api_key=db_password\t\"a " + Diagnostic1509Secret + " b\"", "api_key=<redacted>\t\"<redacted>\"")]
    public async Task Diagnostic1509_KeysAreRedactedAtTheTopLevel(bool json, string diagnostic, string expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var (exit, error) = await RunDiagnostic1491Async(
            new LifecycleRequest(LifecycleOperationKind.Backup, Guid.NewGuid(), "/var/lib/hvo-sky", false, false, json),
            (_, _) => throw new InstallerException(diagnostic));
        Assert.AreEqual(1, exit);
        if (json)
        {
            using var document = JsonDocument.Parse(error);
            error = document.RootElement.GetProperty("message").GetString()!;
        }
        else error = error.TrimEnd('\r', '\n')["Installation failed: ".Length..];
        Assert.IsFalse(error.Contains(Diagnostic1509Secret, StringComparison.Ordinal), "Credential leaked: " + error);
        Assert.AreEqual(expected.Replace("\t", "\\t", StringComparison.Ordinal), error);
    }

    // PR-1508-R1 F2: both top-level writers redact a usage error too, and this one used to lose the word after its first option.
    [TestMethod]
    public void Diagnostic1509_PasswordSourceUsageErrorKeepsEveryWord()
    {
        var request = new InstallRequest
        {
            FriendlyName = "North Camera",
            OwnerEmail = "admin@example.test",
            CatalogBundle = "/srv/hvo/catalog.bundle",
            ImageReference = $"ghcr.io/example/cameraagent@sha256:{new string('a', 64)}",
            PasswordFile = "/srv/hvo/password",
            GeneratePassword = true
        };
        var failure = Assert.ThrowsExactly<InstallUsageException>(request.Validate);
        StringAssert.Contains(failure.Message, "cannot be combined", StringComparison.Ordinal);
        Assert.AreEqual(failure.Message, CameraAgentStateDiagnostics.Message(failure.Message));
    }

    // The same rows through the real RAISE(ABORT) -> PlanCarry -> restore warning path, in human and JSON output.
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DoNotParallelize]
    public async Task Diagnostic1509_KeysAreRedactedInTheRestoreWarning()
    {
        var failures = new List<string>();
        foreach (var row in Diagnostic1509Cases())
        {
            try
            {
                await Diagnostic1491_DecoratedAndCompoundKeysAreRedacted((bool)row[0], (string)row[1], (string)row[2]);
            }
            catch (AssertFailedException failure)
            {
                failures.Add($"{JsonSerializer.Serialize(row[1])} (json {row[0]}): {failure.Message}");
            }
        }
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }
}
