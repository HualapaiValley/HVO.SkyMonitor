using System.Security.Claims;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentProcessingGraphUiServiceTests
{
    private static readonly DateTimeOffset Now = ProcessingExecutionPagesTests.Now;

    [TestMethod]
    public async Task GetExecutions_ReadsBothClassesWithinTheBoundAndSummarizesAsync()
    {
        var operations = new Mock<IProcessingGraphOperations>(MockBehavior.Strict);
        operations.Setup(value => value.ReadExecutionsAsync(ProcessingGraphExecutionClass.Live, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync([ProcessingExecutionPagesTests.Execution(Guid.NewGuid(), ProcessingGraphExecutionClass.Live, ProcessingGraphExecutionStatus.Running)]);
        operations.Setup(value => value.ReadExecutionsAsync(ProcessingGraphExecutionClass.Replay, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var service = CreateService(operations.Object, authorized: true);

        var result = await service.GetExecutionsAsync(5000, CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(50, result.Value!.MaximumPerClass);
        Assert.HasCount(1, result.Value.Live);
        Assert.IsEmpty(result.Value.Replay);
        Assert.IsTrue(result.Value.HasActiveWork);
        Assert.AreEqual(1, result.Value.Running(ProcessingGraphExecutionClass.Live));
        Assert.AreEqual(Now, result.Value.ReadUtc);
    }

    [TestMethod]
    public async Task GetExecutionDetail_DropsLeaseOwnersAndMapsNotFoundAsync()
    {
        var executionId = Guid.NewGuid();
        var execution = ProcessingExecutionPagesTests.Execution(executionId, ProcessingGraphExecutionClass.Replay, ProcessingGraphExecutionStatus.Completed);
        var detail = new ProcessingGraphExecutionDetail(execution,
        [
            new ProcessingGraphExecutionNodeState("preview", true, new string('P', 64), "Completed", "/var/lib/secret failed", 1, Now, Now,
                [], [new ProcessingGraphNodeAttemptState(1, "runner-host-secret-7", Now, Now, "Completed", HVO.SkyMonitor.Processing.ProcessingOutcomeStatus.Produced, null, TimeSpan.FromSeconds(1),
                    HVO.SkyMonitor.CameraAgent.Common.Capture.Processing.ProcessingNodeExecutionRoute.InProcess)],
                 [new ProcessingGraphExecutionOutputState(0, new string('O', 64), Guid.NewGuid(), FrameArtifactRole.Preview, "display", "Missing", "/var/lib/secret failed", false)])
             { Dependencies = [new HVO.SkyMonitor.Processing.ProcessingGraphDependencyDefinition("$raw")] }
        ]);
        var operations = new Mock<IProcessingGraphOperations>(MockBehavior.Strict);
        operations.Setup(value => value.ReadExecutionDetailAsync(executionId, It.IsAny<CancellationToken>())).ReturnsAsync(detail);
        operations.Setup(value => value.ReadExecutionDetailAsync(It.Is<Guid>(id => id != executionId), It.IsAny<CancellationToken>())).ReturnsAsync((ProcessingGraphExecutionDetail?)null);
        var service = CreateService(operations.Object, authorized: true);

        var found = await service.GetExecutionDetailAsync(executionId, CancellationToken.None).ConfigureAwait(false);
        var missing = await service.GetExecutionDetailAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(found.IsSuccess);
        var serialized = JsonSerializer.Serialize(found.Value);
        Assert.IsFalse(serialized.Contains("runner-host-secret-7", StringComparison.Ordinal));
        Assert.IsFalse(serialized.Contains("/var/lib/", StringComparison.Ordinal), "path-like reasons are withheld on the graph path too");
        Assert.AreEqual(CameraAgentReplayUiService.Sanitize("/var/lib/secret failed"), found.Value!.Nodes[0].Reason);
        Assert.AreEqual(CameraAgentReplayUiService.Sanitize("/var/lib/secret failed"), found.Value.Nodes[0].Outputs[0].AvailabilityReason);
        Assert.IsFalse(serialized.Contains("LeaseOwner", StringComparison.Ordinal));
        Assert.AreEqual("Produced", found.Value!.Nodes[0].Attempts[0].Outcome);
        Assert.AreEqual("$raw", found.Value.Nodes[0].Dependencies.Single().ProducerId);
        Assert.AreEqual(OperatorUiResultKind.NotFound, missing.Kind);
        Assert.AreEqual("The execution was not found.", missing.Message);
    }

    [TestMethod]
    public async Task GetLiveExecutionId_UsesExactCaptureIdentityAndReportsMissingAsync()
    {
        var captureId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        var operations = new Mock<IProcessingGraphOperations>(MockBehavior.Strict);
        operations.Setup(value => value.ReadLiveExecutionIdAsync(captureId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(executionId);
        operations.Setup(value => value.ReadLiveExecutionIdAsync(It.Is<Guid>(id => id != captureId), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);
        var service = CreateService(operations.Object, authorized: true);

        var found = await service.GetLiveExecutionIdAsync(captureId, CancellationToken.None).ConfigureAwait(false);
        var missing = await service.GetLiveExecutionIdAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(found.IsSuccess);
        Assert.AreEqual(executionId, found.Value!.ExecutionId);
        Assert.AreEqual(OperatorUiResultKind.NotFound, missing.Kind);
        operations.Verify(value => value.ReadExecutionsAsync(It.IsAny<ProcessingGraphExecutionClass?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Reads_WhenDeniedOrFailing_ReturnFixedStatesAsync()
    {
        var operations = new Mock<IProcessingGraphOperations>(MockBehavior.Strict);
        operations.Setup(value => value.GetRegistryAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("/var/lib/secret"));
        var denied = CreateService(operations.Object, authorized: false);
        var failing = CreateService(operations.Object, authorized: true);

        var deniedResult = await denied.GetExecutionsAsync(10, CancellationToken.None).ConfigureAwait(false);
        var failingResult = await failing.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(OperatorUiResultKind.Unauthorized, deniedResult.Kind);
        Assert.AreEqual(OperatorUiResultKind.Unavailable, failingResult.Kind);
        Assert.AreEqual("Current graph registry data is unavailable.", failingResult.Message);
        operations.Verify(value => value.ReadExecutionsAsync(It.IsAny<ProcessingGraphExecutionClass?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestMethod]
    public async Task Mutations_CarryTheOwnerActorAndMapConflictsAsync()
    {
        var registry = new ProcessingGraphRegistryState(ProcessingGraphRegistryMode.Named, "rev-2", "rev-1", 7, []);
        var operations = new Mock<IProcessingGraphOperations>(MockBehavior.Strict);
        operations.Setup(value => value.ActivateRevisionAsync("rev-2", 6, "key-1", "owner-id", "go", It.IsAny<CancellationToken>())).ReturnsAsync(registry);
        operations.Setup(value => value.RetireRevisionAsync("rev-1", 6, "key-2", "owner-id", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProcessingGraphStoreConflictException("stale"));
        operations.Setup(value => value.ValidateRevisionAsync("rev-9", "key-3", "owner-id", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("rev-9"));
        operations.Setup(value => value.RollbackRevisionAsync("rev-1", 6, "key-4", "owner-id", null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("cannot roll back to the active revision"));
        var service = CreateService(operations.Object, authorized: true);

        var activated = await service.ActivateAsync("rev-2", 6, "key-1", "go", CancellationToken.None).ConfigureAwait(false);
        var conflict = await service.RetireAsync("rev-1", 6, "key-2", null, CancellationToken.None).ConfigureAwait(false);
        var missing = await service.ValidateAsync("rev-9", "key-3", null, CancellationToken.None).ConfigureAwait(false);
        var invalid = await service.RollbackAsync("rev-1", 6, "key-4", null, CancellationToken.None).ConfigureAwait(false);
        var denied = await CreateService(operations.Object, authorized: false).ActivateAsync("rev-2", 6, "key-1", "go", CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(activated.IsSuccess, $"{activated.Kind}: {activated.Message}");
        Assert.AreEqual(7, activated.Value!.StateVersion);
        Assert.AreEqual(OperatorUiResultKind.Conflict, conflict.Kind);
        Assert.AreEqual(OperatorUiResultKind.NotFound, missing.Kind);
        Assert.AreEqual(OperatorUiResultKind.Invalid, invalid.Kind);
        Assert.IsFalse(invalid.Message!.Contains("cannot roll back", StringComparison.Ordinal));
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, denied.Kind);
    }

    [TestMethod]
    public async Task RevisionDetailAndPreview_CompileAgainstTheCurrentConfigurationWithoutWritingAsync()
    {
        var registry = ProcessingGraphPagesTests.Registry();
        var pipeline = new CapturePipelineConfig([new CaptureProcessingStepConfig("Preview", "preview", 10, null, ["$raw"], Enabled: true)]);
        var operations = new Mock<IProcessingGraphOperations>(MockBehavior.Strict);
        operations.Setup(value => value.GetRegistryAsync(It.IsAny<CancellationToken>())).ReturnsAsync(registry);
        operations.Setup(value => value.ReadRevisionPipelineAsync("rev-validated", It.IsAny<CancellationToken>())).ReturnsAsync(pipeline);
        var factory = new Mock<ICaptureProcessingPipelineFactory>(MockBehavior.Strict);
        CameraModuleConfig? compiled = null;
        factory.Setup(value => value.PreviewPlan(It.IsAny<CameraModuleConfig>()))
            .Returns<CameraModuleConfig>(config =>
            {
                compiled = config;
                return new CaptureProcessingPlanPreview(config.Pipeline.SchemaVersion, config.Pipeline.DependencyPolicy, new string('D', 64), new string('E', 64), [], []);
            });
        var service = CreateService(operations.Object, authorized: true, factory.Object);

        var detail = await service.GetRevisionDetailAsync("rev-validated", CancellationToken.None).ConfigureAwait(false);
        var missing = await service.GetRevisionDetailAsync("rev-missing", CancellationToken.None).ConfigureAwait(false);
        var preview = await service.PreviewAsync(pipeline, CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(detail.IsSuccess, detail.Message);
        Assert.AreSame(pipeline, detail.Value!.Pipeline);
        Assert.IsNotNull(detail.Value.Plan);
        Assert.AreSame(pipeline, compiled!.Pipeline);
        Assert.AreEqual("test-agent", compiled.AgentId);
        Assert.AreEqual(OperatorUiResultKind.NotFound, missing.Kind);
        Assert.IsTrue(preview.IsSuccess);
        operations.Verify(value => value.CreateRevisionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CapturePipelineConfig>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

        factory.Setup(value => value.PreviewPlan(It.IsAny<CameraModuleConfig>())).Throws(new InvalidOperationException("dependency cycle at /private/path"));
        var invalid = await service.PreviewAsync(pipeline, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OperatorUiResultKind.Invalid, invalid.Kind);
        Assert.AreEqual("The desired graph contains a dependency cycle.", invalid.Message);
    }

    private static CameraAgentProcessingGraphUiService CreateService(IProcessingGraphOperations operations, bool authorized, ICaptureProcessingPipelineFactory? factory = null)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "owner-id"),
            new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
        ], Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme));
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(service => service.AuthorizeAsync(user, null, It.IsAny<string>()))
            .ReturnsAsync(authorized ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var profile = SchedulePageTests.Profile();
        var accessor = new HVO.SkyMonitor.CameraAgent.Common.Configuration.CameraAgentConfigurationAccessor();
        accessor.SetConfiguration(new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"), profile.Module, profile.Rig, CapturePipelineConfig.Empty, AgentId: "test-agent")
        {
            Schedule = profile.Schedule
        });
        return new CameraAgentProcessingGraphUiService(
            new StubAuthenticationStateProvider(user),
            authorization.Object,
            operations,
            factory ?? Mock.Of<ICaptureProcessingPipelineFactory>(),
            accessor,
            new ProcessingExecutionPagesTests.FixedTimeProvider(Now),
            NullLogger<CameraAgentProcessingGraphUiService>.Instance);
    }

    private sealed class StubAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }
}
