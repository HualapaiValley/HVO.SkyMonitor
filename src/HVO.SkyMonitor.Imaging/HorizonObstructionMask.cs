using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Separate exclusion bit planes. Pixels and raw evidence are never changed.</summary>
public sealed record HorizonObstructionMaskPlanes(Linear16PixelMask Obstructed, Linear16PixelMask Unknown,
    Linear16PixelMask OutsideDomain);

public static class HorizonObstructionMask
{
    public const string AlgorithmVersion = "horizon-pixel-centres-v1";
    public const int MaximumPixelArea = 16_777_216;

    /// <summary>Generates at most three bounded bit planes; no shared or persistent cache is retained.</summary>
    public static HorizonObstructionMaskPlanes Create(HorizonObstructionEvaluator evaluator,
        ProjectedSceneImageTransformV1 transform, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(transform);
        if (!evaluator.IsCompatible) throw new ArgumentException("Horizon profile requires revalidation.", nameof(evaluator));
        var projection = evaluator.Projection;
        ProjectedSceneImageTransform.Validate(transform, projection.WidthPixels, projection.HeightPixels);
        var width = transform.OutputWidthPixels;
        var height = transform.OutputHeightPixels;
        if ((long)width * height > MaximumPixelArea) throw new ArgumentOutOfRangeException(nameof(transform));
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = Linear16MaskOperations.RequiredByteLength(width, height);
        var obstructed = new byte[bytes];
        var unknown = new byte[bytes];
        var outside = new byte[bytes];
        var projector = ProjectorFactory.Create(projection);
        // Resolve the affine inverse once, keeping validation and transform allocations out of the pixel loop.
        var origin = ProjectedSceneImageTransform.Inverse(transform, new(0, 0));
        var dx = ProjectedSceneImageTransform.Inverse(transform, new(1, 0));
        var dy = ProjectedSceneImageTransform.Inverse(transform, new(0, 1));
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var source = new PixelPoint(origin.X + (x + .5) * (dx.X - origin.X) + (y + .5) * (dy.X - origin.X),
                    origin.Y + (x + .5) * (dx.Y - origin.Y) + (y + .5) * (dy.Y - origin.Y));
                var direction = projector.Unproject(source);
                var status = direction is { } value ? evaluator.Evaluate(value) : HorizonObstructionEligibility.OutsideDomain;
                var plane = status switch
                {
                    HorizonObstructionEligibility.Obstructed => obstructed,
                    HorizonObstructionEligibility.Unknown => unknown,
                    HorizonObstructionEligibility.OutsideDomain => outside,
                    _ => null
                };
                if (plane is not null)
                {
                    var index = y * width + x;
                    plane[index >> 3] |= (byte)(1 << (index & 7));
                }
            }
        }
        return new(new(width, height, obstructed), new(width, height, unknown), new(width, height, outside));
    }
}
