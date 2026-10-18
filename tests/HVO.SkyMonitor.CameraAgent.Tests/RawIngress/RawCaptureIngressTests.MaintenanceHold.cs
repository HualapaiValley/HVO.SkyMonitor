using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    /// <summary>
    /// Raw-ingress reconciliation quarantines committed evidence that no longer matches its record, which a snapshot
    /// restore would bring back. Under the installer's durable pause it finds the mismatch but moves nothing, at the
    /// first initialization and at a re-initialization alike, and quarantines it once capture admission opens (#1314 R10).
    /// </summary>
    [TestMethod]
    public async Task InitializeAsync_WhileCaptureIsPaused_MovesNoEvidenceAndQuarantinesItOnceCaptureRuns()
    {
        using var hold = new HVO.SkyMonitor.CameraAgent.Tests.Capture.MaintenanceHoldRoot();
        var root = hold.Path;
        RawCaptureReceipt accepted;
        using (var original = CreateIngress(root, new RawIngressState(TimeProvider.System)))
        {
            accepted = (await original.AcceptAsync(
                CreateConfiguration(), CreateSubmission(Timestamp(5), [5, 5, 5, 5]), CancellationToken.None).ConfigureAwait(false))!;
        }
        SqliteConnection.ClearAllPools();
        var payload = accepted.StoredFrame.AbsolutePath;
        await File.WriteAllBytesAsync(payload, [6, 6, 6, 6]).ConfigureAwait(false);
        using var admission = await hold.AdmissionAsync(paused: true).ConfigureAwait(false);
        var state = new RawIngressState(TimeProvider.System);
        using var restarted = CreateIngress(root, state, maintenanceGate: hold.Gate);

        await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

        Assert.IsTrue(File.Exists(payload), "A paused first initialization moved committed evidence.");
        Assert.AreEqual(RawIngressAvailability.Degraded, state.Snapshot.Availability);
        restarted.InvalidateEvidence();
        await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(File.Exists(payload), "A paused re-initialization moved committed evidence.");

        // Once capture opens, the deferred quarantine runs, and raw ingress reports the mismatched evidence exactly as an
        // unpaused start would.
        await admission.ResumeAsync("raw-resume", null, "test", null, CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        Assert.IsFalse(File.Exists(payload), "The deferred quarantine never ran.");
        Assert.HasCount(1, Directory.EnumerateFiles(Path.Combine(root, "quarantine"), Path.GetFileName(payload), SearchOption.AllDirectories).ToArray());
        using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
        Assert.AreEqual(1L, await ScalarLongAsync(connection,
            $"SELECT COUNT(*) FROM raw_captures WHERE raw_artifact_id = '{accepted.Manifest.Descriptor.Artifact.ArtifactId:N}' AND state = 'quarantined';").ConfigureAwait(false));
    }

    /// <summary>
    /// A projected-scene node that completes while capture is paused defers its stage removal in memory, and the
    /// installer restarts the candidate before it resumes capture, so the restarted image never sees that deferral. The
    /// node's durable completion releases the stage instead: nothing removes it while capture stays paused, and stage
    /// reconciliation reclaims it once capture runs (#1314 R16).
    /// </summary>
    [TestMethod]
    public async Task ProjectedSceneStageRemovalDeferredAcrossARestart_IsReclaimedOnceCaptureRuns()
    {
        using var hold = new HVO.SkyMonitor.CameraAgent.Tests.Capture.MaintenanceHoldRoot();
        var root = hold.Path;
        var stageKey = new string('e', 64);
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
        var (configuration, submission, accepted) = await CommitSceneCaptureAsync(root, stageKey).ConfigureAwait(false);
        using (var processing = new SqliteCaptureProcessingStore(options))
        {
            await processing.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        var stage = Path.Combine(root, "staging", "projected-scenes", $"{stageKey}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stage)!);
        await File.WriteAllTextAsync(stage, "{}").ConfigureAwait(false);

        // The candidate, started under the installer's pause, commits the projected-scene node and defers its cleanup.
        using (await hold.AdmissionAsync(paused: true).ConfigureAwait(false))
        {
            await MarkProjectedSceneNodeCompletedAsync(root, accepted).ConfigureAwait(false);
            using var staging = new ProjectedSceneStagingStore(options);
            var step = new ProjectedSceneCaptureProcessingStep(
                new CaptureProcessingStepMetadata("projected-scene", "ProjectedScene", 0),
                new ProjectedSceneCaptureProcessingStepOptions(), staging, staging,
                new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor()), hold.Gate);
            await step.OnCommittedAsync(
                new CaptureDescriptorProcessingContext(new CaptureProcessingContext(configuration, submission, accepted)),
                CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(File.Exists(stage), "A paused candidate removed a stage.");
        }

        // The installer restarts the candidate: a new process on the paused journal, with nothing deferred.
        using var admission = await hold.RestartAsync().ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Paused, admission.Snapshot.State);
        using var restartedStaging = new ProjectedSceneStagingStore(options);
        using var restarted = CreateIngress(
            root, new RawIngressState(TimeProvider.System), maintenanceGate: hold.Gate, projectedSceneStaging: restartedStaging);
        await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(File.Exists(stage), "A restart under the pause reconciled the stage away.");

        await admission.ResumeAsync("stage-resume", null, "test", null, CancellationToken.None).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        do
        {
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            if (!File.Exists(stage)) break;
            await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.IsFalse(File.Exists(stage), "The stage whose cleanup the restart lost was never reclaimed.");
    }

    /// <summary>A committed capture whose manifest names <paramref name="stageKey"/> in the stage schema this release writes.</summary>
    private static async Task<(CameraModuleConfig Configuration, CaptureLoopSubmission Submission, RawCaptureReceipt Accepted)>
        CommitSceneCaptureAsync(string root, string stageKey)
    {
        var submission = CreateSubmission(Timestamp(9), [9, 8, 7, 6]);
        var frame = submission.Result.Frame!;
        submission = submission with
        {
            Result = submission.Result with
            {
                Frame = frame with
                {
                    Metadata = frame.Metadata with
                    {
                        Scene = new SceneProvenance(
                            new string('B', 64), "rig-v1", "catalog", "1", new string('C', 64),
                            "EquidistantFisheye", "projection-v1", "astronomy-v1", "sensor-v1",
                            ProjectedSceneStageSchemaVersion: StagedProjectedSceneDocument.CurrentSchemaVersion,
                            ProjectedSceneStageKey: stageKey)
                    }
                }
            }
        };
        // Seeded as the capture loop's producer leaves it, as OwnedStageKeys_AcceptsValidManifestReferenceFromNewerStageSchema does.
        var configuration = CreateConfiguration();
        var ids = RawCaptureDescriptorFactory.CreateStableIds(configuration, submission);
        var descriptor = RawCaptureDescriptorFactory.Create(configuration, submission,
            new RawCaptureIdentity(configuration.AgentId!, 1, ids.CaptureId, ids.ArtifactId),
            PayloadChecksum.ComputeSha256(frame.PixelData.Span), DateTimeOffset.UtcNow);
        var files = new RawIngressFileStore(root, new NullRawIngressFaultInjector());
        var paths = files.GetPaths(descriptor.Timing.ExposureStartedUtc, ids.ArtifactId);
        var retained = new ArtifactManifestV2(ArtifactManifestV2.CurrentSchemaVersion,
            descriptor, paths.PayloadRelativePath, submission.Result.Frame!.Metadata.Scene);
        await files.PublishPayloadAsync(paths, frame.PixelData, CancellationToken.None).ConfigureAwait(false);
        await files.PublishSidecarAsync(paths, CaptureContractJson.Serialize(retained), CancellationToken.None).ConfigureAwait(false);
        RawCaptureReceipt accepted;
        using (var ingress = CreateIngress(root, new RawIngressState(TimeProvider.System)))
        {
            accepted = (await ingress.AcceptAsync(configuration, submission, CancellationToken.None).ConfigureAwait(false))!;
            // The committed record names the stage, so it is owned until its node consumes it.
            Assert.IsTrue((await ingress.GetOwnedStageKeysAsync(CancellationToken.None).ConfigureAwait(false)).Contains(stageKey));
        }
        SqliteConnection.ClearAllPools();
        // Processing reads the committed record, scene included.
        return (configuration, submission, accepted with { Manifest = accepted.Manifest with { Scene = retained.Scene } });
    }

    /// <summary>The projected-scene node's durable completion, as the processing store records it before its post-commit cleanup.</summary>
    private static async Task MarkProjectedSceneNodeCompletedAsync(string root, RawCaptureReceipt capture)
    {
        using var connection = await OpenJournalAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO processing_nodes(capture_id, node_id, required, dependencies_json, recipe_name, output_role,
                output_variant, plan_sha256, status, attempt, completed_unix_ms, outcome)
            VALUES($capture, 'projected-scene', 1, '["$raw"]', $recipe, 'Metadata', 'projected-scene-v1', $plan, 'Completed', 1, 0, 'Produced');
            """;
        command.Parameters.AddWithValue("$capture", capture.Manifest.Descriptor.Capture.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$recipe", BuiltInProcessingRecipes.ProjectedScene);
        command.Parameters.AddWithValue("$plan", new string('a', 64));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
