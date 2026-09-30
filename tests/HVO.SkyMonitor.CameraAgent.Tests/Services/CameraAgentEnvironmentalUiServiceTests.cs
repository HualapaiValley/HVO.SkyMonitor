using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentEnvironmentalUiServiceTests
{
    private const string Root = "/var/lib/hvo/test-ingress";
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 4, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task GetLatestReadingsAsync_ReadsOneNewestObservationPerKindAndSkipsEmptyKinds()
    {
        var (principal, authorization) = ReadAuthorized();
        var store = new Mock<ILocalEnvironmentalObservationStore>(MockBehavior.Strict);
        var requested = new List<EnvironmentalObservationKind?>();
        store.Setup(value => value.ReadLocalPageAsync(
                Root, It.IsAny<EnvironmentalObservationKind?>(), 1, null, It.IsAny<CancellationToken>()))
            .Callback<string, EnvironmentalObservationKind?, int, LocalEnvironmentalObservationCursor?, CancellationToken>(
                (_, kind, _, _, _) => requested.Add(kind))
            .Returns<string, EnvironmentalObservationKind?, int, LocalEnvironmentalObservationCursor?, CancellationToken>(
                (_, kind, _, _, _) => ValueTask.FromResult(new LocalEnvironmentalObservationPage(
                    kind == EnvironmentalObservationKind.WindSpeed
                        ? [Record(kind.Value, EnvironmentalObservationUnit.MetersPerSecond, 2.8,
                            EnvironmentalObservationSourceKind.Simulated)]
                        : [],
                    null)));
        var service = CreateService(principal, authorization, EnabledSource(onDemand: false), observationStore: store.Object);

        var result = await service.GetLatestReadingsAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess);
        var reading = result.Value!.Single();
        Assert.AreEqual(EnvironmentalObservationKind.WindSpeed, reading.Kind);
        Assert.AreEqual(EnvironmentalObservationSourceKind.Simulated, reading.SourceKind);
        Assert.AreEqual(2.8, reading.NumericValue);
        Assert.AreEqual("rig-1", reading.RigId);
        CollectionAssert.AreEquivalent(
            Enum.GetValues<EnvironmentalObservationKind>().Cast<EnvironmentalObservationKind?>().ToArray(),
            requested);
    }

    [TestMethod]
    public async Task GetLatestReadingsAsync_RequiresReadAuthorizationBeforeTouchingTheStore()
    {
        var (principal, authorization) = ReadAuthorized(allowed: false);
        var service = CreateService(principal, authorization, EnabledSource(onDemand: false));

        var result = await service.GetLatestReadingsAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
    }

    [TestMethod]
    public async Task GetLatestReadingsAsync_SanitizesStoreFailure()
    {
        var (principal, authorization) = ReadAuthorized();
        var store = new Mock<ILocalEnvironmentalObservationStore>(MockBehavior.Strict);
        store.Setup(value => value.ReadLocalPageAsync(
                Root, It.IsAny<EnvironmentalObservationKind?>(), 1, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException($"{Root}/journal/raw-ingress.db is locked"));
        var service = CreateService(principal, authorization, EnabledSource(onDemand: false), observationStore: store.Object);

        var result = await service.GetLatestReadingsAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.AreEqual("Current environmental readings are unavailable.", result.Message);
    }

    [TestMethod]
    [DataRow(CentralIntegrationMode.Enabled, true, true)]
    [DataRow(CentralIntegrationMode.Disabled, true, false)]
    [DataRow(CentralIntegrationMode.Enabled, false, false)]
    public async Task GetStatusAsync_ProjectsScheduleRetentionAndDeliveryOnlyWhenExportIsEnabled(
        CentralIntegrationMode centralMode, bool deliveryEnabled, bool exportEnabled)
    {
        var (principal, authorization) = ReadAuthorized();
        var stateStore = new Mock<IEnvironmentalAcquisitionStateStore>(MockBehavior.Strict);
        stateStore.Setup(value => value.ReadSourceStatesAsync(Root, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        stateStore.Setup(value => value.ReadAttemptsAsync(Root, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var observationStore = new Mock<ILocalEnvironmentalObservationStore>(MockBehavior.Strict);
        observationStore.Setup(value => value.GetLocalSnapshotAsync(Root, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LocalEnvironmentalObservationSnapshot(3, 900, 0, Now.AddDays(-1), Now));
        var delivery = new EnvironmentalObservationDeliveryState();
        delivery.Update(
            new EnvironmentalObservationOutboxSnapshot(3, 900, 2, 600, 0, 1, 1, 0, 0, Now.AddMinutes(-7), Now),
            EnvironmentalObservationDeliveryAvailability.Degraded,
            "retrying",
            Now.AddMinutes(-2));
        var source = new EnvironmentalSourceConfiguration
        {
            Id = "weather-1",
            Type = "VirtualEnvironment",
            Kind = EnvironmentalObservationKind.AirTemperature,
            Triggers = [EnvironmentalAcquisitionTrigger.Periodic, EnvironmentalAcquisitionTrigger.EveryNthCapture],
            PeriodSeconds = 60,
            EveryNthCapture = 4
        };
        var service = new CameraAgentEnvironmentalUiService(
            new FixedAuthenticationStateProvider(principal),
            authorization,
            stateStore.Object,
            observationStore.Object,
            null!,
            delivery,
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = Root,
                CentralIntegration = new CentralIntegrationOptions { Mode = centralMode },
                EnvironmentalDelivery = new EnvironmentalObservationDeliveryOptions { Enabled = deliveryEnabled },
                EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
                {
                    Enabled = true,
                    RetentionDays = 14,
                    Sources = [source]
                }
            }),
            null!,
            new FixedTimeProvider(Now),
            NullLogger<CameraAgentEnvironmentalUiService>.Instance);

        var result = await service.GetStatusAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess);
        var status = result.Value!;
        Assert.AreEqual(14, status.RetentionDays);
        var projected = status.Sources.Single();
        CollectionAssert.AreEqual(
            new[] { EnvironmentalAcquisitionTrigger.Periodic, EnvironmentalAcquisitionTrigger.EveryNthCapture },
            projected.Triggers.ToArray());
        Assert.AreEqual(60, projected.PeriodSeconds);
        Assert.AreEqual(4, projected.EveryNthCapture);
        Assert.AreEqual(exportEnabled, status.Delivery.ExportEnabled);
        if (exportEnabled)
        {
            Assert.AreEqual("Degraded", status.Delivery.Availability);
            Assert.AreEqual(2, status.Delivery.PendingCount);
            Assert.AreEqual(1, status.Delivery.RetryCount);
            Assert.AreEqual(1, status.Delivery.QuarantineCount);
            Assert.AreEqual(Now.AddMinutes(-7), status.Delivery.OldestPendingUtc);
            Assert.AreEqual(Now.AddMinutes(-2), status.Delivery.LastAcknowledgedUtc);
        }
        else
        {
            Assert.AreEqual(new EnvironmentalUiDelivery(false, "Disabled", null, 0, 0, 0, null), status.Delivery);
        }
    }

    [TestMethod]
    public async Task AcquireAsync_ReauthorizesMutationAndRejectsMissingActor()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "test"));
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(
                principal, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1))
            .ReturnsAsync(AuthorizationResult.Success());
        var service = CreateService(principal, authorization.Object, EnabledSource(onDemand: true));

        var result = await service.AcquireAsync(
            "source-1", "command-1", "operator check", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        authorization.Verify(service => service.AuthorizeAsync(
            principal, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1), Times.Once);
    }

    [TestMethod]
    public async Task AcquireAsync_RejectsDisabledAndIneligibleSourcesBeforeCommandExecution()
    {
        var disabled = CreateAuthorizedService(EnabledSource(onDemand: true), enabled: false);
        var disabledResult = await disabled.AcquireAsync(
            "source-1", "command-1", "operator check", CancellationToken.None).ConfigureAwait(false);
        var ineligible = CreateAuthorizedService(EnabledSource(onDemand: false));
        var ineligibleResult = await ineligible.AcquireAsync(
            "source-1", "command-2", "operator check", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Invalid, disabledResult.Kind);
        Assert.AreEqual("Environmental acquisition is disabled.", disabledResult.Message);
        Assert.AreEqual(OperatorUiResultKind.Invalid, ineligibleResult.Kind);
        Assert.AreEqual("This source does not support on-demand acquisition.", ineligibleResult.Message);
    }

    [TestMethod]
    public async Task AcquireAsync_RejectsInvalidReasonAtServiceBoundary()
    {
        var service = CreateAuthorizedService(EnabledSource(onDemand: true));

        var result = await service.AcquireAsync(
            "source-1", "command-1", " invalid ", CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Invalid, result.Kind);
        Assert.AreEqual("The on-demand request is invalid.", result.Message);
    }

    private static CameraAgentEnvironmentalUiService CreateAuthorizedService(
        EnvironmentalSourceConfiguration source,
        bool enabled = true)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "owner-1"),
                new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
            ],
            IdentityConstants.ApplicationScheme));
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(
                principal, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1))
            .ReturnsAsync(AuthorizationResult.Success());
        return CreateService(principal, authorization.Object, source, enabled);
    }

    private static CameraAgentEnvironmentalUiService CreateService(
        ClaimsPrincipal principal,
        IAuthorizationService authorization,
        EnvironmentalSourceConfiguration source,
        bool enabled = true,
        IEnvironmentalAcquisitionStateStore? stateStore = null,
        ILocalEnvironmentalObservationStore? observationStore = null,
        EnvironmentalObservationDeliveryState? deliveryState = null,
        CentralIntegrationMode centralMode = CentralIntegrationMode.Enabled)
    {
        return new CameraAgentEnvironmentalUiService(
            new FixedAuthenticationStateProvider(principal),
            authorization,
            stateStore ?? new Mock<IEnvironmentalAcquisitionStateStore>(MockBehavior.Strict).Object,
            observationStore ?? new Mock<ILocalEnvironmentalObservationStore>(MockBehavior.Strict).Object,
            null!,
            deliveryState ?? new EnvironmentalObservationDeliveryState(),
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = Root,
                CentralIntegration = new CentralIntegrationOptions { Mode = centralMode },
                EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
                {
                    Enabled = enabled,
                    RetentionDays = 14,
                    Sources = [source]
                }
            }),
            null!,
            new FixedTimeProvider(Now),
            NullLogger<CameraAgentEnvironmentalUiService>.Instance);
    }

    private static (ClaimsPrincipal Principal, IAuthorizationService Authorization) ReadAuthorized(bool allowed = true)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "owner-1")], IdentityConstants.ApplicationScheme));
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(
                principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return (principal, authorization.Object);
    }

    private static LocalEnvironmentalObservationRecord Record(
        EnvironmentalObservationKind kind,
        EnvironmentalObservationUnit unit,
        double value,
        EnvironmentalObservationSourceKind sourceKind) => new(
            1,
            new EnvironmentalObservationFactV1(
                EnvironmentalObservationFactV1.CurrentSchemaVersion,
                Guid.NewGuid(),
                new EnvironmentalObservationSource(
                    "virtual", "wind-1", "1", sourceKind,
                    new EnvironmentalObservationProvenance(
                        new ProcessingAlgorithmIdentity("virtual-wind", "1"), default, new string('0', 64))),
                Now.AddSeconds(-20),
                null,
                null,
                Now.AddSeconds(-20),
                Now.AddMinutes(5),
                Now.AddMinutes(1),
                new EnvironmentalObservationValue(kind, unit, value, null, EnvironmentalObservationQuality.Good),
                [],
                "rig-1"),
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            128,
            Now.AddSeconds(-19));

    private static EnvironmentalSourceConfiguration EnabledSource(bool onDemand) => new()
    {
        Id = "source-1",
        Type = "VirtualEnvironment",
        Kind = EnvironmentalObservationKind.RelativeHumidity,
        Triggers = onDemand
            ? [EnvironmentalAcquisitionTrigger.OnDemand]
            : [EnvironmentalAcquisitionTrigger.Periodic]
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FixedAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
            => Task.FromResult(new AuthenticationState(principal));
    }
}
