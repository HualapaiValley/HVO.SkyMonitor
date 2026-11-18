using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class CloudAssessmentEnvironmentSourceTests
{
    [TestMethod]
    [DataRow("nws-only")]
    [DataRow("sqm-only")]
    [DataRow("acquisition-disabled")]
    [DataRow("fresh-rain")]
    public async Task WeatherCloudOverlayProducesWithCanonicalEnvironmentAndAssessmentProvenance(string profile)
    {
        var hasRain = profile == "fresh-rain";
        using var harness = Harness.Create(hasRain ? "rain-source-no-reading" : profile);
        var provider = harness.Provider;
        var configuration = CreateConfiguration(cloudRequired: false, harness.Root);
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var submission = CreateSubmission(0);
        var receipt = await ingress.AcceptAsync(configuration, submission, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        var descriptor = receipt.Manifest.Descriptor;
        LocalEnvironmentalObservationRecord? rain = null;
        LocalEnvironmentalCaptureAssociation? rainAssociation = null;
        if (hasRain)
        {
            // This overlay control uses a real persisted fresh rain association, not a made-up input identity.
            var parameters = JsonSerializer.SerializeToElement(new { });
            var fact = new EnvironmentalObservationFactV1(
                EnvironmentalObservationSchemaVersions.V1, Guid.NewGuid(),
                new EnvironmentalObservationSource("test-provider", "rain", "1.0.0",
                    EnvironmentalObservationSourceKind.Measured,
                    new EnvironmentalObservationProvenance(new ProcessingAlgorithmIdentity("normalizer", "1.0.0"),
                        parameters, CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
                descriptor.Timing.ExposureStartedUtc.AddSeconds(-1), null, null,
                descriptor.Timing.ExposureStartedUtc.AddMinutes(-1),
                descriptor.Timing.ExposureEndedUtc.AddMinutes(1),
                descriptor.Timing.ExposureEndedUtc.AddMinutes(1),
                new EnvironmentalObservationValue(EnvironmentalObservationKind.RainState,
                    EnvironmentalObservationUnit.Boolean, null, false, EnvironmentalObservationQuality.Good), []);
            rain = (await provider.GetRequiredService<ILocalEnvironmentalObservationStore>()
                .CommitLocalAsync(harness.Root, fact, CancellationToken.None).ConfigureAwait(false)).Record;
        }
        if (provider.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value.EnvironmentalAcquisition.Enabled)
        {
            var driven = await DriveLaneAsync(provider.GetRequiredService<ICaptureLaneStore>(),
                provider.GetRequiredService<CaptureLanePolicy>().Definitions,
                provider.GetServices<ICaptureLaneHandler>().ToArray(), "environment-association", configuration)
                .ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, driven.Result.Outcome);
        }
        if (hasRain)
        {
            rainAssociation = (await provider.GetRequiredService<ILocalEnvironmentalAssociationStore>()
                .ReadAssociationsAsync(harness.Root, descriptor.Capture.CaptureId, CancellationToken.None)
                .ConfigureAwait(false)).Single();
            Assert.AreEqual(LocalEnvironmentalAssociationStatus.Fresh, rainAssociation.Status);
            Assert.AreEqual(rain!.RecordId, rainAssociation.SelectedRecordId);
        }

        var context = CreateStepContext(configuration, submission, receipt);
        var adapter = provider.GetRequiredService<CameraAgentRecipeExecutionAdapter>();
        context.BeginNode("preview", ["$raw"]);
        await new PreviewCaptureProcessingStep(new CaptureProcessingStepMetadata("preview", "Preview", 10),
            new PreviewProcessingStepOptions { FixedTransfer = new FixedDisplayTransferOptions(0, 10, 1) }, adapter)
            .ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
        var preview = context.ProcessingOutcomes.Single();
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, preview.Status, preview.ReasonCode);
        Assert.AreEqual(FrameArtifactRole.Preview, preview.Products.Single().Role);
        Assert.IsNotNull(context.AllArtifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Preview));

        var graph = provider.GetRequiredService<ICaptureProcessingPipelineFactory>().CreateGraph(configuration);
        try
        {
            var cloud = graph.Nodes.Single(static node => node.Id == "CloudAssessment");
            context.BeginNode("CloudAssessment", cloud.Dependencies, cloud.DeclaredDependencies);
            await cloud.Step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
            var assessmentOutcome = context.ProcessingOutcomes[^1];
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, assessmentOutcome.Status, assessmentOutcome.ReasonCode);
            var assessmentProduct = assessmentOutcome.Products.Single();
            var assessment = CloudAssessmentJson.Parse(assessmentProduct.Payload).Assessment;
            Assert.IsNotNull(assessment);
            context.RegisterProcessingProduct(assessmentProduct);

            var expectedEnvironment = hasRain
                ? new CloudAssessmentEnvironmentV1(CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
                    context.Submission.CycleEvidence?.SolarRegime, EnvironmentalObservationMatchStatus.Fresh,
                    rain!.Fact.ObservationId, rain.ContentSha256, false, rainAssociation!.AssociationIdentitySha256)
                : new CloudAssessmentEnvironmentV1(CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
                    context.Submission.CycleEvidence?.SolarRegime, EnvironmentalObservationMatchStatus.Missing,
                    null, null, false);
            var expectedPayload = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(
                CaptureContractJson.SerializeToElement(expectedEnvironment)));
            var expectedIdentity = ProcessingIdentity.ComputePayloadSha256(expectedPayload);
            Assert.AreEqual(expectedEnvironment with { InputIdentitySha256 = expectedIdentity }, assessment.Environment);
            Assert.IsTrue(context.GetCurrentInputEvidence().Any(evidence => evidence.Name == "environment" &&
                evidence.Kind == "CanonicalContext" && evidence.IdentitySha256 == expectedIdentity));
            if (!hasRain)
            {
                Assert.Contains(CloudAssessmentReasonCodes.EnvironmentMissing, assessment.ReasonCodes);
            }

            context.BeginNode("overlay", ["preview", "CloudAssessment"]);
            Assert.HasCount(2, context.GetDependencyProducts(), "Both real prerequisites must reach the overlay.");
            var overlay = new WeatherCloudOverlayCaptureProcessingStep(
                new CaptureProcessingStepMetadata("overlay", "WeatherCloudOverlay", 90),
                new WeatherCloudOverlayProcessingStepOptions { DrawLabels = false }, adapter,
                provider.GetRequiredService<CameraAgentCloudEnvironment>());
            await overlay.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);

            Assert.HasCount(3, context.ProcessingOutcomes, "The overlay must add its own outcome.");
            var outcome = context.ProcessingOutcomes[^1];
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
            Assert.IsNull(outcome.ReasonCode);
            var product = outcome.Products.Single();
            Assert.AreEqual(FrameArtifactRole.AnnotatedPreview, product.Role);
            Assert.Contains(CaptureProcessingContext.CreateArtifactId(assessmentProduct.OutputIdentitySha256),
                product.SourceArtifactIds);
            Assert.Contains(CaptureProcessingContext.CreateArtifactId(preview.Products.Single().OutputIdentitySha256),
                product.SourceArtifactIds);
            Assert.IsTrue(context.GetCurrentInputEvidence().Any(evidence => evidence.Name == "environment" &&
                evidence.Kind == "CanonicalContext" && evidence.IdentitySha256 == expectedIdentity));
            Assert.AreEqual(0, harness.Network.Calls);
        }
        finally
        {
            graph.DisposeSteps();
        }
    }
}
