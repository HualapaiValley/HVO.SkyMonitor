using System.Collections.Immutable;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class ProcessingGraphDeliveryServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task DisabledModesStopBeforeConfigurationIsRequired()
    {
        var cases = new[]
        {
            new CameraAgentHostOptions
            {
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled }
            },
            new CameraAgentHostOptions
            {
                ProcessingGraphDelivery = new ProcessingGraphDeliveryOptions { Enabled = false }
            }
        };

        foreach (var hostOptions in cases)
        {
            var state = new ProcessingGraphDeliveryState();
            using var telemetry = new ProcessingGraphDeliveryTelemetry(state, TimeProvider.System);
            using var service = CreateService(
                Mock.Of<IProcessingGraphDeliveryTransport>(),
                Mock.Of<IProcessingGraphDeliveryInbox>(),
                Mock.Of<IProcessingGraphOperations>(),
                new CameraAgentConfigurationAccessor(),
                state,
                telemetry,
                hostOptions,
                TimeProvider.System);

            await RunUntilAsync(
                service,
                () => state.Snapshot.Availability == ProcessingGraphDeliveryAvailability.Disabled).ConfigureAwait(false);

            Assert.AreEqual(ProcessingGraphDeliveryAvailability.Disabled, state.Snapshot.Availability);
            Assert.AreEqual("disabled", state.Snapshot.ReasonCode);
        }
    }

    [TestMethod]
    public async Task CycleAcknowledgesFactsStagesProposalAndBecomesHealthy()
    {
        var facts = new Queue<ProcessingGraphDeliveryFactV1>(
        [
            CreateFact(ProcessingGraphDeliveryFactKind.Accepted),
            CreateFact(ProcessingGraphDeliveryFactKind.RolledBack),
            CreateFact(ProcessingGraphDeliveryFactKind.Expired)
        ]);
        var acknowledgements = new Queue<ProcessingGraphFactAcknowledgementDisposition>(
        [
            ProcessingGraphFactAcknowledgementDisposition.Recorded,
            ProcessingGraphFactAcknowledgementDisposition.Duplicate,
            ProcessingGraphFactAcknowledgementDisposition.Superseded
        ]);
        var proposal = CreateProposal();
        var inbox = CreateInbox();
        inbox.Setup(value => value.ReadPendingFactAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult<ProcessingGraphDeliveryFactV1?>(
                facts.Count == 0 ? null : facts.Dequeue()));
        inbox.Setup(value => value.AcknowledgeFactAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.CompletedTask);
        inbox.Setup(value => value.SupersedeProposalAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.CompletedTask);
        inbox.Setup(value => value.StageAsync(proposal, It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.CompletedTask);
        inbox.Setup(value => value.ReadBacklogAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(new ProcessingGraphDeliveryBacklog(0, null, 0, null)));
        var transport = new Mock<IProcessingGraphDeliveryTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendFactAsync(It.IsAny<ProcessingGraphDeliveryFactV1>(), It.IsAny<CancellationToken>()))
            .Returns((ProcessingGraphDeliveryFactV1 fact, CancellationToken _) =>
                ValueTask.FromResult(new ProcessingGraphFactTransportResult(
                    ProcessingGraphDeliveryTransportDisposition.Acknowledged,
                    "fact-acknowledged",
                    new ProcessingGraphFactAcknowledgementV1(
                        ProcessingGraphDeliverySchemaVersions.Current,
                        fact.FactId,
                        acknowledgements.Dequeue(),
                        Now))));
        transport.Setup(value => value.PullAsync(It.IsAny<ProcessingGraphProposalPollRequestV1>(), It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(new ProcessingGraphProposalTransportResult(
                ProcessingGraphDeliveryTransportDisposition.Acknowledged,
                "proposal-available",
                new ProcessingGraphProposalPollResponseV1(
                    ProcessingGraphDeliverySchemaVersions.Current,
                    ProcessingGraphProposalPollDisposition.Proposed,
                    "proposal-available",
                    Now,
                    proposal))));
        var state = new ProcessingGraphDeliveryState();
        using var telemetry = new ProcessingGraphDeliveryTelemetry(state, new FixedTimeProvider(Now));
        using var service = CreateService(
            transport.Object,
            inbox.Object,
            CreateOperations().Object,
            CreateConfigurationAccessor(),
            state,
            telemetry,
            EnabledOptions(),
            new FixedTimeProvider(Now));

        await RunUntilAsync(
            service,
            () => state.Snapshot.Availability == ProcessingGraphDeliveryAvailability.Healthy).ConfigureAwait(false);

        Assert.AreEqual("proposal-available", state.Snapshot.ReasonCode);
        Assert.AreEqual(Now, state.Snapshot.LastCentralContactUtc);
        inbox.Verify(value => value.AcknowledgeFactAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        inbox.Verify(value => value.SupersedeProposalAsync(
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
        inbox.Verify(value => value.StageAsync(proposal, It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestMethod]
    public async Task InvalidFactAcknowledgementsAreDeferredWithBoundedRetry()
    {
        var fact = CreateFact(ProcessingGraphDeliveryFactKind.Accepted);
        var invalidResults = new[]
        {
            new ProcessingGraphFactTransportResult(ProcessingGraphDeliveryTransportDisposition.Retry, "retry", RetryAfter: TimeSpan.Zero),
            new ProcessingGraphFactTransportResult(ProcessingGraphDeliveryTransportDisposition.Acknowledged, "missing"),
            AcknowledgedFact(fact, fact.FactId, ProcessingGraphFactAcknowledgementDisposition.Recorded, Now, "invalid"),
            AcknowledgedFact(fact, Guid.NewGuid(), ProcessingGraphFactAcknowledgementDisposition.Recorded, Now),
            AcknowledgedFact(fact, fact.FactId, (ProcessingGraphFactAcknowledgementDisposition)999, Now),
            AcknowledgedFact(fact, fact.FactId, ProcessingGraphFactAcknowledgementDisposition.Recorded,
                new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.FromHours(1)))
        };

        foreach (var invalidResult in invalidResults)
        {
            var inbox = CreateInbox();
            inbox.Setup(value => value.ReadPendingFactAsync(It.IsAny<CancellationToken>()))
                .Returns(() => ValueTask.FromResult<ProcessingGraphDeliveryFactV1?>(fact));
            DateTimeOffset? retryAt = null;
            inbox.Setup(value => value.RetryFactAsync(
                    fact.FactId,
                    It.IsAny<DateTimeOffset>(),
                    invalidResult.ReasonCode,
                    It.IsAny<CancellationToken>()))
                .Callback((Guid _, DateTimeOffset next, string _, CancellationToken _) => retryAt = next)
                .Returns(() => ValueTask.CompletedTask);
            inbox.Setup(value => value.ReadBacklogAsync(It.IsAny<CancellationToken>()))
                .Returns(() => ValueTask.FromResult(new ProcessingGraphDeliveryBacklog(0, null, 1, Now)));
            var transport = new Mock<IProcessingGraphDeliveryTransport>(MockBehavior.Strict);
            transport.Setup(value => value.SendFactAsync(fact, It.IsAny<CancellationToken>()))
                .Returns(() => ValueTask.FromResult(invalidResult));
            transport.Setup(value => value.PullAsync(It.IsAny<ProcessingGraphProposalPollRequestV1>(), It.IsAny<CancellationToken>()))
                .Returns(() => ValueTask.FromResult(new ProcessingGraphProposalTransportResult(
                    ProcessingGraphDeliveryTransportDisposition.Retry,
                    "central-unavailable",
                    RetryAfter: TimeSpan.FromDays(1))));
            var time = new FixedTimeProvider(Now);
            var state = new ProcessingGraphDeliveryState();
            using var telemetry = new ProcessingGraphDeliveryTelemetry(state, time);
            using var service = CreateService(
                transport.Object,
                inbox.Object,
                CreateOperations().Object,
                CreateConfigurationAccessor(),
                state,
                telemetry,
                EnabledOptions(),
                time);

            await RunUntilAsync(
                service,
                () => state.Snapshot.ReasonCode == "central-unavailable").ConfigureAwait(false);

            Assert.AreEqual(ProcessingGraphDeliveryAvailability.Degraded, state.Snapshot.Availability);
            Assert.AreEqual(1, state.Snapshot.PendingFactCount);
            Assert.IsNotNull(retryAt);
            Assert.IsTrue(retryAt >= Now.AddSeconds(1) && retryAt <= Now.AddSeconds(4));
        }
    }

    [TestMethod]
    public async Task RejectedFactDispositionsSettleTerminallyWithoutRetry()
    {
        var rejected = CreateFact(ProcessingGraphDeliveryFactKind.Accepted);
        var facts = new Queue<ProcessingGraphDeliveryFactV1>([rejected]);
        var inbox = CreateInbox();
        inbox.Setup(value => value.ReadPendingFactAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult<ProcessingGraphDeliveryFactV1?>(
                facts.Count == 0 ? null : facts.Dequeue()));
        inbox.Setup(value => value.RejectFactAsync(rejected.FactId, "http-409", It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.CompletedTask);
        inbox.Setup(value => value.ReadBacklogAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(new ProcessingGraphDeliveryBacklog(0, null, 0, null, 1)));
        var transport = new Mock<IProcessingGraphDeliveryTransport>(MockBehavior.Strict);
        transport.Setup(value => value.SendFactAsync(rejected, It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(new ProcessingGraphFactTransportResult(
                ProcessingGraphDeliveryTransportDisposition.Rejected, "http-409")));
        transport.Setup(value => value.PullAsync(It.IsAny<ProcessingGraphProposalPollRequestV1>(), It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(new ProcessingGraphProposalTransportResult(
                ProcessingGraphDeliveryTransportDisposition.Acknowledged,
                "current",
                new ProcessingGraphProposalPollResponseV1(
                    ProcessingGraphDeliverySchemaVersions.Current,
                    ProcessingGraphProposalPollDisposition.Current,
                    "current",
                    Now))));
        var state = new ProcessingGraphDeliveryState();
        using var telemetry = new ProcessingGraphDeliveryTelemetry(state, new FixedTimeProvider(Now));
        using var service = CreateService(
            transport.Object,
            inbox.Object,
            CreateOperations().Object,
            CreateConfigurationAccessor(),
            state,
            telemetry,
            EnabledOptions(),
            new FixedTimeProvider(Now));

        await RunUntilAsync(
            service,
            () => state.Snapshot.Availability == ProcessingGraphDeliveryAvailability.Healthy).ConfigureAwait(false);

        inbox.Verify(value => value.RejectFactAsync(rejected.FactId, "http-409", It.IsAny<CancellationToken>()), Times.Once);
        inbox.Verify(value => value.RetryFactAsync(
            It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        transport.Verify(value => value.SendFactAsync(rejected, It.IsAny<CancellationToken>()), Times.Once);
        Assert.AreEqual(0, state.Snapshot.PendingFactCount);
    }

    [TestMethod]
    public async Task InvalidPollContractsAndDurableFailuresReportBoundedHealthStates()
    {
        var proposal = CreateProposal();
        var invalidResponses = new[]
        {
            new ProcessingGraphProposalPollResponseV1("invalid", ProcessingGraphProposalPollDisposition.Current, "current", Now),
            new ProcessingGraphProposalPollResponseV1(ProcessingGraphDeliverySchemaVersions.Current,
                (ProcessingGraphProposalPollDisposition)999, "invalid", Now),
            new ProcessingGraphProposalPollResponseV1(ProcessingGraphDeliverySchemaVersions.Current,
                ProcessingGraphProposalPollDisposition.Current, " ", Now),
            new ProcessingGraphProposalPollResponseV1(ProcessingGraphDeliverySchemaVersions.Current,
                ProcessingGraphProposalPollDisposition.Current, new string('x', 129), Now),
            new ProcessingGraphProposalPollResponseV1(ProcessingGraphDeliverySchemaVersions.Current,
                ProcessingGraphProposalPollDisposition.Current, "current",
                new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.FromHours(1))),
            new ProcessingGraphProposalPollResponseV1(ProcessingGraphDeliverySchemaVersions.Current,
                ProcessingGraphProposalPollDisposition.Proposed, "proposed", Now),
            new ProcessingGraphProposalPollResponseV1(ProcessingGraphDeliverySchemaVersions.Current,
                ProcessingGraphProposalPollDisposition.Current, "current", Now, proposal)
        };

        foreach (var response in invalidResponses)
        {
            var inbox = CreateInbox();
            inbox.Setup(value => value.ReadPendingFactAsync(It.IsAny<CancellationToken>()))
                .Returns(() => ValueTask.FromResult<ProcessingGraphDeliveryFactV1?>(null));
            inbox.Setup(value => value.ReadBacklogAsync(It.IsAny<CancellationToken>()))
                .Returns(() => ValueTask.FromResult(new ProcessingGraphDeliveryBacklog(1, Now, 0, null)));
            var transport = new Mock<IProcessingGraphDeliveryTransport>(MockBehavior.Strict);
            transport.Setup(value => value.PullAsync(It.IsAny<ProcessingGraphProposalPollRequestV1>(), It.IsAny<CancellationToken>()))
                .Returns(() => ValueTask.FromResult(new ProcessingGraphProposalTransportResult(
                    ProcessingGraphDeliveryTransportDisposition.Acknowledged,
                    "acknowledged",
                    response)));
            var state = new ProcessingGraphDeliveryState();
            using var telemetry = new ProcessingGraphDeliveryTelemetry(state, TimeProvider.System);
            using var service = CreateService(
                transport.Object,
                inbox.Object,
                CreateOperations().Object,
                CreateConfigurationAccessor(),
                state,
                telemetry,
                EnabledOptions(),
                TimeProvider.System);

            await RunUntilAsync(
                service,
                () => state.Snapshot.ReasonCode == "delivery-cycle-failed").ConfigureAwait(false);

            Assert.AreEqual(ProcessingGraphDeliveryAvailability.Degraded, state.Snapshot.Availability);
            Assert.AreEqual(1, state.Snapshot.PendingProposalCount);
        }

        var failedInbox = CreateInbox();
        failedInbox.Setup(value => value.ReadPendingFactAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult<ProcessingGraphDeliveryFactV1?>(null));
        failedInbox.Setup(value => value.ReadBacklogAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromException<ProcessingGraphDeliveryBacklog>(
                new IOException("state unavailable")));
        var unhealthy = new ProcessingGraphDeliveryState();
        using var failedTelemetry = new ProcessingGraphDeliveryTelemetry(unhealthy, TimeProvider.System);
        using var failedService = CreateService(
            Mock.Of<IProcessingGraphDeliveryTransport>(),
            failedInbox.Object,
            CreateOperations().Object,
            CreateConfigurationAccessor(),
            unhealthy,
            failedTelemetry,
            EnabledOptions(),
            TimeProvider.System);

        await RunUntilAsync(
            failedService,
            () => unhealthy.Snapshot.Availability == ProcessingGraphDeliveryAvailability.Unhealthy).ConfigureAwait(false);

        Assert.AreEqual("durable-state-unavailable", unhealthy.Snapshot.ReasonCode);
    }

    private static ProcessingGraphFactTransportResult AcknowledgedFact(
        ProcessingGraphDeliveryFactV1 fact,
        Guid factId,
        ProcessingGraphFactAcknowledgementDisposition disposition,
        DateTimeOffset acknowledgedAtUtc,
        string schemaVersion = ProcessingGraphDeliverySchemaVersions.Current)
        => new(
            ProcessingGraphDeliveryTransportDisposition.Acknowledged,
            "acknowledged",
            new ProcessingGraphFactAcknowledgementV1(schemaVersion, factId, disposition, acknowledgedAtUtc),
            TimeSpan.FromDays(1));

    private static Mock<IProcessingGraphDeliveryInbox> CreateInbox()
    {
        var inbox = new Mock<IProcessingGraphDeliveryInbox>(MockBehavior.Strict);
        inbox.SetupGet(value => value.Capabilities)
            .Returns(ProcessingGraphAgentCapabilities.Create(["RawCapturePersistence"]));
        inbox.Setup(value => value.ObserveActiveRevisionAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.CompletedTask);
        inbox.Setup(value => value.ExpirePendingProposalsAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(0));
        return inbox;
    }

    private static Mock<IProcessingGraphOperations> CreateOperations()
    {
        var revision = new ProcessingGraphRevisionState(
            "active", "active", "1", ProcessingGraphRevisionLifecycle.Active,
            new string('A', 64), new string('B', 64), new string('C', 64),
            Now, Now, Now, null);
        var operations = new Mock<IProcessingGraphOperations>(MockBehavior.Strict);
        operations.Setup(value => value.GetRegistryAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(new ProcessingGraphRegistryState(
                ProcessingGraphRegistryMode.Named, revision.RevisionId, "basic", 1, [revision])));
        return operations;
    }

    private static CameraAgentConfigurationAccessor CreateConfigurationAccessor()
    {
        var accessor = new CameraAgentConfigurationAccessor();
        accessor.SetConfiguration(new CameraModuleConfig(null!, null!, null!, null!, "agent-1"));
        return accessor;
    }

    private static ProcessingGraphDeliveryFactV1 CreateFact(ProcessingGraphDeliveryFactKind kind)
        => new(
            ProcessingGraphDeliverySchemaVersions.Current,
            Guid.NewGuid(),
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            kind,
            Now);

    private static ProcessingGraphDeliveryProposalV1 CreateProposal()
        => new(
            ProcessingGraphDeliverySchemaVersions.Current,
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Guid.Parse("20000000-0000-0000-0000-000000000001"),
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            Guid.Parse("40000000-0000-0000-0000-000000000001"),
            Guid.Parse("50000000-0000-0000-0000-000000000001"),
            Guid.Parse("60000000-0000-0000-0000-000000000001"),
            "active",
            new string('1', 64),
            new string('2', 64),
            new string('3', 64),
            new ProcessingGraphDefinition(
                ProcessingGraphSchemaVersions.Current,
                "test",
                "1",
                ImmutableArray<ProcessingGraphSourceDefinition>.Empty,
                ImmutableArray<ProcessingGraphNodeDefinition>.Empty),
            Now,
            Now.AddHours(1));

    private static CameraAgentHostOptions EnabledOptions()
        => new()
        {
            ProcessingGraphDelivery = new ProcessingGraphDeliveryOptions
            {
                PollIntervalSeconds = 5,
                AcknowledgementBatchSize = 8,
                RetryInitialDelaySeconds = 1,
                RetryMaximumDelaySeconds = 4
            }
        };

    private static ProcessingGraphDeliveryService CreateService(
        IProcessingGraphDeliveryTransport transport,
        IProcessingGraphDeliveryInbox inbox,
        IProcessingGraphOperations operations,
        ICameraAgentConfigurationAccessor configurationAccessor,
        ProcessingGraphDeliveryState state,
        ProcessingGraphDeliveryTelemetry telemetry,
        CameraAgentHostOptions options,
        TimeProvider timeProvider)
        => new(
            transport,
            inbox,
            operations,
            configurationAccessor,
            state,
            telemetry,
            Options.Create(options),
            timeProvider,
            NullLogger<ProcessingGraphDeliveryService>.Instance);

    private static async Task RunUntilAsync(
        ProcessingGraphDeliveryService service,
        Func<bool> predicate)
    {
        await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!predicate())
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
