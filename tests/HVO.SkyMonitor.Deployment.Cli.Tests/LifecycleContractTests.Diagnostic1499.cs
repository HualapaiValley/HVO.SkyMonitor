using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    private const string Diagnostic1499Secret = "review-1491-secret";

    // Read the statically discoverable rows again for the differential corpus; do not maintain a second fixture list.
    private static IEnumerable<object[]> Diagnostic1499Cases()
        => typeof(LifecycleContractTests).GetMethod(nameof(Diagnostic1499_KeysAreRedactedThroughBothOutputPaths))!
            .GetCustomAttributes(typeof(DataRowAttribute), inherit: false).Cast<DataRowAttribute>()
            .Select(row => row.Data.Select(value => value!).ToArray());

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DoNotParallelize]
    [DataRow(false, false, "SECRET_KEY=" + Diagnostic1499Secret, "SECRET_KEY=<redacted>")]
    [DataRow(false, true, "SECRET_KEY=" + Diagnostic1499Secret, "SECRET_KEY=<redacted>")]
    [DataRow(true, false, "SECRET_KEY=" + Diagnostic1499Secret, "SECRET_KEY=<redacted>")]
    [DataRow(true, true, "SECRET_KEY=" + Diagnostic1499Secret, "SECRET_KEY=<redacted>")]
    [DataRow(false, false, "aws_secret_access_key=" + Diagnostic1499Secret, "aws_secret_access_key=<redacted>")]
    [DataRow(false, true, "aws_secret_access_key=" + Diagnostic1499Secret, "aws_secret_access_key=<redacted>")]
    [DataRow(true, false, "aws_secret_access_key=" + Diagnostic1499Secret, "aws_secret_access_key=<redacted>")]
    [DataRow(true, true, "aws_secret_access_key=" + Diagnostic1499Secret, "aws_secret_access_key=<redacted>")]
    [DataRow(false, false, "secret_key=\"a " + Diagnostic1499Secret + " b\"", "secret_key=\"<redacted>\"")]
    [DataRow(false, true, "secret_key=\"a " + Diagnostic1499Secret + " b\"", "secret_key=\"<redacted>\"")]
    [DataRow(true, false, "secret_key=\"a " + Diagnostic1499Secret + " b\"", "secret_key=\"<redacted>\"")]
    [DataRow(true, true, "secret_key=\"a " + Diagnostic1499Secret + " b\"", "secret_key=\"<redacted>\"")]
    [DataRow(false, false, "private_key=\"a " + Diagnostic1499Secret + " b\"", "private_key=\"<redacted>\"")]
    [DataRow(false, true, "private_key=\"a " + Diagnostic1499Secret + " b\"", "private_key=\"<redacted>\"")]
    [DataRow(true, false, "private_key=\"a " + Diagnostic1499Secret + " b\"", "private_key=\"<redacted>\"")]
    [DataRow(true, true, "private_key=\"a " + Diagnostic1499Secret + " b\"", "private_key=\"<redacted>\"")]
    [DataRow(false, false, "passwords=\"a " + Diagnostic1499Secret + " b\"", "passwords=\"<redacted>\"")]
    [DataRow(false, true, "passwords=\"a " + Diagnostic1499Secret + " b\"", "passwords=\"<redacted>\"")]
    [DataRow(true, false, "passwords=\"a " + Diagnostic1499Secret + " b\"", "passwords=\"<redacted>\"")]
    [DataRow(true, true, "passwords=\"a " + Diagnostic1499Secret + " b\"", "passwords=\"<redacted>\"")]
    [DataRow(false, false, "db_password " + Diagnostic1499Secret, "db_password <redacted>")]
    [DataRow(false, true, "db_password " + Diagnostic1499Secret, "db_password <redacted>")]
    [DataRow(true, false, "db_password " + Diagnostic1499Secret, "db_password <redacted>")]
    [DataRow(true, true, "db_password " + Diagnostic1499Secret, "db_password <redacted>")]
    [DataRow(false, false, "x-api-key " + Diagnostic1499Secret, "x-api-key <redacted>")]
    [DataRow(false, true, "x-api-key " + Diagnostic1499Secret, "x-api-key <redacted>")]
    [DataRow(true, false, "x-api-key " + Diagnostic1499Secret, "x-api-key <redacted>")]
    [DataRow(true, true, "x-api-key " + Diagnostic1499Secret, "x-api-key <redacted>")]
    [DataRow(false, false, "api-key=password " + Diagnostic1499Secret, "api-key=<redacted> <redacted>")]
    [DataRow(false, true, "api-key=password " + Diagnostic1499Secret, "api-key=<redacted> <redacted>")]
    [DataRow(true, false, "api-key=password " + Diagnostic1499Secret, "api-key=<redacted> <redacted>")]
    [DataRow(true, true, "api-key=password " + Diagnostic1499Secret, "api-key=<redacted> <redacted>")]
    [DataRow(false, false, "x-api-key: token " + Diagnostic1499Secret, "x-api-key: <redacted> <redacted>")]
    [DataRow(false, true, "x-api-key: token " + Diagnostic1499Secret, "x-api-key: <redacted> <redacted>")]
    [DataRow(true, false, "x-api-key: token " + Diagnostic1499Secret, "x-api-key: <redacted> <redacted>")]
    [DataRow(true, true, "x-api-key: token " + Diagnostic1499Secret, "x-api-key: <redacted> <redacted>")]
    [DataRow(false, false, "api_key=client_secret: " + Diagnostic1499Secret, "api_key=<redacted> <redacted>")]
    [DataRow(false, true, "api_key=client_secret: " + Diagnostic1499Secret, "api_key=<redacted> <redacted>")]
    [DataRow(true, false, "api_key=client_secret: " + Diagnostic1499Secret, "api_key=<redacted> <redacted>")]
    [DataRow(true, true, "api_key=client_secret: " + Diagnostic1499Secret, "api_key=<redacted> <redacted>")]
    [DataRow(false, false, "password=token " + Diagnostic1499Secret, "password=<redacted> <redacted>")]
    [DataRow(false, true, "password=token " + Diagnostic1499Secret, "password=<redacted> <redacted>")]
    [DataRow(true, false, "password=token " + Diagnostic1499Secret, "password=<redacted> <redacted>")]
    [DataRow(true, true, "password=token " + Diagnostic1499Secret, "password=<redacted> <redacted>")]
    [DataRow(false, false, "api-key=db_password " + Diagnostic1499Secret, "api-key=<redacted> <redacted>")]
    [DataRow(false, true, "api-key=db_password " + Diagnostic1499Secret, "api-key=<redacted> <redacted>")]
    [DataRow(true, false, "api-key=db_password " + Diagnostic1499Secret, "api-key=<redacted> <redacted>")]
    [DataRow(true, true, "api-key=db_password " + Diagnostic1499Secret, "api-key=<redacted> <redacted>")]
    [DataRow(false, false, "api_key=client_secret: db_password " + Diagnostic1499Secret, "api_key=<redacted> db_password <redacted>")]
    [DataRow(false, true, "api_key=client_secret: db_password " + Diagnostic1499Secret, "api_key=<redacted> db_password <redacted>")]
    [DataRow(true, false, "api_key=client_secret: db_password " + Diagnostic1499Secret, "api_key=<redacted> db_password <redacted>")]
    [DataRow(true, true, "api_key=client_secret: db_password " + Diagnostic1499Secret, "api_key=<redacted> db_password <redacted>")]
    [DataRow(false, false, "api_key=client_secret: password token " + Diagnostic1499Secret, "api_key=<redacted> password <redacted> <redacted>")]
    [DataRow(false, true, "api_key=client_secret: password token " + Diagnostic1499Secret, "api_key=<redacted> password <redacted> <redacted>")]
    [DataRow(true, false, "api_key=client_secret: password token " + Diagnostic1499Secret, "api_key=<redacted> password <redacted> <redacted>")]
    [DataRow(true, true, "api_key=client_secret: password token " + Diagnostic1499Secret, "api_key=<redacted> password <redacted> <redacted>")]
    [DataRow(false, false, "see\u001b[1mBearer token=\"a " + Diagnostic1499Secret + " b\"", "seeBearer <redacted>\"<redacted>\"")]
    [DataRow(false, true, "see\u001b[1mBearer token=\"a " + Diagnostic1499Secret + " b\"", "seeBearer <redacted>\"<redacted>\"")]
    [DataRow(true, false, "see\u001b[1mBearer token=\"a " + Diagnostic1499Secret + " b\"", "seeBearer <redacted>\"<redacted>\"")]
    [DataRow(true, true, "see\u001b[1mBearer token=\"a " + Diagnostic1499Secret + " b\"", "seeBearer <redacted>\"<redacted>\"")]
    [DataRow(false, false, "see\u001b[1mBearer password='a " + Diagnostic1499Secret + " b'", "seeBearer <redacted>")]
    [DataRow(false, true, "see\u001b[1mBearer password='a " + Diagnostic1499Secret + " b'", "seeBearer <redacted>")]
    [DataRow(true, false, "see\u001b[1mBearer password='a " + Diagnostic1499Secret + " b'", "seeBearer <redacted>")]
    [DataRow(true, true, "see\u001b[1mBearer password='a " + Diagnostic1499Secret + " b'", "seeBearer <redacted>")]
    [DataRow(false, false, "x\u001b[1mauthorization: token=\"a " + Diagnostic1499Secret + " b\"", "xauthorization: <redacted>\"<redacted>\"")]
    [DataRow(false, true, "x\u001b[1mauthorization: token=\"a " + Diagnostic1499Secret + " b\"", "xauthorization: <redacted>\"<redacted>\"")]
    [DataRow(true, false, "x\u001b[1mauthorization: token=\"a " + Diagnostic1499Secret + " b\"", "xauthorization: <redacted>\"<redacted>\"")]
    [DataRow(true, true, "x\u001b[1mauthorization: token=\"a " + Diagnostic1499Secret + " b\"", "xauthorization: <redacted>\"<redacted>\"")]
    [DataRow(false, false, "tokenid=\"kept value\"", "tokenid=\"kept value\"")]
    [DataRow(false, true, "tokenid=\"kept value\"", "tokenid=\"kept value\"")]
    [DataRow(true, false, "tokenid=\"kept value\"", "tokenid=\"kept value\"")]
    [DataRow(true, true, "tokenid=\"kept value\"", "tokenid=\"kept value\"")]
    [DataRow(false, false, "secret-santa=\"kept value\"", "secret-santa=\"kept value\"")]
    [DataRow(false, true, "secret-santa=\"kept value\"", "secret-santa=\"kept value\"")]
    [DataRow(true, false, "secret-santa=\"kept value\"", "secret-santa=\"kept value\"")]
    [DataRow(true, true, "secret-santa=\"kept value\"", "secret-santa=\"kept value\"")]
    [DataRow(false, false, "turnkey=\"kept value\"", "turnkey=\"kept value\"")]
    [DataRow(false, true, "turnkey=\"kept value\"", "turnkey=\"kept value\"")]
    [DataRow(true, false, "turnkey=\"kept value\"", "turnkey=\"kept value\"")]
    [DataRow(true, true, "turnkey=\"kept value\"", "turnkey=\"kept value\"")]
    [DataRow(false, false, "tokenid kept value", "tokenid kept value")]
    [DataRow(false, true, "tokenid kept value", "tokenid kept value")]
    [DataRow(true, false, "tokenid kept value", "tokenid kept value")]
    [DataRow(true, true, "tokenid kept value", "tokenid kept value")]
    [DataRow(false, false, "secret-santa kept value", "secret-santa kept value")]
    [DataRow(false, true, "secret-santa kept value", "secret-santa kept value")]
    [DataRow(true, false, "secret-santa kept value", "secret-santa kept value")]
    [DataRow(true, true, "secret-santa kept value", "secret-santa kept value")]
    [DataRow(false, false, "turnkey kept value", "turnkey kept value")]
    [DataRow(false, true, "turnkey kept value", "turnkey kept value")]
    [DataRow(true, false, "turnkey kept value", "turnkey kept value")]
    [DataRow(true, true, "turnkey kept value", "turnkey kept value")]
    [DataRow(false, false, "a securitytoken describes a field", "a securitytoken describes a field")]
    [DataRow(false, true, "a securitytoken describes a field", "a securitytoken describes a field")]
    [DataRow(true, false, "a securitytoken describes a field", "a securitytoken describes a field")]
    [DataRow(true, true, "a securitytoken describes a field", "a securitytoken describes a field")]
    [DataRow(false, false, "the passwordless column and secretive wording", "the passwordless column and secretive wording")]
    [DataRow(false, true, "the passwordless column and secretive wording", "the passwordless column and secretive wording")]
    [DataRow(true, false, "the passwordless column and secretive wording", "the passwordless column and secretive wording")]
    [DataRow(true, true, "the passwordless column and secretive wording", "the passwordless column and secretive wording")]
    [DataRow(false, false, "see\u001b[1mtoken " + Diagnostic1499Secret, "seetoken <redacted>")]
    [DataRow(false, true, "see\u001b[1mtoken " + Diagnostic1499Secret, "seetoken <redacted>")]
    [DataRow(true, false, "see\u001b[1mtoken " + Diagnostic1499Secret, "seetoken <redacted>")]
    [DataRow(true, true, "see\u001b[1mtoken " + Diagnostic1499Secret, "seetoken <redacted>")]
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
