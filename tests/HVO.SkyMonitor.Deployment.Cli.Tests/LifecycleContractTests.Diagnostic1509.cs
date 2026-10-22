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

    // Each expected value is the redacted text before the writer escapes the controls redaction leaves, such as a tab. The marker
    // ends in the credential word secret, so a row whose quoted value only the second passes find puts the marker last. A quoted
    // value is followed by a space and more text where the row checks that text, because SQLite quotes the whole message: a
    // single quote would be doubled, and text joined to a closing quote is hidden with the value.
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
    [DataRow(false, "ObjectStorage__SecretKey: \"a b " + Diagnostic1509Secret + "\"", "ObjectStorage__SecretKey: \"<redacted>\"")]
    [DataRow(true, "ObjectStorage__SecretKey: \"a b " + Diagnostic1509Secret + "\"", "ObjectStorage__SecretKey: \"<redacted>\"")]
    [DataRow(false, "SecretKey " + Diagnostic1509Secret, "SecretKey <redacted>")]
    [DataRow(true, "SecretKey " + Diagnostic1509Secret, "SecretKey <redacted>")]
    // A word that only starts with a secret key's words stays ordinary.
    [DataRow(false, "secretkeeper=kept-value", "secretkeeper=kept-value")]
    [DataRow(true, "secretkeeper=kept-value", "secretkeeper=kept-value")]
    [DataRow(false, "secretkeeper kept value", "secretkeeper kept value")]
    [DataRow(true, "secretkeeper kept value", "secretkeeper kept value")]
    // Text joined to the closing quote may be part of the value, so the space-only pass still hides it with the value.
    [DataRow(false, "db_password \"a b\"" + Diagnostic1509Secret, "db_password <redacted>")]
    [DataRow(true, "db_password \"a b\"" + Diagnostic1509Secret, "db_password <redacted>")]
    // A quoted value after a tab, a no-break space or a NEL is hidden whole; before, every word after the first was shown.
    [DataRow(false, "db_password\t\"a " + Diagnostic1509Secret + " b\" tail", "db_password\t<redacted> tail")]
    [DataRow(true, "db_password\t\"a " + Diagnostic1509Secret + " b\" tail", "db_password\t<redacted> tail")]
    [DataRow(false, "token\t'a " + Diagnostic1509Secret + " b' tail", "token\t<redacted> tail")]
    [DataRow(true, "token\t'a " + Diagnostic1509Secret + " b' tail", "token\t<redacted> tail")]
    [DataRow(false, "xtoken\t\"a b " + Diagnostic1509Secret + "\"", "xtoken\t\"<redacted>\"")]
    [DataRow(true, "xtoken\t\"a b " + Diagnostic1509Secret + "\"", "xtoken\t\"<redacted>\"")]
    [DataRow(false, "db_password\u00a0\"a " + Diagnostic1509Secret + " b\" tail", "db_password\u00a0<redacted> tail")]
    [DataRow(true, "db_password\u00a0\"a " + Diagnostic1509Secret + " b\" tail", "db_password\u00a0<redacted> tail")]
    [DataRow(false, "password\u0085\"a " + Diagnostic1509Secret + " b\" tail", "password<redacted> tail")]
    [DataRow(true, "password\u0085\"a " + Diagnostic1509Secret + " b\" tail", "password<redacted> tail")]
    // PR-1521-R1 F1: a new spelling or separator around a value the first passes hid cannot show it again. Text joined to a
    // closing quote is the same shell word, so it is part of the value, as is another quoted part joined to it.
    [DataRow(false, "ObjectStorage__SecretKey=\"password=\"" + Diagnostic1509Secret, "ObjectStorage__SecretKey=\"<redacted>\"")]
    [DataRow(true, "ObjectStorage__SecretKey=\"password=\"" + Diagnostic1509Secret, "ObjectStorage__SecretKey=\"<redacted>\"")]
    [DataRow(false, "--accessToken\t\"password=\"" + Diagnostic1509Secret, "--accessToken\t\"<redacted>\"")]
    [DataRow(true, "--accessToken\t\"password=\"" + Diagnostic1509Secret, "--accessToken\t\"<redacted>\"")]
    [DataRow(false, "secret_key=\"a b\"" + Diagnostic1509Secret, "secret_key=\"<redacted>\"")]
    [DataRow(true, "secret_key=\"a b\"" + Diagnostic1509Secret, "secret_key=\"<redacted>\"")]
    [DataRow(false, "token=\"a\"'" + Diagnostic1509Secret + "' tail", "token=\"<redacted>\" tail")]
    [DataRow(true, "token=\"a\"'" + Diagnostic1509Secret + "' tail", "token=\"<redacted>\" tail")]
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

    // PR-1521-R1 F1: a report field is redacted whole, so a quoted value may follow its key on the next line. A space-only value
    // runs to a space, comma or semicolon, as it did before, so it may take the next line with it.
    [TestMethod]
    [DataRow("--accessToken\n\"password=\"" + Diagnostic1509Secret + "\nremedy", "--accessToken\\n\"<redacted>\"")]
    [DataRow("db_password\n\"a b " + Diagnostic1509Secret + "\"\nremedy", "db_password\\n<redacted>")]
    public void Diagnostic1509_ReportFieldHidesAQuotedValueOnTheNextLine(string field, string expected)
    {
        var text = CameraAgentStateDiagnostics.Text(field);
        Assert.IsFalse(text.Contains(Diagnostic1509Secret, StringComparison.Ordinal), "Credential leaked: " + text);
        Assert.AreEqual(expected, text);
    }

    // PR-1521-R1 F2: a name that already reads as redacted still reads as a credential, so the whole claim value is hidden.
    [TestMethod]
    [DataRow("token \"<redacted>\" ", true)]
    [DataRow("token '<redacted>' ", true)]
    [DataRow("password=\"<redacted>\" ", true)]
    [DataRow("password='<redacted>' ", true)]
    [DataRow("token: \"<redacted>\"", true)]
    [DataRow("scope <redacted>", false)]
    public void Diagnostic1509_RedactedLookingNameHidesTheWholeValue(string name, bool credential)
    {
        var shown = CameraAgentStateDiagnostics.Assignment(name, Diagnostic1509Secret);
        Assert.AreEqual(credential, !shown.Contains(Diagnostic1509Secret, StringComparison.Ordinal), shown);
        Assert.AreEqual(CameraAgentStateDiagnostics.Reason(name + "=" + (credential ? "<redacted>" : Diagnostic1509Secret)), shown);
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
