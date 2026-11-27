using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace HVO.SkyMonitor.CameraAgent.Tests;

/// <summary>
/// Canonical <c>Area</c> test property taxonomy for <c>HVO.SkyMonitor.CameraAgent.Tests</c> (#2073).
/// </summary>
/// <remarks>
/// <para>
/// Every test class in <c>HVO.SkyMonitor.CameraAgent.Tests</c> carries exactly one class-level
/// <see cref="TestPropertyAttribute"/> with name <see cref="PropertyName"/> (<c>"Area"</c>) and a value from
/// <see cref="All"/>, and no test method may declare a method-level <c>Area</c> trait (which would precede the
/// class-level value in MSTest metadata and break VSTest <c>--filter "Area=&lt;name&gt;"</c> partitioning).
/// This allows <c>dotnet test --filter "Area=&lt;name&gt;"</c> to partition the assembly cleanly for
/// future class-time sharding and Area-based PR path filtering without modifying CI workflow filters yet.
/// </para>
/// <para>
/// Test classes are mapped by subject/SUT folder rather than a strict 1:1 mirror of every folder in
/// <c>src/HVO.SkyMonitor.CameraAgent.Common</c>. Most classes match their test folder, with dot-delimited
/// sub-areas for <c>Capture/*</c> and <c>Modules/VirtualSky</c>. Exceptions follow the SUT:
/// <c>Contracts/</c>, root-level classes, and specific SUT-owned tests (such as
/// <c>ProcessingGraphDeliveryServiceTests</c> → <see cref="CaptureProcessing"/> and
/// <c>CameraModuleOwnershipTests</c> → <see cref="Capture"/>) map to their SUT's Area; domain health checks
/// map to their domain Area; and host authentication, authorization, data, settings, and controllers map to
/// <see cref="Hosting"/>. The <c>CameraAgent.Common</c> folders <c>DependencyInjection</c>, <c>Frames</c>,
/// <c>Imaging</c>, <c>Logging</c>, <c>Modules</c> root/<c>NoOp</c>/<c>RandomImage</c>, and <c>Reflection</c>
/// have no dedicated Area. Explicit areas cover test classes outside <c>CameraAgent.Common</c>:
/// <see cref="ModulesZwo"/> (<c>HVO.SkyMonitor.CameraAgent.Modules.Zwo</c>),
/// <see cref="Cli"/> (<c>HVO.SkyMonitor.CameraAgent.Cli</c>), <see cref="Endpoints"/>, <see cref="Hosting"/>,
/// <see cref="Services"/>, and <see cref="Ui"/> (<c>HVO.SkyMonitor.CameraAgent</c>), plus
/// <see cref="TestInfrastructure"/> (shared test contracts compile-linked into the test assembly).
/// </para>
/// <para>
/// Enforced at test execution time via <c>ConnectionPoolClearContractTests.AssertAssemblySpecificContracts</c>
/// (hooked in <c>ConnectionPoolClearContractTests.Area.cs</c> and invoked from
/// <c>tests/TestSettings/ConnectionPoolClearContract.cs</c>).
/// </para>
/// </remarks>
internal static class CameraAgentTestAreas
{
    internal const string PropertyName = "Area";

    // CameraAgent.Common folders
    internal const string Automation = "Automation";
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

    private const BindingFlags InspectedMethodFlags =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.FlattenHierarchy;

    /// <summary>
    /// Enforces the <c>Area</c> trait contract across all concrete <c>[TestClass]</c> types in
    /// <paramref name="assembly"/>. Invoked via
    /// <c>ConnectionPoolClearContractTests.AssertAssemblySpecificContracts</c>
    /// (<c>ConnectionPoolClearContractTests.Area.cs</c>).
    /// </summary>
    internal static void AssertEveryTestClassHasValidArea(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var specimenViolations = new List<string>();
        CollectMethodAreaViolations(typeof(MethodAreaSpecimen), specimenViolations);
        Assert.AreEqual(
            2,
            specimenViolations.Count,
            $"Method-level {PropertyName} specimen check expected 2 violations (declared exact-case and inherited wrong-case), " +
            $"but found {specimenViolations.Count}: {string.Join("; ", specimenViolations)}");

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
            CollectMethodAreaViolations(type, violations);

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

    private static void CollectMethodAreaViolations(Type type, List<string> violations)
    {
        var methods = type
            .GetMethods(InspectedMethodFlags)
            .Where(static method => method.DeclaringType != typeof(object))
            .OrderBy(static method => method.Name, StringComparer.Ordinal)
            .ThenBy(static method => method.DeclaringType?.FullName, StringComparer.Ordinal);

        foreach (var method in methods)
        {
            var methodAreaTraits = method
                .GetCustomAttributes<TestPropertyAttribute>(inherit: true)
                .Where(static prop => string.Equals(prop.Name, PropertyName, StringComparison.OrdinalIgnoreCase));

            foreach (var trait in methodAreaTraits)
            {
                violations.Add(
                    $"{type.FullName}.{method.Name}: method-level [TestProperty(\"{trait.Name}\", \"{trait.Value}\")] is forbidden; '{PropertyName}' must be declared at the class level only.");
            }
        }
    }

    // Non-[TestClass] specimen used to self-verify that method-level Area traits (declared and inherited,
    // case-insensitively) are caught without altering MSTest discovery counts.
    private abstract class MethodAreaSpecimenBase
    {
        [SuppressMessage(
            "Usage",
            "MSTEST0007:Use 'TestMethod' attribute",
            Justification = "Compiled metadata specimen for method-level Area guard; intentionally not a [TestMethod] so MSTest discovery count is unchanged.")]
        [TestProperty("area", Capture)]
        public virtual void InheritedMethodWithWrongCaseArea()
        {
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1812:Avoid uninstantiated internal classes",
        Justification = "Compiled metadata specimen inspected via reflection; never instantiated.")]
    private sealed class MethodAreaSpecimen : MethodAreaSpecimenBase
    {
        [SuppressMessage(
            "Usage",
            "MSTEST0007:Use 'TestMethod' attribute",
            Justification = "Compiled metadata specimen for method-level Area guard; intentionally not a [TestMethod] so MSTest discovery count is unchanged.")]
        [TestProperty(PropertyName, Capture)]
        public static void DeclaredMethodWithArea()
        {
        }
    }
}
