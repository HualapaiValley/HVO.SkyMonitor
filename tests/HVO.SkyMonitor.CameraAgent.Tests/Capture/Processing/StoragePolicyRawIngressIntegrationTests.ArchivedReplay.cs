using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class StoragePolicyRawIngressIntegrationTests
{
    private static readonly JsonSerializerOptions ReplayProfileJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [TestMethod]
    public async Task ExplicitArchivedReplayUsesRequestedRevisionWithoutChangingLiveProfile()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-explicit-replay-profile");
        try
        {
            using var provider = Provider(root);
            var file = Profile(root);
            await InitializeAsync(provider, file).ConfigureAwait(false);
            var receipt = (await Ingress(provider).AcceptAsync(file, Submission(1), CancellationToken.None).ConfigureAwait(false))!;
            Assert.IsNotNull(receipt);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, (await ProcessNextAsync(provider, file).ConfigureAwait(false)).Outcome);
            var manifestBefore = await File.ReadAllBytesAsync(Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json")).ConfigureAwait(false);

            const string requestedVariant = "explicit-archived-preview";
            var requestedPipeline = file.Pipeline with
            {
                Steps = [.. file.Pipeline.Steps.Select(step => step.Type == "Preview"
                    ? step with { Options = JsonSerializer.SerializeToElement(new { outputVariant = requestedVariant }) } : step)]
            };
            var operations = Operations(provider);
            var requested = await operations.CreateRevisionAsync("explicit-archived", "1", requestedPipeline,
                "explicit-create", "test", null, CancellationToken.None).ConfigureAwait(false);
            await operations.ValidateRevisionAsync(requested.RevisionId, "explicit-validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var current = await ChangeProfileAsync(provider, file).ConfigureAwait(false);
            var registryBefore = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreNotEqual(receipt.Manifest.CaptureGraphRevisionId, requested.RevisionId);
            Assert.AreNotEqual(registryBefore.ActiveRevisionId, requested.RevisionId);
            Assert.ThrowsExactly<ValidationException>(() => CaptureStoragePolicy.EnsureMatchesProfile(requestedPipeline, current.Pipeline));
            var runtime = provider.GetRequiredService<CaptureScheduleRuntimeCoordinator>();
            var liveBefore = JsonSerializer.Serialize(runtime.Snapshot!.Configuration);
            var scheduleStore = provider.GetRequiredService<SqliteCaptureScheduleStore>();
            var scheduleBefore = await scheduleStore.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);

            var replay = await operations.SubmitReplayAsync(new(receipt.Manifest.Descriptor.Capture.CaptureId,
                requested.RevisionId, receipt.Manifest.Descriptor.Artifact.ArtifactId, Reason: "explicit archived revision"),
                "explicit-replay", "test", CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(requested.RevisionId, replay.Execution.GraphRevisionId);
            using (var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT configuration_json FROM processing_executions WHERE execution_id = $execution;";
                command.Parameters.AddWithValue("$execution", replay.Execution.ExecutionId.ToString("N"));
                var bytes = (byte[])(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
                var frozen = JsonSerializer.Deserialize<CameraModuleConfig>(bytes, ReplayProfileJsonOptions)!;
                Assert.AreEqual(CaptureContractJson.ComputeCanonicalJsonSha256(requestedPipeline),
                    CaptureContractJson.ComputeCanonicalJsonSha256(frozen.Pipeline));
                Assert.AreEqual(StorageOptions(file).StorageRoot, StorageOptions(frozen).StorageRoot);
                Assert.AreNotEqual(StorageOptions(current).StorageRoot, StorageOptions(frozen).StorageRoot);
            }

            var result = await ProcessingGraphOperationsTests.RunReplayToCompletionAsync(provider, replay.Execution.ExecutionId).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, result.Execution.Status, result.Execution.FailureReason);
            Assert.AreEqual(requested.RevisionId, result.Execution.GraphRevisionId);
            Assert.AreEqual(requested.DefinitionIdentitySha256, result.Execution.GraphDefinitionIdentitySha256);
            var preview = result.Nodes.Single(node => node.NodeId == "preview");
            Assert.AreEqual("Completed", preview.Status);
            Assert.AreEqual(requestedVariant, Assert.ContainsSingle(preview.Outputs).Variant);
            Assert.IsTrue(result.Nodes.SelectMany(node => node.Outputs).All(output => !output.Published));
            Assert.AreEqual(liveBefore, JsonSerializer.Serialize(runtime.Snapshot!.Configuration));
            var registryAfter = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(JsonSerializer.Serialize(registryBefore), JsonSerializer.Serialize(registryAfter));
            var scheduleAfter = await scheduleStore.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(scheduleBefore.Version, scheduleAfter.Version);
            Assert.AreEqual(scheduleBefore.ActiveRevision.RevisionId, scheduleAfter.ActiveRevision.RevisionId);
            Assert.AreEqual(scheduleBefore.ActiveRevision.ProfileSha256, scheduleAfter.ActiveRevision.ProfileSha256);
            CollectionAssert.AreEqual(manifestBefore, await File.ReadAllBytesAsync(Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json")).ConfigureAwait(false));
            AssertRawRetained(receipt);
            AssertNoStoragePublication(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
