using System.Buffers.Binary;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

internal sealed record RegisteredDeepSkyInput(float[] Luminance, ReadOnlyMemory<byte> Support)
{
    internal static CenteredRegisteredStackEvidence Read(ProcessingArtifact evidence)
    {
        var checksum = evidence.ContentIdentitySha256
            ?? throw new InvalidDataException("A retained stack lacks its payload identity.");
        var stack = CenteredRegisteredStackEvidence.Read(ToProduct(evidence, checksum));
        if (stack.Plan.ReferenceCaptureId != evidence.CaptureId)
        {
            throw new InvalidDataException("The registration envelope belongs to a different reference exposure.");
        }
        return stack;
    }

    internal static string OutputIdentity(ProcessingArtifact artifact) =>
        ProcessingIdentity.CreateOutputIdentity(artifact.Role, artifact.Variant, artifact.RecipeIdentitySha256,
            artifact.SourceArtifactIds ?? throw new InvalidDataException("Missing retained lineage."));

    internal static bool HasVerifiedRegistration(CenteredRegisteredStackEvidence stack,
        IReadOnlyList<CenteredStackSourceEvidence> contributors)
    {
        var reference = stack.Plan.Slots[2].Exposure!;
        var midpoint = reference.ExposureStartedUtc.AddTicks(
            (reference.ExposureEndedUtc - reference.ExposureStartedUtc).Ticks / 2);
        return stack.ReferenceMidpointUtc == midpoint &&
            stack.Width > 0 && stack.Height > 0 && (long)stack.Width * stack.Height <= LinearRegisteredMean.MaximumPixels &&
            stack.ValidPixelCount > 0 && stack.ValidPixelCount <= stack.Width * stack.Height &&
            stack.Sources[2].Reason == CenteredRegistrationReason.Contributed &&
            stack.Sources[2].Method == CenteredRegistrationMethod.ReferenceIdentity &&
            contributors.Count >= 2 && contributors.Select(source => source.CaptureId).Distinct().Count() == contributors.Count &&
            stack.TotalIntegrationTicks == contributors.Sum(source => stack.Plan.Slots[source.Offset + 2].Exposure?.IntegrationTicks ?? 0) &&
            contributors.All(source => Verified(source, stack.Width * stack.Height));

        static bool Verified(CenteredStackSourceEvidence source, int pixels) =>
            source.CaptureId.HasValue && source.SupportedPixels > 0 && source.SupportedPixels <= pixels &&
            (source.Offset == 0
                ? source.Similarity is null
                : source.PairCheck is { Status: DirectRegistrationStatus.Verified, Verification.Count: >= 3 } &&
                  (source.Method == CenteredRegistrationMethod.VerifiedPlatePair
                      ? source.PairCheck.Verification.Count >= 8
                      : source.PairCheck.Fit.Count >= 5) &&
                  source.PairCheck.Verification.All(match => double.IsFinite(match.ResidualPixels) && match.ResidualPixels is >= 0 and <= 1.25) &&
                  (source.Method switch
                  {
                      CenteredRegistrationMethod.VerifiedPlatePair => source.Plate is not null && source.Similarity is null,
                      CenteredRegistrationMethod.VerifiedCentroidSimilarity => source.Similarity is { } map &&
                          map.A * map.A + map.B * map.B is >= .97 * .97 and <= 1.03 * 1.03 &&
                          double.IsFinite(map.Tx) && double.IsFinite(map.Ty),
                      _ => false
                  }));
    }

    internal static RegisteredDeepSkyInput Decode(CenteredRegisteredStackEvidence stack, ProcessingArtifact evidence,
        ProcessingArtifact selected, Dictionary<Guid, ProcessingArtifact> artifacts, CancellationToken cancellationToken)
    {
        if (selected.ArtifactId != stack.Outputs[0].ArtifactId)
        {
            throw new InvalidDataException("The selected combined variant is not the first plane of the frozen stack.");
        }

        var planes = stack.Outputs.Select(LoadPlane).ToArray();
        stack.VerifyPlanes(planes, cancellationToken);
        var support = planes[^1].Payload;
        var luminance = new float[stack.Width * stack.Height];
        var red = planes[0].Payload.Span;
        var green = planes.Length == 4 ? planes[1].Payload.Span : default;
        var blue = planes.Length == 4 ? planes[2].Payload.Span : default;
        for (var pixel = 0; pixel < luminance.Length; pixel++)
        {
            if ((pixel & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            var r = BinaryPrimitives.ReadUInt16LittleEndian(red.Slice(pixel * 2, 2));
            var g = green.IsEmpty ? r : BinaryPrimitives.ReadUInt16LittleEndian(green.Slice(pixel * 2, 2));
            var b = blue.IsEmpty ? r : BinaryPrimitives.ReadUInt16LittleEndian(blue.Slice(pixel * 2, 2));
            luminance[pixel] = r == ushort.MaxValue || g == ushort.MaxValue || b == ushort.MaxValue
                ? -1 : .2126f * r + .7152f * g + .0722f * b;
        }
        return new(luminance, support);

        ProcessingProduct LoadPlane(CenteredStackOutputPin pin)
        {
            if (!artifacts.TryGetValue(pin.ArtifactId, out var plane) || plane.CaptureId != evidence.CaptureId ||
                plane.Compatibility != evidence.Compatibility)
            {
                throw new InvalidDataException("A retained registered plane differs from the frozen observation.");
            }
            return ToProduct(plane, pin.PayloadSha256);
        }
    }

    private static ProcessingProduct ToProduct(ProcessingArtifact artifact, string checksum)
    {
        var outputIdentity = OutputIdentity(artifact);
        if (artifact.RecipeDescriptor is not { } descriptor || ProcessingIdentity.CreateArtifactId(outputIdentity) != artifact.ArtifactId)
        {
            throw new InvalidDataException("A retained stack artifact lacks its exact recipe or lineage.");
        }
        return new(artifact.Role, artifact.Variant, outputIdentity, artifact.MediaType, artifact.Layout, artifact.Payload,
            checksum, new(descriptor, artifact.RecipeIdentitySha256), [],
            artifact.SourceArtifactIds!, artifact.Integration, artifact.Compatibility)
        {
            Kind = artifact.ProductKind,
            SchemaVersion = artifact.SchemaVersion,
            ContentIdentitySha256 = artifact.ContentIdentitySha256
        };
    }
}
