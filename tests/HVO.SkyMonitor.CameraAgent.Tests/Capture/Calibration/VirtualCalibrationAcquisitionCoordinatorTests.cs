using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Calibration;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Calibration;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
    Justification = "Test helpers receive only constant SQL from this test class.")]
public sealed class VirtualCalibrationAcquisitionCoordinatorTests
{
    [TestMethod]
    public void CalibrationLibraryStrategy_ValidatesWithoutSyntheticModel()
    {
        var library = new CalibrationProcessingStepOptions { Strategy = "CalibrationLibrary" };
        var invalid = new CalibrationProcessingStepOptions
        {
            Strategy = "CalibrationLibrary",
            SyntheticCalibration = new SyntheticCalibrationModelV1()
        };

        Assert.IsTrue(Validator.TryValidateObject(
            library, new ValidationContext(library), [], validateAllProperties: true));
        Assert.IsFalse(Validator.TryValidateObject(
            invalid, new ValidationContext(invalid), [], validateAllProperties: true));
    }

    [TestMethod]
    public async Task AcquireAsync_PublishesExactLineageNormalizedMastersAndDoesNotActivate()
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await Fixture.CreateAsync(root).ConfigureAwait(false);

            var job = await fixture.Coordinator.AcquireAsync(Request("acquire-complete"), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual(CalibrationAcquisitionStates.Published, job.State);
            var bundles = await fixture.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false);
            Assert.HasCount(1, bundles);
            var bundle = bundles[0].Bundle;
            Assert.AreEqual(CalibrationLibraryBundleSources.VirtualAcquisitionV1, bundle.Source);
            Assert.HasCount(16, bundle.Artifacts);
            var sources = bundle.Artifacts.Where(static artifact => artifact.Role == CalibrationLibraryArtifactRoles.Source).ToArray();
            var masters = bundle.Artifacts.Where(static artifact => artifact.Role == CalibrationLibraryArtifactRoles.Master).ToArray();
            Assert.HasCount(12, sources);
            Assert.HasCount(4, masters);
            var captureSequences = new HashSet<long>();
            foreach (var artifact in bundle.Artifacts)
            {
                var manifest = await ReadManifestAsync(root, artifact.ManifestRelativePath).ConfigureAwait(false);
                Assert.IsGreaterThan((long.MaxValue / 2) - 1, manifest.Descriptor.Capture.CaptureSequence);
                Assert.IsTrue(captureSequences.Add(manifest.Descriptor.Capture.CaptureSequence));
            }
            foreach (var kind in CalibrationReferenceKinds.All)
            {
                var kindSources = sources.Where(source => source.Kind == kind).OrderBy(static source => source.SourceIndex).ToArray();
                Assert.HasCount(3, kindSources);
                CollectionAssert.AreEqual(
                    kindSources.Select(static source => source.ArtifactId).ToArray(),
                    masters.Single(master => master.Kind == kind).OrderedSourceArtifactIds.ToArray());
            }
            foreach (var source in sources)
            {
                var bytes = await File.ReadAllBytesAsync(ResolvePayload(root, source.ManifestRelativePath)).ConfigureAwait(false);
                Assert.IsTrue(MaximumSample(bytes) <= 4095, source.ManifestRelativePath);
            }
            foreach (var master in masters)
            {
                var manifest = await ReadManifestAsync(root, master.ManifestRelativePath).ConfigureAwait(false);
                Assert.AreEqual(16, manifest.Descriptor.Layout.SampleDepthBits);
                Assert.AreEqual(ushort.MaxValue, manifest.Descriptor.Layout.WhiteLevel);
                Assert.AreEqual(FrameStoredCodeTransform.IdentityV1, manifest.Descriptor.Layout.StoredCodeTransform);
                Assert.AreEqual(FrameLevelCodeSpace.StoredContainer, manifest.Descriptor.Layout.LevelCodeSpace);
            }
            var state = await fixture.Store.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(state.ActiveBundle);
            Assert.AreEqual(0L, state.Version);
            Assert.AreEqual(CaptureAdmissionState.Running, fixture.Admission.Snapshot.State);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcquireAsync_PreservesPausedBoundaryAndCanonicalPlanFacts()
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await Fixture.CreateAsync(root).ConfigureAwait(false);
            _ = await fixture.Admission.PauseAsync(
                "pause-calibration", 0, "operator", null, CancellationToken.None).ConfigureAwait(false);

