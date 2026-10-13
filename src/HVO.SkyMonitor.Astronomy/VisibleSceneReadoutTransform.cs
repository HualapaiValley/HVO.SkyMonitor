namespace HVO.SkyMonitor.Astronomy;

/// <summary>Transforms native-ROI scene geometry into emitted readout coordinates exactly once.</summary>
public static class VisibleSceneReadoutTransform
{
    public static VisibleScene ToOutput(
        VisibleScene nativeRoiScene,
        ProjectionContext outputProjection,
        int binX,
        int binY)
    {
        ArgumentNullException.ThrowIfNull(nativeRoiScene);
        if (binX < 1 || binY < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(binX));
        }
        outputProjection.Validate();
        var source = nativeRoiScene.Request;
        var request = new VisibleSceneRequest(
            source.Utc,
            source.Observer,
            outputProjection,
            source.CatalogQuery,
            source.CatalogMetadata,
            source.Refraction,
            source.HorizonPolicy,
            source.ProjectionVersion,
            source.AlgorithmVersion,
            source.ConstellationIds,
            source.SolarSystemBodies,
            source.IncludeConstellationEndpointStars);
        return new VisibleScene(
            request,
            nativeRoiScene.Objects.Select(item => item with
            {
                Pixel = Scale(item.Pixel, binX, binY)
            }),
            nativeRoiScene.Segments.Select(segment => segment with
            {
                FromPixel = Scale(segment.FromPixel, binX, binY),
                ToPixel = Scale(segment.ToPixel, binX, binY)
            }),
            nativeRoiScene.ComputationProvenance,
            nativeRoiScene.ResolvedFootprints.Select(footprint => Scale(footprint, binX, binY)),
            nativeRoiScene.DeepSkySelection,
            nativeRoiScene.DeepSky is { } deepSky ? Scale(deepSky, binX, binY) : null);
    }

    private static ProjectedDeepSky Scale(ProjectedDeepSky value, int binX, int binY)
    {
        // Binning keeps angular size; a non-square bin scales a length by the geometric mean of its factors.
        var lengthScale = Math.Sqrt((double)binX * binY);
        return value with
        {
            Objects = value.Objects.Select(item => item with
            {
                Pixel = item.Pixel is { } pixel ? Scale(pixel, binX, binY) : null,
                MajorAxisPixels = item.MajorAxisPixels / lengthScale
            }).ToArray(),
            Outlines = value.Outlines.Select(outline =>
            {
                var parts = Scale(outline.Parts, binX, binY);
                return outline with { Parts = parts, Bounds = ResolvedFootprintSampler.ComputeBounds(parts) };
            }).ToArray()
        };
    }

    private static ResolvedFootprintPart[] Scale(IReadOnlyList<ResolvedFootprintPart> parts, int binX, int binY) =>
        parts.Select(part => part with
        {
            Points = part.Points.Select(point => Scale(point, binX, binY)).ToArray()
        }).ToArray();

    private static ProjectedResolvedFootprint Scale(ProjectedResolvedFootprint value, int binX, int binY)
    {
        var parts = Scale(value.Parts, binX, binY);
        return value with
        {
            CenterPixel = value.CenterPixel is { } center ? Scale(center, binX, binY) : null,
            Parts = parts,
            Bounds = ResolvedFootprintSampler.ComputeBounds(parts)
        };
    }

    private static PixelPoint Scale(PixelPoint value, int binX, int binY)
        => new(value.X / binX, value.Y / binY);
}
