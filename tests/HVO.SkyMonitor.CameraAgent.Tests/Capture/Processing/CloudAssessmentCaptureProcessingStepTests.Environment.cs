using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class CloudAssessmentCaptureProcessingStepTests
{
    [TestMethod]
    [DataRow("OnlineWeather", EnvironmentalObservationKind.AirTemperature)]
    [DataRow("SkyQualityMeter", EnvironmentalObservationKind.SkyQuality)]
    public async Task EnabledAcquisitionWithoutRainSourceProducesMissingEnvironmentWithoutStoreReads(
        string sourceType, EnvironmentalObservationKind kind)
    {
        var context = CreateRawDescriptorContext();
        var stores = new CountingEnvironmentalStores();
        var configured = EnvironmentOptions(sourceType, kind);
        var environment = new CameraAgentCloudEnvironment(
            new EnvironmentalAssociationService(stores, stores, configured, TimeProvider.System), stores, configured);
        var step = CreateEnvironmentStep(configured, environment);
        context.BeginNode("cloud", ["$raw"]);

        await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

        var outcome = context.ProcessingOutcomes.Single();
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products.Single();
        Assert.AreEqual(ProcessingProductKind.Metadata, product.Kind);
        Assert.AreEqual(FrameArtifactRole.Metadata, product.Role);
        var assessment = CloudAssessmentJson.Parse(product.Payload).Assessment;
        Assert.IsNotNull(assessment);
        Assert.Contains(CloudAssessmentReasonCodes.EnvironmentMissing, assessment.ReasonCodes);
        Assert.AreEqual(EnvironmentalObservationMatchStatus.Missing, assessment.Environment!.PrecipitationStatus);
        Assert.IsNull(assessment.Environment.PrecipitationObservationId);
        Assert.IsNull(assessment.Environment.PrecipitationContentSha256);
        Assert.IsFalse(assessment.Environment.PrecipitationDetected);

        var disabledInput = CameraAgentCloudEnvironment.CreateMissingInput(context);
        var input = await environment.CreateInputAsync(context, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(input);
        CollectionAssert.AreEqual(disabledInput.Payload.ToArray(), input.Payload.ToArray());
        Assert.AreEqual(disabledInput.IdentitySha256, input.IdentitySha256);
        Assert.AreEqual(ProcessingIdentity.ComputePayloadSha256(input.Payload), input.IdentitySha256);
        Assert.AreEqual(input.IdentitySha256, assessment.Environment.InputIdentitySha256);
        Assert.IsTrue(context.GetCurrentInputEvidence().Any(evidence =>
            evidence.Kind == "CanonicalContext" && evidence.Name == "environment" &&
            evidence.IdentitySha256 == input.IdentitySha256));
        Assert.AreEqual(0, stores.AssociationLookups);
        Assert.AreEqual(0, stores.ObservationReads);
    }

    [TestMethod]
    public async Task ConfiguredRainSourceWithPendingAssociationStillRequestsRetry()
    {
        var context = CreateRawDescriptorContext();
        var stores = new CountingEnvironmentalStores();
        var configured = EnvironmentOptions("VirtualEnvironment", EnvironmentalObservationKind.RainState);
        var environment = new CameraAgentCloudEnvironment(
            new EnvironmentalAssociationService(stores, stores, configured, TimeProvider.System), stores, configured);
        context.BeginNode("cloud", ["$raw"]);

        await CreateEnvironmentStep(configured, environment).ProcessAsync(context, CancellationToken.None)
            .ConfigureAwait(false);

        var outcome = context.ProcessingOutcomes.Single();
        Assert.AreEqual(ProcessingOutcomeStatus.RetryableFailure, outcome.Status);
        Assert.AreEqual(ProcessingReasonCodes.EnvironmentAssociationPending, outcome.ReasonCode);
        Assert.IsEmpty(outcome.Products);
        Assert.AreEqual(1, stores.AssociationLookups);
        Assert.AreEqual(0, stores.ObservationReads);
    }

    [TestMethod]
    [DataRow(CloudAssessmentStatus.InsufficientEvidence)]
    [DataRow(CloudAssessmentStatus.Contaminated)]
    public async Task NonQuantifiedAssessmentWithEmptyReasonsIsRejectedBeforePresentationIndexing(
        CloudAssessmentStatus status)
    {
        // Hand-written JSON deliberately bypasses CloudAssessmentJson.Serialize. Refs #1777 (C-6).
        var contaminated = status == CloudAssessmentStatus.Contaminated;
        var reasons = "\"cloud.missing-clear-reference\",\"cloud.missing-calibration\",\"cloud.environment-missing\"" +
            (contaminated ? ",\"cloud.saturation-contamination\"" : string.Empty);
        string Json(string reasonList) => $$"""
            {
              "schemaVersion":"cloud-assessment-v1", "status":"{{status}}", "quality":"Unusable",
              "reasonCodes":[{{reasonList}}], "coverageMillionths":null, "confidenceMillionths":0,
              "grid":{"columns":1,"rows":1,"transmissionThresholdMillionths":850000,
                      "validRegionCount":0,"cloudyRegionCount":0,"validSampleCount":0,"cloudySampleCount":0},
              "regions":[{"column":0,"row":0,"x":0,"y":0,"width":2,"height":2,
                          "consideredSampleCount":4,"acceptedSampleCount":0,"saturatedSampleCount":{{(contaminated ? 4 : 0)}},
                          "transmissionMillionths":null,"isCloudy":false}],
              "mask":null,
              "current":{"artifactId":"10000000-0000-0000-0000-000000000001","role":"Raw",
                         "variant":"source","recipeIdentitySha256":"{{new string('A', 64)}}"},
              "clearReference":null,"calibration":null,
              "environment":{"schemaVersion":"cloud-assessment-environment-v1","solarRegime":null,
                             "precipitationStatus":"Missing","precipitationObservationId":null,
                             "precipitationContentSha256":null,"precipitationDetected":false,
                             "inputIdentitySha256":null},
              "recipeIdentitySha256":"{{new string('B', 64)}}",
              "algorithms":[{"name":"cloud-transmission","version":"v1"}]
            }
            """;
        var valid = CloudAssessmentJson.Parse(Encoding.UTF8.GetBytes(Json(reasons)));
        Assert.IsTrue(valid.Validation.IsValid, valid.Validation.ReasonCode);
        Assert.IsNotNull(valid.Assessment);
        var invalid = valid.Assessment with { ReasonCodes = [] };
        var validation = CloudAssessmentJson.Validate(invalid);
        Assert.IsFalse(validation.IsValid);
        Assert.AreEqual("cloud.invalid-contract", validation.ReasonCode);
        Assert.ThrowsExactly<ArgumentException>(() => CloudAssessmentJson.Serialize(invalid));
        var payload = Encoding.UTF8.GetBytes(Json(string.Empty));
        var parsed = CloudAssessmentJson.Parse(payload);
        Assert.IsNull(parsed.Assessment);
        Assert.AreEqual("cloud.invalid-contract", parsed.Validation.ReasonCode);

        var context = CreateRawDescriptorContext();
        context.BeginNode("cloud", ["$raw"]);
        await CreateEnvironmentStep(EnvironmentOptions("OnlineWeather", EnvironmentalObservationKind.AirTemperature))
            .ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
        context.RegisterProcessingProduct(context.ProcessingOutcomes.Single().Products.Single() with { Payload = payload });
        context.BeginNode("presentation", ["cloud"]);
        var presentation = new CloudPresentationLayerCaptureProcessingStep(
            new CaptureProcessingStepMetadata("presentation", "CloudPresentationLayer", 90),
            new CloudPresentationLayerProcessingStepOptions
            {
                RequireQuantifiedAssessment = true,
                WidthPixels = 2,
                HeightPixels = 2
            });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await presentation.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.HasCount(1, context.ProcessingOutcomes, "Invalid input must not add a presentation outcome.");
    }

    private static IOptions<CameraAgentHostOptions> EnvironmentOptions(string type, EnvironmentalObservationKind kind)
        => Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = Path.GetTempPath(),
            EnvironmentalAcquisition = new EnvironmentalAcquisitionOptions
            {
                Enabled = true,
                Sources = [new EnvironmentalSourceConfiguration
                {
                    Id = "environment-fixture", Type = type, Kind = kind,
                    Triggers = [EnvironmentalAcquisitionTrigger.Periodic]
                }]
            }
        });

    private static CloudAssessmentCaptureProcessingStep CreateEnvironmentStep(
        IOptions<CameraAgentHostOptions> configured, CameraAgentCloudEnvironment? environment = null)
        => new(new CaptureProcessingStepMetadata("cloud", "CloudAssessment", 80),
            new CloudAssessmentProcessingStepOptions { GridColumns = 1, GridRows = 1, MinimumSamplesPerTile = 2 },
            new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor()),
            new CameraAgentClearReferenceLoader(configured), environment);

    private static CaptureProcessingContext CreateRawDescriptorContext()
    {
        var bytes = new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 };
        var manifest = ReconstructableCaptureContractTests.CreateManifest(CameraPixelFormat.Mono16, 2, 2, 4, bytes);
        var validation = FrameReconstructor.TryReconstruct(manifest.Descriptor, bytes, out var frame);
        Assert.IsTrue(validation.IsValid, validation.ReasonCode);
        Assert.IsNotNull(frame);
        var artifacts = new FrameArtifactSet(new FrameArtifact(
            manifest.Descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, frame,
            recipeVersion: ProcessingIdentity.CreateRecipeIdentity(manifest.Descriptor.Artifact.Recipe).IdentitySha256));
        var submission = new CaptureLoopSubmission(
            new CaptureRequest(frame.TimestampUtc, frame.Metadata.Exposure, CaptureMode.Still),
            new CaptureResult(frame, new CaptureSetpoint(frame.Metadata.Exposure, frame.Metadata.Gain, null, null),
                TimeSpan.Zero, CaptureMode.Still, false)
            { Artifacts = artifacts },
            frame.TimestampUtc, frame.Metadata.Exposure, TimeSpan.Zero);
        var receipt = new RawCaptureReceipt(RawIngressOutcome.Committed, manifest,
            new StoredFrameReference("raw.bin", Path.Combine(Path.GetTempPath(), "raw.bin"),
                frame.TimestampUtc, FrameArtifactRole.Raw), new string('A', 64));
        return new CaptureProcessingContext(ProcessingConformanceFixture.CameraConfig, submission, receipt);
    }

    private sealed class CountingEnvironmentalStores : ILocalEnvironmentalObservationStore, ILocalEnvironmentalAssociationStore
    {
        internal int AssociationLookups { get; private set; }
        internal int ObservationReads { get; private set; }

        public ValueTask<IReadOnlyList<string>> ReadCaptureUnavailableSourcesAsync(
            string root, Guid captureId, DateTimeOffset exposureFromUtc, CancellationToken cancellationToken)
            => throw UnexpectedRead();

        public ValueTask<IReadOnlyList<LocalEnvironmentalCaptureAssociation>> ReadAssociationsAsync(
            string root, Guid captureId, CancellationToken cancellationToken)
        {
            AssociationLookups++;
            return ValueTask.FromResult<IReadOnlyList<LocalEnvironmentalCaptureAssociation>>([]);
        }

        private InvalidOperationException UnexpectedRead()
        {
            ObservationReads++;
            return new InvalidOperationException("No observation read is expected.");
        }

        public ValueTask<LocalEnvironmentalObservationRecord?> ReadLocalDetailAsync(
            string root, long recordId, CancellationToken cancellationToken) => throw UnexpectedRead();
        public ValueTask<LocalEnvironmentalObservationRecord?> ReadLocalLatestPrimaryAsync(string root,
            EnvironmentalObservationKind kind, IReadOnlyList<string> configuredSourceIds,
            IReadOnlyList<string> unavailableSourceIds, CancellationToken cancellationToken) => throw UnexpectedRead();
        public ValueTask<IReadOnlyList<LocalEnvironmentalObservationRecord>> ReadLocalCandidatesAsync(string root,
            EnvironmentalObservationKind kind, string? rigId, DateTimeOffset intervalFromUtc,
            DateTimeOffset intervalThroughUtc, int maximumResults, CancellationToken cancellationToken) => throw UnexpectedRead();
        public ValueTask<LocalEnvironmentalObservationSnapshot> GetLocalSnapshotAsync(
            string root, CancellationToken cancellationToken) => throw UnexpectedRead();
        public ValueTask<LocalEnvironmentalObservationPage> ReadLocalPageAsync(string root,
            EnvironmentalObservationKind? kind, int pageSize, LocalEnvironmentalObservationCursor? cursor,
            CancellationToken cancellationToken) => throw UnexpectedRead();
        public ValueTask<LocalEnvironmentalObservationCommitResult> CommitLocalAsync(string root,
            EnvironmentalObservationFactV1 fact, EnvironmentalObservationResolvedTarget? deliveryTarget,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No observation write is expected.");
        public ValueTask SaveAssociationAsync(string root, LocalEnvironmentalCaptureAssociation association,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No association write is expected.");
        public ValueTask SaveAssociationSetAsync(string root, IReadOnlyList<LocalEnvironmentalCaptureAssociation> associations,
            CancellationToken cancellationToken) => throw new InvalidOperationException("No association write is expected.");
    }
}
