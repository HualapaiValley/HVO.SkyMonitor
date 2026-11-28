using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

public sealed partial class RawCaptureIngressTests
{
    [TestMethod]
    public async Task CenteredRegistrationReopensExactRawAndLinearCustodyWithoutRecalibration()
    {
        var token = CancellationToken.None;
        var root = CreateRoot();
        try
        {
            var config = CenteredConfiguration("registration-camera");
            var rawTime = Timestamp(1);
            ExposureKey key;
            ExposureProductRef product;
            using var siloProvider = SiloInfrastructureFixture.OpenStore(root);
            using (var original = new CenteredFixture(root, siloProvider: siloProvider))
            {
                var receipt = await original.AdmitAsync(config, rawTime).ConfigureAwait(false);
                var descriptor = receipt.Manifest.Descriptor;
                var attempt = receipt.Manifest.CenteredExposure!.Source;
                key = new(attempt.Camera, attempt.RunId, descriptor.Capture.CaptureId);
                var persistence = new CaptureProcessingPersistence(original.Options, original.Store, original.Telemetry,
                    NullLogger<CaptureProcessingPersistence>.Instance, silo: original.Silo);
                using var standard = new StandardCaptureLaneHandler(original.Factory, persistence, original.Telemetry,
                    NullLogger<StandardCaptureLaneHandler>.Instance, original.Ingress, original.Options);
                var lane = original.Policy.Definitions.Single(definition => definition.Name == "standard");
                var pending = await original.Ingress.ClaimAsync(lane, "registration-input", config, token).ConfigureAwait(false);
                Assert.IsNotNull(pending);
                var result = await standard.HandleAsync(pending.Context, token).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
                await original.Ingress.CompleteAsync(pending, token).ConfigureAwait(false);
                var reader = new CenteredExposureProductReader(original.Ingress, original.Store);
                var settled = await reader.ReadLinearAsync(key, token).ConfigureAwait(false);
                Assert.IsNotNull(settled?.Product);
                product = settled.Product;
            }

            using var restarted = new CenteredFixture(root, siloProvider: siloProvider);
            await SiloInfrastructureFixture.SettleRawAdmissionsAsync(restarted.Ingress, token).ConfigureAwait(false);
            var reopenedPersistence = new CaptureProcessingPersistence(restarted.Options, restarted.Store, restarted.Telemetry,
                NullLogger<CaptureProcessingPersistence>.Instance, silo: restarted.Silo);
            var products = new CenteredExposureProductReader(restarted.Ingress, restarted.Store);
            var custody = new CenteredExposureInputCustody(restarted.Ingress, products, restarted.Store, reopenedPersistence, TimeProvider.System);
            var lease = await custody.OpenLinearAsync(product, TimeSpan.FromMinutes(1), token).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            await using var hold = lease.ConfigureAwait(false);
            var admitted = await restarted.Ingress.ReadCenteredRawAsync(key, token).ConfigureAwait(false);
            Assert.IsNotNull(admitted);
            var frame = await restarted.Ingress.ReadCenteredFrameAsync(admitted, token).ConfigureAwait(false);
            var rawDescriptor = admitted.Manifest.Descriptor;
            var raw = CameraAgentRecipeExecutionAdapter.CreateArtifact(admitted.Run.Configuration,
                new(rawDescriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, frame), rawDescriptor.Artifact.Variant,
                reconstructionDescriptor: rawDescriptor);
            var linear = lease.Input.Linear;
            var read = new CenteredStackRetainedInputReader(products, (_, _) =>
                ValueTask.FromResult<CenteredStackMeasurementGeometry?>(new(null, null)));
            var qualified = await read.ReadLinearAsync(key, linear, token).ConfigureAwait(false);
            Assert.IsNotNull(qualified);
            var validity = new byte[raw.Layout!.Width * raw.Layout.Height];
            validity.AsSpan().Fill(1);
            var mask = CenteredStackValidityMask.Create(raw, linear, validity);
            var reference = CenteredStackExposurePin.Create(qualified, raw, mask, null);
            var slots = Enumerable.Range(-2, 5).Select(offset => offset == 0
                ? new CenteredStackSlot(offset, rawTime, CenteredSlotState.Ready, key.CaptureId, reference)
                : new(offset, rawTime.AddSeconds(offset), CenteredSlotState.Missing, null, null)).ToArray();
            var plan = new CenteredStackPlanV1(key.Camera, key.RunId, "reopen-test-generation", key.CaptureId, linear.ArtifactId,
                rawTime.AddSeconds(3), rawTime.AddSeconds(3), "reopen-custody", slots);
            var request = CenteredRegisteredStackRecipe.CreateRequest(plan, linear.Variant, "reopen", [raw, linear, mask]);
            var outcome = await new ProcessingRecipeExecutor([new CenteredRegisteredStackRecipe(read)])
                .ExecuteAsync(request, token).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, $"{outcome.ReasonCode} {outcome.Field}");
            var evidence = CenteredRegisteredStackEvidence.Read(Assert.ContainsSingle(outcome.Products));
            Assert.AreEqual(CenteredStackDisposition.SingleExposure, evidence.Disposition);
            Assert.AreEqual(linear.ArtifactId, evidence.Plan.ReferenceLinearArtifactId);
            Assert.AreEqual(product.PayloadSha256, evidence.Plan.Slots[2].Exposure!.Linear.PayloadSha256);
            Assert.AreEqual(raw.Integration.Ticks, reference.IntegrationTicks);
        }
        finally
        {
            DeleteRoot(root);
        }
    }
}
