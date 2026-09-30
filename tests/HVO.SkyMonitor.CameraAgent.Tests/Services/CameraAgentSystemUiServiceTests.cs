using System.Security.Claims;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Catalog.Sqlite;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentSystemUiServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Health_MapsEveryCheckToAFixedLabelInScopeOrder()
    {
        using var fixture = new Fixture();
        fixture.Report(
            ("brand-new-check", Entry(HealthStatus.Healthy)),
            ("artifact-outbox", Entry(HealthStatus.Unhealthy)),
            ("identity-database", Entry(HealthStatus.Healthy)),
            ("capture-processing", Entry(HealthStatus.Degraded)),
            ("environmental-acquisition", Entry(HealthStatus.Healthy, availability: "Disabled")),
            ("self", Entry(HealthStatus.Healthy)));

        var result = await fixture.Service.GetHealthAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess);
        var view = result.Value!;
        string[] expectedLabels =
            ["Host process", "Environmental acquisition", "Capture processing", "Identity database", "Artifact delivery", "brand-new-check"];
        CollectionAssert.AreEqual(expectedLabels, view.Checks.Select(static check => check.Label).ToArray());
        string[] expectedScopes = ["Host", "Acquisition", "Processing", "Local", "Central", "Other"];
        CollectionAssert.AreEqual(expectedScopes, view.Checks.Select(static check => check.Scope).ToArray());
        Assert.AreEqual(SystemCheckState.Disabled, Check(view, "environmental-acquisition").State);
        Assert.AreEqual(SystemCheckState.Degraded, Check(view, "capture-processing").State);
        Assert.AreEqual(SystemCheckState.Unhealthy, Check(view, "artifact-outbox").State);
        Assert.AreEqual("/operations/pipeline", Check(view, "capture-processing").Href);
        Assert.IsNull(Check(view, "brand-new-check").Href);
        Assert.AreEqual(TimeSpan.FromMilliseconds(7), Check(view, "self").Duration);
        Assert.AreEqual(SystemCheckState.Unhealthy, view.Overall);
        Assert.IsTrue(view.CentralIntegrationEnabled);
        Assert.AreEqual(Start, view.EvaluatedUtc);
        Assert.IsGreaterThan(0, view.Host.ProcessorCount);
    }

    [TestMethod]
    public async Task Health_WhenStandalone_SwitchesOffCentralChecksAndLeavesThemOutOfTheOverall()
    {
        using var fixture = new Fixture(centralEnabled: false);
        fixture.Report(
            ("artifact-outbox", Entry(HealthStatus.Unhealthy)),
            ("fleet-heartbeat", Entry(HealthStatus.Unhealthy)),
            ("capture-processing", Entry(HealthStatus.Degraded)));

        var result = await fixture.Service.GetHealthAsync(CancellationToken.None).ConfigureAwait(false);

        var view = result.Value!;
        Assert.IsFalse(view.CentralIntegrationEnabled);
        Assert.AreEqual(SystemCheckState.Disabled, Check(view, "artifact-outbox").State);
        Assert.AreEqual(SystemCheckState.Disabled, Check(view, "fleet-heartbeat").State);
        Assert.AreEqual(SystemCheckState.Degraded, view.Overall);
    }

    [TestMethod]
    public async Task Health_WithOnlySwitchedOffChecks_IsHealthy()
    {
        using var fixture = new Fixture();
        fixture.Report(("environmental-acquisition", Entry(HealthStatus.Unhealthy, availability: "Disabled")));

        var result = await fixture.Service.GetHealthAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(SystemCheckState.Healthy, result.Value!.Overall);
    }

    [TestMethod]
    public async Task Health_WhenTheChecksTimeOut_SaysSoWithoutFailingTheCaller()
    {
        using var fixture = new Fixture();
        fixture.Health.Setup(value => value.CheckHealthAsync(
                It.IsAny<Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var result = await fixture.Service.GetHealthAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.AreEqual("The health checks did not finish within 15 seconds.", result.Message);
    }

    [TestMethod]
    public async Task Health_WhenTheCallerCancels_Throws()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        fixture.Health.Setup(value => value.CheckHealthAsync(
                It.IsAny<Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await fixture.Service.GetHealthAsync(cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task Health_WhenAChecksThrows_ReturnsAFixedMessage()
    {
        using var fixture = new Fixture();
        fixture.Health.Setup(value => value.CheckHealthAsync(
                It.IsAny<Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("/var/lib/hvo/private/identity.db is locked"));

        var result = await fixture.Service.GetHealthAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.AreEqual("The health checks could not be run.", result.Message);
    }

    [TestMethod]
    public async Task Software_ReportsTheSelectedCatalogWithoutItsPaths()
    {
        using var fixture = new Fixture();

        var result = await fixture.Service.GetSoftwareAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess);
        var view = result.Value!;
        Assert.IsFalse(string.IsNullOrWhiteSpace(view.Version));
        Assert.IsFalse(string.IsNullOrWhiteSpace(view.Runtime));
        Assert.AreEqual(
            new SystemCatalogView("hyg-v42-production", "4.2", "Fixture", "2026.09.1", 2, "celestial-v2", "3",
                118_218, 52_428_800, new string('a', 64)),
            view.Catalog);
    }

    [TestMethod]
    public async Task EveryRead_IsRefusedWithoutOperationsRead()
    {
        using var fixture = new Fixture(authorized: false);

        var health = await fixture.Service.GetHealthAsync(CancellationToken.None).ConfigureAwait(false);
        var software = await fixture.Service.GetSoftwareAsync(CancellationToken.None).ConfigureAwait(false);
        var receipts = await fixture.Service.GetControlReceiptsAsync(5, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, health.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, software.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, receipts.Kind);
        fixture.Health.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Receipts_MergeCaptureCommandsAndOverridesNewestFirst()
    {
        using var fixture = new Fixture();
        var schedule = await fixture.InitializeStoresAsync().ConfigureAwait(false);
        var coordinator = fixture.Coordinator;

        fixture.Clock.Now = Start.AddMinutes(1);
        var paused = await coordinator.PauseAsync("pause-1", 0, "owner-1", "Dome maintenance", CancellationToken.None)
            .ConfigureAwait(false);
        fixture.Clock.Now = Start.AddMinutes(2);
        var closed = new CaptureScheduleOverride("closed", schedule.ActiveRevision.RevisionId,
            schedule.ActiveRevision.ScheduleSha256, CaptureScheduleOverrideMode.ForceClosed,
            Start.AddMinutes(2), Start.AddMinutes(30));
        _ = await fixture.Schedule.AddOverrideAsync(closed, "add-closed", schedule.Version, "owner-1", "Rain",
            CancellationToken.None).ConfigureAwait(false);
        fixture.Clock.Now = Start.AddMinutes(3);
        _ = await coordinator.ResumeAsync("resume-1", paused.Version, "installer-lifecycle:upgrade", null,
            CancellationToken.None).ConfigureAwait(false);
        fixture.Clock.Now = Start.AddMinutes(4);
        _ = await coordinator.ResumeAsync("resume-2", null, "owner-1", null, CancellationToken.None).ConfigureAwait(false);

        var result = await fixture.Service.GetControlReceiptsAsync(
            CameraAgentSystemUiService.MaxReceipts, CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess);
        var receipts = result.Value!;
        SystemControlReceiptKind[] expectedKinds =
        [
            SystemControlReceiptKind.ResumeCapture,
            SystemControlReceiptKind.ResumeCapture,
            SystemControlReceiptKind.OverrideCreated,
            SystemControlReceiptKind.PauseCapture,
        ];
        CollectionAssert.AreEqual(expectedKinds, receipts.Select(static receipt => receipt.Kind).ToArray());
        Assert.AreEqual(new SystemControlReceipt(Start.AddMinutes(4), SystemControlReceiptKind.ResumeCapture,
            SystemControlActor.LocalOwner, null, SystemControlOutcome.NoChange, null, null, null), receipts[0]);
        Assert.AreEqual(SystemControlActor.Installer, receipts[1].Actor);
        Assert.AreEqual(SystemControlOutcome.Applied, receipts[1].Outcome);
        Assert.AreEqual(new SystemControlReceipt(Start.AddMinutes(2), SystemControlReceiptKind.OverrideCreated,
            SystemControlActor.LocalOwner, "Rain", SystemControlOutcome.Applied, CaptureScheduleOverrideMode.ForceClosed,
            closed.StartUtc, closed.EndUtc), receipts[2]);
        Assert.AreEqual("Dome maintenance", receipts[3].Reason);
        Assert.AreEqual(Start.AddMinutes(1), receipts[3].OccurredUtc);

        var two = await fixture.Service.GetControlReceiptsAsync(2, CancellationToken.None).ConfigureAwait(false);
        CollectionAssert.AreEqual(expectedKinds[..2], two.Value!.Select(static receipt => receipt.Kind).ToArray());
        var none = await fixture.Service.GetControlReceiptsAsync(0, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(1, none.Value!);
        var many = await fixture.Service.GetControlReceiptsAsync(500, CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(4, many.Value!);
    }

    [TestMethod]
    public async Task Receipts_WhenTheStoresCannotBeRead_ReturnAFixedMessage()
    {
        using var fixture = new Fixture();
        fixture.Ingress.Setup(value => value.InitializeAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromException(new IOException("/var/lib/hvo/private/raw-ingress.db is locked")));

        var result = await fixture.Service.GetControlReceiptsAsync(5, CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.AreEqual("Control receipts could not be read.", result.Message);
    }

    [TestMethod]
    [DataRow("installer-lifecycle:upgrade", "Installer")]
    [DataRow("installer-lifecycle:", "Installer")]
    [DataRow("system", "System")]
    [DataRow("System", "LocalOwner")]
    [DataRow("5f0c2a9e-owner", "LocalOwner")]
    public void MapActor_NamesOnlyFixedActors(string actor, string expected)
        => Assert.AreEqual(expected, CameraAgentSystemUiService.MapActor(actor).ToString());

    [TestMethod]
    [DataRow(null, "unknown", null)]
    [DataRow(" ", "unknown", null)]
    [DataRow("1.4.2", "1.4.2", null)]
    [DataRow("1.4.2+abc123", "1.4.2", "abc123")]
    [DataRow("1.4.2+0123456789abcdef0123", "1.4.2", "0123456789ab")]
    public void SplitVersion_SeparatesAndShortensTheRevision(string? informational, string version, string? revision)
        => Assert.AreEqual((version, revision), CameraAgentSystemUiService.SplitVersion(informational));

    private static SystemHealthCheck Check(SystemHealthView view, string name)
        => view.Checks.Single(check => check.Name == name);

    private static HealthReportEntry Entry(HealthStatus status, string? availability = null)
        => new(
            status,
            "/var/lib/hvo/private must never be shown",
            TimeSpan.FromMilliseconds(7),
            null,
            availability is null
                ? new Dictionary<string, object>()
                : new Dictionary<string, object> { ["Availability"] = availability });

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hvo-system-ui", Guid.NewGuid().ToString("N"));
        private readonly CaptureControlTelemetry _telemetry = new();

        internal Mock<IRawCaptureIngress> Ingress { get; } = new();

        internal Fixture(bool centralEnabled = true, bool authorized = true)
        {
            Directory.CreateDirectory(_root);
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = _root,
                RawIngressSqliteBusyTimeoutSeconds = 1,
                CentralIntegration = new CentralIntegrationOptions
                {
                    Mode = centralEnabled ? CentralIntegrationMode.Enabled : CentralIntegrationMode.Disabled
                }
            });
            var ingress = Ingress;
            ingress.Setup(value => value.InitializeAsync(It.IsAny<CancellationToken>()))
                .Returns((CancellationToken token) => new ValueTask(new SqliteRawCaptureJournal(
                    Path.Combine(_root, "journal", "raw-ingress.db"), 1).InitializeAsync(token)));
            Coordinator = new CaptureAdmissionCoordinator(ingress.Object, options, Clock, _telemetry);
            Schedule = new SqliteCaptureScheduleStore(ingress.Object, options, Clock);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "test"));
            var authentication = new Mock<AuthenticationStateProvider>();
            authentication.Setup(value => value.GetAuthenticationStateAsync())
                .ReturnsAsync(new AuthenticationState(principal));
            var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
            authorization.Setup(value => value.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
                .ReturnsAsync(authorized ? AuthorizationResult.Success() : AuthorizationResult.Failed());
            Service = new CameraAgentSystemUiService(
                authentication.Object,
                authorization.Object,
                Health.Object,
                Coordinator,
                Schedule,
                new CatalogSnapshotResult("2026.09.1", CatalogSnapshotPackageKind.Fixture, 2, "hyg-v42-production", "4.2",
                    "celestial-v2", "3", "/opt/hvo/catalog/manifest.json", "/opt/hvo/catalog/catalog.db",
                    new string('a', 64), 52_428_800, 118_218, null!),
                options,
                Clock,
                NullLogger<CameraAgentSystemUiService>.Instance);
        }

        internal SettableTimeProvider Clock { get; } = new(Start);

        internal Mock<HealthCheckService> Health { get; } = new(MockBehavior.Strict);

        internal CaptureAdmissionCoordinator Coordinator { get; }

        internal SqliteCaptureScheduleStore Schedule { get; }

        internal CameraAgentSystemUiService Service { get; }

        internal void Report(params (string Name, HealthReportEntry Entry)[] entries)
            => Health.Setup(value => value.CheckHealthAsync(
                    It.IsAny<Func<HealthCheckRegistration, bool>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HealthReport(
                    entries.ToDictionary(static entry => entry.Name, static entry => entry.Entry, StringComparer.Ordinal),
                    TimeSpan.FromMilliseconds(20)));

        internal async Task<CaptureScheduleStoreSnapshot> InitializeStoresAsync()
        {
            await Coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            return await Schedule.InitializeAsync(Configuration(), CancellationToken.None).ConfigureAwait(false);
        }

        public void Dispose()
        {
            Schedule.Dispose();
            Coordinator.Dispose();
            _telemetry.Dispose();
            Directory.Delete(_root, recursive: true);
        }

        private static CameraModuleConfig Configuration()
            => new(
                new ObservatoryLocation(35, -114, 1000, "UTC"),
                new CameraModuleDescriptor("test"),
                new CameraRigConfig(
                    new SensorProfile("test", 2, 2, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                    new OpticsProfile("Perspective", 50, 10, 0),
                    new RigOrientation(90, 0, 0),
                    new PipelineExposureProfile(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 1, 10),
                    new CameraControlPolicy
                    {
                        ExposureControl = AutomaticControlOwnership.Disabled,
                        GainControl = AutomaticControlOwnership.Disabled
                    }),
                CapturePipelineConfig.Empty)
            {
                Schedule = new CaptureScheduleDefinition(
                    "capture-schedule-v1",
                    [new CaptureScheduleSetpointProfile("night", TimeSpan.FromSeconds(5), 1, TimeSpan.FromSeconds(10))],
                    [new CaptureWeeklyScheduleWindow(
                        "monday",
                        DayOfWeek.Monday,
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(18, 0)),
                        new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(6, 0), DayOffset: 1),
                        "night")])
            };
    }

    private sealed class SettableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
