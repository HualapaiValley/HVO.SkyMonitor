using System.Text.Json;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>Strict supplemental canonical JSON. Existing snapshot, track and pass bytes are unchanged.</summary>
public static class OrbitalPassArcJson
{
    public const string SchemaVersion = "hvo-orbital-pass-arc-v1";
    public const string InterpolationVersion = "unit-vector-slerp-by-utc-v1";
    public const string EstimatorVersion = "quartic-cartesian-to-time-slerp-empirical-v1";
    // 256 KiB reserves parent/identity metadata, including escaped Unicode population text;
    // 2 KiB covers each worst-case sample and singleton segment.
    // Enforce before copying/materializing, in addition to the final exact serialized byte check.
    public const int MaximumMaterializedSamples = (OrbitalPredictionJson.MaximumProductBytes - 256 * 1024) / 2048;

    public static OrbitalPassArcV1 Create(OrbitalPassArcV1 value)
        => Create(value, null);

    internal static OrbitalPassArcV1 Create(OrbitalPassArcV1 value, Action? checkpoint)
    {
        checkpoint?.Invoke();
        ArgumentNullException.ThrowIfNull(value); ArgumentNullException.ThrowIfNull(value.Pass);
        ArgumentNullException.ThrowIfNull(value.Samples); ArgumentNullException.ThrowIfNull(value.Segments);
        OrbitalContract.Require(value.Samples.Count is > 0 and <= MaximumMaterializedSamples &&
            value.Segments.Count <= value.Samples.Count, "arc-materialization-bound");
        var pass = OrbitalPredictionJson.CreatePass(value.Pass);
        OrbitalContract.Require(pass.ProductSha256 == value.Pass.ProductSha256, "arc-parent-identity");
        var samples = new OrbitalTrackSample[value.Samples.Count];
        for (var i = 0; i < samples.Length; i++) { checkpoint?.Invoke(); samples[i] = value.Samples[i]; }
        var copy = value with
        {
            ProductSha256 = string.Empty,
            Pass = pass,
            Samples = Array.AsReadOnly(samples),
            Segments = Array.AsReadOnly(value.Segments.ToArray())
        };
        Validate(copy, checkpoint);
        checkpoint?.Invoke();
        copy = copy with { ProductSha256 = Identity(copy) };
        checkpoint?.Invoke();
        OrbitalContract.Require(OrbitalContract.SerializeCanonical(copy).Length <= OrbitalPredictionJson.MaximumProductBytes, "product-byte-bound");
        checkpoint?.Invoke();
        return copy;
    }

    public static byte[] Serialize(OrbitalPassArcV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var copy = Create(value);
        OrbitalContract.Require(copy.ProductSha256 == value.ProductSha256, "arc-identity");
        return OrbitalContract.SerializeCanonical(copy);
    }

    public static OrbitalPassArcV1 Parse(ReadOnlySpan<byte> json)
    {
        OrbitalContract.Require(json.Length is > 0 and <= OrbitalPredictionJson.MaximumProductBytes, "product-byte-bound");
        ValidateArrayAllowances(json);
        var value = JsonSerializer.Deserialize<OrbitalPassArcV1>(json, OrbitalContract.JsonOptions) ?? throw new ArgumentException("missing-arc");
        OrbitalContract.Require(json.SequenceEqual(Serialize(value)), "noncanonical-arc");
        return Create(value);
    }

