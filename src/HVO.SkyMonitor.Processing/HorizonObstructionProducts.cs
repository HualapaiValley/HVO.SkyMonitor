using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Immutable provenance shared by prediction eligibility and generated masks.</summary>
public sealed record HorizonObstructionProductBinding(string SchemaVersion, string SourceIdentitySha256,
    string ProfileIdentitySha256, string GeometryIdentitySha256, string CalibrationVersion,
    string ProjectionAlgorithmVersion, ProjectedSceneImageTransformV1 ImageTransform);

public sealed record HorizonObstructionMaskProduct(HorizonObstructionProductBinding Binding, string IdentitySha256,
    string ObstructedChecksumSha256, string UnknownChecksumSha256, string OutsideDomainChecksumSha256,
    HorizonObstructionMaskPlanes Planes);

public sealed record HorizonObstructionPredictionProduct(HorizonObstructionProductBinding Binding, string IdentitySha256,
    IReadOnlyList<HorizonObstructionEligibility> Eligibility, IReadOnlyList<bool> ClearSegments);

/// <summary>Produces eligibility without discarding predicted tracks or changing source pixels.</summary>
public static class HorizonObstructionProducts
{
    public const string SchemaVersion = "horizon-obstruction-product-v1";
    public const int MaximumPredictionPoints = 65_536;

    public static HorizonObstructionProductBinding Bind(string sourceIdentitySha256, HorizonObstructionEvaluator evaluator,
        string calibrationVersion, ProjectedSceneImageTransformV1 transform)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ArgumentNullException.ThrowIfNull(sourceIdentitySha256);
        if (sourceIdentitySha256 is not { Length: 64 } || !sourceIdentitySha256.All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(calibrationVersion) || calibrationVersion.Length > 128 || calibrationVersion != evaluator.CalibrationVersion)
            throw new ArgumentException("A bounded source and calibration identity is required.");
        ProjectedSceneImageTransform.Validate(transform, evaluator.Projection.WidthPixels, evaluator.Projection.HeightPixels);
        return new(SchemaVersion, sourceIdentitySha256.ToUpperInvariant(), evaluator.Profile.ComputeIdentitySha256(),
            evaluator.GeometryIdentitySha256, calibrationVersion, RigProjectionContextFactory.AlgorithmVersion, transform);
    }

    public static HorizonObstructionMaskProduct CreateMask(HorizonObstructionEvaluator evaluator,
        HorizonObstructionProductBinding binding, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ValidateBinding(evaluator, binding);
        var planes = HorizonObstructionMask.Create(evaluator, binding.ImageTransform, cancellationToken);
        var obstructed = Hash(planes.Obstructed);
        var unknown = Hash(planes.Unknown);
        var outside = Hash(planes.OutsideDomain);
        var identity = CaptureContractJson.ComputeCanonicalJsonSha256(new
        { binding, algorithm = HorizonObstructionMask.AlgorithmVersion, obstructed, unknown, outside });
        return new(binding, identity, obstructed, unknown, outside, planes);
    }

    public static HorizonObstructionPredictionProduct Predict(HorizonObstructionEvaluator evaluator,
        HorizonObstructionProductBinding binding, IReadOnlyList<AltAzPoint> directions)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        ValidateBinding(evaluator, binding);
        ArgumentNullException.ThrowIfNull(directions);
        if (directions.Count > MaximumPredictionPoints) throw new ArgumentOutOfRangeException(nameof(directions));
        var frozenDirections = directions.ToArray();
        var eligibility = Array.AsReadOnly(frozenDirections.Select(evaluator.Evaluate).ToArray());
        var clearSegments = Array.AsReadOnly(Enumerable.Range(0, Math.Max(0, frozenDirections.Length - 1))
            .Select(i => evaluator.IsSegmentClear(frozenDirections[i], frozenDirections[i + 1])).ToArray());
        var identity = CaptureContractJson.ComputeCanonicalJsonSha256(new
        { binding, algorithm = HorizonObstructionEvaluator.AlgorithmVersion, directions = frozenDirections, eligibility, clearSegments });
        return new(binding, identity, eligibility, clearSegments);
    }

    private static void ValidateBinding(HorizonObstructionEvaluator evaluator, HorizonObstructionProductBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var expected = Bind(binding.SourceIdentitySha256, evaluator, binding.CalibrationVersion, binding.ImageTransform);
        if (binding != expected) throw new ArgumentException("Obstruction product binding does not match the profile.", nameof(binding));
    }

    private static string Hash(Linear16PixelMask mask) => Convert.ToHexString(SHA256.HashData(mask.Bits.Span));
}
