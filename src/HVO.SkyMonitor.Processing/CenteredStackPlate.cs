using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

internal sealed record CenteredStackPlate(AstrometricMapping? Mapping, MeasuredAstrometrySolutionV1 Solution)
{
    internal static CenteredStackPlate Verify(ProcessingArtifact raw, ProcessingArtifact association, ProcessingArtifact scene,
        ProcessingArtifact context, ProcessingArtifact artifact, CancellationToken cancellationToken)
    {
        var product = RetainedAstrometryProducts.Product(artifact);
        if (artifact.ArtifactId != ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256))
        {
            throw new ArgumentException("Retained solution product identity differs.", nameof(artifact));
        }
        var options = product.Recipe.Descriptor.Options.GetProperty("parameters")
            .Deserialize<MeasuredAstrometryOptions>(CenteredStackPlanV1.Json)
            ?? throw new ArgumentException("Retained solution options unavailable.", nameof(artifact));
        var request = RetainedAstrometryProducts.Request(raw, association, scene, context, options, product.Variant);
        var solution = RetainedAstrometryProducts.Verify(request, product, cancellationToken);
        if (!solution.Assessment.HasMeasuredMapping)
        {
            return new(null, solution);
        }

        var frozen = MeasuredAstrometryJson.ParseContext(context.Payload);
        var measured = MeasuredStellarAssociationJson.Parse(association.Payload).Associations
            ?? throw new ArgumentException("Retained associations unavailable.", nameof(association));
        var calibration = new AstrometricCalibration(frozen.Projection, frozen.CalibrationVersion, frozen.ReadoutIdentitySha256);
        return new(MeasuredAstrometryJson.ReconstructMapping(solution, calibration, measured), solution);
    }

    internal bool IsCompatibleWith(CenteredStackPlate reference) =>
        reference.Solution.CatalogPackage == Solution.CatalogPackage &&
        reference.Solution.ReadoutIdentitySha256 == Solution.ReadoutIdentitySha256 &&
        reference.Solution.Assessment.CalibrationIdentitySha256 == Solution.Assessment.CalibrationIdentitySha256 &&
        reference.Solution.Assessment.Frame.ObserverIdentitySha256 == Solution.Assessment.Frame.ObserverIdentitySha256 &&
        reference.Solution.Assessment.CoordinateModel == Solution.Assessment.CoordinateModel &&
        reference.Solution.Assessment.RefractionModel == Solution.Assessment.RefractionModel;

    internal sealed record Pair(AstrometricMapping Source, AstrometricMapping Reference) : IImageRegistrationMap
    {
        public PixelPoint? SourceToReference(PixelPoint source) => Source.MapPixelTo(source, Reference);
        public PixelPoint? ReferenceToSource(PixelPoint reference) => Reference.MapPixelTo(reference, Source);
    }
}
