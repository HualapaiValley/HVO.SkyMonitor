using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class CameraAgentTransientUiServiceTests
{
    [TestMethod]
    public async Task EveryCallReReadsPrincipalAndReauthorizesOperationsRead()
    {
        var principal = Principal();
        var authentication = new CountingAuthenticationStateProvider(principal);
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(
                principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(AuthorizationResult.Failed());
        var projection = new Mock<ICameraAgentTransientOperatorProjection>(MockBehavior.Strict);
        using var telemetry = new CameraAgentOperatorTelemetry();
        var service = CreateService(authentication, authorization.Object, projection.Object, telemetry);

        var page = await service.GetPageAsync(
            new CameraAgentTransientOperatorQuery(), CancellationToken.None).ConfigureAwait(false);
        var detail = await service.GetCandidateAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(2, authentication.ReadCount);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, page.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, detail.Kind);
        authorization.Verify(service => service.AuthorizeAsync(
            principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1), Times.Exactly(2));
    }

    [TestMethod]
    public async Task CaptureStages_RejectsUnauthorizedReadBeforeConsultingTheJournal()
    {
        var principal = Principal();
        var authentication = new CountingAuthenticationStateProvider(principal);
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(
                principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(AuthorizationResult.Failed());
        var runtime = new Mock<ITransientRuntimeManagement>(MockBehavior.Strict);
        using var telemetry = new CameraAgentOperatorTelemetry();
        var service = new CameraAgentTransientUiService(authentication, authorization.Object,
            Mock.Of<ICameraAgentTransientOperatorProjection>(), runtime.Object, telemetry,
            NullLogger<CameraAgentTransientUiService>.Instance);

        var result = await service.GetCaptureStagesAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, result.Kind);
        runtime.Verify(value => value.ReadCaptureStageEventsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task ProjectionFailureReturnsFixedSanitizedMessage()
    {
        var principal = Principal();
        var authorization = Authorized(principal);
        var projection = new Mock<ICameraAgentTransientOperatorProjection>(MockBehavior.Strict);
        projection.Setup(value => value.GetCandidateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidDataException("/private/raw-ingress.db secret actor stack"));
        using var telemetry = new CameraAgentOperatorTelemetry();
        var service = CreateService(
            new CountingAuthenticationStateProvider(principal), authorization.Object, projection.Object, telemetry);

        var result = await service.GetCandidateAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
        Assert.AreEqual("Transient evidence is unavailable.", result.Message);
        Assert.IsFalse(result.Message!.Contains("private", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(result.Message.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task TelemetryUsesExactInstrumentsUnitsOnlyOutcomeTagAndSerializedDtoBytes()
    {
        var measurements = new List<(string Name, double Value, KeyValuePair<string, object?>[] Tags)>();
        var instruments = new Dictionary<string, string?>();
        var stoppedActivities = new List<Activity>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == CameraAgentOperatorTelemetry.InstrumentationName &&
                    instrument.Name.StartsWith("hvo.transient.operator.", StringComparison.Ordinal))
                {
                    instruments[instrument.Name] = instrument.Unit;
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Add((instrument.Name, value, tags.ToArray())));
        listener.Start();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CameraAgentOperatorTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = stoppedActivities.Add
        };
        ActivitySource.AddActivityListener(activityListener);
        using var telemetry = new CameraAgentOperatorTelemetry();
        telemetry.RecordTransientQuery(TimeSpan.Zero, 0, "candidate-secret-value");
        var principal = Principal();
        var authorization = Authorized(principal);
        var page = new CameraAgentTransientOperatorPage(
            [new(
                Guid.NewGuid(), Guid.NewGuid(), "Provisional", "Provisional", "candidate_persisted",
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "Available", "Pending", "Pending", "Pending")],
            "next");
        var projection = new Mock<ICameraAgentTransientOperatorProjection>(MockBehavior.Strict);
        projection.Setup(value => value.GetPageAsync(It.IsAny<CameraAgentTransientOperatorQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);
        var service = CreateService(
            new CountingAuthenticationStateProvider(principal), authorization.Object, projection.Object, telemetry);

        using var activity = telemetry.StartTransientQuery();
        Assert.IsNotNull(activity);
        Assert.AreEqual("transient.operator.query", activity.OperationName);
        var result = await service.GetPageAsync(
            new CameraAgentTransientOperatorQuery(), CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsFalse(measurements.SelectMany(static value => value.Tags)
            .Any(static tag => string.Equals(tag.Value as string, "candidate-secret-value", StringComparison.Ordinal)));
        Assert.IsTrue(measurements.Any(value => value.Name == "hvo.transient.operator.queries" &&
            value.Tags.Any(static tag => string.Equals(tag.Value as string, "unavailable", StringComparison.Ordinal))));
        Assert.AreEqual(null, instruments["hvo.transient.operator.queries"]);
        Assert.AreEqual("ms", instruments["hvo.transient.operator.query.duration"]);
        Assert.AreEqual("By", instruments["hvo.transient.operator.response.bytes"]);
        var response = measurements.Last(value => value.Name == "hvo.transient.operator.response.bytes");
        Assert.AreEqual(JsonSerializer.SerializeToUtf8Bytes(page).LongLength, (long)response.Value);
        Assert.HasCount(1, response.Tags);
        Assert.AreEqual("outcome", response.Tags[0].Key);
        Assert.AreEqual("success", response.Tags[0].Value);
        var queryActivity = stoppedActivities.Single(value => value.OperationName == "transient.operator.query");
        Assert.AreEqual(ActivityStatusCode.Ok, queryActivity.Status);
        Assert.AreEqual("success", queryActivity.GetTagItem("outcome"));
        Assert.IsTrue(measurements.Where(value => value.Name.StartsWith("hvo.transient.operator.", StringComparison.Ordinal))
            .All(value => value.Tags.Length == 1 && value.Tags[0].Key == "outcome"));
    }

    private static CameraAgentTransientUiService CreateService(
        AuthenticationStateProvider authentication,
        IAuthorizationService authorization,
        ICameraAgentTransientOperatorProjection projection,
        CameraAgentOperatorTelemetry telemetry) => new(
            authentication,
            authorization,
            projection,
            Mock.Of<ITransientRuntimeManagement>(),
            telemetry,
            NullLogger<CameraAgentTransientUiService>.Instance);

    private static Mock<IAuthorizationService> Authorized(ClaimsPrincipal principal)
    {
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(
                principal, null, CameraAgentAuthorizationPolicyNames.OperationsReadV1))
            .ReturnsAsync(AuthorizationResult.Success());
        return authorization;
    }

    private static ClaimsPrincipal Principal() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "operator-id")], "test"));

    private sealed class CountingAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        internal int ReadCount { get; private set; }

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            ReadCount++;
            return Task.FromResult(new AuthenticationState(principal));
        }
    }
}
