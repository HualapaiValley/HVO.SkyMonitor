using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Video.FFmpeg;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests.Silo;

public sealed partial class SiloStockStoreTests
{
    private static async Task AssertSiloTransientReservationAsync(IServiceProvider services, RawIngressJournalEntry raw,
        ExactReadFault fault, SiloExactLocator locator, string root, CancellationToken token)
    {
        var configuration = await services.GetRequiredService<ICameraAgentConfigurationLoader>().LoadAsync(token).ConfigureAwait(false);
        var lane = services.GetRequiredService<CaptureLanePolicy>().Definitions.Single(static lane => lane.Name == "transient");
        var lanes = services.GetRequiredService<ICaptureLaneStore>();
        var lease = await lanes.ClaimAsync(lane, "silo-test", configuration, token).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        Assert.AreEqual(raw.ArtifactId, lease.Context.RawCapture.Manifest.Descriptor.Artifact.ArtifactId);
        var journal = services.GetRequiredService<SqliteTransientCandidateJournal>();
        await journal.StageCaptureAsync(lease.Context, token).ConfigureAwait(false);
        await lanes.CompleteAsync(lease, token).ConfigureAwait(false);
        var runtime = services.GetRequiredService<SqliteTransientRuntimeStore>();
        await runtime.InitializeAsync(token).ConfigureAwait(false);
        var window = await runtime.LoadWindowAsync(raw.AgentId, raw.CaptureSequence, [0], token).ConfigureAwait(false);
        Assert.HasCount(1, window);
        Assert.AreEqual(raw.PayloadSha256, PayloadChecksum.ComputeSha256(window[0].Artifact.Payload.Span));
        var reservation = new TransientCandidateReservation(Guid.NewGuid(), Guid.NewGuid(), raw.AgentId, [window[0].Source]);

        fault.Set(locator, ReadFault.Unavailable);
        try
        {
            await Assert.ThrowsExactlyAsync<SiloStoreUnavailableException>(async () =>
                await journal.ReserveAsync(reservation, token).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsNull(await journal.ReadAsync(reservation.CandidateId, token).ConfigureAwait(false));
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "journal", "raw-ingress.db"),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(token).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM transient_candidate_conflicts;";
            Assert.AreEqual(0L, await command.ExecuteScalarAsync(token).ConfigureAwait(false));
        }
        finally
        {
            fault.Clear();
        }

        var recovered = await journal.ReserveAsync(reservation, token).ConfigureAwait(false);
        Assert.AreEqual(TransientCandidateReservationDisposition.Created, recovered.Disposition);
        Assert.IsNull(recovered.Entry.QuarantineReason);
        Assert.AreEqual(raw.ArtifactId, recovered.Entry.Sources.Single().Locator.Artifact.ArtifactId);
    }

