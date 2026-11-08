using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing;

/// <summary>A source-bound presentation view. The retained scene and its measured associations are never rewritten.</summary>
public sealed class HorizonObstructionSceneEligibility
{
    private readonly HorizonObstructionEvaluator _evaluator;
    private readonly ProjectedSceneImageTransformV1 _transform;
    private readonly IImageProjector _projector;
    private readonly RefractionOptions _refraction;
    public string SceneIdentitySha256 { get; }
    public string IdentitySha256 { get; }
    public bool IsAligned { get; }

    public HorizonObstructionSceneEligibility(HorizonObstructionEvaluator evaluator, CameraRigConfig rig, ProjectedSceneV1 scene)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(rig);
        ArgumentNullException.ThrowIfNull(scene);
        ProjectedSceneJson.Validate(scene);
        _evaluator = evaluator;
        _transform = scene.ImageTransform;
        _refraction = scene.Refraction;
        var projection = ProjectedSceneJson.ToProjectionContext(scene.Projection);
        _projector = ProjectorFactory.Create(projection);
        var effective = RigProjectionContextFactory.Create(rig);
        var native = RigProjectionContextFactory.CreateNative(rig);
        var nativeRoi = RigProjectionContextFactory.CreateNativeRoi(rig);
        var geometryMatches = projection == effective;
        if (!geometryMatches && rig.Readout is { } readout)
        {
            var geometry = SensorReadoutResolver.Resolve(rig.Sensor, readout).Geometry;
            geometryMatches = _transform.BinX == geometry.BinX && _transform.BinY == geometry.BinY &&
                _transform.CropWidth == geometry.RoiWidth && _transform.CropHeight == geometry.RoiHeight &&
                (projection == native && _transform.CropX == geometry.RoiX && _transform.CropY == geometry.RoiY ||
                 projection == nativeRoi && _transform.CropX == 0 && _transform.CropY == 0);
        }
        IsAligned = geometryMatches && evaluator.Projection == effective && evaluator.Observer == scene.Observer &&
            evaluator.GeometryIdentitySha256 == HorizonObstructionProfile.ComputeGeometryIdentitySha256(rig) &&
            scene.Projection.CalibrationVersion == evaluator.CalibrationVersion;
        SceneIdentitySha256 = scene.SceneIdentitySha256;
        IdentitySha256 = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            algorithm = "horizon-scene-eligibility-v1",
            scene.SceneIdentitySha256,
            profile = evaluator.Profile.ComputeIdentitySha256(),
            evaluator.GeometryIdentitySha256,
            evaluator.CalibrationVersion,
            IsAligned
        });
    }

    public void ValidateScene(ProjectedSceneV1 scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (scene.SceneIdentitySha256 != SceneIdentitySha256)
            throw new ArgumentException("The horizon eligibility belongs to another retained scene.", nameof(scene));
    }

    /// <summary>Freezes eligible curated IDs for downstream composition without rewriting retained scene evidence.</summary>
    public HorizonObstructionSceneSelection SelectDeepSky(ProjectedSceneV1 scene)
    {
        ProjectedSceneJson.Validate(scene);
        ValidateScene(scene);
        var footprints = (scene.ResolvedFootprints ?? [])
            .Where(static item => item.SourceKind == ResolvedFootprintSourceKind.DeepSkyObject)
            .ToDictionary(static item => item.Id, StringComparer.Ordinal);
        bool Eligible(ProjectedDeepSkyObject item)
        {
            if (item.Pixel is { } pixel && !IsClear(pixel)) return false;
            if (!footprints.TryGetValue(item.Id, out var footprint)) return item.Pixel is not null;
            return footprint.Parts.All(part => part.Points.All(IsClear) &&
                Enumerable.Range(0, part.Closed ? part.Points.Count : part.Points.Count - 1)
                    .All(i => IsSegmentClear(part.Points[i], part.Points[(i + 1) % part.Points.Count])));
        }
        var ids = (scene.DeepSky?.Objects ?? []).Where(Eligible).Select(static item => item.Id)
            .Order(StringComparer.Ordinal).ToArray();
        return new(HorizonObstructionSceneSelection.CurrentSchemaVersion, SceneIdentitySha256, IdentitySha256, ids);
    }

    public bool IsClear(PixelPoint pixel) => Direction(pixel) is { } direction &&
        _evaluator.Evaluate(direction) == HorizonObstructionEligibility.Clear;

    public bool IsSegmentClear(PixelPoint from, PixelPoint to) => Direction(from) is { } a && Direction(to) is { } b &&
        _evaluator.IsSegmentClear(a, b);

    private AltAzPoint? Direction(PixelPoint pixel)
    {
        if (!IsAligned || !_evaluator.IsCompatible || !double.IsFinite(pixel.X) || !double.IsFinite(pixel.Y) ||
            pixel.X < 0 || pixel.Y < 0 || pixel.X > _transform.OutputWidthPixels || pixel.Y > _transform.OutputHeightPixels)
            return null;
        var apparent = _projector.Unproject(ProjectedSceneImageTransform.Inverse(_transform, pixel));
        if (apparent is not { } direction || !_refraction.Enabled) return apparent;
        return AtmosphericRefraction.RemoveAboveHorizon(direction.AltitudeDegrees, _refraction) is { } geometric
            ? direction with { AltitudeDegrees = geometric } : null;
    }
}