    private static void ValidateArrayAllowances(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 32 });
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1 ||
                !reader.ValueTextEquals("samples") && !reader.ValueTextEquals("segments")) continue;
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) throw new JsonException("arc-array-required");
            var count = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                OrbitalContract.Require(++count <= MaximumMaterializedSamples, "arc-materialization-bound");
                reader.Skip();
            }
        }
    }

    private static string Identity(OrbitalPassArcV1 v) => OrbitalContract.Hash(new
    {
        v.SchemaVersion,
        v.QuerySha256,
        v.SearchQuerySha256,
        v.Pass,
        v.CoveredInterval,
        v.Samples,
        v.Segments,
        v.Anchors,
        v.Sampling,
        v.OrbitUncertainty,
        v.Status,
        v.Reason
    });

    private static void Validate(OrbitalPassArcV1 v, Action? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(v.CoveredInterval); ArgumentNullException.ThrowIfNull(v.Anchors);
        ArgumentNullException.ThrowIfNull(v.Sampling); ArgumentNullException.ThrowIfNull(v.OrbitUncertainty);
        v.CoveredInterval.Validate();
        var sf = v.Sampling;
        OrbitalContract.Require(v.SchemaVersion == SchemaVersion && OrbitalContract.IsSha256(v.QuerySha256) &&
            OrbitalContract.IsSha256(v.SearchQuerySha256) && !v.Pass.Model.NominalZeroEarthOrientation &&
            Enum.IsDefined(v.Status) && Enum.IsDefined(v.Reason) && v.Status != OrbitalExecutionStatus.Unavailable &&
            (v.Status == OrbitalExecutionStatus.Complete ? v.Reason == OrbitalPredictionReason.None &&
                v.Pass.Status == OrbitalExecutionStatus.Complete : v.Reason != OrbitalPredictionReason.None) &&
            v.CoveredInterval.StartUtc >= v.Pass.PassInterval.StartUtc && v.CoveredInterval.EndUtc <= v.Pass.PassInterval.EndUtc &&
            (v.Status != OrbitalExecutionStatus.Complete || v.CoveredInterval == v.Pass.PassInterval), "arc-coverage-or-status");
        OrbitalContract.Require(double.IsFinite(sf.MaximumStepSeconds) && sf.MaximumStepSeconds is > 0 and <= 1 &&
            double.IsFinite(sf.MaximumAngularErrorDegrees) && sf.MaximumAngularErrorDegrees is > 0 and <= .25 &&
            double.IsFinite(sf.AchievedMaximumStepSeconds) && sf.AchievedMaximumStepSeconds >= 0 &&
            (sf.AchievedEstimatedAngularErrorDegrees is null || double.IsFinite(sf.AchievedEstimatedAngularErrorDegrees.Value) &&
                sf.AchievedEstimatedAngularErrorDegrees >= 0 && sf.AchievedEstimatedAngularErrorDegrees <= sf.MaximumAngularErrorDegrees) &&
            sf.InterpolationVersion == InterpolationVersion && sf.EstimatorVersion == EstimatorVersion &&
            (v.Status != OrbitalExecutionStatus.Complete || sf.AchievedMaximumStepSeconds <= sf.MaximumStepSeconds &&
                sf.AchievedEstimatedAngularErrorDegrees is not null), "arc-sampling");
        OrbitalContract.Require(v.OrbitUncertainty is { Value: null, Unit: "degrees", Reason: "physical-orbit-error-unqualified" }, "arc-physical-uncertainty");
        DateTimeOffset? last = null; var step = 0d;
        foreach (var sample in v.Samples)
        {
            checkpoint?.Invoke();
            OrbitalContract.Require(sample is not null && OrbitalContract.Utc(sample.Utc) && (last is null || sample.Utc > last) &&
                sample.Utc >= v.CoveredInterval.StartUtc && sample.Utc <= v.CoveredInterval.EndUtc && sample.Pixel is null &&
                double.IsFinite(sample.Horizontal.AltitudeDegrees) && sample.Horizontal.AltitudeDegrees is >= -90 and <= 90 &&
                double.IsFinite(sample.Horizontal.AzimuthDegrees) && sample.Horizontal.AzimuthDegrees is >= 0 and < 360 &&
                (sample.RangeKilometers is null || double.IsFinite(sample.RangeKilometers.Value) && sample.RangeKilometers > 0) &&
                (sample.RangeRateKilometersPerSecond is null || double.IsFinite(sample.RangeRateKilometersPerSecond.Value)) &&
                (sample.AngularSpeedDegreesPerSecond is null || double.IsFinite(sample.AngularSpeedDegreesPerSecond.Value) &&
                    sample.AngularSpeedDegreesPerSecond >= 0) && sample.Illumination == OrbitalIllumination.Unknown, "arc-sample");
            if (last is not null) step = Math.Max(step, (sample!.Utc - last.Value).TotalSeconds);
            last = sample!.Utc;
        }
        OrbitalContract.Require(v.CoveredInterval.StartUtc == v.Samples[0].Utc && v.CoveredInterval.EndUtc == v.Samples[^1].Utc &&
            step == sf.AchievedMaximumStepSeconds, "arc-achieved-coverage");
        Anchor(v.Anchors.StartSampleIndex, v.Pass.PassInterval.StartUtc);
        Anchor(v.Anchors.PeakSampleIndex, v.Pass.PeakUtc);
        Anchor(v.Anchors.EndSampleIndex, v.Pass.PassInterval.EndUtc);
        if (v.Anchors.PeakSampleIndex is { } peak)
            OrbitalContract.Require(Math.Abs(v.Samples[peak].Horizontal.AltitudeDegrees - v.Pass.MaximumElevationDegrees) <= 1e-8,
                "arc-parent-peak-altitude");
        var end = 0;
        foreach (var segment in v.Segments)
        {
            checkpoint?.Invoke();
            OrbitalContract.Require(segment is not null && segment.StartSampleIndex >= end && segment.SampleCount > 0 &&
                (long)segment.StartSampleIndex + segment.SampleCount <= v.Samples.Count, "arc-segment");
            end = segment!.StartSampleIndex + segment.SampleCount;
            for (var i = segment.StartSampleIndex + 1; i < end; i++)
                OrbitalContract.Require((v.Samples[i].Utc - v.Samples[i - 1].Utc).TotalSeconds <= sf.MaximumStepSeconds &&
                    OrbitalAngularPath.CanInterpolate(CameraBasis.FromHorizontal(v.Samples[i - 1].Horizontal), CameraBasis.FromHorizontal(v.Samples[i].Horizontal)) &&
                    sf.AchievedEstimatedAngularErrorDegrees is not null, "arc-unresolved-edge");
        }
        OrbitalContract.Require(v.Status != OrbitalExecutionStatus.Complete || v.Anchors.StartSampleIndex == 0 &&
            v.Anchors.PeakSampleIndex is not null && v.Anchors.EndSampleIndex == v.Samples.Count - 1 &&
            v.Segments.Count == 1 && v.Segments[0] == new OrbitalSkyArcSegmentV1(0, v.Samples.Count), "arc-complete-continuity");

        void Anchor(int? index, DateTimeOffset utc)
        {
            var actual = -1;
            for (var i = 0; i < v.Samples.Count; i++) if (v.Samples[i].Utc == utc) { actual = i; break; }
            OrbitalContract.Require(actual < 0 ? index is null : index == actual, "arc-anchor");
        }
    }
}
