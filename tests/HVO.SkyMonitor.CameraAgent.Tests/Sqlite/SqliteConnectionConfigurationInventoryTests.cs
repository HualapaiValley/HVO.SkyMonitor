using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Sqlite;

[TestClass]
[TestCategory("Unit")]
public sealed partial class SqliteConnectionConfigurationInventoryTests
{
    private const string SolutionFileName = "HVO.SkyMonitor.v9.slnx";

    private static readonly (string Path, string Surface, string GateApi)[] ConfigurationSurfaces =
    [
        ("Automation/SqliteLocalAutomationStore.cs", "OpenAsync", "OpenAndConfigureAsync"),
        ("Capture/Calibration/SqliteCalibrationLibraryStore.cs", "OpenAsync", "OpenAndConfigureAsync"),
        ("Capture/CaptureAdmissionCoordinator.cs", "OpenAsync", "OpenAndConfigureAsync"),
        ("Capture/Distribution/SqliteCaptureLaneStore.cs", "OpenAsync", "OpenAndConfigureAsync"),
        ("Capture/Processing/SqliteCaptureProcessingStore.cs", "ConfigureConnectionAsync", "RunAsync"),
        ("Environmental/SqliteEnvironmentalObservationOutbox.cs", "ConfigureConnectionAsync", "RunAsync"),
        ("Evidence/SqliteExecutionEvidenceOutbox.cs", "OpenAsync", "OpenAndConfigureAsync"),
        ("Fleet/SqliteFleetStatusOutbox.cs", "OpenAsync", "OpenAndConfigureAsync"),
        ("Gallery/CameraAgentArtifactService.cs", "OpenReadOnlyAsync", "OpenAndConfigureAsync"),
        ("Gallery/SqliteCameraAgentGallery.cs", "OpenReadOnlyAsync", "OpenAndConfigureAsync"),
        ("RawIngress/SqliteInspectionSnapshot.cs", "InspectOnceAsync", "OpenAndConfigureAsync"),
        ("RawIngress/SqliteRawCaptureJournal.cs", "ConfigureConnectionAsync", "RunAsync"),
        ("Scheduling/SqliteCaptureScheduleStore.cs", "OpenAsync", "OpenAndConfigureAsync"),
        ("Transients/SqliteCameraAgentTransientOperatorProjection.cs", "OpenReadOnlyAsync", "OpenAndConfigureAsync"),
        ("Transients/SqliteTransientCandidateJournal.cs", "OpenAsync", "OpenAndConfigureAsync"),
        ("Transients/SqliteTransientRuntimeStore.cs", "ConfigureConnectionAsync", "RunAsync"),
        ("Upload/SqliteArtifactOutbox.cs", "OpenAsync", "OpenAndConfigureAsync"),
    ];

    [TestMethod]
    public void KnownConfigurationSurfacesUseProcessWideGateAndSeparatePragmaCommands()
    {
        var sourceRoot = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "HVO.SkyMonitor.CameraAgent.Common");
        var violations = new List<string>();

        foreach (var surface in ConfigurationSurfaces)
        {
            var sourcePath = Path.Combine(sourceRoot, surface.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourcePath))
            {
                violations.Add($"Missing inventory file: {surface.Path}");
                continue;
            }

            var source = File.ReadAllText(sourcePath);
            var methodBody = FindMethodBody(source, surface.Surface);
            if (methodBody is null)
            {
                violations.Add($"{surface.Path} no longer contains the inventoried {surface.Surface} surface.");
                continue;
            }
            if (!methodBody.Contains(
                $"SqliteConnectionConfigurationGate.{surface.GateApi}",
                StringComparison.Ordinal))
            {
                violations.Add($"{surface.Path}:{surface.Surface} must use {surface.GateApi}.");
            }
            if (CombinedConfigurationPragmas().IsMatch(methodBody))
            {
                violations.Add($"{surface.Path} combines configuration PRAGMAs in one command string.");
            }
        }

        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    private static string? FindMethodBody(string source, string methodName)
    {
        var declaration = Regex.Match(
            source,
            $@"\b(?:private|internal|public)\b[^;{{}}]*\b{Regex.Escape(methodName)}(?:<[^>]+>)?\s*\(",
            RegexOptions.CultureInvariant);
        if (!declaration.Success)
        {
            return null;
        }

        var bodyStart = source.IndexOf('{', declaration.Index + declaration.Length);
        if (bodyStart < 0)
        {
            return null;
        }

        var depth = 0;
        for (var index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}' && --depth == 0)
            {
                return source[bodyStart..(index + 1)];
            }
        }

        return null;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"No ancestor of {AppContext.BaseDirectory} contains {SolutionFileName}.");
    }

    [GeneratedRegex("(?:\\$?\"[^\"\\r\\n]*PRAGMA[^\"\\r\\n]*;\\s*PRAGMA)", RegexOptions.IgnoreCase)]
    private static partial Regex CombinedConfigurationPragmas();
}
