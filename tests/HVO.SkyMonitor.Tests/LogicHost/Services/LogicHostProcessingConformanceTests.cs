using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using HVO.SkyMonitor.AgentCore;
using System.Text.Json;
using System.Collections.Immutable;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
public sealed class LogicHostProcessingConformanceTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void LogicHostGraphAdapterCompilesSharedPlanWithoutEdgeOrInfrastructureState()
    {
        Assert.IsTrue(BuiltInProcessingRecipes.TryGetDefinition(
            BuiltInProcessingRecipes.EncodedPreview,
            out var previewRecipe));
        var effectiveOptions = BuiltInProcessingRecipes.NormalizeOptions(
            BuiltInProcessingRecipes.EncodedPreview,
            JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")));
        var definition = new ProcessingGraphDefinition(
            ProcessingGraphSchemaVersions.Current,
            "central-reconstruction",
            "1",
            [new(
                "$archive",
                [new(FrameArtifactRole.Raw, "archive", ProcessingProductKind.PixelData)])],
            [new ProcessingGraphNodeDefinition(
                "preview",
                "encoded-preview",
                previewRecipe!.ImplementationVersion,
                previewRecipe.OperationKind,
                true,
                ProcessingGraphNodeFailurePolicy.Required,
                0,
                effectiveOptions,
                [new("$archive")],
                [new(
                    [FrameArtifactRole.Raw],
                    [ProcessingProductKind.PixelData],
                    [],
                    [],
                    [],
                    true)],
                [new(
                    FrameArtifactRole.Preview,
                    "central-preview",
                    ProcessingProductKind.PixelData,
                    previewRecipe)],
                null,
                ["cpu"],
                [ProcessingGraphHosts.LogicHost])]);

        var result = LogicHostProcessingGraphAdapter.Compile(definition, ["cpu"]);

        Assert.IsTrue(result.IsValid, string.Join(Environment.NewLine, result.Diagnostics));
        var plan = result.Plan!;
        var bindings = LogicHostProcessingGraphAdapter.Bind(plan);
        var hostBinding = Assert.ContainsSingle(bindings);
        var node = Assert.ContainsSingle(plan.Nodes).Definition;
        Assert.AreEqual("preview", node.Id);
        Assert.AreEqual(previewRecipe, node.Outputs[0].Recipe);
        Assert.AreEqual(previewRecipe.ImplementationVersion, node.StepVersion);
        Assert.AreEqual(node.Id, hostBinding.NodeId);
        Assert.AreEqual(node.StepAlias, hostBinding.StepAlias);
        Assert.AreEqual(node.StepVersion, hostBinding.StepVersion);
        Assert.AreEqual("input", Assert.ContainsSingle(hostBinding.Inputs).BindingName);
        Assert.AreEqual(ProcessingGraphInputBindingKind.PrimaryArtifact, hostBinding.Inputs[0].BindingKind);
        Assert.AreEqual(node.Outputs[0], Assert.ContainsSingle(hostBinding.Outputs));
        Assert.AreEqual(64, plan.PlanIdentitySha256.Length);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task EdgeAndReconstructionAdaptersPreserveReportedObservationTimingAndDetectorIdentity()
    {
        var descriptor = ProcessingConformanceFixture.CreateDescriptor();
        var frameTimestamp = descriptor.Timing.ExposureStartedUtc.AddSeconds(5);
        var frame = new CameraFrame(
            frameTimestamp,
            descriptor.Layout.Width,
            descriptor.Layout.Height,
            descriptor.Layout.PixelFormat,
            ProcessingConformanceFixture.Payload,
            new FrameMetadata(
                descriptor.Controls.EffectiveExposure,
                descriptor.Controls.EffectiveGain,
                0,
                Extra: new Dictionary<string, string>
                {
                    ["blackLevelAdu"] = "0",
                    ["whiteLevelAdu"] = ushort.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }));
        var frameArtifact = new FrameArtifact(
            descriptor.Artifact.ArtifactId,
            descriptor.Artifact.Role,
            frame,
            descriptor.Artifact.SourceArtifactIds,
            ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256);
        var timing = new CaptureAcquisitionTiming(
            descriptor.Timing.ExposureStartedUtc,
            descriptor.Timing.ExposureEndedUtc,
            descriptor.Timing.ReadoutCompletedUtc);
        var edge = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            ProcessingConformanceFixture.CameraConfig,
            frameArtifact,
            descriptor.Artifact.Variant,
            timing,
            descriptor);
        var recorder = new RecordingExecutor();
        var centralAdapter = new LogicHostRecipeExecutionAdapter(recorder);

        _ = await centralAdapter.ExecuteAsync(
            descriptor,
            ProcessingConformanceFixture.Payload,
            BuiltInProcessingRecipes.EncodedPreview,
            JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")),
            ProcessingInputSelector.Raw(descriptor.Artifact.Variant),
            "timing-conformance").ConfigureAwait(false);

        var central = recorder.Request!.Inputs.Single();
        Assert.AreNotEqual(frameTimestamp, edge.ObservationStartedUtc);
        Assert.AreEqual(descriptor.Timing.ExposureStartedUtc, edge.ObservationStartedUtc);
        Assert.AreEqual(descriptor.Timing.ExposureEndedUtc, edge.ObservationEndedUtc);
        Assert.AreEqual(edge.ObservationStartedUtc, central.ObservationStartedUtc);
        Assert.AreEqual(edge.ObservationEndedUtc, central.ObservationEndedUtc);
        Assert.AreEqual(descriptor.Capture.CaptureSequence, edge.CaptureSequence);
        Assert.AreEqual(edge.CaptureSequence, central.CaptureSequence);
        var source = new TransientSourceEvidenceReferenceV1(
            TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
            Guid.Parse("93000000-0000-0000-0000-000000000099"),
            new TransientWholeArtifactLocatorV1(
                TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                TransientSourceLocatorKind.WholeArtifact,
                new TransientArtifactReferenceV1(
                    edge.ArtifactId,
                    edge.Role,
                    edge.Variant,
                    edge.RecipeIdentitySha256,
                    PayloadChecksum.ComputeSha256(edge.Payload.Span))),
            timing.ExposureStartedUtc,
            timing.ExposureEndedUtc,
            TransientTimingQuality.Reported,
            new TransientTimingProvenanceV1("camera-module", "conformance-v1"));
        var levels = new TransientLinearLevelsV1(0, ushort.MaxValue, ushort.MaxValue);
        var edgeInput = TransientDetectorInputFactory.Create(edge, source, levels);
        var centralInput = TransientDetectorInputFactory.Create(central, source, levels);
        Assert.IsTrue(edgeInput.Validation.IsValid, edgeInput.Validation.ReasonCode);
        Assert.IsTrue(centralInput.Validation.IsValid, centralInput.Validation.ReasonCode);
        Assert.AreEqual(
            edgeInput.Input!.Descriptor.InputIdentitySha256,
            centralInput.Input!.Descriptor.InputIdentitySha256);
        CollectionAssert.AreEqual(
            edgeInput.Input.SaturationMask.Bits.ToArray(),
            centralInput.Input.SaturationMask.Bits.ToArray());

        var acceleratedDescriptor = descriptor with
        {
            Timing = descriptor.Timing with
            {
                ExposureEndedUtc = descriptor.Timing.ExposureStartedUtc,
                ReadoutCompletedUtc = descriptor.Timing.ExposureStartedUtc
            }
        };
        var acceleratedTiming = new CaptureAcquisitionTiming(
            acceleratedDescriptor.Timing.ExposureStartedUtc,
            acceleratedDescriptor.Timing.ExposureEndedUtc,
            acceleratedDescriptor.Timing.ReadoutCompletedUtc);
        var acceleratedEdge = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            ProcessingConformanceFixture.CameraConfig,
            frameArtifact,
            acceleratedDescriptor.Artifact.Variant,
            acceleratedTiming,
            acceleratedDescriptor);
        var acceleratedRecorder = new RecordingExecutor();
        _ = await new LogicHostRecipeExecutionAdapter(acceleratedRecorder).ExecuteAsync(
            acceleratedDescriptor,
            ProcessingConformanceFixture.Payload,
            BuiltInProcessingRecipes.EncodedPreview,
            JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")),
            ProcessingInputSelector.Raw(acceleratedDescriptor.Artifact.Variant),
            "accelerated-timing-conformance").ConfigureAwait(false);
        var acceleratedCentral = acceleratedRecorder.Request!.Inputs.Single();
        Assert.AreEqual(
            acceleratedDescriptor.Timing.ExposureStartedUtc.Add(acceleratedDescriptor.Controls.EffectiveExposure),
            acceleratedEdge.ObservationEndedUtc);
        Assert.AreEqual(acceleratedEdge.ObservationStartedUtc, acceleratedCentral.ObservationStartedUtc);
        Assert.AreEqual(acceleratedEdge.ObservationEndedUtc, acceleratedCentral.ObservationEndedUtc);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task EdgeAndCentralAdaptersProduceEquivalentCenteredTransientBackground()
    {
        var baseline = ProcessingConformanceFixture.CreateDescriptor();
        var edgeWindow = new Dictionary<TransientTemporalPosition, TransientTemporalSource>();
        var centralWindow = new Dictionary<TransientTemporalPosition, TransientTemporalSource>();
        foreach (var position in new[]
        {
            TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1, TransientTemporalPosition.N,
            TransientTemporalPosition.NPlus1, TransientTemporalPosition.NPlus2
        })
        {
            var sequence = 100 + (int)position;
            var offset = TimeSpan.FromSeconds((int)position * 25);
            var payload = new byte[]
            {
                (byte)(10 + sequence), 0,
                (byte)(20 + sequence + (position == TransientTemporalPosition.N ? 50 : 0)), 0,
                (byte)(30 + sequence + (position == TransientTemporalPosition.N ? 50 : 0)), 0,
                (byte)(40 + sequence), 0
            };
            var started = baseline.Timing.ExposureStartedUtc.Add(offset);
            var ended = started.Add(baseline.Controls.EffectiveExposure);
            var descriptor = baseline with
            {
                Capture = baseline.Capture with
                {
                    CaptureId = new Guid($"96000000-0000-0000-0000-{sequence:D12}"),
                    CaptureSequence = sequence
                },
                Timing = baseline.Timing with
                {
                    RequestedStartUtc = baseline.Timing.RequestedStartUtc.Add(offset),
                    ExposureStartedUtc = started,
                    ExposureEndedUtc = ended,
                    ReadoutCompletedUtc = baseline.Timing.ReadoutCompletedUtc.Add(offset),
                    DurableIngressUtc = baseline.Timing.DurableIngressUtc.Add(offset),
                    SetpointAppliedUtc = baseline.Timing.SetpointAppliedUtc?.Add(offset)
                },
                Artifact = baseline.Artifact with
                {
                    ArtifactId = new Guid($"97000000-0000-0000-0000-{sequence:D12}"),
                    CreatedUtc = baseline.Artifact.CreatedUtc.Add(offset),
                    ChecksumSha256 = PayloadChecksum.ComputeSha256(payload)
                }
            };
            var frame = new CameraFrame(
                started,
                descriptor.Layout.Width,
                descriptor.Layout.Height,
                descriptor.Layout.PixelFormat,
                payload,
                new FrameMetadata(
                    descriptor.Controls.EffectiveExposure,
                    descriptor.Controls.EffectiveGain,
                    descriptor.Controls.EffectiveOffset ?? 0,
                    Extra: new Dictionary<string, string>
                    {
                        ["blackLevelAdu"] = "0",
                        ["whiteLevelAdu"] = ushort.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    }));
            var frameArtifact = new FrameArtifact(
                descriptor.Artifact.ArtifactId,
                descriptor.Artifact.Role,
                frame,
                descriptor.Artifact.SourceArtifactIds,
                ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256);
            var edgeArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(
                ProcessingConformanceFixture.CameraConfig,
                frameArtifact,
                descriptor.Artifact.Variant,
                new CaptureAcquisitionTiming(started, ended, descriptor.Timing.ReadoutCompletedUtc),
                descriptor);
            var recorder = new RecordingExecutor();
            var adapterOutcome = await new LogicHostRecipeExecutionAdapter(recorder).ExecuteAsync(
                descriptor,
                payload,
                BuiltInProcessingRecipes.EncodedPreview,
                JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")),
                ProcessingInputSelector.Raw(descriptor.Artifact.Variant),
                "transient-conformance").ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Skipped, adapterOutcome.Status,
                $"{adapterOutcome.ReasonCode}:{adapterOutcome.Field}");
            var centralArtifact = recorder.Request!.Inputs.Single();
            var evidence = new TransientSourceEvidenceReferenceV1(
                TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
                new Guid($"98000000-0000-0000-0000-{sequence:D12}"),
                new TransientWholeArtifactLocatorV1(
                    TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                    TransientSourceLocatorKind.WholeArtifact,
                    new TransientArtifactReferenceV1(
                        descriptor.Artifact.ArtifactId,
                        descriptor.Artifact.Role,
                        descriptor.Artifact.Variant,
                        edgeArtifact.RecipeIdentitySha256,
                        PayloadChecksum.ComputeSha256(payload))),
                started,
                ended,
                TransientTimingQuality.Reported,
                new TransientTimingProvenanceV1("conformance", "v1"));
            var levels = new TransientLinearLevelsV1(0, ushort.MaxValue, ushort.MaxValue);
            var edgeInput = TransientDetectorInputFactory.Create(edgeArtifact, evidence, levels);
            var centralInput = TransientDetectorInputFactory.Create(centralArtifact, evidence, levels);
            Assert.IsTrue(edgeInput.Validation.IsValid, edgeInput.Validation.ReasonCode);
            Assert.IsTrue(centralInput.Validation.IsValid, centralInput.Validation.ReasonCode);
            var masks = CreateTransientMasks(descriptor.Layout.Width, descriptor.Layout.Height);
            var sensitivity = new TransientSensitivityV1("conformance-response-v1", 1, 1);
            edgeWindow[position] = new TransientTemporalSource(position, sequence, edgeInput.Input!, sensitivity, masks);
            centralWindow[position] = new TransientTemporalSource(position, sequence, centralInput.Input!, sensitivity, masks);
        }

        var contextPositions = new[]
        {
            TransientTemporalPosition.NMinus2, TransientTemporalPosition.NMinus1,
            TransientTemporalPosition.NPlus1, TransientTemporalPosition.NPlus2
        };
        var edgeOutcome = TransientTemporalBackgroundFactory.Create(new TransientTemporalBackgroundRequest(
            TransientTemporalBackgroundKind.CenteredFinal,
            edgeWindow[TransientTemporalPosition.N],
            contextPositions.Select(position => edgeWindow[position]).ToArray(),
            [],
            TimeSpan.FromSeconds(30)));
        var centralOutcome = TransientTemporalBackgroundFactory.Create(new TransientTemporalBackgroundRequest(
            TransientTemporalBackgroundKind.CenteredFinal,
            centralWindow[TransientTemporalPosition.N],
            contextPositions.Select(position => centralWindow[position]).ToArray(),
            [],
            TimeSpan.FromSeconds(30)));

        Assert.AreEqual(TransientTemporalBackgroundStatus.Produced, edgeOutcome.Status, edgeOutcome.ReasonCode);
        Assert.AreEqual(TransientTemporalBackgroundStatus.Produced, centralOutcome.Status, centralOutcome.ReasonCode);
        CollectionAssert.AreEqual(edgeOutcome.Product!.Pixels.ToArray(), centralOutcome.Product!.Pixels.ToArray());
        CollectionAssert.AreEqual(
            edgeOutcome.Product.EffectiveMask.Bits.ToArray(),
            centralOutcome.Product.EffectiveMask.Bits.ToArray());
        CollectionAssert.AreEqual(
            JsonSerializer.SerializeToUtf8Bytes(edgeOutcome.Product.Descriptor),
            JsonSerializer.SerializeToUtf8Bytes(centralOutcome.Product.Descriptor));

        var options = new TransientCandidateExtractionOptionsV1(
            20,
            2,
            40,
            4,
            4,
            16,
            1_000,
            1,
            0.9);
        var identitySlots = Enumerable.Range(1, options.MaximumCandidates).Select(index =>
            new TransientCandidateIdentitySlot(
                Guid.Parse($"99000000-0000-0000-0000-{index:D12}"),
                Guid.Parse($"9a000000-0000-0000-0000-{index:D12}"))).ToArray();
        var edgeByEvidence = edgeWindow.Values.ToDictionary(static source => source.Input.Descriptor.Source.EvidenceId);
        var centralByEvidence = centralWindow.Values.ToDictionary(static source => source.Input.Descriptor.Source.EvidenceId);
        var createdUtc = edgeOutcome.Product.Descriptor.Sources.Max(static source => source.ObservationEndedUtc).AddSeconds(1);
        var edgeExtraction = TransientCandidateExtractionFactory.Create(new TransientCandidateExtractionRequest(
            "conformance-agent",
            createdUtc,
            edgeWindow[TransientTemporalPosition.N],
            edgeOutcome.Product,
            edgeOutcome.Product.Descriptor.Sources.Select(source =>
                edgeByEvidence[source.EvidenceId]).ToArray(),
            identitySlots,
            options,
            CenteredContextConverged: true));
        var centralExtraction = TransientCandidateExtractionFactory.Create(new TransientCandidateExtractionRequest(
            "conformance-agent",
            createdUtc,
            centralWindow[TransientTemporalPosition.N],
            centralOutcome.Product,
            centralOutcome.Product.Descriptor.Sources.Select(source =>
                centralByEvidence[source.EvidenceId]).ToArray(),
            identitySlots,
            options,
            CenteredContextConverged: true));

        Assert.AreEqual(TransientCandidateExtractionStatus.Produced, edgeExtraction.Status, edgeExtraction.ReasonCode);
        Assert.AreEqual(TransientCandidateExtractionStatus.Produced, centralExtraction.Status, centralExtraction.ReasonCode);
        CollectionAssert.AreEqual(
            TransientCandidateExtractionJson.Serialize(edgeExtraction.Descriptor!),
            TransientCandidateExtractionJson.Serialize(centralExtraction.Descriptor!));
        CollectionAssert.AreEqual(
            TransientContractJson.Serialize(edgeExtraction.Candidates.Single()),
            TransientContractJson.Serialize(centralExtraction.Candidates.Single()));

        var observationId = Guid.Parse("9b000000-0000-0000-0000-000000000001");
        var edgeObservation = TransientObservationFactory.CreateAssessmentObservation(new TransientObservationPromotionRequest(
            edgeExtraction.Candidates.Single().CandidateId,
            observationId,
            0,
            edgeExtraction.Descriptor!));
        var centralObservation = TransientObservationFactory.CreateAssessmentObservation(new TransientObservationPromotionRequest(
            centralExtraction.Candidates.Single().CandidateId,
            observationId,
            0,
            centralExtraction.Descriptor!));
        var assessmentOptions = new TransientDeterministicAssessmentOptionsV1(
            5, 3, 4, 3, 1.8, 0.5, 5, 1_000, 3, 3, 3, 100, 20, 2);
        var assessmentRequest = new TransientAssessmentExecutionRequest(
            identitySlots[0].EventId,
            Guid.Parse("9c000000-0000-0000-0000-000000000001"),
            createdUtc.AddSeconds(1),
            TransientAssessmentAuthority.Provisional,
            [edgeObservation],
            assessmentOptions,
            []);
        var edgeAssessment = TransientAssessmentFactory.Create(assessmentRequest);
        var centralAssessment = TransientAssessmentFactory.Create(assessmentRequest with
        {
            Observations = [centralObservation]
        });
        Assert.AreEqual(TransientAssessmentExecutionStatus.Produced, edgeAssessment.Status, edgeAssessment.ReasonCode);
        Assert.AreEqual(TransientAssessmentExecutionStatus.Produced, centralAssessment.Status, centralAssessment.ReasonCode);
        CollectionAssert.AreEqual(
            TransientAssessmentJson.Serialize(edgeAssessment.Descriptor!),
            TransientAssessmentJson.Serialize(centralAssessment.Descriptor!));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task LogicHostAdapterProducesCanonicalPreviewFixture()
    {
        var descriptor = ProcessingConformanceFixture.CreateDescriptor();
        var adapter = new LogicHostRecipeExecutionAdapter(new ProcessingRecipeExecutor());
        var request = ProcessingConformanceFixture.CreateRequest(
            ProcessingConformanceFixture.CreateProcessingArtifact());

        var outcome = await adapter.ExecuteAsync(
            descriptor,
            ProcessingConformanceFixture.Payload,
            request.RecipeName,
            request.Options,
            request.Input,
            request.OutputVariant).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        ProcessingConformanceFixture.AssertProduct(outcome.Products.Single());

        var jpeg = await adapter.ExecuteAsync(
            descriptor,
            ProcessingConformanceFixture.Payload,
            BuiltInProcessingRecipes.EncodedPreview,
            System.Text.Json.JsonSerializer.SerializeToElement(new EncodedPreviewOptions()),
            ProcessingInputSelector.Raw("source"),
            "jpeg").ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, jpeg.Status);
        Assert.AreEqual(HVO.SkyMonitor.Imaging.JpegImageCodec.MediaType, jpeg.Products.Single().MediaType);

        var invalidChecksum = descriptor with
        {
            Artifact = descriptor.Artifact with { ChecksumSha256 = new string('0', 64) }
        };
        var invalid = await adapter.ExecuteAsync(
            invalidChecksum,
            ProcessingConformanceFixture.Payload,
            request.RecipeName,
            request.Options,
            request.Input,
            request.OutputVariant).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, invalid.Status);
        Assert.AreEqual(ProcessingReasonCodes.InvalidInput, invalid.ReasonCode);
        Assert.AreEqual("payload", invalid.Field);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task LogicHostAdapterExecutesOrderedRollingWindow()
    {
        var firstPayload = new byte[] { 10, 0, 20, 0, 30, 0, 40, 0 };
        var secondPayload = new byte[] { 30, 0, 40, 0, 50, 0, 60, 0 };
        var baseline = ProcessingConformanceFixture.CreateDescriptor();
        var first = CreateSource(baseline, firstPayload, 1);
        var second = CreateSource(baseline, secondPayload, 2);
        var adapter = new LogicHostRecipeExecutionAdapter(new ProcessingRecipeExecutor());

        var outcome = await adapter.ExecuteAsync(
            [new LogicHostProcessingInput(first, firstPayload), new LogicHostProcessingInput(second, secondPayload)],
            BuiltInProcessingRecipes.RollingMean,
            System.Text.Json.JsonSerializer.SerializeToElement(new RollingMeanOptions(2)),
            ProcessingInputSelector.Raw("source"),
            "mean-2").ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        CollectionAssert.AreEqual(
            new byte[] { 20, 0, 30, 0, 40, 0, 50, 0 },
            outcome.Products.Single().Payload.ToArray());
        CollectionAssert.AreEqual(
            new[] { first.Artifact.ArtifactId, second.Artifact.ArtifactId },
            outcome.Products.Single().SourceArtifactIds.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task EquivalentAnnotationRequestMatchesCameraAgentAdapter()
    {
        var artifact = ProcessingConformanceFixture.CreateProcessingArtifact();
        var options = System.Text.Json.JsonSerializer.SerializeToElement(
            new AnnotationRecipeOptions(OutputEncoding: "Packed"));
        var selector = ProcessingInputSelector.Raw("source");
        var annotation = new ProcessingAnnotationInput(
            [new ProjectedAnnotationObject("fixture", "Fixture", new PixelPoint(1, 1), true, true)],
            [],
            new PreviewTransform(1, 1),
            null,
            new string('A', 64));
        var cameraAgent = new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor());
        var logicHost = new LogicHostRecipeExecutionAdapter(new ProcessingRecipeExecutor());

        var cameraOutcome = await cameraAgent.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.Annotation,
            options,
            selector,
            [artifact],
            "annotated-conformance",
            annotation), CancellationToken.None).ConfigureAwait(false);
        var logicOutcome = await logicHost.ExecuteAsync(
            ProcessingConformanceFixture.CreateDescriptor(),
            ProcessingConformanceFixture.Payload,
            BuiltInProcessingRecipes.Annotation,
            options,
            selector,
            "annotated-conformance",
            annotation).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, cameraOutcome.Status);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, logicOutcome.Status);
        var expected = cameraOutcome.Products.Single();
        var actual = logicOutcome.Products.Single();
        CollectionAssert.AreEqual(expected.Payload.ToArray(), actual.Payload.ToArray());
        Assert.AreEqual(expected.ChecksumSha256, actual.ChecksumSha256);
        Assert.AreEqual(expected.OutputIdentitySha256, actual.OutputIdentitySha256);
        Assert.AreEqual(expected.Recipe.IdentitySha256, actual.Recipe.IdentitySha256);
        CollectionAssert.AreEqual(expected.Algorithms.ToArray(), actual.Algorithms.ToArray());
        CollectionAssert.AreEqual(expected.SourceArtifactIds.ToArray(), actual.SourceArtifactIds.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task EquivalentCloudAssessmentRequestMatchesCameraAgentAdapter()
    {
        var baseline = ProcessingConformanceFixture.CreateDescriptor();
        var referencePayload = CreateLinearPayload((x, y) => (ushort)(1000 + x * 100 + y * 10));
        var currentPayload = CreateLinearPayload((x, y) =>
        {
            var reference = 1000 + x * 100 + y * 10;
            return (ushort)(x < 2 ? reference : reference / 2 + 100);
        });
        var currentDescriptor = CreateCloudSource(baseline, currentPayload, 41);
        var referenceDescriptor = CreateCloudSource(baseline, referencePayload, 42);
        var current = CreateProcessingArtifact(currentDescriptor, currentPayload);
        var reference = CreateProcessingArtifact(referenceDescriptor, referencePayload);
        var options = JsonSerializer.SerializeToElement(new CloudAssessmentOptions(
            GridColumns: 2,
            GridRows: 1,
            TransmissionThresholdMillionths: 750_000,
            MinimumReferenceSignal: 1,
            MinimumSamplesPerTile: 1));
        var selector = ProcessingInputSelector.Raw("source");
        ProcessingAuxiliaryInput[] auxiliary =
        [
            new(
                "clear-reference",
                ProcessingAuxiliaryInputKind.Artifact,
                ProcessingInputSelector.Raw("source"),
                ArtifactId: reference.ArtifactId)
        ];
        var cameraAgent = new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor());
        var logicHost = new LogicHostRecipeExecutionAdapter(new ProcessingRecipeExecutor());

        var edge = await cameraAgent.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.CloudAssessment,
            options,
            selector,
            [current, reference],
            "cloud-assessment-v1",
            AuxiliaryInputs: auxiliary,
            InputArtifactId: current.ArtifactId), CancellationToken.None).ConfigureAwait(false);
        var central = await logicHost.ExecuteAsync(
            [
                new LogicHostProcessingInput(currentDescriptor, currentPayload),
                new LogicHostProcessingInput(referenceDescriptor, referencePayload, "clear-reference")
            ],
            BuiltInProcessingRecipes.CloudAssessment,
            options,
            selector,
            "cloud-assessment-v1",
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, edge.Status, edge.ReasonCode);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, central.Status, central.ReasonCode);
        var expected = edge.Products.Single();
        var actual = central.Products.Single();
        CollectionAssert.AreEqual(expected.Payload.ToArray(), actual.Payload.ToArray());
        Assert.AreEqual(expected.ChecksumSha256, actual.ChecksumSha256);
        Assert.AreEqual(expected.OutputIdentitySha256, actual.OutputIdentitySha256);
        Assert.AreEqual(expected.Recipe.IdentitySha256, actual.Recipe.IdentitySha256);
        CollectionAssert.AreEqual(expected.Algorithms.ToArray(), actual.Algorithms.ToArray());
        CollectionAssert.AreEqual(expected.SourceArtifactIds.ToArray(), actual.SourceArtifactIds.ToArray());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task LogicHostAdapterExecutesOverlayWithLayoutlessAssessmentInput()
    {
        var baseline = ProcessingConformanceFixture.CreateDescriptor();
        var referencePayload = CreateLinearPayload((x, y) => (ushort)(1000 + x * 100 + y * 10));
        var currentPayload = CreateLinearPayload((x, y) => (ushort)(800 + x * 80 + y * 8));
        var currentDescriptor = CreateCloudSource(baseline, currentPayload, 51);
        var referenceDescriptor = CreateCloudSource(baseline, referencePayload, 52);
        var environment = new CloudAssessmentEnvironmentV1(
            CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            CaptureSolarRegime.Night,
            EnvironmentalObservationMatchStatus.Missing,
            null,
            null,
            false);
        var environmentPayload = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(
            CaptureContractJson.SerializeToElement(environment)));
        var environmentInput = new ProcessingAuxiliaryInput(
            "environment",
            ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            IdentitySha256: ProcessingIdentity.ComputePayloadSha256(environmentPayload),
            Payload: environmentPayload);
        var adapter = new LogicHostRecipeExecutionAdapter(new ProcessingRecipeExecutor());

        var assessment = await adapter.ExecuteAsync(
            [
                new LogicHostProcessingInput(currentDescriptor, currentPayload),
                new LogicHostProcessingInput(referenceDescriptor, referencePayload, "clear-reference")
            ],
            BuiltInProcessingRecipes.CloudAssessment,
            JsonSerializer.SerializeToElement(new CloudAssessmentOptions(
                GridColumns: 2,
                GridRows: 1,
                MinimumReferenceSignal: 1,
                MinimumSamplesPerTile: 1)),
            ProcessingInputSelector.Raw("source"),
            "cloud-assessment-v1",
            auxiliaryInputs: [environmentInput],
            cancellationToken: CancellationToken.None).ConfigureAwait(false);
        var preview = await adapter.ExecuteAsync(
            currentDescriptor,
            currentPayload,
            BuiltInProcessingRecipes.EncodedPreview,
            JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")),
            ProcessingInputSelector.Raw("source"),
            "preview").ConfigureAwait(false);
        var assessmentProduct = assessment.Products.Single();
        var previewProduct = preview.Products.Single();

        var overlay = await adapter.ExecuteAsync(
            [
                new LogicHostProcessingInput(
                    null,
                    previewProduct.Payload,
                    Artifact: ToArtifact(previewProduct, currentDescriptor.Timing.ExposureStartedUtc)),
                new LogicHostProcessingInput(
                    null,
                    assessmentProduct.Payload,
                    "assessment",
                    ToArtifact(assessmentProduct, currentDescriptor.Timing.ExposureStartedUtc))
            ],
            BuiltInProcessingRecipes.WeatherCloudOverlay,
            JsonSerializer.SerializeToElement(new WeatherCloudOverlayOptions(OutputEncoding: "Packed")),
            ProcessingInputSelector.RecipeResult(
                FrameArtifactRole.Preview,
                previewProduct.Variant,
                previewProduct.Recipe.IdentitySha256),
            "weather-cloud-overlay-v1",
            auxiliaryInputs: [environmentInput],
            cancellationToken: CancellationToken.None).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, overlay.Status, overlay.ReasonCode);
        Assert.HasCount(2, overlay.Products.Single().SourceArtifactIds);
        Assert.IsNotNull(overlay.Products.Single().Layout);
    }

    private static byte[] CreateLinearPayload(Func<int, int, ushort> value)
    {
        const int width = 4;
        const int height = 2;
        var payload = new byte[width * height * 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sample = value(x, y);
                var offset = (y * width + x) * 2;
                payload[offset] = (byte)sample;
                payload[offset + 1] = (byte)(sample >> 8);
            }
        }
        return payload;
    }

    private static ProcessingArtifact ToArtifact(ProcessingProduct product, DateTimeOffset createdUtc)
        => new(
            ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256),
            product.Role,
            product.Variant,
            product.Recipe.IdentitySha256,
            product.MediaType,
            product.Layout,
            product.Payload,
            createdUtc,
            product.TotalIntegration,
            product.Compatibility,
            SourceArtifactIds: product.SourceArtifactIds);

    private static ReconstructionDescriptor CreateCloudSource(
        ReconstructionDescriptor baseline,
        byte[] payload,
        int suffix)
        => baseline with
        {
            Capture = baseline.Capture with
            {
                CaptureId = new Guid($"94000000-0000-0000-0000-{suffix:D12}"),
                CaptureSequence = suffix
            },
            Layout = baseline.Layout with
            {
                Width = 4,
                Height = 2,
                StrideBytes = 8,
                BlackLevel = 0,
                WhiteLevel = ushort.MaxValue,
                ByteLength = payload.Length
            },
            Artifact = baseline.Artifact with
            {
                ArtifactId = new Guid($"95000000-0000-0000-0000-{suffix:D12}"),
                Role = FrameArtifactRole.Raw,
                Variant = "source",
                ChecksumSha256 = PayloadChecksum.ComputeSha256(payload),
                SourceArtifactIds = []
            }
        };

    private static ProcessingArtifact CreateProcessingArtifact(
        ReconstructionDescriptor descriptor,
        byte[] payload)
        => new(
            descriptor.Artifact.ArtifactId,
            descriptor.Artifact.Role,
            descriptor.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
            descriptor.Artifact.MediaType,
            descriptor.Layout,
            payload,
            descriptor.Timing.ExposureStartedUtc,
            descriptor.Controls.EffectiveExposure,
            LogicHostRecipeExecutionAdapter.CreateCompatibility(descriptor),
            descriptor.Capture.CaptureSequence);

    private static HVO.SkyMonitor.AgentCore.ReconstructionDescriptor CreateSource(
        HVO.SkyMonitor.AgentCore.ReconstructionDescriptor baseline,
        byte[] payload,
        int sequence)
    {
        var artifactId = new Guid($"93000000-0000-0000-0000-{sequence + 10:D12}");
        return baseline with
        {
            Capture = baseline.Capture with
            {
                CaptureId = new Guid($"93000000-0000-0000-0000-{sequence + 20:D12}"),
                CaptureSequence = sequence
            },
            Artifact = baseline.Artifact with
            {
                ArtifactId = artifactId,
                Variant = "source",
                CreatedUtc = baseline.Artifact.CreatedUtc.AddSeconds(sequence),
                ChecksumSha256 = HVO.SkyMonitor.AgentCore.PayloadChecksum.ComputeSha256(payload)
            }
        };
    }

    private static TransientDetectorMask[] CreateTransientMasks(int width, int height)
    {
        var empty = Linear16MaskOperations.Empty(width, height);
        return new[]
        {
            TransientDetectorMaskKind.Sky,
            TransientDetectorMaskKind.ImageCircle,
            TransientDetectorMaskKind.Horizon,
            TransientDetectorMaskKind.Obstruction,
            TransientDetectorMaskKind.BadPixel,
            TransientDetectorMaskKind.Star
        }.Select(kind => TransientDetectorMask.Create(
            kind,
            new ProcessingAlgorithmIdentity($"conformance-{kind.ToString().ToUpperInvariant()}-mask", "v1"),
            empty)).ToArray();
    }

    private sealed class RecordingExecutor : IProcessingRecipeExecutor
    {
        internal ProcessingExecutionRequest? Request { get; private set; }

        public ValueTask<ProcessingOutcome> ExecuteAsync(
            ProcessingExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            return ValueTask.FromResult(ProcessingOutcome.Skipped("recorded"));
        }
    }
}
