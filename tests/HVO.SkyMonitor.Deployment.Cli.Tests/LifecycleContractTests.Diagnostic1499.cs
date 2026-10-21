using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    private const string Diagnostic1499Secret = "review-1491-secret";

    public static IEnumerable<object[]> Diagnostic1499Cases()
    {
        string s = Diagnostic1499Secret;
        (string Input, string Expected)[] cases =
        [
            ("SECRET_KEY=" + s, "SECRET_KEY=<redacted>"),
            ("aws_secret_access_key=" + s, "aws_secret_access_key=<redacted>"),
            ("secret_key=\"a " + s + " b\"", "secret_key=\"<redacted>\""),
            ("private_key=\"a " + s + " b\"", "private_key=\"<redacted>\""),
            ("passwords=\"a " + s + " b\"", "passwords=\"<redacted>\""),
            ("db_password " + s, "db_password <redacted>"),
            ("x-api-key " + s, "x-api-key <redacted>"),
            ("api-key=password " + s, "api-key=<redacted> <redacted>"),
            ("x-api-key: token " + s, "x-api-key: <redacted> <redacted>"),
            ("api_key=client_secret: " + s, "api_key=<redacted> <redacted>"),
            ("password=token " + s, "password=<redacted> <redacted>"),
            ("api_key=client_secret: password token " + s, "api_key=<redacted> password <redacted> <redacted>"),
            ("see\u001b[1mBearer token=\"a " + s + " b\"", "seeBearer <redacted>\"<redacted>\""),
            ("see\u001b[1mBearer password='a " + s + " b'", "seeBearer <redacted>"),
            ("x\u001b[1mauthorization: token=\"a " + s + " b\"", "xauthorization: <redacted>\"<redacted>\""),
            // Ordinary words and prose retain their values. Delimited compound keys are explicit; arbitrary suffixes are not.
            ("tokenid=\"kept value\"", "tokenid=\"kept value\""),
            ("secret-santa=\"kept value\"", "secret-santa=\"kept value\""),
            ("turnkey=\"kept value\"", "turnkey=\"kept value\""),
            ("tokenid kept value", "tokenid kept value"),
            ("secret-santa kept value", "secret-santa kept value"),
            ("turnkey kept value", "turnkey kept value"),
            ("a securitytoken describes a field", "a securitytoken describes a field"),
            ("the passwordless column and secretive wording", "the passwordless column and secretive wording"),
            // The boundary before a bare key must survive the F10 correction too.
            ("see\u001b[1mtoken " + s, "seetoken <redacted>"),
        ];
        foreach (var (input, expected) in cases)
            foreach (var json in new[] { false, true })
                foreach (var sqlite in new[] { false, true })
                    yield return [json, sqlite, input, expected];
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DoNotParallelize]
    [DynamicData(nameof(Diagnostic1499Cases))]
    public async Task Diagnostic1499_KeysAreRedactedThroughBothOutputPaths(bool json, bool sqlite, string diagnostic, string expected)
    {
        if (sqlite)
        {
            // The real RAISE(ABORT) -> PlanCarry -> restore warning path, including its formatter and JSON envelope.
            await Diagnostic1491_DecoratedAndCompoundKeysAreRedacted(json, diagnostic, expected);
            return;
        }
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
        Assert.IsFalse(error.Contains(Diagnostic1499Secret, StringComparison.Ordinal), "Credential leaked: " + error);
        Assert.AreEqual(expected, error);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Diagnostic1499_FormattedProseKeepsPasswordAndSecurityStamp(bool json)
    {
        const string prose = "The password and security stamp are restored; tokenid, secret-santa and turnkey remain ordinary.";
        var output = await RenderDiagnostic1476ErrorAsync(prose, json, formatted: true);
        StringAssert.Contains(output, prose, StringComparison.Ordinal);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false, "failure")]
    [DataRow(true, "failure")]
    [DataRow(false, "refusal")]
    [DataRow(true, "refusal")]
    [DataRow(false, "unexpected")]
    [DataRow(true, "unexpected")]
    public async Task Diagnostic1499_RawErrorsEscapeControlsAfterRedaction(bool json, string kind)
    {
        var diagnostic = "source\tname\0 token=\"a " + Diagnostic1499Secret + " b\"";
        Exception failure = kind switch
        {
            "failure" => new InstallerException(diagnostic),
            "refusal" => InstallerException.Refused(diagnostic),
            _ => new IOException(diagnostic)
        };
        var (_, error) = await RunDiagnostic1491Async(
            new LifecycleRequest(LifecycleOperationKind.Backup, Guid.NewGuid(), "/var/lib/hvo-sky", false, false, json),
            (_, _) => throw failure);
        if (json)
        {
            using var document = JsonDocument.Parse(error);
            error = document.RootElement.GetProperty("message").GetString()!;
        }
        Assert.IsFalse(error.TrimEnd('\r', '\n').Any(char.IsControl), "A raw source reached the terminal with controls.");
        Assert.IsFalse(error.Contains(Diagnostic1499Secret, StringComparison.Ordinal));
        StringAssert.Contains(error, "source\\tname\\u0000 token=\"<redacted>\"", StringComparison.Ordinal);
    }
}
