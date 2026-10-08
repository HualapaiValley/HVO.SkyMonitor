using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

/// <summary>Replaceable handlers for the Health, Control and Software pages, with read counts.</summary>
internal sealed class TestSystemUiService : ICameraAgentSystemUiService
{
    internal Func<CancellationToken, ValueTask<OperatorUiResult<SystemHealthView>>> HealthHandler { get; set; } =
        _ => ValueTask.FromResult(OperatorUiResult<SystemHealthView>.Success(Health()));

    internal Func<CancellationToken, ValueTask<OperatorUiResult<SystemSoftwareView>>> SoftwareHandler { get; set; } =
        _ => ValueTask.FromResult(OperatorUiResult<SystemSoftwareView>.Success(Software()));

    internal Func<int, CancellationToken, ValueTask<OperatorUiResult<IReadOnlyList<SystemControlReceipt>>>> ReceiptsHandler { get; set; } =
        (_, _) => ValueTask.FromResult(OperatorUiResult<IReadOnlyList<SystemControlReceipt>>.Success([]));

    internal int HealthReads { get; private set; }

    internal int ReceiptReads { get; private set; }

    public ValueTask<OperatorUiResult<SystemHealthView>> GetHealthAsync(CancellationToken cancellationToken)
    {
        HealthReads++;
        return HealthHandler(cancellationToken);
    }

    public ValueTask<OperatorUiResult<SystemSoftwareView>> GetSoftwareAsync(CancellationToken cancellationToken)
        => SoftwareHandler(cancellationToken);

    public ValueTask<OperatorUiResult<IReadOnlyList<SystemControlReceipt>>> GetControlReceiptsAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        ReceiptReads++;
        return ReceiptsHandler(limit, cancellationToken);
    }

    internal static SystemHealthView Health(bool centralEnabled = true, params SystemHealthCheck[] checks) => new(
        OperatorUiTestData.Now,
        checks.Length == 0
            ? SystemCheckState.Degraded
            : checks.Where(static check => check.State != SystemCheckState.Disabled).Select(static check => check.State).DefaultIfEmpty().Max(),
        checks.Length == 0 ? DefaultChecks(centralEnabled) : checks,
        new SystemHostFacts(
            OperatorUiTestData.Now.AddHours(-2),
            TimeSpan.FromMinutes(125),
            256L * 1024 * 1024,
            3.26,
            4,
            ".NET 10.0.0",
            "linux-x64"),
        centralEnabled);

    internal static SystemHealthCheck[] DefaultChecks(bool centralEnabled = true) =>
    [
        new("self", "Host process", "Host", SystemCheckState.Healthy, TimeSpan.FromMilliseconds(1), null),
        new("camera-configuration", "Camera configuration", "Acquisition", SystemCheckState.Healthy, TimeSpan.FromMilliseconds(4), "/operations/camera"),
        new("environmental-acquisition", "Environmental acquisition", "Acquisition", SystemCheckState.Disabled, TimeSpan.Zero, "/operations/environment"),
        new("capture-processing", "Capture processing", "Processing", SystemCheckState.Degraded, TimeSpan.FromMilliseconds(1500), "/operations/pipeline"),
        new("identity-database", "Identity database", "Local", SystemCheckState.Healthy, TimeSpan.FromMilliseconds(12), null),
        new("artifact-outbox", "Artifact delivery", "Central",
            centralEnabled ? SystemCheckState.Healthy : SystemCheckState.Disabled, TimeSpan.FromMilliseconds(3), "/operations/delivery"),
    ];

    internal static SystemSoftwareView Software(string? revision = "0123456789ab", string packageKind = "Production") => new(
        "1.4.2",
        revision,
        ".NET 10.0.0",
        "linux-x64",
        OperatorUiTestData.Now.AddHours(-2),
        TimeSpan.FromMinutes(125),
        new SystemCatalogView(
            "hyg-v41-openngc",
            "4.1",
            packageKind,
            "2026.07.1",
            3,
            "celestial-v2",
            "prep-7",
            118_218,
            52_428_800,
            "0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF"));
}
