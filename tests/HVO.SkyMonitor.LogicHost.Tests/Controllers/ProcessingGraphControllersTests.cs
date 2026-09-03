using System.Security.Claims;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Common.Identity;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.LogicHost.Controllers;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace HVO.SkyMonitor.Tests.LogicHost.Controllers;

[TestClass]
[TestCategory("Unit")]
public sealed class ProcessingGraphControllersTests
{
    [TestMethod]
    public async Task CatalogControllerMapsValidationAuthorityScopeAndMutationOutcomes()
    {
        var now = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
        var catalog = new StubCatalogService(now);
        var controller = SetContext(new ProcessingGraphsController(catalog, new TestTimeProvider(now)), Principal());

        Assert.IsInstanceOfType<BadRequestObjectResult>(
            (await controller.ListRevisionsAsync(0, CancellationToken.None).ConfigureAwait(false)).Result);
        Assert.IsInstanceOfType<OkObjectResult>(
            (await controller.ListRevisionsAsync(100, CancellationToken.None).ConfigureAwait(false)).Result);
        using var malformed = JsonDocument.Parse("{}");
        Assert.IsInstanceOfType<BadRequestObjectResult>(
            (await controller.CreateRevisionAsync(malformed.RootElement, CancellationToken.None)
                .ConfigureAwait(false)).Result);

        var definition = DatabaseSeeder.CreateBasicCentralProcessingGraph();
        using var definitionDocument = JsonDocument.Parse(ProcessingGraphJson.SerializeCanonical(definition));
        catalog.RevisionResult = new(CentralProcessingGraphMutationOutcome.Applied, catalog.Revision);
        Assert.IsInstanceOfType<OkObjectResult>(
            (await controller.CreateRevisionAsync(
                definitionDocument.RootElement, CancellationToken.None).ConfigureAwait(false)).Result);
        Assert.AreEqual("owner", catalog.Actor);
        Assert.IsTrue(catalog.PlatformEditor);

        var expectedResults = new (CentralProcessingGraphMutationOutcome Outcome, Type ResultType)[]
        {
            (CentralProcessingGraphMutationOutcome.Applied, typeof(OkObjectResult)),
            (CentralProcessingGraphMutationOutcome.Unchanged, typeof(OkObjectResult)),
            (CentralProcessingGraphMutationOutcome.Invalid, typeof(BadRequestObjectResult)),
            (CentralProcessingGraphMutationOutcome.Conflict, typeof(ConflictObjectResult)),
            (CentralProcessingGraphMutationOutcome.NotFoundOrDenied, typeof(NotFoundResult))
        };
        foreach (var expected in expectedResults)
        {
            catalog.RevisionResult = new(expected.Outcome, catalog.Revision);
            var result = await controller.PublishRevisionAsync(
                catalog.Revision.Id, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(expected.ResultType, result.Result!.GetType());
        }

        catalog.RevisionResult = new(CentralProcessingGraphMutationOutcome.Applied, catalog.Revision);
        Assert.IsInstanceOfType<OkObjectResult>(
            (await controller.RetireRevisionAsync(
                catalog.Revision.Id,
                new("superseded"),
                CancellationToken.None).ConfigureAwait(false)).Result);
        catalog.AssignmentResult = new(CentralProcessingGraphMutationOutcome.Applied, catalog.Assignment);
        Assert.IsInstanceOfType<OkObjectResult>(
            (await controller.AssignAsync(
                new(
                    catalog.Revision.Id,
                    CentralProcessingGraphTargetHost.Central,
                    CentralProcessingGraphAssignmentScope.GlobalDefault,
                    null,
                    null,
                    now,
                    null,
                    "test"),
                CancellationToken.None).ConfigureAwait(false)).Result);

        Assert.IsInstanceOfType<BadRequestObjectResult>(
            (await controller.ResolveAsync(
                "central", Guid.NewGuid(), cancellationToken: CancellationToken.None).ConfigureAwait(false)).Result);
        catalog.ResolvedAssignment = null;
        Assert.IsInstanceOfType<NotFoundResult>(
            (await controller.ResolveAsync(
                nameof(CentralProcessingGraphTargetHost.Central),
                Guid.NewGuid(),
                cancellationToken: CancellationToken.None).ConfigureAwait(false)).Result);
        catalog.ResolvedAssignment = catalog.Assignment;
        Assert.IsInstanceOfType<OkObjectResult>(
            (await controller.ResolveAsync(
                nameof(CentralProcessingGraphTargetHost.Central),
                Guid.NewGuid(),
                effectiveAtUtc: now,
                cancellationToken: CancellationToken.None).ConfigureAwait(false)).Result);

        catalog.RevisionResult = new((CentralProcessingGraphMutationOutcome)int.MaxValue, catalog.Revision);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await controller.PublishRevisionAsync(catalog.Revision.Id, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ExecutionControllerMapsCredentialIdempotencyScheduleReadAndCancellationOutcomes()
    {
        var now = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
        var service = new StubExecutionService(now);
        var controller = SetContext(
            new ProcessingGraphExecutionsController(service, new TestTimeProvider(now)), Principal());
        var request = new ProcessingGraphExecutionsController.ProcessingGraphReplayApiRequest(
            Guid.NewGuid(), [Guid.NewGuid()], "test");

        controller.Request.Headers["Idempotency-Key"] = StringValues.Empty;
        Assert.IsInstanceOfType<BadRequestObjectResult>(
            (await controller.ScheduleAsync(request, CancellationToken.None).ConfigureAwait(false)).Result);
        controller.Request.Headers["Idempotency-Key"] = "key";
        service.ScheduleResult = new(CentralProcessingGraphScheduleOutcome.Conflict);
        Assert.IsInstanceOfType<ConflictObjectResult>(
            (await controller.ScheduleAsync(request, CancellationToken.None).ConfigureAwait(false)).Result);
        service.ScheduleResult = new(
            CentralProcessingGraphScheduleOutcome.Invalid, ReasonCode: "sources-not-found-or-denied");
        Assert.IsInstanceOfType<NotFoundResult>(
            (await controller.ScheduleAsync(request, CancellationToken.None).ConfigureAwait(false)).Result);
        service.ScheduleResult = new(CentralProcessingGraphScheduleOutcome.Invalid, ReasonCode: "invalid-replay");
        Assert.IsInstanceOfType<BadRequestObjectResult>(
            (await controller.ScheduleAsync(request, CancellationToken.None).ConfigureAwait(false)).Result);
        service.ScheduleResult = new(CentralProcessingGraphScheduleOutcome.Created, service.Execution);
        Assert.IsInstanceOfType<CreatedAtActionResult>(
            (await controller.ScheduleAsync(request, CancellationToken.None).ConfigureAwait(false)).Result);
        service.ScheduleResult = new(CentralProcessingGraphScheduleOutcome.Existing, service.Execution);
        Assert.IsInstanceOfType<OkObjectResult>(
            (await controller.ScheduleAsync(request, CancellationToken.None).ConfigureAwait(false)).Result);

        Assert.IsInstanceOfType<BadRequestObjectResult>(
            (await controller.ListAsync(101, CancellationToken.None).ConfigureAwait(false)).Result);
        Assert.IsInstanceOfType<OkObjectResult>(
            (await controller.ListAsync(100, CancellationToken.None).ConfigureAwait(false)).Result);
        service.Detail = null;
        Assert.IsInstanceOfType<NotFoundResult>(
            (await controller.GetAsync(service.Execution.Id, CancellationToken.None).ConfigureAwait(false)).Result);
        service.Detail = CreateExecutionDetail(service.Execution.Id, now);
        Assert.IsInstanceOfType<OkObjectResult>(
            (await controller.GetAsync(service.Execution.Id, CancellationToken.None).ConfigureAwait(false)).Result);

        var cancellationResults = new (CentralProcessingGraphCancellationOutcome Outcome, Type ResultType)[]
        {
            (CentralProcessingGraphCancellationOutcome.Applied, typeof(AcceptedResult)),
            (CentralProcessingGraphCancellationOutcome.Unchanged, typeof(NoContentResult)),
            (CentralProcessingGraphCancellationOutcome.NotFoundOrDenied, typeof(NotFoundResult))
        };
        foreach (var expected in cancellationResults)
        {
            service.CancellationOutcome = expected.Outcome;
            var result = await controller.CancelAsync(service.Execution.Id, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(expected.ResultType, result.GetType());
        }
        service.CancellationOutcome = (CentralProcessingGraphCancellationOutcome)int.MaxValue;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await controller.CancelAsync(service.Execution.Id, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        var anonymous = SetContext(
            new ProcessingGraphExecutionsController(service, new TestTimeProvider(now)), new ClaimsPrincipal());
        Assert.IsInstanceOfType<ForbidResult>(
            (await anonymous.ScheduleAsync(request, CancellationToken.None).ConfigureAwait(false)).Result);
        Assert.IsInstanceOfType<ForbidResult>(
            (await anonymous.ListAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false)).Result);
        Assert.IsInstanceOfType<ForbidResult>(
            (await anonymous.GetAsync(service.Execution.Id, CancellationToken.None).ConfigureAwait(false)).Result);
        Assert.IsInstanceOfType<ForbidResult>(
            await anonymous.CancelAsync(service.Execution.Id, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DeviceControllerMapsStrictPayloadCredentialAndDurableStateFailures()
    {
        var now = new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero);
        var validator = new StubCredentialValidator(new DeviceRegistration { DeviceId = "device" });
        var delivery = new StubDeliveryService(now);
        var controller = new DeviceProcessingGraphController(validator, delivery);
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var poll = JsonSerializer.SerializeToElement(new ProcessingGraphProposalPollRequestV1(
            ProcessingGraphDeliverySchemaVersions.Current,
            "device",
            null,
            null,
            null,
            ProcessingGraphAgentCapabilities.Create([BuiltInProcessingRecipes.EncodedPreview])), serializerOptions);
        var factValue = new ProcessingGraphDeliveryFactV1(
            ProcessingGraphDeliverySchemaVersions.Current,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ProcessingGraphDeliveryFactKind.Rejected,
            now,
            ReasonCode: "unsupported");
        var fact = JsonSerializer.SerializeToElement(factValue, serializerOptions);

        Assert.IsInstanceOfType<JsonResult>(
            (await controller.PullAsync("device", "key", poll, CancellationToken.None).ConfigureAwait(false)).Result);
        Assert.IsInstanceOfType<JsonResult>(
            (await controller.AcknowledgeAsync("device", "key", fact, CancellationToken.None)
                .ConfigureAwait(false)).Result);

        using var invalid = JsonDocument.Parse("null");
        Assert.IsInstanceOfType<BadRequestObjectResult>(
            (await controller.PullAsync("device", "key", invalid.RootElement, CancellationToken.None)
                .ConfigureAwait(false)).Result);
        Assert.IsInstanceOfType<BadRequestObjectResult>(
            (await controller.AcknowledgeAsync("device", "key", invalid.RootElement, CancellationToken.None)
                .ConfigureAwait(false)).Result);

        validator.Exception = new DeviceRegistrationException("rejected", "invalid-credential");
        Assert.IsInstanceOfType<UnauthorizedObjectResult>(
            (await controller.PullAsync("device", "key", poll, CancellationToken.None).ConfigureAwait(false)).Result);
        Assert.IsInstanceOfType<UnauthorizedObjectResult>(
            (await controller.AcknowledgeAsync("device", "key", fact, CancellationToken.None)
                .ConfigureAwait(false)).Result);
        validator.Exception = null;

        foreach (var exception in new Exception[]
                 {
                     new JsonException(), new NotSupportedException(), new ArgumentException()
                 })
        {
            delivery.PullException = exception;
            Assert.IsInstanceOfType<BadRequestObjectResult>(
                (await controller.PullAsync("device", "key", poll, CancellationToken.None)
                    .ConfigureAwait(false)).Result);
        }
        delivery.PullException = null;
        foreach (var (exception, resultType) in new (Exception Exception, Type ResultType)[]
                 {
                     (new KeyNotFoundException(), typeof(NotFoundObjectResult)),
                     (new JsonException(), typeof(BadRequestObjectResult)),
                     (new NotSupportedException(), typeof(BadRequestObjectResult)),
                     (new ArgumentException(), typeof(BadRequestObjectResult)),
                     (new InvalidOperationException(), typeof(ConflictObjectResult))
                 })
        {
            delivery.FactException = exception;
            var result = await controller.AcknowledgeAsync(
                "device", "key", fact, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(resultType, result.Result!.GetType());
        }
    }

    private static T SetContext<T>(T controller, ClaimsPrincipal principal)
        where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        return controller;
    }

    private static ClaimsPrincipal Principal()
        => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "owner"),
            new Claim("account_type", "User"),
            new Claim(ClaimTypes.Role, AuthorizationRoleNames.PlatformEditor)
        ], IdentityConstants.ApplicationScheme));

    private static CentralProcessingGraphExecutionDetail CreateExecutionDetail(Guid id, DateTimeOffset now)
        => new(
            new(
                id,
                nameof(CentralProcessingGraphExecutionClass.Replay),
                nameof(CentralProcessingGraphExecutionStatus.Running),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                nameof(CentralProcessingGraphTrigger.Replay),
                "owner",
                "test",
                1,
                1,
                now,
                now,
                null),
            [],
            []);

    private sealed class StubCatalogService(DateTimeOffset now) : IProcessingGraphCatalogService
    {
        public CentralProcessingGraphRevisionView Revision { get; } = new(
            Guid.NewGuid(),
            "test",
            "1",
            CentralProcessingGraphLifecycle.Published,
            new string('1', 64),
            new string('2', 64),
            new string('3', 64),
            new string('4', 64),
            JsonSerializer.SerializeToElement(new { schema = "test" }),
            now,
            now,
            null);

        public CentralProcessingGraphAssignmentView Assignment => new(
            Guid.NewGuid(),
            Revision.Id,
            CentralProcessingGraphTargetHost.Central,
            CentralProcessingGraphAssignmentScope.GlobalDefault,
            null,
            null,
            now,
            null,
            now,
            "test",
            Revision);

        public CentralProcessingGraphMutationResult<CentralProcessingGraphRevisionView> RevisionResult { get; set; } =
            new(CentralProcessingGraphMutationOutcome.NotFoundOrDenied);

        public CentralProcessingGraphMutationResult<CentralProcessingGraphAssignmentView> AssignmentResult { get; set; } =
            new(CentralProcessingGraphMutationOutcome.NotFoundOrDenied);

        public CentralProcessingGraphAssignmentView? ResolvedAssignment { get; set; }

        public string? Actor { get; private set; }

        public bool PlatformEditor { get; private set; }

        public Task<IReadOnlyList<CentralProcessingGraphRevisionView>> ListRevisionsAsync(
            int maximumCount,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CentralProcessingGraphRevisionView>>([Revision]);

        public Task<CentralProcessingGraphMutationResult<CentralProcessingGraphRevisionView>> CreateRevisionAsync(
            ProcessingGraphDefinition definition,
            string actorUserId,
            bool isPlatformEditor,
            CancellationToken cancellationToken)
        {
            Actor = actorUserId;
            PlatformEditor = isPlatformEditor;
            return Task.FromResult(RevisionResult);
        }

        public Task<CentralProcessingGraphMutationResult<CentralProcessingGraphRevisionView>> PublishRevisionAsync(
            Guid revisionId,
            string actorUserId,
            bool isPlatformEditor,
            CancellationToken cancellationToken)
            => Task.FromResult(RevisionResult);

        public Task<CentralProcessingGraphMutationResult<CentralProcessingGraphRevisionView>> RetireRevisionAsync(
            Guid revisionId,
            string actorUserId,
            string reasonCode,
            bool isPlatformEditor,
            CancellationToken cancellationToken)
            => Task.FromResult(RevisionResult);

        public Task<CentralProcessingGraphMutationResult<CentralProcessingGraphAssignmentView>> AssignAsync(
            CentralProcessingGraphAssignmentRequest request,
            string actorUserId,
            bool isPlatformEditor,
            Guid? credentialObservatoryId,
            CancellationToken cancellationToken)
            => Task.FromResult(AssignmentResult);

        public Task<CentralProcessingGraphAssignmentView?> ResolveForUserAsync(
            CentralProcessingGraphTargetHost targetHost,
            Guid observatoryId,
            Guid? logicalCameraId,
            DateTimeOffset effectiveAtUtc,
            string actorUserId,
            bool isPlatformEditor,
            Guid? credentialObservatoryId,
            CancellationToken cancellationToken)
            => Task.FromResult(ResolvedAssignment);
    }

    private sealed class StubExecutionService(DateTimeOffset now) : ICentralProcessingGraphExecutionService
    {
        public CentralProcessingGraphExecution Execution { get; } = new() { Id = Guid.NewGuid() };

        public CentralProcessingGraphScheduleResult ScheduleResult { get; set; } = new(
            CentralProcessingGraphScheduleOutcome.Created);

        public CentralProcessingGraphExecutionDetail? Detail { get; set; } = CreateExecutionDetail(Guid.NewGuid(), now);

        public CentralProcessingGraphCancellationOutcome CancellationOutcome { get; set; }

        public Task<CentralProcessingGraphScheduleResult> ScheduleReplayAsync(
            Guid revisionId,
            IReadOnlyList<Guid> sourceArtifactIds,
            string actorId,
            Guid? observatoryScope,
            string idempotencyKey,
            string reasonCode,
            DateTimeOffset effectiveNow,
            CancellationToken cancellationToken)
            => Task.FromResult(ScheduleResult);

        public Task<IReadOnlyList<CentralProcessingGraphExecutionSummary>> ListAsync(
            string actorId,
            Guid? observatoryScope,
            int take,
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CentralProcessingGraphExecutionSummary>>(
                [CreateExecutionDetail(Execution.Id, now).Execution]);

        public Task<CentralProcessingGraphExecutionDetail?> GetAsync(
            Guid executionId,
            string actorId,
            Guid? observatoryScope,
            CancellationToken cancellationToken)
            => Task.FromResult(Detail);

        public Task<CentralProcessingGraphCancellationOutcome> CancelAsync(
            Guid executionId,
            string actorId,
            Guid? observatoryScope,
            DateTimeOffset effectiveNow,
            CancellationToken cancellationToken)
            => Task.FromResult(CancellationOutcome);
    }

    private sealed class StubCredentialValidator(DeviceRegistration registration) : IDeviceCredentialValidator
    {
        public DeviceRegistrationException? Exception { get; set; }

        public Task<DeviceRegistration> ValidateAsync(
            string deviceId,
            string deviceKey,
            CancellationToken cancellationToken = default)
            => Exception is null
                ? Task.FromResult(registration)
                : Task.FromException<DeviceRegistration>(Exception);
    }

    private sealed class StubDeliveryService(DateTimeOffset now) : IProcessingGraphDeliveryService
    {
        public Exception? PullException { get; set; }

        public Exception? FactException { get; set; }

        public Task<ProcessingGraphProposalPollResponseV1> PullAsync(
            DeviceRegistration registration,
            ProcessingGraphProposalPollRequestV1 request,
            CancellationToken cancellationToken)
            => PullException is null
                ? Task.FromResult(new ProcessingGraphProposalPollResponseV1(
                    ProcessingGraphDeliverySchemaVersions.Current,
                    ProcessingGraphProposalPollDisposition.Current,
                    "current",
                    now))
                : Task.FromException<ProcessingGraphProposalPollResponseV1>(PullException);

        public Task<ProcessingGraphFactAcknowledgementV1> AcknowledgeAsync(
            DeviceRegistration registration,
            ProcessingGraphDeliveryFactV1 fact,
            CancellationToken cancellationToken)
            => FactException is null
                ? Task.FromResult(new ProcessingGraphFactAcknowledgementV1(
                    ProcessingGraphDeliverySchemaVersions.Current,
                    fact.FactId,
                    ProcessingGraphFactAcknowledgementDisposition.Recorded,
                    now))
                : Task.FromException<ProcessingGraphFactAcknowledgementV1>(FactException);
    }

    private sealed class TestTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