    private static async Task AssertSiloTimeLapseRetentionAndPublicationAsync(IServiceProvider services, string root, CancellationToken token)
    {
        var config = NightlyProductFixture.Configuration() with
        {
            Pipeline = new([new("ProjectedScene", "scene", DependsOn: ["$raw"])],
                CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent)
        };
        var options = new TimeLapseOptions { RigProfileSha256 = NightlyProductFixture.RigProfileSha256, MaximumDimension = 128 };
        var preset = options.Freeze(config);
        var policy = new LocalAutomationSourceWindowPolicy(LocalAutomationSourceWindowPolicy.CurrentVersion,
            LocalAutomationSourceWindowKind.CompletedCivilHour, LocalAutomationSourceSelection.AllActualSources, TimeSpan.FromMinutes(5));
        var planner = new LocalAutomationWindowPlanner(new FixedObservingDayCalendarProvider(NightlyProductFixture.Calendar));
        var window = planner.ResolveWindows(NightlyProductFixture.ObservingDate, policy).Single(static item => item.StartUtc.Hour == 5);
        var definition = new LocalAutomationDefinition("silo-video-test", "Silo video", true, LocalAutomationTaskKind.TimeLapseGeneration,
            options.Target, LocalAutomationTriggerKind.SourceWindowClosed, 1, window.StartUtc, policy);
        var occurrence = LocalAutomationWindowPlanner.CreateOccurrence(new(definition, 1,
            LocalAutomationContract.ComputeRevisionSha256(definition), null, null), window);

        var start = window.StartUtc.AddMinutes(1);
        var exposure = TimeSpan.FromSeconds(1);
        var setpoint = new CaptureSetpoint(exposure, 1, null, null);
        var pixels = new byte[NightlyProductFixture.Size * NightlyProductFixture.Size * 2];
        pixels[0] = 23;
        var frame = new CameraFrame(start, NightlyProductFixture.Size, NightlyProductFixture.Size, CameraPixelFormat.Mono16,
            pixels, new FrameMetadata(exposure, 1, 10, "silo-video-fixture"));
        var ingress = services.GetRequiredService<RawCaptureIngress>();
        var receipt = await ingress.AcceptAsync(config, new CaptureLoopSubmission(
            new CaptureRequest(start, exposure, CaptureMode.Still, setpoint),
            new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false)
            { AcquisitionTiming = new(start, start + exposure, start + exposure) },
            start, exposure, exposure), token).ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        Assert.IsNull((await ingress.PublishSiloAdmissionsAsync(16, token).ConfigureAwait(false)).Status);
        Assert.IsFalse(File.Exists(receipt.StoredFrame.AbsolutePath));
        Assert.IsFalse(File.Exists(Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json")));

        var videos = services.GetRequiredService<SqliteTimeLapseStore>();
        await videos.EnqueueAsync(occurrence, preset, [], token).ConfigureAwait(false);
        var job = await videos.ClaimAsync(token).ConfigureAwait(false);
        Assert.IsNotNull(job);
        var sourceReader = services.GetRequiredService<TimeLapseSourceReader>();
        var frozen = await sourceReader.FreezeAsync(job, token).ConfigureAwait(false);
        Assert.HasCount(1, frozen.Sources);
        var raw = receipt.Manifest.Descriptor.Artifact;
        var retained = await videos.GetPlanAsync(job.JobId, token).ConfigureAwait(false);
        Assert.IsNotNull(retained);
        Assert.AreEqual(raw.ArtifactId, retained.Sources.Single().Descriptor.Artifact.ArtifactId);
        var restored = await sourceReader.RestoreAsync(retained.Sources.Single(), token).ConfigureAwait(false);
        CollectionAssert.AreEqual(pixels, restored.Payload.ToArray());

        var activeConfig = await services.GetRequiredService<ICameraAgentConfigurationLoader>().LoadAsync(token).ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Running, services.GetRequiredService<CaptureAdmissionCoordinator>().Snapshot.State);
        var retention = services.GetServices<IHostedService>().OfType<RetentionBackgroundService>().Single();
        await retention.ApplyRetentionAsync(activeConfig, token).ConfigureAwait(false);
        Assert.AreEqual(new ProcessingRetentionHold(raw.ArtifactId, SiloArtifactStorage.RawReference(raw.ArtifactId, ".bin"),
            SiloArtifactStorage.RawReference(raw.ArtifactId, ".json")),
            (await videos.GetRetentionHoldsAsync(root, token).ConfigureAwait(false)).Single());
        CollectionAssert.AreEqual(pixels, (await sourceReader.RestoreAsync(retained.Sources.Single(), token).ConfigureAwait(false)).Payload.ToArray());

        await AssertSiloVideoReadbackAsync(videos, job, receipt.Manifest.Descriptor, root, token).ConfigureAwait(false);
    }

