using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [DataRow("host", false)]
    [DataRow("host", true)]
    [DataRow("state", false)]
    [DataRow("state", true)]
    public void Diagnostic1499_DirectReportRenderersFormatUntrustedFields(string kind, bool json)
    {
        var hostile = "source\tname\0 token=\"a " + Diagnostic1499Secret + " b\"";
        var host = new HostPrerequisiteReport(1, "refused", false, hostile,
            [new("finding", "boundary", true, hostile, hostile, hostile, hostile)], DateTimeOffset.UtcNow);
        var state = new CameraAgentStatePreflightReport(1, "refused", Guid.NewGuid(), hostile, hostile, hostile, hostile, hostile,
            hostile, false, [new("finding", "boundary", true, hostile, hostile, hostile, hostile)], DateTimeOffset.UtcNow);
        var report = json
            ? kind == "host" ? JsonSerializer.Serialize(HostPrerequisites.ForOutput(host)) : JsonSerializer.Serialize(CameraAgentStatePreflight.ForOutput(state))
            : kind == "host"
            ? HostPrerequisites.Render(host)
            : CameraAgentStatePreflight.Render(state);
        Assert.IsFalse(report.Contains(Diagnostic1499Secret, StringComparison.Ordinal), report);
        Assert.IsFalse(report.Any(c => char.IsControl(c) && c is not ('\r' or '\n')), report);
        StringAssert.Contains(report, json ? "source\\\\tname\\\\u0000" : "source\\tname\\u0000", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Diagnostic1499_HostWarningsFormatUntrustedFields()
    {
        var hostile = "source\tname token=\"a " + Diagnostic1499Secret + " b\"";
        var report = new HostPrerequisiteReport(1, "satisfied", true, "/product",
            [new("finding", "boundary", false, hostile, hostile, hostile, hostile)], DateTimeOffset.UtcNow);
        var warning = HostPrerequisites.Warnings(report).Single();
        Assert.IsFalse(warning.Message.Contains(Diagnostic1499Secret, StringComparison.Ordinal));
        Assert.IsFalse(warning.Message.Any(char.IsControl));
        var recovery = report with { Findings = [new("docker-memory-limit-unsupported", "resource-limits", false, hostile, hostile, hostile, hostile)] };
        var recoveryWarning = HostPrerequisites.RollbackWarning(recovery, "rollback")!;
        Assert.IsFalse(recoveryWarning.Contains(Diagnostic1499Secret, StringComparison.Ordinal));
        Assert.IsFalse(recoveryWarning.Any(c => char.IsControl(c) && c is not ('\r' or '\n')));
    }

    [TestMethod]
    public async Task Diagnostic1499_OutputAndMarkerBoundariesAreClosed()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "HVO.SkyMonitor.v9.slnx"))) root = root.Parent;
        Assert.IsNotNull(root);
        var sourceRoot = Path.Combine(root.FullName, "src", "HVO.SkyMonitor.Deployment.Cli");
        string[] formattedOwners =
        [
            "CameraAgentStateSnapshot.cs", "CameraAgentLifecycleManager.Backup.cs", "CameraAgentLifecycleManager.Restore.cs",
            "BackupArchive.cs", "BackupInventory.cs", "RestoredCredentials.cs", "BackupVault.cs", "InstallerException.cs"
        ];
        var sourceFiles = Directory.GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(sourceRoot, file).Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"));
        var directExceptions = 0;
        foreach (var file in sourceFiles)
        {
            var source = await File.ReadAllTextAsync(file);
            var name = Path.GetFileName(file);
            if (Regex.IsMatch(source, @"InstallerException\.(?:Formatted|RefusedFormatted)\(", RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)))
                CollectionAssert.Contains(formattedOwners, name, "Add a source-composed message to the hostile interpolation sweep before marking it: " + name);
            foreach (Match site in Regex.Matches(source, @"(?:Console\.Error|interactive\.Error|error)\.Write(?:Line)?(?:Async)?\(([^\r\n]*)",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                var expression = site.Groups[1].Value;
                if (expression.StartsWith("CameraAgentStateDiagnostics.Message(exception.Message)", StringComparison.Ordinal))
                {
                    directExceptions++;
                    continue;
                }
                var known = name switch
                {
                    "CameraAgentStateSnapshot.cs" or "CameraAgentLifecycleManager.Backup.cs" or
                        "CameraAgentLifecycleManager.Restore.cs" => true, // Covered by the hostile interpolation sweep.
                    "CameraAgentLifecycleManager.Admission.cs" => expression.StartsWith("warning", StringComparison.Ordinal),
                    "UpgradeNotes.cs" => expression.StartsWith("Event(plan)", StringComparison.Ordinal), // Release content, not a failure/warning.
                    "Program.cs" => expression.StartsWith("json", StringComparison.Ordinal) ||
                        expression.StartsWith("HostPrerequisites.Render(report)", StringComparison.Ordinal) ||
                        expression.StartsWith("exception.Usage", StringComparison.Ordinal) ||
                        expression.Contains("CameraAgentStateDiagnostics.Message(warning.Message)", StringComparison.Ordinal),
                    "CameraAgentInstaller.cs" => expression.StartsWith("HostPrerequisites.Render(report)", StringComparison.Ordinal),
                    "CameraAgentStatePreflight.cs" => expression.StartsWith("Render(report)", StringComparison.Ordinal),
                    "InstallWizardRunner.cs" => expression.Contains("CameraAgentStateDiagnostics.Message(exception.Message)", StringComparison.Ordinal) ||
                        expression.StartsWith("text.Notice(HvoNotice.Info,", StringComparison.Ordinal),
                    _ => false
                };
                Assert.IsTrue(known, $"Classify and test a new direct diagnostic output boundary: {name}: {expression}");
            }
        }
        Assert.AreEqual(7, directExceptions, "All helper catches plus the usage error must pass the shared formatter.");
    }
}
