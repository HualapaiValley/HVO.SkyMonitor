using System.Text;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    [DataRow("negative")]
    [DataRow("deep")]
    [DataRow("unicode")]
    public async Task OptionalFrozenInputCorruptionPreservesAllJournalConsumersAcrossRestart(string variant)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-input-evidence");
        try
        {
            var originals = new Dictionary<Guid, byte[]>();
            var facts = new Dictionary<(Guid Id, string Consumer), string>();
            using (var provider = CreateProvider(root))
            {
                var liveId = await CompleteManifestPreviewAsync(provider).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                var live = (await operations.ReadExecutionDetailAsync(liveId, CancellationToken.None).ConfigureAwait(false))!;
                var replay = await ExecuteReplayAsync(provider, new(live.Execution.CaptureId,
                    live.Execution.GraphRevisionId, live.Execution.PrimaryArtifactId), "input-evidence").ConfigureAwait(false);
                foreach (var id in new[] { liveId, replay.Execution.ExecutionId })
                {
                    originals[id] = await ReadFrozenExecutionJsonAsync(root, id).ConfigureAwait(false);
                    foreach (var consumer in new[] { "detail", "api", "ui", "replay", "export" })
                        facts[(id, consumer)] = await ReadManifestJournalConsumerAsync(root, operations, id, consumer, false).ConfigureAwait(false);
                    await ReplaceFrozenInputEvidenceAsync(root, id, FrozenInputCorruption(variant)).ConfigureAwait(false);
                    await RunManifestTestAssertions.ResponseAsync(operations, id, CameraAgentRunManifest.CorruptReason).ConfigureAwait(false);
                    foreach (var ((run, consumer), expected) in facts.Where(pair => pair.Key.Id == id))
                        Assert.AreEqual(expected, await ReadManifestJournalConsumerAsync(root, operations, run, consumer, true).ConfigureAwait(false));
                }
            }
            SqliteConnection.ClearAllPools();
            using var restarted = CreateProvider(root);
            await restarted.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var recovered = restarted.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            foreach (var ((id, consumer), expected) in facts)
                Assert.AreEqual(expected, await ReadManifestJournalConsumerAsync(root, recovered, id, consumer, true).ConfigureAwait(false));
            foreach (var (id, original) in originals)
            {
                await RunManifestTestAssertions.ResponseAsync(recovered, id, CameraAgentRunManifest.CorruptReason).ConfigureAwait(false);
                await ReplaceFrozenEvidenceAsync(root, id, original).ConfigureAwait(false);
                await RunManifestTestAssertions.ResponseAsync(recovered, id).ConfigureAwait(false);
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    internal static string FrozenInputCorruption(string variant) => variant switch
    {
        "deep" => new string('[', 70) + "0" + new string(']', 70),
        "unicode" => "{\"\\uD800\":1}",
        _ => "{\"preview\":-1}"
    };

    internal static Task ReplaceFrozenInputEvidenceAsync(string root, Guid id, string counts)
        => ReplaceManifestCounterAsync(root, id, "frozenOutputInputCounts", counts);
}