    private static async Task AssertSiloVideoReadbackAsync(SqliteTimeLapseStore store, CameraAgentTimeLapseJob job,
        ReconstructionDescriptor descriptor, string root, CancellationToken token)
    {
        // The storage contract starts with encoder evidence and an immutable stream.
        var bytes = "installed-Silo encoded video fixture"u8.ToArray();
        var hash = PayloadChecksum.ComputeSha256(bytes);
        var window = job.Window;
        var id = descriptor.Artifact.ArtifactId;
        var timeline = TimeLapseTimelinePlanner.Create(window.StartUtc, window.EndUtc,
            [new(id, CaptureContractJson.ComputeDescriptorSha256(descriptor), descriptor.Timing.ExposureStartedUtc,
                descriptor.Controls.EffectiveExposure)], job.Preset.Timing);
        var packets = timeline.Intervals.Select(interval => new TimeLapsePacket(interval.StartTick, interval.StartTick,
            interval.DurationTicks, "SHA256:" + hash)).ToArray();
        var evidence = new TimeLapseEncodingEvidence(FFmpegTimeLapseEncoder.ProfileVersion, TimeLapseEncoderProfile.Software,
            new(true, null, "fixture", hash, hash), timeline.IdentitySha256, hash, new Dictionary<int, string>(), hash,
            bytes.LongLength, new(16, 16, timeline.DurationTicks, hash, packets));
        var frames = timeline.Intervals.Where(static interval => interval.SourceOrdinal.HasValue).Select(_ =>
            new CameraAgentTimeLapseFrameLineage(id, hash, [id], [descriptor.Artifact.ChecksumSha256], descriptor.Controls.EffectiveExposure)).ToArray();
        var product = new CameraAgentTimeLapseProduct(Guid.Empty, string.Empty, job.JobId, window.ReportingPeriod.ReportDate,
            window.ReportingPeriod.IdentitySha256, window.IdentitySha256, window.StartUtc, window.EndUtc, false, job.Preset.IdentitySha256,
            timeline, frames, [], new Dictionary<string, int>(), evidence, DateTimeOffset.UtcNow)
        { HasGaps = timeline.Intervals.Any(static interval => !interval.SourceOrdinal.HasValue) };
        var identity = CameraAgentTimeLapseProductIdentity.Compute(product);
        product = product with { OutputIdentitySha256 = identity, ProductId = ProcessingIdentity.CreateArtifactId(identity) };
        var workspace = Directory.CreateDirectory(Path.Combine(root, "encoded-fixture")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(workspace, "video.mp4"), bytes, token).ConfigureAwait(false);
        using var slot = new SemaphoreSlim(0, 1);
        using var lease = File.Create(Path.Combine(workspace, "fixture.lock"));
        var encoded = new EncodedTimeLapse(workspace, evidence, lease, slot);
        await using var cleanup = encoded.ConfigureAwait(false);
        await store.PublishAsync(product, encoded, token).ConfigureAwait(false);
        using var read = await store.OpenVideoAsync(product.ProductId, token).ConfigureAwait(false);
        Assert.IsNotNull(read);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Convert.ToHexStringLower(await SHA256.HashDataAsync(read, token).ConfigureAwait(false)));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, ".time-lapses", "products")));
        await store.CompleteAsync(job.JobId, CameraAgentTimeLapseState.Produced, null, product.ProductId, token).ConfigureAwait(false);
        Assert.IsEmpty(await store.GetRetentionHoldsAsync(root, token).ConfigureAwait(false));
    }

    private static async Task AssertSiloNightlyPublicationAsync(IServiceProvider services, string root, CancellationToken token)
    {
        var kind = NightlyProductKind.StarTrail;
        var occurrence = NightlyProductFixture.Occurrence(kind,
            windowKind: LocalAutomationSourceWindowKind.CompletedCivilHour,
            hourStart: new DateTimeOffset(2026, 10, 2, 4, 0, 0, TimeSpan.Zero));
        var window = occurrence.SourceWindow!;
        var frames = new[] { NightlyProductFixture.Frame(1, window.StartUtc.AddMinutes(1)),
            NightlyProductFixture.Frame(2, window.StartUtc.AddMinutes(2)) };
        var result = await new ProcessingRecipeExecutor().ExecuteAsync(new(BuiltInProcessingRecipes.StarTrail,
            JsonSerializer.SerializeToElement(new StarTrailRecipeOptions(NightlyProductRecipeLimits.MaximumSourceCount)),
            ProcessingInputSelector.RecipeResult(FrameArtifactRole.Preview, NightlyProductFixture.PreviewVariant, NightlyProductFixture.PreviewRecipe),
            frames.Select(static item => item.Artifact).ToArray(), NightlyProductGenerator.StarTrailSegmentVariant,
            AuxiliaryInputs: [NightlyProductPreset.BindOccurrence(occurrence)]), token).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, result.Status, result.ReasonCode);
        var product = result.Products.Single();
        var lineage = product.SourceArtifactIds.Select((id, ordinal) =>
        {
            var candidate = frames.Single(item => item.Artifact.ArtifactId == id).Candidate;
            return new NightlyProductSource(ordinal, NightlyProductSourceKind.PreviewFrame, id,
                candidate.OutputIdentitySha256, candidate.CaptureId, candidate.ExposureStartedUtc);
        }).ToArray();
        var store = services.GetRequiredService<SqliteNightlyProductStore>();
        var detail = await store.PublishAsync(new(kind, NightlyProductScope.Segment, window.ReportingPeriod.ReportDate,
            window.StartUtc, window.EndUtc, 0, BuiltInProcessingRecipes.StarTrail, product, lineage, 90)
        { Occurrence = occurrence }, token).ConfigureAwait(false);

        var id = detail.Summary.ProductId;
        var still = await store.ReadStoredProductAsync(id, token).ConfigureAwait(false);
        CollectionAssert.AreEqual(product.Payload.ToArray(), still.Payload.ToArray());
        var rendition = await store.OpenRenditionAsync(id, token).ConfigureAwait(false);
        Assert.IsNotNull(rendition);
        Assert.AreEqual(SqliteNightlyProductStore.RenditionMediaType, rendition.MediaType);
        Assert.IsTrue(rendition.Content.Span.StartsWith(new byte[] { 0xFF, 0xD8 }));
        var provenance = await store.OpenProvenanceAsync(id, token).ConfigureAwait(false);
        Assert.IsNotNull(provenance);
        Assert.AreEqual(provenance.ChecksumSha256, PayloadChecksum.ComputeSha256(provenance.Content.Span));
        using var document = JsonDocument.Parse(provenance.Content);
        Assert.AreEqual(product.OutputIdentitySha256, document.RootElement.GetProperty("outputIdentitySha256").GetString());
        Assert.AreEqual(lineage.Length, document.RootElement.GetProperty("sources").GetArrayLength());
        Assert.IsFalse(Directory.Exists(Path.Combine(root, SqliteNightlyProductStore.ProductDirectoryName)));
    }

    private static async Task AssertSiloReplayPinsAsync(IServiceProvider services, Guid captureId, string root, CancellationToken token)
    {
        var worker = services.GetRequiredService<ProcessingReplayWorker>();
        await worker.StopAsync(token).ConfigureAwait(false);
        Assert.IsTrue(worker.ExecuteTask?.IsCompletedSuccessfully, "Stopping replay must complete its background task cleanly.");
        var store = services.GetRequiredService<SqliteCaptureProcessingStore>();
        var source = await store.ReadReplaySourceAsync(captureId, null, token).ConfigureAwait(false);
        var rawId = source.RawCapture.Manifest.Descriptor.Artifact.ArtifactId;
        Assert.IsFalse(File.Exists(source.RawCapture.StoredFrame.AbsolutePath));
        Assert.AreEqual(source.RawCapture.Manifest.Descriptor.Artifact.ChecksumSha256, source.PayloadSha256);
        var current = await store.ReadActiveRevisionAsync(token).ConfigureAwait(false);
        Assert.IsNotNull(current);
        var operations = services.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var replay = await operations.SubmitReplayAsync(new(captureId, current.State.RevisionId),
            "installed-silo-replay", "operator", token).ConfigureAwait(false);
        var execution = replay.Execution.ExecutionId;
        var raw = await store.ReadFrozenReplayRawInputsAsync(execution, "ProjectedScene", token).ConfigureAwait(false);
        Assert.AreEqual(rawId, raw.Single().Descriptor.Artifact.ArtifactId);
        var output = (await store.ReadFrozenExecutionOutputsAsync(execution, "ProjectedScene", token).ConfigureAwait(false)).Single();
        Assert.AreEqual(captureId, output.Capture.CaptureId);
        Assert.IsFalse(File.Exists(Path.Combine(root, output.PayloadRelativePath)));
        var silo = services.GetRequiredService<SiloArtifactStorage>();
        Assert.IsTrue(await silo.HasMemberAsync(output.ArtifactId, output.OutputIdentitySha256, token).ConfigureAwait(false));
        Assert.IsTrue(await silo.HasMemberAsync(output.ArtifactId, SiloArtifactStorage.ProductEvidenceRevision, token).ConfigureAwait(false));
    }
}
