using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class RollingCombinationWindowLineageTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("SyntheticReferences")]
    [DataRow("CalibrationLibrary")]
    public async Task RunManifestRetainsOrdinaryCalibrationReferencesWithoutActiveSelectionOrPayloads(string strategy)
    {
        var root = CreateRoot();
        ICameraModule? module = null;
        try
        {
            using var provider = CreateProvider(root);
            var configuration = CreateConfiguration(root);
            configuration = configuration with
            {
                Pipeline = configuration.Pipeline with
                {
                    Steps = [configuration.Pipeline.Steps[0] with
                    {
                        Options = JsonSerializer.SerializeToElement(new
                        {
                            strategy,
                            outputVariant = "reference-corrected",
                            syntheticCalibration = strategy == "SyntheticReferences" ? SyntheticCalibration : null
                        })
                    }, .. configuration.Pipeline.Steps.Skip(2)]
                }
            };
            module = await CreateModuleAsync(provider, configuration).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
            var receipt = await ingress.AcceptAsync(configuration,
                await CreateSubmissionAsync(module, 0, CancellationToken.None).ConfigureAwait(false),
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            var bundle = await provider.GetRequiredService<SyntheticCalibrationReferenceStore>()
                .GetOrCreateAsync(receipt.Manifest.Descriptor, SyntheticCalibration, CancellationToken.None).ConfigureAwait(false);
            if (strategy == "CalibrationLibrary")
                await provider.GetRequiredService<SqliteCalibrationLibraryStore>().ActivateAsync(
                    bundle.LibraryBundle.BundleId, "manifest-reference", 0, "test", null, CancellationToken.None).ConfigureAwait(false);
            await DrainOneAsync(provider, configuration).ConfigureAwait(false);
            var executionId = (await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId,
                CancellationToken.None).ConfigureAwait(false))!.Value;
            var detail = await operations.ReadRunManifestAsync(executionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(detail);
            var result = CameraAgentRunManifest.Create(detail);
            Assert.IsNotNull(result.Payload, result.Reason);
            Assert.IsNull(detail.Nodes.Single(node => node.NodeId == "calibration").NormalizationPlanIdentitySha256);
            using var json = JsonDocument.Parse(result.Payload);
            var inputs = json.RootElement.GetProperty("nodes").EnumerateArray()
                .Single(node => node.GetProperty("nodeId").GetString() == "calibration")
                .GetProperty("inputs").EnumerateArray().ToArray();
            Assert.HasCount(5, inputs);
            foreach (var reference in bundle.LibraryBundle.Artifacts)
            {
                var exported = inputs.Single(input => input.GetProperty("artifactId").GetGuid() == reference.ArtifactId);
                Assert.AreEqual("CalibrationMaster", exported.GetProperty("kind").GetString());
                Assert.AreEqual(reference.PayloadSha256, exported.GetProperty("payloadSha256").GetString());
                Assert.AreEqual(reference.Kind, exported.GetProperty("calibrationKind").GetString());
                Assert.AreEqual(JsonValueKind.Null, exported.GetProperty("captureId").ValueKind);
            }
            Assert.IsFalse(json.RootElement.GetRawText().Contains("relativePath", StringComparison.OrdinalIgnoreCase));
            foreach (var reference in bundle.ReferenceManifests.Values)
                File.Delete(Path.Combine(root, reference.RelativeArtifactPath));
            using var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE calibration_library_state SET active_bundle_id = NULL;";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            var retained = await operations.ReadRunManifestAsync(executionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(retained);
            CollectionAssert.AreEqual(result.Payload, CameraAgentRunManifest.Create(retained).Payload,
                "Output-bound immutable metadata must survive payload retirement and selection changes.");
            command.CommandText = "DELETE FROM calibration_library_artifacts WHERE artifact_id = $artifact;";
            command.Parameters.AddWithValue("$artifact", bundle.LibraryBundle.Artifacts[0].ArtifactId.ToString("N"));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            var incomplete = await operations.ReadRunManifestAsync(executionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(incomplete);
            var unavailable = CameraAgentRunManifest.Create(incomplete);
            Assert.IsNull(unavailable.Payload);
            Assert.AreEqual(CameraAgentRunManifest.ArtifactEvidenceUnavailableReason, unavailable.Reason);
        }
        finally
        {
            if (module is not null) await module.DisposeAsync().ConfigureAwait(false);
            Cleanup(root);
        }
    }
}
