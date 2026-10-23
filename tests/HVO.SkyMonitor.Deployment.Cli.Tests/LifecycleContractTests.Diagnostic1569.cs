using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    private const string Diagnostic1569Value = "review-1569-value";

    private static IEnumerable<object[]> Diagnostic1569Cases()
        => typeof(LifecycleContractTests).GetMethod(nameof(Diagnostic1569_BareOuterKeyHidesNestedQuotedTail))!
            .GetCustomAttributes(typeof(DataRowAttribute), inherit: false).Cast<DataRowAttribute>()
            .Select(row => row.Data.Select(value => value!).ToArray());

    [TestMethod]
    [DataRow("token db_password\t\"a " + Diagnostic1569Value + "\" tail")]
    [DataRow("token db_password\t'a " + Diagnostic1569Value + "' tail")]
    [DataRow("password db_password\u00a0\"a " + Diagnostic1569Value + "\" tail")]
    [DataRow("api_key db_password\u0085\"a " + Diagnostic1569Value + "\" tail")]
    [DataRow("token\tdb_password\t\"a " + Diagnostic1569Value + "\"joined tail")]
    [DataRow("token db_password\t\"a \\\" " + Diagnostic1569Value + "\" tail")]
    [DataRow("token db_password\t\"a " + Diagnostic1569Value)]
    [DataRow("token db__password\t\"a " + Diagnostic1569Value + "\" tail")]
    [DataRow("token SecretKey\t\"a " + Diagnostic1569Value + "\" tail")]
    [DataRow("ObjectStorage__SecretKey db_password\t\"a " + Diagnostic1569Value + "\" tail")]
    [DataRow("token db_password=api_key\t\"a " + Diagnostic1569Value + "\" tail")]
    public void Diagnostic1569_BareOuterKeyHidesNestedQuotedTail(string diagnostic)
    {
        foreach (var shown in new[]
        {
            Redaction.SafeDiagnostic(diagnostic), Redaction.Redact(diagnostic),
            CameraAgentStateDiagnostics.Message(diagnostic), CameraAgentStateDiagnostics.Text(diagnostic),
            CameraAgentStateDiagnostics.Reason(diagnostic), Redaction.SafeDiagnostics("safe line\n" + diagnostic)
        })
        {
            Assert.IsFalse(shown.Contains(Diagnostic1569Value, StringComparison.Ordinal), shown);
            Assert.IsFalse(shown.Contains("tail", StringComparison.Ordinal), shown);
        }
    }

    [TestMethod]
    [DataRow("ordinary words remain readable", "ordinary words remain readable")]
    [DataRow("tokenid db_passwordless\t\"safe words\" tail", "tokenid db_passwordless\t\"safe words\" tail")]
    [DataRow("secretkeeper ordinary words", "secretkeeper ordinary words")]
    [DataRow("token=value; safe tail", "token=<redacted>; safe tail")]
    [DataRow("token db_password value tail", "token <redacted> value tail")]
    [DataRow("token=db_password\t\"a value\" tail", "token=<redacted>…")]
    public void Diagnostic1569_PreservesProseAndExistingAssignments(string diagnostic, string expected)
        => Assert.AreEqual(expected, Redaction.SafeDiagnostic(diagnostic));

    // The pre-change output for the issue's reproduction exposed the quoted tail. Keep its safe prefix and terminate it
    // with the existing marker so another error cannot pull a second reason forward when this reason becomes shorter.
    [TestMethod]
    public void Diagnostic1569_KeepsThePreChangeVisiblePrefix()
    {
        const string diagnostic = "token db_password\t\"a " + Diagnostic1569Value + "\" tail";
        const string before = "token <redacted> " + Diagnostic1569Value + "\" tail";
        var shown = Redaction.SafeDiagnostic(diagnostic);
        Assert.AreEqual("token <redacted>…", shown);
        StringAssert.StartsWith(before, shown[..^"<redacted>…".Length], StringComparison.Ordinal);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false, 0, 0)]
    [DataRow(true, 0, 0)]
    [DataRow(false, 1, 0)]
    [DataRow(true, 1, 0)]
    [DataRow(false, 2, 0)]
    [DataRow(true, 2, 0)]
    [DataRow(false, 0, 230)]
    [DataRow(true, 1, 240)]
    [DataRow(false, 2, 300)]
    public async Task Diagnostic1569_ComposedOutputKeepsValuesBeyondTheCutHidden(bool json, int encodings, int padding)
    {
        var raw = new string('p', padding) + " token db_password\t\"a " + Diagnostic1569Value + "\" tail";
        var reason = Redaction.SafeDiagnostic(raw);
        for (var count = 0; count < encodings; count++) reason = JsonEncodedText.Encode(reason).ToString();
        var (exit, error) = await RunDiagnostic1491Async(
            new LifecycleRequest(LifecycleOperationKind.Backup, Guid.NewGuid(), "/var/lib/hvo-sky", false, false, json),
            (_, _) => throw new InstallerException("Rollback failed: " + reason + "; second-reason-value"));
        Assert.AreEqual(1, exit);
        if (json)
        {
            using var document = JsonDocument.Parse(error);
            error = document.RootElement.GetProperty("message").GetString()!;
        }
        else error = error.TrimEnd('\r', '\n')["Installation failed: ".Length..];
        Assert.IsFalse(error.Contains(Diagnostic1569Value, StringComparison.Ordinal), error);
        Assert.IsFalse(error.Contains("second-reason-value", StringComparison.Ordinal), error);
        Assert.IsLessThanOrEqualTo(256, error.Length);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Diagnostic1569_DirectWriterHidesTheQuotedTail(bool json)
    {
        var (exit, error) = await RunDiagnostic1491Async(
            new LifecycleRequest(LifecycleOperationKind.Backup, Guid.NewGuid(), "/var/lib/hvo-sky", false, false, json),
            (_, _) => throw new InstallerException("token db_password\t\"a " + Diagnostic1569Value + "\" tail"));
        Assert.AreEqual(1, exit);
        if (json)
        {
            using var document = JsonDocument.Parse(error);
            error = document.RootElement.GetProperty("message").GetString()!;
        }
        else error = error.TrimEnd('\r', '\n')["Installation failed: ".Length..];
        Assert.AreEqual("token <redacted>…", error);
    }

    [TestMethod]
    public void Diagnostic1569_BareOuterKeyRemainsLinear()
    {
        var diagnostic = string.Concat(Enumerable.Repeat("token db_password\t\"a value\" ", 50_000));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.AreEqual("token <redacted>…", Redaction.Redact(diagnostic));
        Assert.IsLessThanOrEqualTo(3_000L, watch.ElapsedMilliseconds);
        watch.Restart();
        Assert.AreEqual("token <redacted>…", Redaction.SafeDiagnostic(diagnostic));
        Assert.IsLessThanOrEqualTo(3_000L, watch.ElapsedMilliseconds);
    }
}
