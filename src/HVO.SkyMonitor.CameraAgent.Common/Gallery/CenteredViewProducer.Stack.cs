using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal sealed partial class CenteredViewProducer
{
    private static readonly JsonSerializerOptions GeometryJson = new(JsonSerializerDefaults.Web);

    public async ValueTask<CenteredView?> PublishRegisteredStackAsync(
        CenteredViewStream stream, string windowIdentity, Guid registrationEvidenceArtifactId,
        IExposureProductReader exposureProducts, CenteredViewPreviewOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(exposureProducts);
        ArgumentNullException.ThrowIfNull(options);
        var disposition = await store.ReadCenteredWindowDispositionAsync(windowIdentity, cancellationToken).ConfigureAwait(false);
        if (disposition?.Publication is not { } publication)
            return null;

        var closure = disposition.Closure;
        if (closure.Schedule.Camera != stream.Camera || closure.Schedule.RunId != stream.RunId ||
            (closure.Schedule.GenerationId != stream.GenerationId &&
                closure.Schedule.GenerationId != stream.RunId.ToString("N")) ||
            publication.ClosureIdentity != closure.Identity ||
            publication.EvidenceArtifactId != registrationEvidenceArtifactId ||
            closure.Schedule.RegistrationRecipeIdentitySha256 != CenteredRegisteredStackRecipe.FixedIdentity.IdentitySha256 ||
            !publication.RegistrationArtifactIds.Contains(registrationEvidenceArtifactId))
            throw new InvalidDataException("The registered preview does not name its exact frozen window and generation.");
        var retained = await views.ReadRegisteredBaseAsync(stream, publication.ReferenceCaptureId, CenteredViewKind.Stack,
            cancellationToken).ConfigureAwait(false);
        if (retained is not null)
        {
            var binding = retained.Base.Definition.Window;
            if (retained.Base.Definition.Science.ArtifactId != registrationEvidenceArtifactId ||
                binding is null || binding.ScheduleIdentitySha256 != windowIdentity ||
                binding.ClosureIdentitySha256 != closure.Identity ||
                binding.RegistrationPlanIdentitySha256 != publication.PlanSha256)
                throw new InvalidDataException("The registered base belongs to a different frozen window or registration.");
            return retained;
        }
        var output = await store.ReadOutputByArtifactIdAsync(registrationEvidenceArtifactId, cancellationToken).ConfigureAwait(false);
        if (output is null || output.Capture.CaptureId != publication.ReferenceCaptureId ||
            output.ProductSchemaVersion != CenteredRegisteredStackEvidence.CurrentSchemaVersion)
            throw new InvalidDataException("The registered preview lacks its committed science evidence.");
        var science = new CenteredViewArtifact(output.ArtifactId, output.OutputIdentitySha256, output.Artifact.ChecksumSha256);
        var artifact = await persistence.ReadCenteredViewRetainedProductAsync(publication.ReferenceCaptureId, science, cancellationToken)
            .ConfigureAwait(false);
        var evidence = CenteredRegisteredStackEvidence.Read(artifact);
        RequireWindowBinding(closure, publication, evidence);
        var referenceLinear = closure.Center.Products?.Linear;
        if (referenceLinear is null ||
            artifact.Recipe.IdentitySha256 != CenteredRegisteredStackRecipe.ExecutionIdentity(evidence.Plan, referenceLinear.Slot.Variant).IdentitySha256)
            throw new InvalidDataException("The retained registration recipe is not bound to the frozen plan and reference selector.");
        if (evidence.Disposition != CenteredStackDisposition.Registered)
            return null;

        var contributors = new List<CenteredViewContributor>(CenteredWindowScheduler.SlotCount);
        foreach (var source in evidence.Sources.Where(source => source.Reason == CenteredRegistrationReason.Contributed))
        {
            var slot = evidence.Plan.Slots[source.Offset + CenteredWindowScheduler.CenterIndex];
            var pin = slot.Exposure ?? throw new InvalidDataException("A contributed stack slot has no immutable exposure.");
            var qualified = await exposureProducts.ReadLinearAsync(pin.Key, cancellationToken).ConfigureAwait(false);
            var scheduled = closure.Slots[source.Offset + CenteredWindowScheduler.CenterIndex];
            if (qualified?.Product is not { } product || scheduled.Input is not { } admission ||
                product.Slot.ExecutionId != ProcessingGraphOperationsCoordinator.GetLiveExecutionId(pin.Key.CaptureId, stream.GenerationId) ||
                scheduled.Products?.Linear != product || product.ArtifactId != pin.Linear.ArtifactId ||
                product.PayloadSha256 != pin.Linear.PayloadSha256 || qualified.Exposure.Key != pin.Key ||
                qualified.Exposure.Raw.Artifact.ArtifactId != admission.RawArtifactId ||
                qualified.Provenance?.RawDescriptorSha256 != admission.RawDescriptorSha256 ||
                qualified.Provenance?.RawPayloadSha256 != admission.RawPayloadSha256 ||
                source.Method is null)
                throw new InvalidDataException("The stack contributor is missing its exact admitted source and registration.");
            var exposure = BindQualifiedExposure(qualified, product.Slot.ExecutionId);
            contributors.Add(new(exposure, source.Method.Value.ToString(),
                JsonSerializer.SerializeToElement(source, GeometryJson)));
        }
        var reference = contributors.Single(contributor => contributor.Exposure.Capture.CaptureId == publication.ReferenceCaptureId).Exposure;
        var (channels, support) = await ReadRegisteredPreviewPlanesAsync(publication, evidence, cancellationToken)
            .ConfigureAwait(false);
        var referencePixels = await persistence.ReadCenteredViewProductAsync(reference.Capture.CaptureId,
            reference.Linear!, cancellationToken).ConfigureAwait(false);
        var levelSource = referencePixels.Layout ?? throw new InvalidDataException("The retained reference has no pixel layout.");
        var closedWindow = await PersistClosureAsync(closure, publication, reference, science, cancellationToken).ConfigureAwait(false);
        var encoded = CenteredViewPreviewEncoder.Encode(channels, levelSource, CenteredViewImageSource.RegisteredLinear,
            null, null, BoundOptions(options), [closedWindow.ArtifactId],
            new(referencePixels, evidence.Outputs[^1].ArtifactId, support), cancellationToken);
        var definition = new CenteredViewBaseDefinition(stream, reference, reference.Capture.CaptureSequence,
            CenteredViewKind.Stack, science, encoded.Preview, contributors.AsReadOnly(), closedWindow)
        {
            Window = new(windowIdentity, publication.ClosureIdentity, evidence.PlanSha256)
            {
                DeadlineUtc = closure.Schedule.DeadlineUtc,
                EvidenceCutoffUtc = closure.EvidenceCutoffUtc,
                Slots = Array.AsReadOnly(closure.Slots.Select((slot, index) => new CenteredViewWindowSlot(
                    slot.Offset, slot.Time.ExpectedStartUtc, slot.Input?.Key.CaptureId,
                    slot.Input?.Timing.ExposureStartedUtc, slot.Input?.Timing.ExposureEndedUtc,
                    slot.State.ToString(), evidence.Sources[index].Reason.ToString(), slot.Reason)).ToArray())
            }
        };
        return await PersistAndRegisterAsync(definition, encoded.Product, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CenteredViewArtifact> PersistClosureAsync(CenteredWindowClosure closure,
        CenteredWindowPublication receipt, CenteredViewExposure reference, CenteredViewArtifact science, CancellationToken token)
    {
        const string schema = "cameraagent-centered-view-closure-v1";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { schema, closure, receipt }, GeometryJson);
        var recipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(schema, "1.0.0", schema,
            JsonSerializer.SerializeToElement(new { receipt.ClosureIdentity, scheduleIdentity = closure.Schedule.Identity })));
        Guid[] lineage = [reference.Input.Raw.Artifact.ArtifactId, science.ArtifactId];
        var output = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, schema, recipe.IdentitySha256, lineage);
        var checksum = ProcessingIdentity.ComputePayloadSha256(payload);
        var product = new ProcessingProduct(FrameArtifactRole.Metadata, schema, output,
            "application/vnd.hvo.centered-view-closure+json", null, payload, checksum, recipe,
            [new("centered-view-closure", schema)], lineage, reference.Input.Raw.Controls.EffectiveExposure,
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(reference.Input.Raw))
        {
            Kind = ProcessingProductKind.Metadata,
            SchemaVersion = schema,
            ContentIdentitySha256 = receipt.ClosureIdentity
        };
        await persistence.PersistCenteredViewProductAsync(reference.Input.Raw, product, token).ConfigureAwait(false);
        return new(ProcessingIdentity.CreateArtifactId(output), output, checksum);
    }

    private async ValueTask<(ProcessingArtifact[] Channels, ReadOnlyMemory<byte> Support)> ReadRegisteredPreviewPlanesAsync(CenteredWindowPublication publication,
        CenteredRegisteredStackEvidence evidence, CancellationToken token)
    {
        if (publication.RegistrationArtifactIds.Length != evidence.Outputs.Count + 1 ||
            evidence.Outputs.Any(output => !publication.RegistrationArtifactIds.Contains(output.ArtifactId)))
            throw new InvalidDataException("The registered preview must use the committed channels of its frozen stack.");

        var planes = new ProcessingProduct[evidence.Outputs.Count];
        for (var index = 0; index < planes.Length; index++)
        {
            var pin = evidence.Outputs[index];
            planes[index] = await persistence.ReadCenteredViewRetainedProductAsync(publication.ReferenceCaptureId,
                new(pin.ArtifactId, pin.OutputIdentitySha256, pin.PayloadSha256), token).ConfigureAwait(false);
        }
        evidence.VerifyPlanes(planes, token);
        var channels = planes.Take(planes.Length - 1).Select((plane, index) => new ProcessingArtifact(
            evidence.Outputs[index].ArtifactId, plane.Role, plane.Variant, plane.Recipe.IdentitySha256,
            plane.MediaType, plane.Layout, plane.Payload, evidence.ReferenceMidpointUtc!.Value,
            plane.TotalIntegration, plane.Compatibility)).ToArray();
        return (channels, planes[^1].Payload);
    }

    private static void RequireWindowBinding(CenteredWindowClosure closure, CenteredWindowPublication publication,
        CenteredRegisteredStackEvidence evidence)
    {
        var plan = evidence.Plan;
        if (evidence.SchemaVersion != CenteredRegisteredStackEvidence.CurrentSchemaVersion ||
            plan.ToAuxiliaryInput().IdentitySha256 != evidence.PlanSha256 ||
            publication.PlanSha256 != evidence.PlanSha256 || publication.StackDisposition != evidence.Disposition ||
            plan.Camera != closure.Schedule.Camera || plan.RunId != closure.Schedule.RunId ||
            plan.ClosureIdentity != closure.Identity ||
            plan.CloseDeadlineUtc != closure.Schedule.DeadlineUtc || plan.ReferenceCaptureId != publication.ReferenceCaptureId ||
            plan.ReferenceLinearArtifactId != closure.Center.Products?.Linear?.ArtifactId ||
            evidence.Sources.Count != CenteredWindowScheduler.SlotCount)
            throw new InvalidDataException("The science product disagrees with the frozen centered window.");

        for (var index = 0; index < evidence.Sources.Count; index++)
        {
            var source = evidence.Sources[index];
            var closed = closure.Slots[index];
            var planned = plan.Slots[index];
            if (source.Offset != closed.Offset || source.CaptureId != planned.CaptureId ||
                planned.CaptureId is { } plannedCapture && closed.Input?.Key.CaptureId != plannedCapture ||
                source.Reason == CenteredRegistrationReason.Contributed &&
                (closed.State != CenteredWindowSlotState.Eligible || planned.Exposure?.Key != closed.Input?.Key))
                throw new InvalidDataException("A frozen stack slot disagrees with its registration evidence.");
        }
        var contributed = evidence.Sources.Where(source => source.Reason == CenteredRegistrationReason.Contributed)
            .Select(source => source.CaptureId!.Value);
        if (!contributed.SequenceEqual(publication.ContributingCaptureIds))
            throw new InvalidDataException("The registered stack does not match the ordered, published contributors.");
    }
}
