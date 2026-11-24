using System.Collections.Frozen;
using System.Reflection;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Canonical <c>Area</c> test property taxonomy for <c>HVO.SkyMonitor.CameraAgent.Tests</c> (#2073).
/// </summary>
/// <remarks>
/// <para>
/// Every test class in <c>HVO.SkyMonitor.CameraAgent.Tests</c> carries exactly one
/// <see cref="TestPropertyAttribute"/> with name <see cref="PropertyName"/> (<c>"Area"</c>) and a value from
/// <see cref="All"/>, so <c>dotnet test --filter "Area=&lt;name&gt;"</c> partitions the assembly cleanly for
/// future class-time sharding and Area-based PR path filtering without modifying CI workflow filters yet.
/// </para>
/// <para>
/// Areas align 1:1 with <c>src/HVO.SkyMonitor.CameraAgent.Common</c> domain folders (with dot-delimited sub-areas
/// for <c>Capture/*</c> and <c>Modules/VirtualSky</c>), plus explicit areas for test classes outside
/// <c>CameraAgent.Common</c>: <see cref="ModulesZwo"/> (<c>HVO.SkyMonitor.CameraAgent.Modules.Zwo</c>),
/// <see cref="Cli"/> (<c>HVO.SkyMonitor.CameraAgent.Cli</c>), <see cref="Endpoints"/>, <see cref="Hosting"/>,
/// <see cref="Services"/>, and <see cref="Ui"/> (<c>HVO.SkyMonitor.CameraAgent</c>), and
/// <see cref="TestInfrastructure"/> (shared test contracts compile-linked into the test assembly).
/// </para>
/// </remarks>
internal static class CameraAgentTestAreas
{
    internal const string PropertyName = "Area";

    // CameraAgent.Common folders
    internal const string Automation = "Automation";
    internal const string Background = "Background";
    internal const string Capture = "Capture";
    internal const string CaptureCalibration = "Capture.Calibration";
    internal const string CaptureDistribution = "Capture.Distribution";
    internal const string CaptureExposure = "Capture.Exposure";
    internal const string CaptureFocus = "Capture.Focus";
    internal const string CaptureProcessing = "Capture.Processing";
    internal const string Configuration = "Configuration";
    internal const string Deployment = "Deployment";
    internal const string DeploymentLocation = "DeploymentLocation";
    internal const string Diagnostics = "Diagnostics";
    internal const string Environmental = "Environmental";
    internal const string Evidence = "Evidence";
    internal const string Fleet = "Fleet";
    internal const string Gallery = "Gallery";
    internal const string ModulesVirtualSky = "Modules.VirtualSky";
    internal const string NightlyProducts = "NightlyProducts";
    internal const string Operations = "Operations";
    internal const string OpticalCalibration = "OpticalCalibration";
    internal const string Options = "Options";
    internal const string Orbital = "Orbital";
    internal const string OrbitalPlanning = "OrbitalPlanning";
    internal const string RawIngress = "RawIngress";
    internal const string Scheduling = "Scheduling";
    internal const string Silo = "Silo";
    internal const string SiteProfile = "SiteProfile";
    internal const string SkyMap = "SkyMap";
    internal const string SpaceWeather = "SpaceWeather";
    internal const string Sqlite = "Sqlite";
    internal const string Storage = "Storage";
    internal const string Telemetry = "Telemetry";
    internal const string TimeLapses = "TimeLapses";
    internal const string TimeSync = "TimeSync";
    internal const string Transients = "Transients";
    internal const string Upload = "Upload";

    // Outside CameraAgent.Common (CameraAgent host, CLI, UI, ZWO module, shared test contracts)
    internal const string Cli = "Cli";
    internal const string Endpoints = "Endpoints";
    internal const string Hosting = "Hosting";
    internal const string ModulesZwo = "Modules.Zwo";
    internal const string Services = "Services";
    internal const string TestInfrastructure = "TestInfrastructure";
    internal const string Ui = "Ui";

    internal static readonly FrozenSet<string> All = new[]
    {
        Automation,
        Background,
        Capture,
        CaptureCalibration,
        CaptureDistribution,
        CaptureExposure,
        CaptureFocus,
        CaptureProcessing,
        Cli,
        Configuration,
        Deployment,
        DeploymentLocation,
        Diagnostics,
        Endpoints,
        Environmental,
        Evidence,
        Fleet,
        Gallery,
        Hosting,
        ModulesVirtualSky,
        ModulesZwo,
        NightlyProducts,
        Operations,
        OpticalCalibration,
        Options,
        Orbital,
        OrbitalPlanning,
        RawIngress,
        Scheduling,
        Services,
        Silo,
        SiteProfile,
        SkyMap,
        SpaceWeather,
        Sqlite,
        Storage,
        Telemetry,
        TestInfrastructure,
        TimeLapses,
        TimeSync,
        Transients,
        Ui,
        Upload
    }.ToFrozenSet(StringComparer.Ordinal);

    internal static void AssertEveryTestClassHasValidArea(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var testClasses = assembly
            .GetTypes()
            .Where(static type => !type.IsAbstract && type.IsDefined(typeof(TestClassAttribute), inherit: true))
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .ToArray();

        Assert.IsNotEmpty(
            testClasses,
            $"No [TestClass] types were discovered in {assembly.GetName().Name}, so Area coverage could not be verified.");

        var violations = new List<string>();
        var observedAreas = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in testClasses)
        {
            var properties = type.GetCustomAttributes<TestPropertyAttribute>(inherit: true).ToArray();
            var wrongCase = properties
                .Where(static prop => !string.Equals(prop.Name, PropertyName, StringComparison.Ordinal) &&
                    string.Equals(prop.Name, PropertyName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (var bad in wrongCase)
            {
                violations.Add($"{type.FullName}: property name '{bad.Name}' must be exact '{PropertyName}'.");
            }

            var areas = properties
                .Where(static prop => string.Equals(prop.Name, PropertyName, StringComparison.Ordinal))
                .Select(static prop => prop.Value)
                .ToArray();

            if (areas.Length == 0)
            {
                violations.Add($"{type.FullName}: missing [TestProperty(\"{PropertyName}\", \"<Area>\")].");
                continue;
            }

            if (areas.Length > 1)
            {
                violations.Add($"{type.FullName}: declares {areas.Length} '{PropertyName}' traits ({string.Join(", ", areas)}); expected exactly 1.");
                continue;
            }

            var area = areas[0];
            if (string.IsNullOrWhiteSpace(area) || !All.Contains(area))
            {
                violations.Add($"{type.FullName}: unknown {PropertyName} '{area}'.");
                continue;
            }

            observedAreas.Add(area);
        }

        foreach (var unused in All.Except(observedAreas, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            violations.Add($"Unused {PropertyName} declared in {nameof(CameraAgentTestAreas)}: '{unused}'.");
        }

        Assert.IsEmpty(
            violations,
            $"CameraAgent.Tests {PropertyName} taxonomy violations ({violations.Count}):{Environment.NewLine}" +
            string.Join(Environment.NewLine, violations));
    }
}
