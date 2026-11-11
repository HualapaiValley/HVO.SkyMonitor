using System.Security.Claims;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

/// <summary>
/// The run page's download button over the real store: one read of the execution decides it, the manifest read and
/// its payload are never used for it, and what it reports is what the download then does.
/// </summary>
public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task RunPageReportsRetainedExpiredAndMissingManifestEvidenceFromOneRead()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-availability");
        try
        {
            using var provider = CreateProvider(root);
            var id = await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();

            // Retained: the page offers the download and the download produces the manifest.
            var retained = await ReadRunPageAsync(operations, id).ConfigureAwait(false);
            Assert.IsNull(retained.ManifestUnavailableReason);
            Assert.HasCount(1, retained.Nodes);
            await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);

            var read = (await operations.ReadExecutionDetailWithRunManifestAsync(id, CancellationToken.None).ConfigureAwait(false))!;
            Assert.AreEqual(ProcessingRunManifestEvidenceFailure.None, read.RunManifestFailure);
            var journal = read.Detail.Nodes.Single();
            var evidence = read.RunManifest!.Nodes.Single();
            // The journal is the ordinary detail, and it carries none of the manifest-only evidence.
            Assert.AreEqual(
                JsonSerializer.Serialize(await operations.ReadExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false)),
                JsonSerializer.Serialize(read.Detail));
            Assert.IsNull(read.Detail.FrozenNodeCount);
            Assert.AreEqual(1, read.RunManifest.FrozenNodeCount);
            // The manifest view is built on the rows the journal already read. Only the input bindings are its own.
            Assert.AreSame(read.Detail.Execution, read.RunManifest.Execution);
            Assert.AreSame(journal.Attempts, evidence.Attempts);
            Assert.AreSame(journal.Outputs, evidence.Outputs);
            Assert.AreSame(journal.Dependencies, evidence.Dependencies);
            Assert.AreSame(journal.OutputContracts, evidence.OutputContracts);
            Assert.AreNotSame(journal.Inputs, evidence.Inputs);
            // The retained manifest is known to fit without being encoded.
            Assert.IsNull(CameraAgentRunManifest.UnavailableReason(read.RunManifest,
                static _ => throw new AssertFailedException("The payload was encoded to decide availability.")));

            // Expired: retention deletes the node's output, and with it the evidence of what the node produced.
            var target = (await store.ReadOutputByArtifactIdAsync(journal.Outputs.Single().ArtifactId, CancellationToken.None).ConfigureAwait(false))!;
            var deletion = new ProcessingLifecycleOperation($"delete:{Guid.NewGuid():N}", "delete", target.OutputIdentitySha256,
                target.PayloadRelativePath, target.SidecarRelativePath, $"processing-deletion-tombstones/{Guid.NewGuid():N}", "retention-expired", 0);
            await store.PlanLifecycleOperationAsync(deletion, CancellationToken.None).ConfigureAwait(false);
            await new DerivedProductReconciler(root, store).ResumeOperationAsync(deletion, CancellationToken.None).ConfigureAwait(false);
            var expired = await ReadRunPageAsync(operations, id).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentRunManifest.ArtifactEvidenceUnavailableReason, expired.ManifestUnavailableReason);
            Assert.HasCount(1, expired.Nodes);
            Assert.IsEmpty(expired.Nodes[0].Outputs);
            await RunManifestTestAssertions.ResponseAsync(operations, id, CameraAgentRunManifest.ArtifactEvidenceUnavailableReason).ConfigureAwait(false);

            // Missing: the node's own evidence is gone while the execution remains.
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM processing_execution_nodes WHERE execution_id = $execution;";
                command.Parameters.AddWithValue("$execution", id.ToString("N"));
                Assert.AreEqual(1, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            }
            var missing = await ReadRunPageAsync(operations, id).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentRunManifest.MissingReason, missing.ManifestUnavailableReason);
            Assert.IsEmpty(missing.Nodes);
            await RunManifestTestAssertions.ResponseAsync(operations, id, CameraAgentRunManifest.MissingReason).ConfigureAwait(false);

            // An execution that was never recorded is not found, and nothing is said about a manifest.
            Assert.IsNull(await operations.ReadExecutionDetailWithRunManifestAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task RunPageKeepsTheJournalWhenManifestEvidenceIsOversizedOrUnreadableAndRecovers()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-availability-bounds");
        try
        {
            using var provider = CreateProvider(root);
            var id = await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE n(i) AS (VALUES(1) UNION ALL SELECT i+1 FROM n WHERE i<65)
                INSERT INTO processing_execution_nodes(execution_id,node_id,required,plan_sha256,shared_plan_node_identity_sha256,dependencies_json,inputs_json,outputs_json,status)
                SELECT e.execution_id,'extra-' || n.i,1,e.local_plan_identity_sha256,e.shared_plan_identity_sha256,'[]','[]','[]','Pending'
                FROM processing_executions e CROSS JOIN n WHERE e.execution_id = $execution;
                """;
            command.Parameters.AddWithValue("$execution", id.ToString("N"));
            Assert.AreEqual(65, await command.ExecuteNonQueryAsync().ConfigureAwait(false));

            // The download is refused for its size, and the page says so while still showing every node.
            var oversized = await ReadRunPageAsync(operations, id).ConfigureAwait(false);
            Assert.AreEqual(ProcessingRunManifestBounds.UnavailableReason, oversized.ManifestUnavailableReason);
            Assert.HasCount(66, oversized.Nodes);
            var read = (await operations.ReadExecutionDetailWithRunManifestAsync(id, CancellationToken.None).ConfigureAwait(false))!;
            Assert.AreEqual(ProcessingRunManifestEvidenceFailure.ExceedsBounds, read.RunManifestFailure);
            Assert.IsNull(read.RunManifest);
            await RunManifestTestAssertions.ResponseAsync(operations, id, ProcessingRunManifestBounds.UnavailableReason).ConfigureAwait(false);

            // The refusal does not outlast its cause.
            command.CommandText = "DELETE FROM processing_execution_nodes WHERE execution_id = $execution AND node_id LIKE 'extra-%';";
            Assert.AreEqual(65, await command.ExecuteNonQueryAsync().ConfigureAwait(false));
            Assert.IsNull((await ReadRunPageAsync(operations, id).ConfigureAwait(false)).ManifestUnavailableReason);

            // Evidence that cannot be read at all: only the manifest's bounds check reads the lineage table. The
            // page reports a fixed reason beside the journal it has already read.
            command.CommandText = "ALTER TABLE processing_output_sources RENAME TO processing_output_sources_unreadable;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            var unreadable = await ReadRunPageAsync(operations, id).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentRunManifest.TemporarilyUnavailableReason, unreadable.ManifestUnavailableReason);
            Assert.HasCount(1, unreadable.Nodes);
            Assert.AreEqual(ProcessingRunManifestEvidenceFailure.Unavailable,
                (await operations.ReadExecutionDetailWithRunManifestAsync(id, CancellationToken.None).ConfigureAwait(false))!.RunManifestFailure);
            command.CommandText = "ALTER TABLE processing_output_sources_unreadable RENAME TO processing_output_sources;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);

            // A canceled read is canceled rather than reported, and does not poison the next one.
            using var canceled = new CancellationTokenSource();
            await canceled.CancelAsync().ConfigureAwait(false);
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await operations.ReadExecutionDetailWithRunManifestAsync(id, canceled.Token).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsNull((await ReadRunPageAsync(operations, id).ConfigureAwait(false)).ManifestUnavailableReason);
            await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// Reads the run page through its service over operations that answer the combined read alone. Any other
    /// operations call, the manifest read above all, fails the strict mock.
    /// </summary>
    private static async Task<CameraAgentProcessingExecutionDetailView> ReadRunPageAsync(
        ProcessingGraphOperationsCoordinator operations, Guid id)
    {
        var only = new Mock<IProcessingGraphOperations>(MockBehavior.Strict);
        only.Setup(value => value.ReadExecutionDetailWithRunManifestAsync(id, It.IsAny<CancellationToken>()))
            .Returns((Guid execution, CancellationToken token) => operations.ReadExecutionDetailWithRunManifestAsync(execution, token));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "run-page-reader")]));
        var authentication = new Mock<AuthenticationStateProvider>();
        authentication.Setup(value => value.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(principal));
        var authorization = new Mock<IAuthorizationService>();
        authorization.Setup(value => value.AuthorizeAsync(principal, null, It.IsAny<string>())).ReturnsAsync(AuthorizationResult.Success());
        var configuration = new CameraAgentConfigurationAccessor();
        configuration.SetConfiguration(CreateConfiguration());
        var page = new CameraAgentProcessingGraphUiService(authentication.Object, authorization.Object, only.Object,
            Mock.Of<ICaptureProcessingPipelineFactory>(), configuration, null, TimeProvider.System,
            NullLogger<CameraAgentProcessingGraphUiService>.Instance);

        var result = await page.GetExecutionDetailAsync(id, CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(result.IsSuccess, result.Message);
        only.Verify(value => value.ReadExecutionDetailWithRunManifestAsync(id, It.IsAny<CancellationToken>()), Times.Once);
        only.VerifyNoOtherCalls();
        return result.Value!;
    }
}
