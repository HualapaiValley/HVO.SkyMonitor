using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class MixedGainRollingWindowEndToEndTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("all")]
    [DataRow("primary")]
    [DataRow("last-companion")]
    public Task RunManifestRefusesActualCompleteAndPartialMetadataRetention(string removed)
        => RunAsync([LowGain, HighGain], [LowGain, HighGain], [MixedGainStep()], async run =>
        {
            var operations = run.Provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var id = (await operations.ReadLiveExecutionIdAsync(run.CaptureId(1), CancellationToken.None).ConfigureAwait(false))!.Value;
            var before = await operations.ReadRunManifestAsync(id, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(before);
            AssertRunManifestAvailable(before);
            var original = before.Nodes.Single(node => node.NodeId == NodeId).Outputs;
            Assert.HasCount(4, original);
            var targets = (removed switch
            {
                "primary" => original.Take(3),
                "last-companion" => original.Skip(3),
                _ => original
            }).Select(output => output.ArtifactId).ToHashSet();
            if (removed == "last-companion")
            {
                // Observe the normal lifecycle primitive midway through a retention pass, after
                // the last companion's delete commits but before its combined image is deleted.
                var target = await run.Store.ReadOutputByArtifactIdAsync(original[3].ArtifactId, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(target);
                var operation = new ProcessingLifecycleOperation($"delete:{Guid.NewGuid():N}", "delete", target.OutputIdentitySha256,
                    target.PayloadRelativePath, target.SidecarRelativePath, $"processing-deletion-tombstones/{Guid.NewGuid():N}",
                    "retention-expired", new FileInfo(Path.Combine(run.Root, target.PayloadRelativePath)).Length +
                    new FileInfo(Path.Combine(run.Root, target.SidecarRelativePath)).Length);
                await run.Store.PlanLifecycleOperationAsync(operation, CancellationToken.None).ConfigureAwait(false);
                await new DerivedProductReconciler(run.Root, run.Store).ResumeOperationAsync(operation, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                var held = await HoldManifestOutputsExceptAsync(run, targets).ConfigureAwait(false);
                Assert.IsGreaterThan(0, await run.Provider.GetRequiredService<CaptureProcessingPersistence>()
                    .ExpireOutputsAsync(run.Root, DateTimeOffset.MaxValue, held, CancellationToken.None).ConfigureAwait(false));
            }
            foreach (var artifact in targets)
                Assert.IsNull(await run.Store.ReadOutputByArtifactIdAsync(artifact, CancellationToken.None).ConfigureAwait(false));
            var after = await operations.ReadRunManifestAsync(id, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(after);
            var remaining = after.Nodes.Single(node => node.NodeId == NodeId).Outputs;
            Assert.HasCount(removed switch { "primary" => 1, "last-companion" => 3, _ => 0 }, remaining);
            foreach (var output in remaining)
                Assert.AreEqual(original.Single(saved => saved.ArtifactId == output.ArtifactId).Ordinal, output.Ordinal,
                    "Retained output ordinals must not be renumbered after deletion.");
            var manifest = CameraAgentRunManifest.Create(after);
            Assert.IsNull(manifest.Payload, "Expired associations must never produce a successful partial manifest.");
            Assert.IsNotNull(manifest.Reason);
        });

    [TestMethod]
    [TestCategory("Integration")]
    public Task RunManifestRefusesDeletedReleasedRollingInputWhileItsOutputSurvives()
        => RunAsync([LowGain, HighGain], [LowGain, HighGain], [MixedGainStep(), MeanStep("mean", NodeId, 2)], async run =>
        {
            var operations = run.Provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var id = (await operations.ReadLiveExecutionIdAsync(run.CaptureId(1), CancellationToken.None).ConfigureAwait(false))!.Value;
            var before = await operations.ReadRunManifestAsync(id, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(before);
            AssertRunManifestAvailable(before);
            var mean = before.Nodes.Single(node => node.NodeId == "mean");
            Assert.HasCount(2, mean.Inputs);
            Assert.HasCount(1, mean.Outputs);
            var source = (await run.ReadNodeAsync(0, NodeId).ConfigureAwait(false)).Outputs
                .Single(output => output.Artifact.Role == FrameArtifactRole.Combined);
            var held = await HoldManifestOutputsExceptAsync(run, new HashSet<Guid> { source.ArtifactId }).ConfigureAwait(false);
            Assert.IsGreaterThan(0, await run.Provider.GetRequiredService<CaptureProcessingPersistence>()
                .ExpireOutputsAsync(run.Root, DateTimeOffset.MaxValue, held, CancellationToken.None).ConfigureAwait(false));
            Assert.IsNull(await run.Store.ReadOutputByArtifactIdAsync(source.ArtifactId, CancellationToken.None).ConfigureAwait(false));
            var after = await operations.ReadRunManifestAsync(id, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(after);
            var retained = after.Nodes.Single(node => node.NodeId == "mean");
            Assert.HasCount(1, retained.Inputs);
            Assert.HasCount(1, retained.Outputs);
            Assert.IsNull(CameraAgentRunManifest.Create(after).Payload, "A released rolling-input pin can be pruned without deleting the consumer's output.");
        });

    private static void AssertRunManifestAvailable(ProcessingGraphExecutionDetail detail)
    {
        var result = CameraAgentRunManifest.Create(detail);
        Assert.IsNotNull(result.Payload, result.Reason + " " + JsonSerializer.Serialize(detail.Nodes.Select(node => new
        {
            node.NodeId,
            node.Status,
            node.ArtifactBindingsRetained,
            ContractCount = node.OutputContracts.Count,
            Contracts = node.OutputContracts.Select(contract => new { contract.Role, contract.Variant }),
            Outputs = node.Outputs.Select(output => new { output.Role, output.Variant }),
            Inputs = node.Inputs.Count,
            Missing = node.Outputs.SelectMany(output => output.SourceArtifactIds).Except(node.Inputs.Select(input => input.ArtifactId)
                .Concat(node.CalibrationInputs.Select(input => input.ArtifactId)))
        })));
        using var document = JsonDocument.Parse(result.Payload!);
        foreach (var node in detail.Nodes)
        {
            var exported = document.RootElement.GetProperty("nodes").EnumerateArray()
                .Single(item => item.GetProperty("nodeId").GetString() == node.NodeId);
            var inputs = exported.GetProperty("inputs").EnumerateArray().ToArray();
            var identities = inputs.Select(input => input.GetProperty("artifactId").GetGuid()).ToHashSet();
            Assert.IsTrue(node.Outputs.SelectMany(output => output.SourceArtifactIds).All(identities.Contains));
            foreach (var master in node.CalibrationInputs)
            {
                var input = inputs.Single(item => item.GetProperty("artifactId").GetGuid() == master.ArtifactId);
                Assert.AreEqual("CalibrationMaster", input.GetProperty("kind").GetString());
                Assert.AreEqual(master.PayloadSha256, input.GetProperty("payloadSha256").GetString());
                Assert.AreEqual(JsonValueKind.Null, input.GetProperty("captureId").ValueKind);
            }
        }
    }

    private static async Task<HashSet<string>> HoldManifestOutputsExceptAsync(Run run, HashSet<Guid> removedArtifacts)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(run.Root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT artifact_id, payload_relative_path, sidecar_relative_path FROM processing_outputs;";
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var held = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (removedArtifacts.Contains(Guid.ParseExact(reader.GetString(0), "N"))) continue;
            held.Add(Path.GetFullPath(Path.Combine(run.Root, reader.GetString(1))));
            held.Add(Path.GetFullPath(Path.Combine(run.Root, reader.GetString(2))));
        }
        return held;
    }
}