            var job = await fixture.Coordinator.AcquireAsync(Request("acquire-paused"), CancellationToken.None)
                .ConfigureAwait(false);

            Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Admission.Snapshot.State);
            Assert.AreEqual(82d, job.Plan.Gain);
            Assert.AreEqual(1d, job.Plan.Offset);
            Assert.AreEqual(-10d, job.Plan.TemperatureC);
            Assert.AreEqual(CameraPixelFormat.BayerRggb16, job.Plan.InputLayout.PixelFormat);
            Assert.AreEqual(12, job.Plan.InputLayout.SampleDepthBits);
            Assert.AreEqual(FrameStoredCodeTransform.RightAlignedV1, job.Plan.InputLayout.StoredCodeTransform);
            Assert.AreEqual(FrameLevelCodeSpace.NativeSample, job.Plan.InputLayout.LevelCodeSpace);
            Assert.AreEqual(
                VirtualCalibrationSourceGenerator.ComputeModelIdentitySha256(job.Plan.SourceModel),
                job.Plan.SourceModelIdentitySha256);
            Assert.AreEqual(job.PlanIdentitySha256, VirtualCalibrationAcquisitionContractJson.ComputePlanIdentitySha256(job.Plan));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ProcessingInputLoader_LoadsOnlyFourSelectedMasters()
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await Fixture.CreateAsync(root).ConfigureAwait(false);
            var job = await fixture.Coordinator.AcquireAsync(
                Request("processing-inputs"), CancellationToken.None).ConfigureAwait(false);
            var bundle = (await fixture.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false)).Single();
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressSqliteBusyTimeoutSeconds = 1
            });
            var loader = new CalibrationLibraryProcessingInputLoader(
                options, new CameraAgentClearReferenceLoader(options));

            var inputs = await loader.LoadAsync(bundle, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(job.BundleId, bundle.Bundle.BundleId);
            Assert.HasCount(4, inputs.References);
            Assert.HasCount(5, inputs.AuxiliaryInputs);
            Assert.IsTrue(inputs.References.Values.All(static reference => reference.Role == FrameArtifactRole.Combined));
            Assert.IsTrue(inputs.References.Values.All(static reference => reference.SourceArtifactIds?.Count == 3));
            Assert.IsTrue(CalibrationReferenceKinds.All.All(inputs.References.ContainsKey));
            Assert.IsFalse(inputs.References.Values.Any(reference =>
                bundle.Bundle.Artifacts.Any(artifact =>
                    artifact.Role == CalibrationLibraryArtifactRoles.Source && artifact.ArtifactId == reference.ArtifactId)));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CalibrationLibraryStrategy_UsesExplicitlyActivatedBundleAndPreservesRawBytes()
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await Fixture.CreateAsync(root).ConfigureAwait(false);
            var job = await fixture.Coordinator.AcquireAsync(
                Request("process-active-library"), CancellationToken.None).ConfigureAwait(false);
            _ = await fixture.Store.ActivateAsync(
                job.BundleId!, "activate-processing", 0, "operator", null, CancellationToken.None)
                .ConfigureAwait(false);
            var generated = VirtualCalibrationSourceGenerator.Generate(
                VirtualCalibrationSourceKind.Flat,
                1,
                job.Plan.InputLayout,
                job.Plan.ApplicableLightExposure,
                job.Plan.Gain,
                job.Plan.Offset,
                job.Plan.TemperatureC,
                job.Plan.SourceModel);
            var sourceArtifact = (await fixture.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false))
                .Single().Bundle.Artifacts.First(static artifact => artifact.Role == CalibrationLibraryArtifactRoles.Source);
            var template = await ReadManifestAsync(root, sourceArtifact.ManifestRelativePath).ConfigureAwait(false);
            var started = job.Plan.EffectiveFromUtc.AddDays(1);
            var ended = started.Add(job.Plan.ApplicableLightExposure);
            var rawArtifactId = Guid.NewGuid();
            var descriptor = template.Descriptor with
            {
                Capture = template.Descriptor.Capture with
                {
                    CaptureSequence = 42,
                    CaptureId = Guid.NewGuid()
                },
                Timing = new CaptureTimingDescriptor(started, started, ended, ended, ended),
                Controls = new CaptureControlDescriptor(
                    job.Plan.ApplicableLightExposure,
                    job.Plan.ApplicableLightExposure,
                    job.Plan.Gain,
                    job.Plan.Gain,
                    job.Plan.Offset,
                    job.Plan.Offset,
                    job.Plan.TemperatureC,
                    job.Plan.TemperatureC),
                Artifact = template.Descriptor.Artifact with
                {
                    ArtifactId = rawArtifactId,
                    Role = FrameArtifactRole.Raw,
                    SourceId = "VirtualCalibrationLight",
                    Variant = "source",
                    CreatedUtc = ended,
                    SourceArtifactIds = [],
                    ChecksumSha256 = PayloadChecksum.ComputeSha256(generated.PixelData.Span)
                }
            };
            var manifest = new ArtifactManifestV2(
                ArtifactManifestV2.CurrentSchemaVersion, descriptor, "light.bin");
            Assert.IsTrue(FrameReconstructor.TryReconstruct(
                descriptor, generated.PixelData, out var frame).IsValid);
            var submission = new CaptureLoopSubmission(
                new CaptureRequest(started, job.Plan.ApplicableLightExposure, CaptureMode.Still),
                new CaptureResult(
                    frame,
                    new CaptureSetpoint(job.Plan.ApplicableLightExposure, job.Plan.Gain, null, null),
                    TimeSpan.Zero,
                    CaptureMode.Still,
                    false),
                started,
                job.Plan.ApplicableLightExposure,
                TimeSpan.Zero);
            var receipt = new RawCaptureReceipt(
                RawIngressOutcome.Committed,
                manifest,
                new StoredFrameReference("light.bin", Path.Combine(root, "light.bin"), started, FrameArtifactRole.Raw),
                CaptureContractJson.ComputeManifestSha256(manifest));
            var context = new CaptureProcessingContext(Configuration(), submission, receipt);
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressSqliteBusyTimeoutSeconds = 1
            });
            var inputLoader = new CalibrationLibraryProcessingInputLoader(
                options, new CameraAgentClearReferenceLoader(options));
            var step = new CalibrationCaptureProcessingStep(
                new CaptureProcessingStepMetadata("calibration", "calibration", 0),
                new CalibrationProcessingStepOptions { Strategy = "CalibrationLibrary", OutputVariant = "calibrated" },
                new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor()),
                calibrationLibrary: fixture.Store,
                libraryInputLoader: inputLoader);
            var rawChecksum = PayloadChecksum.ComputeSha256(generated.PixelData.Span);

            await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

            var outcome = context.ProcessingOutcomes.Single();
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
            Assert.HasCount(5, outcome.Products.Single().SourceArtifactIds);
            Assert.AreEqual(context.Artifacts!.Raw.ArtifactId, outcome.Products.Single().SourceArtifactIds[0]);
            Assert.AreEqual(rawChecksum, PayloadChecksum.ComputeSha256(generated.PixelData.Span));
            Assert.IsTrue(context.Artifacts?.Artifacts.ContainsKey(FrameArtifactRole.Calibrated) == true);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ResumePendingAsync_RestartsAfterMarkerBoundaryWithoutMutatingSources()
    {
        var root = CreateRoot();
        try
        {
            var fault = new OneShotFaultInjector(CalibrationPublicationFaultPoint.BeforeProfileWrite);
            using (var interrupted = await Fixture.CreateAsync(root, fault).ConfigureAwait(false))
            {
                await Assert.ThrowsExactlyAsync<IOException>(async () =>
                    await interrupted.Coordinator.AcquireAsync(Request("restart-marker"), CancellationToken.None)
                        .ConfigureAwait(false)).ConfigureAwait(false);
                Assert.IsFalse(File.Exists(Path.Combine(
                    root, "calibration", "virtual", OneShotFaultInjector.JobDirectoryName(root), "reference-calibration-profile.json")));
                Assert.IsEmpty(await interrupted.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
            }
            var before = HashFiles(Path.Combine(root, "calibration", "virtual"), "sources");

            using var restarted = await Fixture.CreateAsync(root).ConfigureAwait(false);
            var resumed = await restarted.Coordinator.ResumePendingAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsNotNull(resumed);
            Assert.AreEqual(CalibrationAcquisitionStates.Published, resumed.State);
            Assert.HasCount(1, await restarted.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
            CollectionAssert.AreEquivalent(before.Keys.ToArray(), HashFiles(Path.Combine(root, "calibration", "virtual"), "sources").Keys.ToArray());
            foreach (var pair in before)
            {
                Assert.AreEqual(pair.Value, HashFiles(Path.Combine(root, "calibration", "virtual"), "sources")[pair.Key]);
            }

            var replay = await restarted.Coordinator.AcquireAsync(Request("restart-marker"), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(resumed.Plan.JobId, replay.Plan.JobId);
            Assert.AreEqual(resumed.PlanIdentitySha256, replay.PlanIdentitySha256);
            Assert.HasCount(1, await restarted.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcquireAsync_OneNonterminalJobPerCameraAndExactCommandReplay()
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await Fixture.CreateAsync(
                root, new OneShotFaultInjector(CalibrationPublicationFaultPoint.BeforeManifestWrite)).ConfigureAwait(false);
            var first = Request("single-camera-first");
            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await fixture.Coordinator.AcquireAsync(first, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<CalibrationLibraryStoreConflictException>(async () =>
                await fixture.Coordinator.AcquireAsync(Request("single-camera-second"), CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<CalibrationLibraryStoreConflictException>(async () =>
                await fixture.Coordinator.AcquireAsync(first with { Gain = 83 }, CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);

            using var connection = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(
                connection, "SELECT COUNT(*) FROM calibration_acquisition_jobs;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ResumePendingAsync_ImmutableConflictFailsJobWithoutDeletingEvidence()
    {
        var root = CreateRoot();
        try
        {
            using (var interrupted = await Fixture.CreateAsync(
                       root, new OneShotFaultInjector(CalibrationPublicationFaultPoint.AfterPayloadPublished))
                   .ConfigureAwait(false))
            {
                await Assert.ThrowsExactlyAsync<IOException>(async () =>
                    await interrupted.Coordinator.AcquireAsync(Request("immutable-conflict"), CancellationToken.None)
                        .ConfigureAwait(false)).ConfigureAwait(false);
            }
            var payload = Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).Single();
            var conflicting = await File.ReadAllBytesAsync(payload).ConfigureAwait(false);
            conflicting[0] ^= 0xFF;
            await File.WriteAllBytesAsync(payload, conflicting).ConfigureAwait(false);

            using var restarted = await Fixture.CreateAsync(root).ConfigureAwait(false);
            var exception = await Assert.ThrowsExactlyAsync<CalibrationLibraryAcquisitionException>(async () =>
                await restarted.Coordinator.ResumePendingAsync(CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);

            Assert.AreEqual(CalibrationLibraryReasonCodes.PublicationConflict, exception.ReasonCode);
            Assert.IsTrue(File.Exists(payload));
            CollectionAssert.AreEqual(conflicting, await File.ReadAllBytesAsync(payload).ConfigureAwait(false));
            var failed = await restarted.Store.GetAcquisitionJobAsync(
                (await ReadOnlyJobAsync(root).ConfigureAwait(false)), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CalibrationAcquisitionStates.Failed, failed?.State);
            Assert.AreEqual(CalibrationLibraryReasonCodes.PublicationConflict, failed?.FailureReason);
            Assert.IsEmpty(await restarted.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ResumePendingAsync_MissingEvidenceBehindProfileMarkerFailsClosed()
    {
        var root = CreateRoot();
        try
        {
            using (var interrupted = await Fixture.CreateAsync(
                       root, new OneShotFaultInjector(CalibrationPublicationFaultPoint.AfterProfilePublished))
                   .ConfigureAwait(false))
            {
                await Assert.ThrowsExactlyAsync<IOException>(async () =>
                    await interrupted.Coordinator.AcquireAsync(Request("missing-committed"), CancellationToken.None)
                        .ConfigureAwait(false)).ConfigureAwait(false);
                Assert.IsEmpty(await interrupted.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
            }
            var missing = Directory.EnumerateFiles(root, "dark-1.json", SearchOption.AllDirectories).Single();
            File.Delete(missing);

            using var restarted = await Fixture.CreateAsync(root).ConfigureAwait(false);
            var exception = await Assert.ThrowsExactlyAsync<CalibrationLibraryAcquisitionException>(async () =>
                await restarted.Coordinator.ResumePendingAsync(CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);

            Assert.AreEqual(CalibrationLibraryReasonCodes.PublicationConflict, exception.ReasonCode);
            Assert.IsFalse(File.Exists(missing));
            Assert.IsEmpty(await restarted.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
            using var connection = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual("failed", await ScalarStringAsync(
                connection, "SELECT state FROM calibration_acquisition_jobs;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcquireAsync_InterruptionBeforeAndAfterPlanningLeavesDurableWorkResumable()
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await Fixture.CreateAsync(
                root, new OneShotCancellationFaultInjector(CalibrationPublicationFaultPoint.BeforePayloadWrite))
                .ConfigureAwait(false);
            using (var cancelled = new CancellationTokenSource())
            {
                await cancelled.CancelAsync().ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                    await fixture.Coordinator.AcquireAsync(Request("cancel-before"), cancelled.Token)
                        .ConfigureAwait(false)).ConfigureAwait(false);
            }
            using (var connection = await OpenAsync(root).ConfigureAwait(false))
            {
                Assert.AreEqual(0L, await ScalarLongAsync(
                    connection, "SELECT COUNT(*) FROM calibration_acquisition_jobs;").ConfigureAwait(false));
            }

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await fixture.Coordinator.AcquireAsync(Request("cancel-after"), CancellationToken.None)
                    .ConfigureAwait(false)).ConfigureAwait(false);
            using (var connection = await OpenAsync(root).ConfigureAwait(false))
            {
                Assert.AreEqual("acquiring", await ScalarStringAsync(
                    connection, "SELECT state FROM calibration_acquisition_jobs;").ConfigureAwait(false));
            }
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "calibration", "virtual")));
            Assert.IsEmpty(await fixture.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));

            var resumed = await fixture.Coordinator.ResumePendingAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CalibrationAcquisitionStates.Published, resumed?.State);
            Assert.AreEqual(0, resumed?.AttemptCount);
            Assert.HasCount(1, await fixture.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcquireAsync_ConcurrentExactReplayExecutesOneAttempt()
    {
        var root = CreateRoot();
        try
        {
            var blocker = new BlockingFaultInjector(CalibrationPublicationFaultPoint.BeforePayloadWrite);
            using var fixture = await Fixture.CreateAsync(root, blocker).ConfigureAwait(false);
            var request = Request("concurrent-replay");
            var first = Task.Run(() => fixture.Coordinator.AcquireAsync(request, CancellationToken.None));
            await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var second = Task.Run(() => fixture.Coordinator.AcquireAsync(request, CancellationToken.None));
            await Task.Delay(50).ConfigureAwait(false);

            blocker.Release.Set();
            var results = await Task.WhenAll(first, second).ConfigureAwait(false);

            Assert.IsTrue(results.All(static result => result.State == CalibrationAcquisitionStates.Published));
            Assert.AreEqual(results[0].Plan.JobId, results[1].Plan.JobId);
            Assert.IsTrue(results.All(static result => result.AttemptCount == 0));
            Assert.HasCount(1, await fixture.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RecoveryService_AdoptsCommittedMarkerAfterInterruptedRequest()
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await Fixture.CreateAsync(
                root, new OneShotCancellationFaultInjector(CalibrationPublicationFaultPoint.AfterProfilePublished))
                .ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                fixture.Coordinator.AcquireAsync(Request("recover-committed-marker"), CancellationToken.None))
                .ConfigureAwait(false);
            Assert.IsNotEmpty(Directory.EnumerateFiles(
                root, "reference-calibration-profile.json", SearchOption.AllDirectories));
            Assert.IsEmpty(await fixture.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));

            var service = new VirtualCalibrationAcquisitionRecoveryService(fixture.Coordinator);
            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);

            var job = await fixture.Store.ReadPendingAcquisitionJobAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(job);
            Assert.HasCount(1, await fixture.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task RecoveryService_DrainsPendingJobsForDistinctCameraKeys()
    {
        var root = CreateRoot();
        try
        {
            using (var first = await Fixture.CreateAsync(
                       root, new OneShotFaultInjector(CalibrationPublicationFaultPoint.BeforePayloadWrite))
                   .ConfigureAwait(false))
            {
                await Assert.ThrowsExactlyAsync<IOException>(() =>
                    first.Coordinator.AcquireAsync(Request("recover-camera-one"), CancellationToken.None))
                    .ConfigureAwait(false);
            }
            using (var second = await Fixture.CreateAsync(
                       root,
                       new OneShotFaultInjector(CalibrationPublicationFaultPoint.BeforePayloadWrite),
                       Configuration("agent-calibration-test-two"))
                   .ConfigureAwait(false))
            {
                await Assert.ThrowsExactlyAsync<IOException>(() =>
                    second.Coordinator.AcquireAsync(Request("recover-camera-two"), CancellationToken.None))
                    .ConfigureAwait(false);
            }

            using var recovered = await Fixture.CreateAsync(root).ConfigureAwait(false);
            var service = new VirtualCalibrationAcquisitionRecoveryService(recovered.Coordinator);
            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);

            Assert.IsNull(await recovered.Store.ReadPendingAcquisitionJobAsync(CancellationToken.None)
                .ConfigureAwait(false));
            Assert.HasCount(2, await recovered.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CancelAsync_SerializesWithExecutionBeforeCommitMarker()
    {
        var root = CreateRoot();
        try
        {
            var blocker = new BlockingFaultInjector(CalibrationPublicationFaultPoint.BeforePayloadWrite);
            using var fixture = await Fixture.CreateAsync(root, blocker).ConfigureAwait(false);
            var acquire = Task.Run(() =>
                fixture.Coordinator.AcquireAsync(Request("cancel-running"), CancellationToken.None));
            await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var jobId = await WaitForJobIdAsync(root).ConfigureAwait(false);
            var firstCancel = fixture.Coordinator.CancelAsync(jobId, CancellationToken.None);
            var secondCancel = fixture.Coordinator.CancelAsync(jobId, CancellationToken.None);

            blocker.Release.Set();
            await Assert.ThrowsAsync<OperationCanceledException>(() => acquire).ConfigureAwait(false);
            var cancellations = await Task.WhenAll(firstCancel, secondCancel).ConfigureAwait(false);

            Assert.IsTrue(cancellations.All(static job => job.State == CalibrationAcquisitionStates.Cancelled));
            Assert.IsEmpty(await fixture.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CancelAsync_AfterCommitMarkerFinalizesPublishedBundle()
    {
        var root = CreateRoot();
        try
        {
            var blocker = new BlockingFaultInjector(CalibrationPublicationFaultPoint.AfterProfilePublished);
            using var fixture = await Fixture.CreateAsync(root, blocker).ConfigureAwait(false);
            var acquire = Task.Run(() =>
                fixture.Coordinator.AcquireAsync(Request("cancel-after-marker"), CancellationToken.None));
            await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var jobId = await WaitForJobIdAsync(root).ConfigureAwait(false);
            var cancel = fixture.Coordinator.CancelAsync(jobId, CancellationToken.None);

            blocker.Release.Set();
            var completed = await acquire.ConfigureAwait(false);
            var cancellationResult = await cancel.ConfigureAwait(false);

            Assert.AreEqual(CalibrationAcquisitionStates.Published, completed.State);
            Assert.AreEqual(CalibrationAcquisitionStates.Published, cancellationResult.State);
            Assert.HasCount(1, await fixture.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task CancelAsync_QueuedJobCannotStartAfterCancellationIntent()
    {
        var root = CreateRoot();
        try
        {
            using (var first = await Fixture.CreateAsync(
                       root, new OneShotFaultInjector(CalibrationPublicationFaultPoint.BeforePayloadWrite))
                   .ConfigureAwait(false))
            {
                await Assert.ThrowsExactlyAsync<IOException>(() =>
                    first.Coordinator.AcquireAsync(Request("queued-camera-one"), CancellationToken.None))
                    .ConfigureAwait(false);
            }
            await Task.Delay(10).ConfigureAwait(false);
            using (var second = await Fixture.CreateAsync(
                       root,
                       new OneShotFaultInjector(CalibrationPublicationFaultPoint.BeforePayloadWrite),
                       Configuration("agent-calibration-queued-two"))
                   .ConfigureAwait(false))
            {
                await Assert.ThrowsExactlyAsync<IOException>(() =>
                    second.Coordinator.AcquireAsync(Request("queued-camera-two"), CancellationToken.None))
                    .ConfigureAwait(false);
            }

            var blocker = new BlockingFaultInjector(CalibrationPublicationFaultPoint.BeforePayloadWrite);
            using var recovered = await Fixture.CreateAsync(root, blocker).ConfigureAwait(false);
            var service = new VirtualCalibrationAcquisitionRecoveryService(recovered.Coordinator);
            var recovery = Task.Run(() => service.StartAsync(CancellationToken.None));
            await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var queuedJobId = await JobIdForKeyAsync(root, "queued-camera-two").ConfigureAwait(false);
            var cancel = recovered.Coordinator.CancelAsync(queuedJobId, CancellationToken.None);

            blocker.Release.Set();
            await recovery.ConfigureAwait(false);
            var cancelled = await cancel.ConfigureAwait(false);

            Assert.AreEqual(CalibrationAcquisitionStates.Cancelled, cancelled.State);
            Assert.HasCount(1, await recovered.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
            using var connection = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(connection, """
                SELECT COUNT(*) FROM calibration_acquisition_jobs WHERE state = 'cancelled';
                """).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ResumePendingAsync_RepeatedInterruptionDoesNotConsumeFailureBudget()
    {
        var root = CreateRoot();
        try
        {
            using (var interrupted = await Fixture.CreateAsync(
                       root, new PersistentCancellationFaultInjector(CalibrationPublicationFaultPoint.BeforePayloadWrite))
                   .ConfigureAwait(false))
            {
                var request = Request("repeated-interruption");
                for (var attempt = 0; attempt < SqliteCalibrationLibraryStore.MaximumAcquisitionAttempts + 1; attempt++)
                {
                    await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                        interrupted.Coordinator.AcquireAsync(request, CancellationToken.None)).ConfigureAwait(false);
                }
                var pending = await interrupted.Store.ReadPendingAcquisitionJobAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                Assert.AreEqual(0, pending?.AttemptCount);
            }

            using var restarted = await Fixture.CreateAsync(root).ConfigureAwait(false);
            var resumed = await restarted.Coordinator.ResumePendingAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CalibrationAcquisitionStates.Published, resumed?.State);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task ResumePendingAsync_MissingMarkerAfterDurableCommitPhaseFailsClosed()
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await Fixture.CreateAsync(
                root, new OneShotFaultInjector(CalibrationPublicationFaultPoint.AfterProfilePublished))
                .ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<IOException>(() =>
                fixture.Coordinator.AcquireAsync(Request("missing-marker"), CancellationToken.None))
                .ConfigureAwait(false);
            var marker = Directory.EnumerateFiles(
                root, "reference-calibration-profile.json", SearchOption.AllDirectories).Single();
            File.Delete(marker);
            using (var connection = await OpenAsync(root).ConfigureAwait(false))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE calibration_acquisition_jobs
                    SET state = 'publishing', phase = 'profile-published';
                    """;
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            var exception = await Assert.ThrowsExactlyAsync<CalibrationLibraryAcquisitionException>(() =>
                fixture.Coordinator.ResumePendingAsync(CancellationToken.None)).ConfigureAwait(false);

            Assert.AreEqual(CalibrationLibraryReasonCodes.PublicationConflict, exception.ReasonCode);
            Assert.IsFalse(File.Exists(marker));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task InitializeAsync_RepairsAdditiveUnshippedSchemaV9Index()
    {
        var root = CreateRoot();
        try
        {
            using (var fixture = await Fixture.CreateAsync(root).ConfigureAwait(false))
            using (var connection = await OpenAsync(root).ConfigureAwait(false))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DROP INDEX ux_calibration_acquisition_jobs_camera_nonterminal;";
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1)
                .InitializeAsync(CancellationToken.None).ConfigureAwait(false);

            using var verify = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarLongAsync(
                verify,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ux_calibration_acquisition_jobs_camera_nonterminal';")
                .ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcquireAsync_BoundsTransientPublicationAttemptsAndTerminatesFailure()
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await Fixture.CreateAsync(
                root, new PersistentFaultInjector(CalibrationPublicationFaultPoint.BeforePayloadWrite))
                .ConfigureAwait(false);
            var request = Request("bounded-attempts");
            for (var attempt = 1; attempt < SqliteCalibrationLibraryStore.MaximumAcquisitionAttempts; attempt++)
            {
                await Assert.ThrowsExactlyAsync<IOException>(async () =>
                    await fixture.Coordinator.AcquireAsync(request, CancellationToken.None).ConfigureAwait(false))
                    .ConfigureAwait(false);
            }

            var failure = await Assert.ThrowsExactlyAsync<CalibrationLibraryAcquisitionException>(async () =>
                await fixture.Coordinator.AcquireAsync(request, CancellationToken.None).ConfigureAwait(false))
                .ConfigureAwait(false);

            Assert.AreEqual(CalibrationLibraryReasonCodes.AcquisitionFailure, failure.ReasonCode);
            var terminal = await fixture.Coordinator.AcquireAsync(request, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CalibrationAcquisitionStates.Failed, terminal.State);
            Assert.AreEqual(SqliteCalibrationLibraryStore.MaximumAcquisitionAttempts, terminal.AttemptCount);
            Assert.AreEqual(CalibrationLibraryReasonCodes.AcquisitionFailure, terminal.FailureReason);
            Assert.IsEmpty(await fixture.Store.GetBundlesAsync(10, CancellationToken.None).ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task AcquireAsync_RejectsSourceModelThatCannotExecuteForConfiguredSensorBeforePlanning()
    {
        var root = CreateRoot();
        try
        {
            using var fixture = await Fixture.CreateAsync(root).ConfigureAwait(false);
            var request = Request("invalid-source-capacity") with
            {
                SourceModel = new VirtualCalibrationSourceModelV1
                {
                    Seed = 208,
                    PersistentDefectCount = 64,
                    SourceSpecificDefectCount = 1
                }
            };

            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                fixture.Coordinator.AcquireAsync(request, CancellationToken.None)).ConfigureAwait(false);

            using var connection = await OpenAsync(root).ConfigureAwait(false);
            Assert.AreEqual(0L, await ScalarLongAsync(
                connection, "SELECT COUNT(*) FROM calibration_acquisition_jobs;").ConfigureAwait(false));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static VirtualCalibrationAcquisitionRequestV1 Request(string idempotencyKey)
        => new(
            VirtualCalibrationAcquisitionRequestV1.CurrentSchemaVersion,
            idempotencyKey,
            82,
            1,
            -10,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(3),
            TimeSpan.FromMilliseconds(32),
            new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2027, 7, 26, 0, 0, 0, TimeSpan.Zero),
            new VirtualCalibrationSourceModelV1 { Seed = 208 },
            "operator",
            "unit test");

    private static CameraModuleConfig Configuration(string agentId = "agent-calibration-test")
    {
        var sensor = new SensorProfile(
            "VirtualAsi676McTest", 8, 8, 2, SensorColorMode.Color, CameraPixelFormat.BayerRggb16,
            SensorResponseMode.BayerRaw, 16, SampleByteOrder.LittleEndian, "asi676-test-v1");
        var readout = new SensorReadoutProfile(
            new SensorCrop(0, 0, 8, 8), 1, 1, FrameBinningAlgorithm.IdentityV1,
            CameraPixelFormat.BayerRggb16, 12, 16, FrameSamplePacking.ByteAligned,
            FrameStoredCodeTransform.RightAlignedV1, FrameLevelCodeSpace.NativeSample,
            64, 4095, 16, SampleByteOrder.LittleEndian, ColorFilterArrayPattern.Rggb, 0, 0);
        return new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(
                sensor,
                new OpticsProfile("EquidistantFisheye", 2.5, 170, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(32), TimeSpan.FromMilliseconds(32), 82, 82),
                ProfileVersion: "rig-test-v1",
                Readout: readout),
            AgentId: agentId);
    }

    private static async Task<ArtifactManifestV2> ReadManifestAsync(string root, string relativePath)
    {
        var parsed = CaptureContractJson.ParseManifest(await File.ReadAllBytesAsync(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))).ConfigureAwait(false));
        Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
        return parsed.Document!.Manifest!;
    }

    private static string ResolvePayload(string root, string manifestRelativePath)
        => Path.Combine(root, manifestRelativePath[..^5].Replace('/', Path.DirectorySeparatorChar) + ".bin");

    private static ushort MaximumSample(ReadOnlySpan<byte> bytes)
    {
        ushort maximum = 0;
        for (var index = 0; index < bytes.Length; index += 2)
        {
            maximum = Math.Max(maximum, (ushort)(bytes[index] | bytes[index + 1] << 8));
        }
        return maximum;
    }

    private static Dictionary<string, string> HashFiles(string root, string directorySegment)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => path.Split(Path.DirectorySeparatorChar).Contains(directorySegment, StringComparer.Ordinal))
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.Ordinal);

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-calibration-acquisition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static async Task<SqliteConnection> OpenAsync(string root)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<string> ReadOnlyJobAsync(string root)
    {
        using var connection = await OpenAsync(root).ConfigureAwait(false);
        return await ScalarStringAsync(connection, "SELECT job_id FROM calibration_acquisition_jobs;").ConfigureAwait(false);
    }

    private static async Task<string> WaitForJobIdAsync(string root)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var connection = await OpenAsync(root).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT job_id FROM calibration_acquisition_jobs LIMIT 1;";
            if (await command.ExecuteScalarAsync().ConfigureAwait(false) is string jobId)
            {
                return jobId;
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
        throw new AssertFailedException("The calibration acquisition job was not persisted in time.");
    }

    private static async Task<string> JobIdForKeyAsync(string root, string idempotencyKey)
    {
        using var connection = await OpenAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT job_id FROM calibration_acquisition_jobs WHERE idempotency_key = $key;";
        command.Parameters.AddWithValue("$key", idempotencyKey);
        return await command.ExecuteScalarAsync().ConfigureAwait(false) as string
            ?? throw new AssertFailedException("The calibration acquisition job was not found.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly CaptureControlTelemetry _telemetry;

        private Fixture(
            SqliteCalibrationLibraryStore store,
            CaptureAdmissionCoordinator admission,
            CaptureControlTelemetry telemetry,
            VirtualCalibrationAcquisitionCoordinator coordinator)
        {
            Store = store;
            Admission = admission;
            _telemetry = telemetry;
            Coordinator = coordinator;
        }

        internal SqliteCalibrationLibraryStore Store { get; }
        internal CaptureAdmissionCoordinator Admission { get; }
        internal VirtualCalibrationAcquisitionCoordinator Coordinator { get; }

        internal static async Task<Fixture> CreateAsync(
            string root,
            ICalibrationPublicationFaultInjector? faultInjector = null,
            CameraModuleConfig? configuration = null)
        {
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressSqliteBusyTimeoutSeconds = 1
            });
            var ingress = new InitializingIngress(root);
            var telemetry = new CaptureControlTelemetry();
            var admission = new CaptureAdmissionCoordinator(
                ingress, options, TimeProvider.System, telemetry);
            await admission.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var store = new SqliteCalibrationLibraryStore(ingress, options, TimeProvider.System);
            _ = await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var accessor = new CameraAgentConfigurationAccessor();
            accessor.SetConfiguration(configuration ?? Configuration());
            var publisher = new CalibrationArtifactPublisher(
                options, faultInjector ?? NullCalibrationPublicationFaultInjector.Instance);
            var coordinator = new VirtualCalibrationAcquisitionCoordinator(
                store, publisher, admission, accessor, TimeProvider.System);
            return new Fixture(store, admission, telemetry, coordinator);
        }

        public void Dispose()
        {
            Coordinator.Dispose();
            Store.Dispose();
            Admission.Dispose();
            _telemetry.Dispose();
        }
    }

    private sealed class InitializingIngress(string root) : IRawCaptureIngress
    {
        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
            => await new SqliteRawCaptureJournal(
                Path.Combine(root, "journal", "raw-ingress.db"), 1)
                .InitializeAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RawCaptureReceipt?>(null);
    }

    private sealed class OneShotFaultInjector(CalibrationPublicationFaultPoint target) : ICalibrationPublicationFaultInjector
    {
        private int _injected;

        public void Inject(CalibrationPublicationFaultPoint point, string relativePath)
        {
            if (point == target && Interlocked.Exchange(ref _injected, 1) == 0)
            {
                throw new IOException($"Injected calibration publication fault at {point}: {relativePath}");
            }
        }

        internal static string JobDirectoryName(string root)
            => Directory.Exists(Path.Combine(root, "calibration", "virtual"))
                ? Directory.GetDirectories(Path.Combine(root, "calibration", "virtual")).Select(Path.GetFileName).Single()!
                : string.Empty;
    }

    private sealed class OneShotCancellationFaultInjector(CalibrationPublicationFaultPoint target)
        : ICalibrationPublicationFaultInjector
    {
        private int _injected;

        public void Inject(CalibrationPublicationFaultPoint point, string relativePath)
        {
            if (point == target && Interlocked.Exchange(ref _injected, 1) == 0)
            {
                throw new OperationCanceledException("Injected acquisition interruption.");
            }
        }
    }

    private sealed class PersistentCancellationFaultInjector(CalibrationPublicationFaultPoint target)
        : ICalibrationPublicationFaultInjector
    {
        public void Inject(CalibrationPublicationFaultPoint point, string relativePath)
        {
            if (point == target)
            {
                throw new OperationCanceledException("Injected persistent acquisition interruption.");
            }
        }
    }

    private sealed class BlockingFaultInjector(CalibrationPublicationFaultPoint target)
        : ICalibrationPublicationFaultInjector
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(initialState: false);

        public void Inject(CalibrationPublicationFaultPoint point, string relativePath)
        {
            if (point != target || Entered.Task.IsCompleted)
            {
                return;
            }
            Entered.SetResult();
            Release.Wait(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class PersistentFaultInjector(CalibrationPublicationFaultPoint target)
        : ICalibrationPublicationFaultInjector
    {
        public void Inject(CalibrationPublicationFaultPoint point, string relativePath)
        {
            if (point == target)
            {
                throw new IOException($"Persistent calibration publication fault: {relativePath}");
            }
        }
    }
}
