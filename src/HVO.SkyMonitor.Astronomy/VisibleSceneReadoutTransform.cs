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
            nativeRoiScene.ComputationProvenance);
    }

    private static PixelPoint Scale(PixelPoint value, int binX, int binY)
        => new(value.X / binX, value.Y / binY);
}
