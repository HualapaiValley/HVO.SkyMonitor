using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    private const string Diagnostic1546Value = "review-1546-value";

    [TestMethod]
    [DataRow("\u001b]0;SecretKey\u0007 ")]
    [DataRow("\u001b]0;SecretKey\u001b\\ ")]
    [DataRow("\u009d0;SecretKey\u009c ")]
    [DataRow("\u001b]2;ObjectStorage__SecretKey\u0007 ")]
    [DataRow("\u001bPSecretKey\u001b\\ ")]
    [DataRow("\u001b]0;https://password@example.test/SecretKey\u0007 ")]
    public void Diagnostic1546_HiddenCredentialIndicatorSuppressesAmbiguousTail(string prefix)
    {
        var raw = prefix + Diagnostic1546Value + " tail";
        foreach (var shown in new[] { Redaction.SafeDiagnostic(raw), Redaction.Redact(raw),
                     CameraAgentStateDiagnostics.Reason(raw), CameraAgentStateDiagnostics.Message(raw),
                     CameraAgentStateDiagnostics.Text(raw), Redaction.SafeDiagnostics("safe line\n" + raw) })
        {
            Assert.IsFalse(shown.Contains(Diagnostic1546Value, StringComparison.Ordinal), shown);
            Assert.IsFalse(shown.Contains("tail", StringComparison.Ordinal), shown);
        }
    }

    [TestMethod]
    [DataRow("\u001b]0;Build complete\u0007 safe text", " safe text")]
    [DataRow("\u001b]0;secretkeeper\u0007 safe text", " safe text")]
    [DataRow("\u001b]0;tokenid\u0007 safe text", " safe text")]
    [DataRow("\u001b]8;;https://example.test/docs\u001b\\read docs\u001b]8;;\u001b\\", "read docs")]
    public void Diagnostic1546_OrdinaryDecorationRemainsHarmless(string raw, string expected)
        => Assert.AreEqual(expected, Redaction.SafeDiagnostic(raw));

    [TestMethod]
    [DataRow(0xD800)]
    [DataRow(0xDC00)]
    public void Diagnostic1546_LoneSurrogatesBecomeReplacementCharactersAtEveryBoundary(int surrogate)
    {
        var raw = "before " + (char)surrogate + " after \U0001F30C password=" + Diagnostic1546Value;
        foreach (var shown in new[] { Redaction.SafeDiagnostic(raw), Redaction.Redact(raw),
                     CameraAgentStateDiagnostics.Reason(raw), CameraAgentStateDiagnostics.Message(raw),
                     CameraAgentStateDiagnostics.Text(raw), CameraAgentStateDiagnostics.Identity(raw),
                     CameraAgentStateDiagnostics.Field(raw), CameraAgentStateDiagnostics.Assignment("name", raw) })
        {
            _ = JsonEncodedText.Encode(shown);
            Assert.IsFalse(shown.Contains(Diagnostic1546Value, StringComparison.Ordinal), shown);
            var decoded = shown.Contains("\\u", StringComparison.Ordinal)
                ? JsonSerializer.Deserialize<string>("\"" + shown + "\"")! : shown;
            StringAssert.Contains(decoded, "before \uFFFD after \U0001F30C", StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false, false, 0)]
    [DataRow(true, false, 0)]
    [DataRow(false, true, 0)]
    [DataRow(true, true, 0)]
    [DataRow(false, false, 240)]
    [DataRow(true, true, 300)]
    public async Task Diagnostic1546_WriterAndComposedErrorsStaySafe(bool json, bool malformed, int padding)
    {
        var raw = malformed ? "\uD800\U0001F30C password=" + Diagnostic1546Value
            : new string('p', padding) + "\u001b]0;SecretKey\u0007 " + Diagnostic1546Value;
        var reason = CameraAgentStateDiagnostics.Reason(raw);
        var (exit, error) = await RunDiagnostic1491Async(
            new LifecycleRequest(LifecycleOperationKind.Backup, Guid.NewGuid(), "/var/lib/hvo-sky", false, false, json),
            (_, _) => throw new InstallerException("Rollback failed: " + reason + "; second-reason-value"));
        Assert.AreEqual(1, exit);
        if (json)
        {
            using var document = JsonDocument.Parse(error);
            error = document.RootElement.GetProperty("message").GetString()!;
        }
        Assert.IsFalse(error.Contains(Diagnostic1546Value, StringComparison.Ordinal), error);
        if (!malformed) Assert.IsFalse(error.Contains("second-reason-value", StringComparison.Ordinal), error);
        _ = JsonEncodedText.Encode(error);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Diagnostic1546_DirectWriterHandlesHiddenKeysAndMalformedInput(bool json, bool malformed)
    {
        var raw = malformed ? "\uD800 text password=" + Diagnostic1546Value
            : "\u001b]0;SecretKey\u0007 " + Diagnostic1546Value;
        var (exit, error) = await RunDiagnostic1491Async(
            new LifecycleRequest(LifecycleOperationKind.Backup, Guid.NewGuid(), "/var/lib/hvo-sky", false, false, json),
            (_, _) => throw new InstallerException(raw));
        Assert.AreEqual(1, exit);
        if (json)
        {
            using var document = JsonDocument.Parse(error);
            error = document.RootElement.GetProperty("message").GetString()!;
        }
        Assert.IsFalse(error.Contains(Diagnostic1546Value, StringComparison.Ordinal), error);
        if (malformed) StringAssert.Contains(error, "\uFFFD text", StringComparison.Ordinal);
        _ = JsonEncodedText.Encode(error);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Diagnostic1546_UrlDecorationInteractionCannotExposeTailOrComposedReason(bool json, bool composed)
    {
        var raw = "\u001b]0;https://password@example.test/SecretKey\u0007 " + Diagnostic1546Value + " tail";
        var message = composed ? "Rollback failed: " + CameraAgentStateDiagnostics.Reason(raw) + "; second-reason-value" : raw;
        var (exit, error) = await RunDiagnostic1491Async(
            new LifecycleRequest(LifecycleOperationKind.Backup, Guid.NewGuid(), "/var/lib/hvo-sky", false, false, json),
            (_, _) => throw new InstallerException(message));
        Assert.AreEqual(1, exit);
        if (json)
        {
            using var document = JsonDocument.Parse(error);
            error = document.RootElement.GetProperty("message").GetString()!;
        }
        Assert.IsFalse(error.Contains(Diagnostic1546Value, StringComparison.Ordinal), error);
        Assert.IsFalse(error.Contains("tail", StringComparison.Ordinal), error);
        Assert.IsFalse(error.Contains("second-reason-value", StringComparison.Ordinal), error);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Diagnostic1546_IdentityRepairsTheOriginalInputEvenWhenDecorationChangedItsValidity(bool insideDecoration)
    {
        var raw = insideDecoration ? "\u001b]0;\uD800\u0007counter" : "\uD83C\u001b[0m\uDF0C";
        var shown = CameraAgentStateDiagnostics.Identity(raw);
        var decoded = JsonSerializer.Deserialize<string>("\"" + shown + "\"")!;
        _ = JsonEncodedText.Encode(decoded);
        StringAssert.Contains(decoded, "\uFFFD", StringComparison.Ordinal);
        Assert.AreEqual(JsonEncodedText.Encode("valid \U0001F30C").ToString(), CameraAgentStateDiagnostics.Identity("valid \U0001F30C"));
    }
}
