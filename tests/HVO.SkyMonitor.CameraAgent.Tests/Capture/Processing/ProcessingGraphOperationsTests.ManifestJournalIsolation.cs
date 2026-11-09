using System.Security.Claims;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Endpoints;
using HVO.SkyMonitor.CameraAgent.Tests.Evidence;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    [DataRow("detail")]
    [DataRow("api")]
    [DataRow("ui")]
    [DataRow("replay")]
    [DataRow("export")]
    public async Task CorruptOptionalManifestCountsPreserveJournalConsumersAndRecover(string consumer)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-journal");
        try
        {
            using var provider = CreateProvider(root);
            var id = await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            if (consumer == "replay")
            {
                var live = (await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false))!;
                id = (await ExecuteReplayAsync(provider, new ProcessingReplaySubmission(
                    live.Execution.CaptureId, live.Execution.GraphRevisionId, live.Execution.PrimaryArtifactId),
                    "manifest-journal-replay").ConfigureAwait(false)).Execution.ExecutionId;
            }
            var expected = await ReadManifestJournalConsumerAsync(root, operations, id, consumer, corrupt: false).ConfigureAwait(false);
            await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE processing_executions SET frozen_plan_json =
                    CAST(json_set(CAST(frozen_plan_json AS TEXT), '$.committedOutputCounts', json('{"preview":-1}')) AS BLOB)
                WHERE execution_id = $execution;
                """;
            command.Parameters.AddWithValue("$execution", id.ToString("N"));
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            await RunManifestTestAssertions.ResponseAsync(operations, id, CameraAgentRunManifest.CorruptReason).ConfigureAwait(false);
            Assert.AreEqual(expected, await ReadManifestJournalConsumerAsync(root, operations, id, consumer, corrupt: true).ConfigureAwait(false));

            // Optional metadata must not turn cancellation into an unavailable/success response,
            // and a canceled read must not poison the next journal or manifest read.
            using var canceled = new CancellationTokenSource();
            await canceled.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await operations.ReadExecutionDetailAsync(id, canceled.Token).ConfigureAwait(false)).ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await operations.ReadRunManifestAsync(id, canceled.Token).ConfigureAwait(false)).ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await ReadManifestJournalConsumerAsync(root, operations, id, consumer, corrupt: true, canceled.Token).ConfigureAwait(false)).ConfigureAwait(false);

            // Simulate restoring a legacy frozen plan; do not fabricate a replacement count.
            command.CommandText = """
                UPDATE processing_executions SET frozen_plan_json =
                    CAST(json_remove(CAST(frozen_plan_json AS TEXT), '$.committedOutputCounts') AS BLOB)
                WHERE execution_id = $execution;
                """;
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            Assert.AreEqual(expected, await ReadManifestJournalConsumerAsync(root, operations, id, consumer, corrupt: false).ConfigureAwait(false));
            await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static async Task<string> ReadManifestJournalConsumerAsync(
        string root, ProcessingGraphOperationsCoordinator operations, Guid id, string consumer, bool corrupt,
        CancellationToken cancellationToken = default)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "journal-reader")]));
        var authentication = new Mock<AuthenticationStateProvider>();
        authentication.Setup(value => value.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(principal));
        var authorization = new Mock<IAuthorizationService>();
        authorization.Setup(value => value.AuthorizeAsync(principal, null, It.IsAny<string>())).ReturnsAsync(AuthorizationResult.Success());
        var configuration = new CameraAgentConfigurationAccessor();
        configuration.SetConfiguration(CreateConfiguration());
        switch (consumer)
        {
            case "detail":
                var detail = await operations.ReadExecutionDetailAsync(id, cancellationToken).ConfigureAwait(false);
                Assert.IsNotNull(detail);
                Assert.HasCount(1, detail.Nodes);
                Assert.HasCount(1, detail.Nodes[0].Attempts);
                Assert.HasCount(1, detail.Nodes[0].Outputs);
                return JsonSerializer.Serialize(detail);
            case "api":
                var app = CameraAgentProcessingGraphOperationsEndpointsTests.CreateApp(operations, Mock.Of<ICameraAgentArtifactService>());
                await using (app.ConfigureAwait(false))
                {
                    var (status, body) = await CameraAgentProcessingGraphOperationsEndpointsTests.InvokeForBodyAsync(
                        app, "GetCameraAgentProcessingExecution", HttpMethods.Get,
                        routeValues: new() { ["executionId"] = id.ToString() }, cancellationToken: cancellationToken).ConfigureAwait(false);
                    Assert.AreEqual(200, status, body);
                    using var json = JsonDocument.Parse(body);
                    Assert.AreEqual(1, json.RootElement.GetProperty("nodes").GetArrayLength());
                    return body;
                }
            case "ui":
                var ui = new CameraAgentProcessingGraphUiService(authentication.Object, authorization.Object, operations,
                    Mock.Of<ICaptureProcessingPipelineFactory>(), configuration, null, TimeProvider.System,
                    NullLogger<CameraAgentProcessingGraphUiService>.Instance);
                var result = await ui.GetExecutionDetailAsync(id, cancellationToken).ConfigureAwait(false);
                Assert.IsTrue(result.IsSuccess, result.Message);
                Assert.AreEqual(corrupt ? CameraAgentRunManifest.CorruptReason : null, result.Value!.ManifestUnavailableReason);
                Assert.HasCount(1, result.Value.Nodes);
                return JsonSerializer.Serialize(result.Value.Nodes);
            case "replay":
                var replay = new CameraAgentReplayUiService(authentication.Object, authorization.Object, operations,
                    Mock.Of<ICameraAgentGallery>(), Options.Create(new CameraAgentHostOptions()), NullLogger<CameraAgentReplayUiService>.Instance);
                var progress = await replay.GetReplayExecutionAsync(id, cancellationToken).ConfigureAwait(false);
                Assert.IsTrue(progress.IsSuccess, progress.Message);
                Assert.AreEqual("Replay", progress.Value!.ExecutionClass);
                Assert.HasCount(1, progress.Value.Nodes);
                return JsonSerializer.Serialize(progress.Value);
            case "export":
                var sink = new ExecutionEvidenceConformanceSink(TimeProvider.System);
                var origin = new Mock<IExecutionEvidenceOriginProvider>();
                origin.Setup(value => value.GetDescriptorAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
                    new ExecutionEvidenceOriginDescriptor(Guid.NewGuid(), Guid.NewGuid(), "1.0.0-journal-test"));
                var state = new ExecutionEvidenceExportState();
                using (var outbox = new SqliteExecutionEvidenceOutbox(TimeProvider.System))
                using (var telemetry = new ExecutionEvidenceExportTelemetry(state, TimeProvider.System))
                using (var exporter = new ExecutionEvidenceExportService(outbox, sink, origin.Object, operations, configuration,
                    state, telemetry, new ExecutionEvidenceExportWakeup(), new StoragePressureState(),
                    Options.Create(new CameraAgentHostOptions { RawIngressRoot = Path.Combine(root, $"export-{Guid.NewGuid():N}") }),
                    TimeProvider.System, NullLogger<ExecutionEvidenceExportService>.Instance))
                {
                    await exporter.RunCycleOnceAsync(cancellationToken).ConfigureAwait(false);
                    await exporter.RunCycleOnceAsync(cancellationToken).ConfigureAwait(false);
                    var execution = sink.AcceptedEnvelopes.Single(envelope => envelope.Kind == ExecutionEvidenceBodyKind.GraphExecution &&
                        envelope.Execution!.ExecutionId == id).Execution!;
                    Assert.HasCount(1, execution.Nodes);
                    Assert.HasCount(1, execution.Nodes[0].Attempts);
                    Assert.HasCount(1, execution.Nodes[0].Outputs);
                    return JsonSerializer.Serialize(execution);
                }
            default: throw new AssertFailedException($"Unknown consumer {consumer}.");
        }
    }
}
