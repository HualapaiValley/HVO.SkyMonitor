using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    // Holds no credential word, so a row shows whether the value itself is hidden rather than whether a key inside it is found.
    private const string Diagnostic1523Value = "review-1523-value";

    // Read the statically discoverable rows again for the differential corpus; do not keep a second list.
    private static IEnumerable<object[]> Diagnostic1523Cases()
        => typeof(LifecycleContractTests).GetMethod(nameof(Diagnostic1523_PreExistingGapsAreRedacted))!
            .GetCustomAttributes(typeof(DataRowAttribute), inherit: false).Cast<DataRowAttribute>()
            .Select(row => row.Data.Select(value => value!).ToArray());

    // Each expected value is the redacted text before the writer escapes the controls redaction leaves, such as a tab. The writer
    // cuts the line, so it ends at a value only the #1523 passes hide that is longer than its marker; uncut, the rest is kept.
    [TestMethod]
    [DoNotParallelize]
    // #1523 item 1: a quoted value that ends in a key leaves that key's value after the closing quote.
    [DataRow(false, "token=\"password=\" " + Diagnostic1523Value + " tail", "token=\"<redacted>\" <redacted>")]
    [DataRow(true, "token=\"password=\" " + Diagnostic1523Value + " tail", "token=\"<redacted>\" <redacted>")]
    [DataRow(false, "SecretKey=\"SecretKey=\" " + Diagnostic1523Value + " tail", "SecretKey=\"<redacted>\" <redacted>")]
    [DataRow(true, "SecretKey=\"SecretKey=\" " + Diagnostic1523Value + " tail", "SecretKey=\"<redacted>\" <redacted>")]
    [DataRow(false, "token='api_key: ' \"a " + Diagnostic1523Value + "\" tail", "token='<redacted>' <redacted>")]
    [DataRow(true, "token='api_key: ' \"a " + Diagnostic1523Value + "\" tail", "token='<redacted>' <redacted>")]
    // A word that is itself a key is not hidden as the value, so its own value is still found.
    [DataRow(false, "token=\"password=\" db_password " + Diagnostic1523Value, "token=\"<redacted>\" db_password <redacted>")]
    [DataRow(true, "token=\"password=\" db_password " + Diagnostic1523Value, "token=\"<redacted>\" db_password <redacted>")]
    [DataRow(false, "token=\"password=\" ObjectStorage__SecretKey " + Diagnostic1523Value, "token=\"<redacted>\" ObjectStorage__SecretKey <redacted>")]
    [DataRow(true, "token=\"password=\" ObjectStorage__SecretKey " + Diagnostic1523Value, "token=\"<redacted>\" ObjectStorage__SecretKey <redacted>")]
    // PR-1528-R0 F2: a key word inside the value, with no assignment of its own, does not keep the value shown.
    [DataRow(false, "token=\"password=\" mysecret tail", "token=\"<redacted>\" <redacted> tail")]
    [DataRow(true, "token=\"password=\" mysecret tail", "token=\"<redacted>\" <redacted> tail")]
    [DataRow(false, "token=\"password=\" \"mysecret\" tail", "token=\"<redacted>\" <redacted> tail")]
    [DataRow(true, "token=\"password=\" \"mysecret\" tail", "token=\"<redacted>\" <redacted> tail")]
    [DataRow(false, "token=\"password=\" '" + Diagnostic1523Value + "secret' tail", "token=\"<redacted>\" <redacted>")]
    [DataRow(true, "token=\"password=\" '" + Diagnostic1523Value + "secret' tail", "token=\"<redacted>\" <redacted>")]
    // #1523 item 2: an inner key that an outer assignment consumes still hides a quoted value after a tab.
    [DataRow(false, "api_key=db_password\t\"a " + Diagnostic1523Value + "\" tail", "api_key=<redacted>\t\"<redacted>")]
    [DataRow(true, "api_key=db_password\t\"a " + Diagnostic1523Value + "\" tail", "api_key=<redacted>\t\"<redacted>")]
    [DataRow(false, "api_key=password\t\"a " + Diagnostic1523Value + "\" tail", "api_key=<redacted>\t\"<redacted>")]
    [DataRow(true, "api_key=password\t\"a " + Diagnostic1523Value + "\" tail", "api_key=<redacted>\t\"<redacted>")]
    // PR-1528-R0 F3: the inner and outer keys may take the #1509 spellings and a configuration key's double underscore.
    [DataRow(false, "api_key=SecretKey\t\"a " + Diagnostic1523Value + "\" tail", "api_key=<redacted>\t\"<redacted>")]
    [DataRow(true, "api_key=SecretKey\t\"a " + Diagnostic1523Value + "\" tail", "api_key=<redacted>\t\"<redacted>")]
    [DataRow(false, "api_key=SecretAccessKey\t\"a " + Diagnostic1523Value + "\" tail", "api_key=<redacted>\t\"<redacted>")]
    [DataRow(true, "api_key=SecretAccessKey\t\"a " + Diagnostic1523Value + "\" tail", "api_key=<redacted>\t\"<redacted>")]
    [DataRow(false, "api_key=db__password\t\"a " + Diagnostic1523Value + "\" tail", "api_key=<redacted>\t\"<redacted>")]
    [DataRow(true, "api_key=db__password\t\"a " + Diagnostic1523Value + "\" tail", "api_key=<redacted>\t\"<redacted>")]
    [DataRow(false, "api_key=SecretKey\t'a " + Diagnostic1523Value + "' tail", "api_key=<redacted>\t'<redacted>")]
    [DataRow(true, "api_key=SecretKey\t'a " + Diagnostic1523Value + "' tail", "api_key=<redacted>\t'<redacted>")]
    [DataRow(false, "api_key: SecretKey\t\"a " + Diagnostic1523Value + "\" tail", "api_key: <redacted>\t\"<redacted>")]
    [DataRow(true, "api_key: SecretKey\t\"a " + Diagnostic1523Value + "\" tail", "api_key: <redacted>\t\"<redacted>")]
    [DataRow(false, "api_key=SecretKey\u0085\"a " + Diagnostic1523Value + "\" tail", "api_key=<redacted>\"<redacted>")]
    [DataRow(true, "api_key=SecretKey\u0085\"a " + Diagnostic1523Value + "\" tail", "api_key=<redacted>\"<redacted>")]
    [DataRow(false, "api_key=SecretKey\u00a0'a " + Diagnostic1523Value + "' tail", "api_key=<redacted>\u00a0'<redacted>")]
    [DataRow(true, "api_key=SecretKey\u00a0'a " + Diagnostic1523Value + "' tail", "api_key=<redacted>\u00a0'<redacted>")]
    [DataRow(false, "api_key=SecretKey \"a " + Diagnostic1523Value + "\" tail", "api_key=<redacted> \"<redacted>")]
    [DataRow(true, "api_key=SecretKey \"a " + Diagnostic1523Value + "\" tail", "api_key=<redacted> \"<redacted>")]
    // An outer key only the second passes find consumes its inner key after the space-only pass has hidden the quoted value.
    [DataRow(false, "SecretKey=db_password\t\"a " + Diagnostic1523Value + "\" tail", "SecretKey=<redacted>\t<redacted> tail")]
    [DataRow(true, "SecretKey=db_password\t\"a " + Diagnostic1523Value + "\" tail", "SecretKey=<redacted>\t<redacted> tail")]
    // #1523 item 3: a space-only key's words may be joined by a double underscore, as configuration keys are.
    [DataRow(false, "ObjectStorage__SecretKey " + Diagnostic1523Value, "ObjectStorage__SecretKey <redacted>")]
    [DataRow(true, "ObjectStorage__SecretKey " + Diagnostic1523Value, "ObjectStorage__SecretKey <redacted>")]
    [DataRow(false, "db__password " + Diagnostic1523Value, "db__password <redacted>")]
    [DataRow(true, "db__password " + Diagnostic1523Value, "db__password <redacted>")]
    [DataRow(false, "db__passwordless kept value", "db__passwordless kept value")]
    [DataRow(true, "db__passwordless kept value", "db__passwordless kept value")]
    // #1523 item 4: removed decoration starts a word before a new spelling too.
    [DataRow(false, "see\u001b[1mSecretKey " + Diagnostic1523Value, "seeSecretKey <redacted>")]
    [DataRow(true, "see\u001b[1mSecretKey " + Diagnostic1523Value, "seeSecretKey <redacted>")]
    [DataRow(false, "see\u001b[1mSecretAccessKey " + Diagnostic1523Value, "seeSecretAccessKey <redacted>")]
    [DataRow(true, "see\u001b[1mSecretAccessKey " + Diagnostic1523Value, "seeSecretAccessKey <redacted>")]
    [DataRow(false, "see\u001b[1mSecretKeeper kept value", "seeSecretKeeper kept value")]
    [DataRow(true, "see\u001b[1mSecretKeeper kept value", "seeSecretKeeper kept value")]
    public async Task Diagnostic1523_PreExistingGapsAreRedacted(bool json, string diagnostic, string expected)
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
        Assert.IsFalse(error.Contains(Diagnostic1523Value, StringComparison.Ordinal), "Credential leaked: " + error);
        Assert.AreEqual(expected.Replace("\t", "\\t", StringComparison.Ordinal), error);
        var uncut = Redaction.Redact(diagnostic);
        Assert.IsFalse(uncut.Contains(Diagnostic1523Value, StringComparison.Ordinal), "Credential leaked: " + uncut);
        StringAssert.StartsWith(uncut, expected, StringComparison.Ordinal);
    }

    // The merge base's passes and the #1523 passes each read the whole text, and a value a quoted key leaves is found once, so a
    // megabyte of each new shape still takes milliseconds.
    [TestMethod]
    [DataRow("token=\"password=\" x ", "token=\"<redacted>\" <redacted> ")]
    [DataRow("api_key=db_password\t\"a b\" ", "api_key=<redacted>\t\"<redacted>\" ")]
    [DataRow("ObjectStorage__SecretKey x ", "ObjectStorage__SecretKey <redacted> ")]
    [DataRow("see\u001b[1mSecretKey x ", "seeSecretKey <redacted> ")]
    [DataRow("token=\"password=\" mysecret ", "token=\"<redacted>\" <redacted> ")]
    [DataRow("api_key=SecretKey\t\"a b\" ", "api_key=<redacted>\t\"<redacted>\" ")]
    public void Diagnostic1523_NewPassesAreLinear(string unit, string expected)
    {
        var text = string.Concat(Enumerable.Repeat(unit, 50_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var redacted = Redaction.Redact(text);
        watch.Stop();
        Console.WriteLine($"{JsonSerializer.Serialize(unit)} x 50,000: {watch.ElapsedMilliseconds} ms");
        var all = string.Concat(Enumerable.Repeat(expected, 50_000));
        Assert.AreEqual(all, redacted);
        Assert.IsLessThanOrEqualTo(3_000L, watch.ElapsedMilliseconds);
        // Each unit is longer redacted than the merge base left it, so the diagnostic's cut ends inside the units it keeps.
        watch.Restart();
        var diagnostic = Redaction.SafeDiagnostic(text);
        watch.Stop();
        Assert.AreEqual(all[..256], diagnostic);
        Assert.IsLessThanOrEqualTo(3_000L, watch.ElapsedMilliseconds);
    }

    // PR-1528-R0 F1: the merge base cut each diagnostic at 256 characters. Text the #1523 passes hide is shorter once redacted,
    // so the cut is made where the merge base made it, and no character after that is shown.
    [TestMethod]
    [DataRow("ObjectStorage__SecretKey ", "ObjectStorage__SecretKey ", "ObjectStorage__SecretKey <redacted>")]
    [DataRow("token=\"password=\" ", "token=\"<redacted>\" ", "token=\"<redacted>\" <redacted>")]
    [DataRow("api_key=db_password\t\"a ", "api_key=<redacted>\t\"a ", "api_key=<redacted>\t\"<redacted>")]
    [DataRow("see\u001b[1mSecretKey ", "seeSecretKey ", "seeSecretKey <redacted>")]
    public void Diagnostic1523_TruncationKeepsTheMergeBaseCut(string lead, string mergeBaseLead, string expected)
    {
        ArgumentNullException.ThrowIfNull(lead);
        ArgumentNullException.ThrowIfNull(mergeBaseLead);
        ArgumentNullException.ThrowIfNull(expected);
        // The review's input with each #1523 shape as its lead: a long value only the #1523 passes hide, then, beyond the cut, a
        // value neither hides.
        var diagnostic = lead + new string('x', 300) + " token db_password\t\"a " + Diagnostic1523Value + "\" tail";
        var mergeBase = (mergeBaseLead + new string('x', 256))[..256];
        var shown = Redaction.SafeDiagnostic(diagnostic);
        Assert.AreEqual(expected, shown);
        Assert.AreEqual(expected.Replace("\t", "\\t", StringComparison.Ordinal), CameraAgentStateDiagnostics.Message(diagnostic));
        Assert.AreEqual("first line" + Environment.NewLine + expected, Redaction.SafeDiagnostics("first line\n" + diagnostic));
        // Differential: what is shown, apart from the markers, is a part of what the merge base showed, in order.
        var rest = mergeBase.Replace("<redacted>", string.Empty, StringComparison.Ordinal);
        var position = 0;
        foreach (var character in shown.Replace("<redacted>", string.Empty, StringComparison.Ordinal))
        {
            position = rest.IndexOf(character, position) + 1;
            Assert.IsGreaterThan(0, position, $"{JsonSerializer.Serialize(shown)} shows what {JsonSerializer.Serialize(mergeBase)} did not.");
        }
        Assert.IsFalse(shown.Contains(Diagnostic1523Value, StringComparison.Ordinal), "Credential leaked: " + shown);
    }

    // A diagnostic is often embedded in an installer error, which the top-level writer cuts at 256 characters again. Text the
    // #1523 passes hide is shorter once redacted, so the second cut would reach text the merge base's second cut left out: the
    // diagnostic ends at such a value's marker, and nothing after it moves forward.
    [TestMethod]
    [DoNotParallelize]
    [DataRow(false, "ObjectStorage__SecretKey ", "ObjectStorage__SecretKey ", "ObjectStorage__SecretKey <redacted>")]
    [DataRow(true, "ObjectStorage__SecretKey ", "ObjectStorage__SecretKey ", "ObjectStorage__SecretKey <redacted>")]
    [DataRow(false, "token=\"password=\" ", "token=\"<redacted>\" ", "token=\"<redacted>\" <redacted>")]
    [DataRow(true, "token=\"password=\" ", "token=\"<redacted>\" ", "token=\"<redacted>\" <redacted>")]
    [DataRow(false, "api_key=db_password\t\"a ", "api_key=<redacted>\t\"a ", "api_key=<redacted>\t\"<redacted>")]
    [DataRow(true, "api_key=db_password\t\"a ", "api_key=<redacted>\t\"a ", "api_key=<redacted>\t\"<redacted>")]
    [DataRow(false, "see\u001b[1mSecretKey ", "seeSecretKey ", "seeSecretKey <redacted>")]
    [DataRow(true, "see\u001b[1mSecretKey ", "seeSecretKey ", "seeSecretKey <redacted>")]
    public async Task Diagnostic1523_EmbeddedDiagnosticKeepsTheMergeBaseCut(bool json, string lead, string mergeBaseLead, string expected)
    {
        ArgumentNullException.ThrowIfNull(lead);
        ArgumentNullException.ThrowIfNull(mergeBaseLead);
        ArgumentNullException.ThrowIfNull(expected);
        // The merge base shows the whole value inside its own 256 characters, but the error's second cut, after the docker
        // command's prefix, ends among the x characters before it.
        const string prefix = "Docker command failed (docker run): ";
        var diagnostic = lead + new string('x', 190) + " token db_password\t\"a " + Diagnostic1523Value + "\" tail";
        // At least this much of what the merge base wrote comes before its second cut.
        var mergeBase = prefix + mergeBaseLead + new string('x', 190);
        var (exit, error) = await RunDiagnostic1491Async(
            new LifecycleRequest(LifecycleOperationKind.Backup, Guid.NewGuid(), "/var/lib/hvo-sky", false, false, json),
            (_, _) => throw new InstallerException(prefix + Redaction.SafeDiagnostic(diagnostic)));
        Assert.AreEqual(1, exit);
        if (json)
        {
            using var document = JsonDocument.Parse(error);
            error = document.RootElement.GetProperty("message").GetString()!;
        }
        else error = error.TrimEnd('\r', '\n')["Installation failed: ".Length..];
        Assert.IsFalse(error.Contains(Diagnostic1523Value, StringComparison.Ordinal), "Credential leaked: " + error);
        Assert.AreEqual(prefix + expected.Replace("\t", "\\t", StringComparison.Ordinal), error);
        // Differential: what is shown, apart from the markers, is a part of what the merge base showed, in order.
        var shown = (prefix + expected).Replace("<redacted>", string.Empty, StringComparison.Ordinal);
        var rest = mergeBase.Replace("<redacted>", string.Empty, StringComparison.Ordinal);
        var position = 0;
        foreach (var character in shown)
        {
            position = rest.IndexOf(character, position) + 1;
            Assert.IsGreaterThan(0, position, $"{JsonSerializer.Serialize(error)} shows what {JsonSerializer.Serialize(mergeBase)} did not.");
        }
    }

    // A control between a key and its quoted value keeps the merge base's passes from reading them as an assignment. The control
    // is removed afterwards, so when an installer error embeds the diagnostic and the top-level writer redacts it again, the merge
    // base hid the value then. The #1523 passes hide that key as the value of the key before it, which must not show the value
    // the second reading hid (PR-1528-R1 F4). The rows end in each of the #1523 items 1 to 4.
    [TestMethod]
    [DataRow(false, "token='password=' tailtoken \u009C'" + Diagnostic1523Value + " db_password:' \"X\" tail",
        "token='<redacted>' tailtoken '<redacted>'", "token='<redacted>' <redacted> '<redacted>")]
    [DataRow(true, "token='password=' tailtoken \u009C'" + Diagnostic1523Value + " db_password:' \"X\" tail",
        "token='<redacted>' tailtoken '<redacted>'", "token='<redacted>' <redacted> '<redacted>")]
    [DataRow(false, "token='password=' xtailtoken \u009C'" + Diagnostic1523Value + " db_password:' \"X\" tail",
        "token='<redacted>' xtailtoken '<redacted>'", "token='<redacted>' <redacted> '<redacted>")]
    [DataRow(true, "token='password=' xtailtoken \u009C'" + Diagnostic1523Value + " db_password:' \"X\" tail",
        "token='<redacted>' xtailtoken '<redacted>'", "token='<redacted>' <redacted> '<redacted>")]
    [DataRow(false, "api_key=db_password\t\"a\"apassword \u0080\t\"" + Diagnostic1523Value + "\" tail",
        "api_key=<redacted>\t\"a\"apassword \t\"<redacted>\" tail", "api_key=<redacted>\t\"<redacted>\"<redacted> \t\"<redacted>\" tail")]
    [DataRow(true, "api_key=db_password\t\"a\"apassword \u0080\t\"" + Diagnostic1523Value + "\" tail",
        "api_key=<redacted>\t\"a\"apassword \t\"<redacted>\" tail", "api_key=<redacted>\t\"<redacted>\"<redacted> \t\"<redacted>\" tail")]
    [DataRow(false, "ObjectStorage__SecretKey xtailtoken \u0090\t \"" + Diagnostic1523Value + "\" tail",
        "ObjectStorage__SecretKey xtailtoken \t \"<redacted>\" tail", "ObjectStorage__SecretKey <redacted> \t \"<redacted>")]
    [DataRow(true, "ObjectStorage__SecretKey xtailtoken \u0090\t \"" + Diagnostic1523Value + "\" tail",
        "ObjectStorage__SecretKey xtailtoken \t \"<redacted>\" tail", "ObjectStorage__SecretKey <redacted> \t \"<redacted>")]
    [DataRow(false, "see\u001b[1mSecretKey xtailtoken \u0090 \"" + Diagnostic1523Value + "\" tail",
        "seeSecretKey xtailtoken  \"<redacted>\" tail", "seeSecretKey <redacted>  \"<redacted>")]
    [DataRow(true, "see\u001b[1mSecretKey xtailtoken \u0090 \"" + Diagnostic1523Value + "\" tail",
        "seeSecretKey xtailtoken  \"<redacted>\" tail", "seeSecretKey <redacted>  \"<redacted>")]
    public async Task Diagnostic1523_EmbeddedDiagnosticKeepsTheMergeBaseRescan(bool json, string diagnostic, string mergeBase, string expected)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        ArgumentNullException.ThrowIfNull(mergeBase);
        ArgumentNullException.ThrowIfNull(expected);
        const string prefix = "Docker command failed (docker run): ";
        var (exit, error) = await RunDiagnostic1491Async(
            new LifecycleRequest(LifecycleOperationKind.Backup, Guid.NewGuid(), "/var/lib/hvo-sky", false, false, json),
            (_, _) => throw new InstallerException(prefix + Redaction.SafeDiagnostic(diagnostic)));
        Assert.AreEqual(1, exit);
        if (json)
        {
            using var document = JsonDocument.Parse(error);
            error = document.RootElement.GetProperty("message").GetString()!;
        }
        else error = error.TrimEnd('\r', '\n')["Installation failed: ".Length..];
        Assert.IsFalse(error.Contains(Diagnostic1523Value, StringComparison.Ordinal), "Credential leaked: " + error);
        Assert.AreEqual(prefix + expected.Replace("\t", "\\t", StringComparison.Ordinal), error);
        // Differential: what is shown, apart from the markers, is a part of what the merge base's writer showed, in order.
        var shown = expected.Replace("<redacted>", string.Empty, StringComparison.Ordinal);
        var rest = mergeBase.Replace("<redacted>", string.Empty, StringComparison.Ordinal);
        var position = 0;
        foreach (var character in shown)
        {
            position = rest.IndexOf(character, position) + 1;
            Assert.IsGreaterThan(0, position, $"{JsonSerializer.Serialize(error)} shows what {JsonSerializer.Serialize(mergeBase)} did not.");
        }
    }

    // A failed rollback's error embeds an error that embeds a diagnostic, and each is redacted and cut again. Reading a text
    // again can shorten it, as when a marker runs into the marker after it, and a later cut then reaches a character the merge
    // base's did not, so the diagnostic ends before any character the merge base's passes, reading it once or twice, show
    // earlier than they show it in the merge base's output (PR-1528-R1 F4). Q stands for a value.
    [TestMethod]
    [DataRow(false, "docker run exited 125: ", 119,
        "db__password xsecret\t\u0080 :Q=Q\tsee\u001b[1mSecretKey:'\"\"api_key=SecretKey\t'a b'\tx\u0080'Q Qfile= \" ",
        "db__password xsecret\t :<redacted>\tseeSecretKey:'<redacted>' b'\tx", "db__password <redacted> :<redacted>\tseeSecretKey:'<redacted>'")]
    [DataRow(true, "docker run exited 125: ", 119,
        "db__password xsecret\t\u0080 :Q=Q\tsee\u001b[1mSecretKey:'\"\"api_key=SecretKey\t'a b'\tx\u0080'Q Qfile= \" ",
        "db__password xsecret\t :<redacted>\tseeSecretKey:'<redacted>' b'\tx", "db__password <redacted> :<redacted>\tseeSecretKey:'<redacted>'")]
    [DataRow(false, "Docker image pull failed: ", 78,
        "api_key=db_password\t\"a\"\txsecret  \u001b:'Q Q'client_secret=  'Q' tail see\u001b[1mSecretKey  SecretKey \u009C\u009C Q'Q\t/ ' ",
        "api_key=<redacted>\t\"a\"\txsecret  :'<redacted>'  '<redacted>' tail seeSecretKey  SecretKey <redacted> Q'",
        "api_key=<redacted>\t\"<redacted>\"\txsecret  :'<redacted>'  '<redacted>' tail seeSecretKey  <redacted>")]
    [DataRow(true, "Docker image pull failed: ", 78,
        "api_key=db_password\t\"a\"\txsecret  \u001b:'Q Q'client_secret=  'Q' tail see\u001b[1mSecretKey  SecretKey \u009C\u009C Q'Q\t/ ' ",
        "api_key=<redacted>\t\"a\"\txsecret  :'<redacted>'  '<redacted>' tail seeSecretKey  SecretKey <redacted> Q'",
        "api_key=<redacted>\t\"<redacted>\"\txsecret  :'<redacted>'  '<redacted>' tail seeSecretKey  <redacted>")]
    public async Task Diagnostic1523_NestedErrorKeepsTheMergeBaseCut(
        bool json, string command, int padding, string diagnostic, string mergeBase, string expected)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(diagnostic);
        ArgumentNullException.ThrowIfNull(mergeBase);
        ArgumentNullException.ThrowIfNull(expected);
        const string rollback = "Candidate failed and exact rollback also failed: ";
        // The failed command's error, with the arguments it names before the diagnostic.
        var embedding = command + new string('p', padding) + " ";
        var (exit, error) = await RunDiagnostic1491Async(
            new LifecycleRequest(LifecycleOperationKind.Backup, Guid.NewGuid(), "/var/lib/hvo-sky", false, false, json),
            (_, _) => throw new InstallerException(rollback + Redaction.SafeDiagnostic(embedding + Redaction.SafeDiagnostic(diagnostic))));
        Assert.AreEqual(1, exit);
        if (json)
        {
            using var document = JsonDocument.Parse(error);
            error = document.RootElement.GetProperty("message").GetString()!;
        }
        else error = error.TrimEnd('\r', '\n')["Installation failed: ".Length..];
        Assert.IsFalse(error.Contains('Q', StringComparison.Ordinal), "Credential leaked: " + error);
        Assert.AreEqual(rollback + embedding + expected.Replace("\t", "\\t", StringComparison.Ordinal), error);
        // Differential: what is shown, apart from the markers, is a part of what the merge base's writer showed, in order.
        var shown = expected.Replace("<redacted>", string.Empty, StringComparison.Ordinal);
        var rest = mergeBase.Replace("<redacted>", string.Empty, StringComparison.Ordinal);
        var position = 0;
        foreach (var character in shown)
        {
            position = rest.IndexOf(character, position) + 1;
            Assert.IsGreaterThan(0, position, $"{JsonSerializer.Serialize(error)} shows what {JsonSerializer.Serialize(mergeBase)} did not.");
        }
    }

    // #1523 item 5:the word after a bare password is hidden as its value, so a fixed message is worded to put none there. No
    // message bypasses the scan; the top-level writer redacts each as it would any diagnostic, and every word must survive.
    [TestMethod]
    [DataRow("CredentialFile.cs")]
    [DataRow("OwnerRecoveryManager.cs")]
    [DataRow("CameraAgentInstaller.cs")]
    public async Task Diagnostic1523_FixedPasswordMessagesKeepEveryWord(string file)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "HVO.SkyMonitor.v9.slnx"))) root = root.Parent;
        Assert.IsNotNull(root);
        var source = await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "HVO.SkyMonitor.Deployment.Cli", file));
        var swept = 0;
        var changed = new List<string>();
        foreach (Match site in Regex.Matches(source, "new InstallerException\\(\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            var position = site.Index + site.Length - 1;
            var message = Diagnostic1476Literal(source, ref position, out _);
            swept++;
            var shown = CameraAgentStateDiagnostics.Message(message);
            if (shown != message) changed.Add($"{file}:{source.AsSpan(0, site.Index).Count('\n') + 1}: {JsonSerializer.Serialize(shown)}");
        }
        Assert.IsGreaterThan(0, swept, $"No messages swept in {file}.");
        Assert.IsEmpty(changed, string.Join(Environment.NewLine, changed));
    }
}
