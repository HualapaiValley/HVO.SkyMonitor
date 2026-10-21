using System.Text;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Diagnostic1476_AllOwnedFailureMessagesAreSafe(bool json)
    {
        // An unsupported literal spelling must never silently turn a runtime control into printable source text.
        const string unsupported = "\"\\u001b\"";
        var unsupportedPosition = 0;
        Assert.ThrowsExactly<AssertFailedException>(() => Diagnostic1476Literal(unsupported, ref unsupportedPosition, out _));
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "HVO.SkyMonitor.v9.slnx"))) root = root.Parent;
        Assert.IsNotNull(root);
        string[] files =
        [
            "CameraAgentStateSnapshot.cs", "CameraAgentLifecycleManager.Backup.cs",
            "CameraAgentLifecycleManager.Restore.cs", "BackupArchive.cs", "BackupInventory.cs", "RestoredCredentials.cs"
        ];
        var failures = new List<string>();
        foreach (var file in files)
        {
            var source = await File.ReadAllTextAsync(Path.Combine(root.FullName, "src", "HVO.SkyMonitor.Deployment.Cli", file));
            var rendered = 0;
            // Include helper-returned archive refusals and composed recovery/cleanup/credential warnings as well as throws.
            foreach (Match site in Regex.Matches(source,
                         """new InstallerException\(|InstallerException\.(?:Formatted|RefusedFormatted|Refused)\(|throw Invalid\(|return \$"|parts\.Add\(|changes\.Add\(|new InstallationWarning\(|await ShowCredentialWarningAsync\(|var (?:warning|kept) = string.Create\(|return string.Create\(""",
                         RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                var position = site.Index;
                var before = rendered;
                while (position < source.Length && source[position] != ';')
                {
                    if (source[position] != '"')
                    {
                        position++;
                        continue;
                    }
                    Assert.IsFalse(position > 0 && source[position - 1] == '@' ||
                        source.AsSpan(position).StartsWith("\"\"\"", StringComparison.Ordinal),
                        "Extend the diagnostic sweep for verbatim or raw string syntax.");
                    var message = Diagnostic1476Literal(source, ref position, out _);
                    rendered++;
                    var output = await RenderDiagnostic1476ErrorAsync(message, json);
                    if (output.TrimEnd('\r', '\n').Any(char.IsControl) ||
                        output.Contains("diagnostic-1476-sentinel", StringComparison.Ordinal))
                        failures.Add($"{file}:{source.AsSpan(0, site.Index).Count('\n') + 1}: {System.Text.Json.JsonSerializer.Serialize(output)}");
                }
                if (rendered == before)
                {
                    var forwarding = Regex.Replace(source[site.Index..position], @"\s+", " ",
                        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    Assert.IsTrue(forwarding is "new InstallerException(exception.Message, exception)" or
                        "InstallerException.Refused(exception.Message, exception)" or "throw Invalid(problem)" or
                        "await ShowCredentialWarningAsync(archived.Archive, warning, json, error).ConfigureAwait(false)" or
                        "await ShowCredentialWarningAsync(archived.Archive, kept, json, error).ConfigureAwait(false)",
                        $"Classify a new computed diagnostic before adding it: {forwarding}");
                    // Typed InstallerException messages and NameProblem results are forwarded without double escaping.
                    var output = await RenderDiagnostic1476ErrorAsync(CameraAgentStateDiagnostics.Reason(HostileDiagnostic1476Name), json);
                    AssertDiagnostic1476Safe(output);
                    rendered++;
                }
            }
            Assert.IsGreaterThan(0, rendered, $"No messages swept in {file}.");
            Console.WriteLine($"{file}: rendered {rendered} source-owned diagnostic literals ({(json ? "JSON" : "human")}).");
        }
        Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
    }

    // This is a closed model of message interpolation, not a formatter-only test: every source-owned failure
    // template is discovered anew. New unclassified text expressions receive hostile input and fail the sweep.
    // The real SQLite, archive, filesystem and credential tests verify the actual runtime paths separately.
    private static string Diagnostic1476Expression(string expression)
    {
        if ((expression.StartsWith("CameraAgentStateDiagnostics.Reason(", StringComparison.Ordinal) ||
             expression.StartsWith("CameraAgentStateDiagnostics.Field(", StringComparison.Ordinal) ||
             expression.StartsWith("CameraAgentStateDiagnostics.Failure(", StringComparison.Ordinal) ||
             expression.StartsWith("CameraAgentStateDiagnostics.InstallerFailure(", StringComparison.Ordinal) ||
             expression.StartsWith("CameraAgentStateDiagnostics.Assignment(", StringComparison.Ordinal)) &&
            Diagnostic1476WholeCall(expression))
            return CameraAgentStateDiagnostics.Reason(HostileDiagnostic1476Name);
        if (expression.StartsWith("Redaction.SafeDiagnostic(", StringComparison.Ordinal) &&
            Diagnostic1476WholeCall(expression))
            return Redaction.SafeDiagnostic(HostileDiagnostic1476Name);
        if (expression is "string.Join(\"; \", outcome.Changes)" or
            "string.Join(\", \", changed)")
            return string.Join("; ", new[] { HostileDiagnostic1476Name, HostileDiagnostic1476Name }.Select(CameraAgentStateDiagnostics.Reason));

        // These are compositions of already formatted fragments; formatting them again would corrupt Describe keys.
        // Their constituent message templates are included above; CarryFailures/Describe have real-path assertions.
        // A forwarded exception message is not one of them: an unmarked typed message is raw (PR-1487-R2 F6).
        if (expression is "string.Join(\"; \", parts)" or "string.Join(\"; \", lowered)" or "reason" or "unread" or "list")
            return CameraAgentStateCounters.Describe(new(AutomationStore, "automation-definition-version", "agent\u001b[31m\"\t\u0085", 2));
        if (expression.StartsWith("CameraAgentStateDiagnostics.CarryFailures(", StringComparison.Ordinal) &&
            Diagnostic1476WholeCall(expression))
            return CameraAgentStateDiagnostics.CarryFailures(
                [(CameraAgentStateCounters.Describe(new(AutomationStore, "automation-definition-version", "agent\u001b[31m\"\t\u0085", 2)), HostileDiagnostic1476Name)]);

        // Scalar types cannot carry terminal text. 'what' is one of three private fixed capacity labels.
        if (expression is "instanceId:D" or "record.InstanceId:D" or "SchemaVersion" or "manifest.SchemaVersion" or
            "archive.CreatedUtc:yyyy-MM-ddTHH:mm:ssZ" or "archived.Archive.CreatedUtc:yyyy-MM-ddTHH:mm:ssZ" or
            "snapshot.RecordedUtc:yyyy-MM-ddTHH:mm:ssZ" or "snapshot.SnapshotId:D" or "FormatBytes(required)" or
            "FormatBytes(measured.AvailableBytes + reclaimed)" or "RestoredOwnerSettings.AdminEmailSetting" or "what" or "held" or
            "(holder.Email && holder.Name ? \"them\" : \"it\")")
            return "safe-scalar";
        return HostileDiagnostic1476Name;
    }

    private static bool Diagnostic1476WholeCall(string expression)
    {
        var depth = 0;
        for (var index = expression.IndexOf('(', StringComparison.Ordinal); index < expression.Length; index++)
        {
            if (expression[index] == '(') depth++;
            if (expression[index] == ')' && --depth == 0) return index == expression.Length - 1;
        }
        return false;
    }

    private static string Diagnostic1476Literal(string source, ref int position, out bool interpolated)
    {
        interpolated = position > 0 && source[position - 1] == '$';
        var result = new StringBuilder();
        position++;
        while (position < source.Length)
        {
            var character = source[position++];
            if (character == '"') return result.ToString();
            if (character == '\\')
            {
                var escaped = source[position++];
                Assert.IsTrue(escaped is 'n' or 'r' or 't' or '\\' or '"' or '\'',
                    "Extend the diagnostic sweep for this C# literal escape.");
                result.Append(escaped switch { 'n' => '\n', 'r' => '\r', 't' => '\t', _ => escaped });
            }
            else if (interpolated && character == '{')
            {
                if (source[position] == '{')
                {
                    result.Append('{');
                    position++;
                    continue;
                }
                var start = position;
                var depth = 1;
                while (depth > 0 && position < source.Length)
                {
                    if (source[position] == '"')
                    {
                        _ = Diagnostic1476Literal(source, ref position, out _);
                        continue;
                    }
                    if (source[position] == '{') depth++;
                    if (source[position] == '}') depth--;
                    position++;
                }
                Assert.AreEqual(0, depth, "Unsupported diagnostic interpolation.");
                result.Append(Diagnostic1476Expression(source[start..(position - 1)]));
            }
            else result.Append(character);
        }
        Assert.Fail("Unterminated diagnostic literal.");
        return string.Empty;
    }
}
