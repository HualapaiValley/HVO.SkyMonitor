using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy.Tests;

public sealed partial class SmallBodyGoldenFixtureTests
{
    [TestMethod]
    public void NegativeEvaluationInputsExerciseDistinctFailClosedConditions()
    {
        using var cases = Open("evaluation/cases.json");
        using var manifest = Open("snapshots/manifest-v2.json");
        using var original = Open("snapshots/payload-v1.json");
        using var payload = Open("snapshots/payload-v2.json");
        var vectors = cases.RootElement;
        var wrongObserver = vectors.GetProperty("observerMismatch");
        var requested = wrongObserver.GetProperty("requestedObserver");
        var actual = payload.RootElement.GetProperty("observerBinding");
        AssertDigest(requested, "observerBindingSha256");
        Assert.AreEqual(Str(actual, "observerBindingSha256"), Str(wrongObserver, "snapshotObserverBindingSha256"));
        Assert.AreNotEqual(Str(actual, "observerBindingSha256"), Str(requested, "observerBindingSha256"));
        foreach (var field in new[] { "deploymentLocationId", "latitudeDegrees", "longitudeDegrees", "elevationMeters", "heightDatum" })
            JsonEqual(actual.GetProperty(field), requested.GetProperty(field));
        Assert.AreNotEqual(Int(actual, "deploymentLocationVersion"), Int(requested, "deploymentLocationVersion"));
        AssertWithheld(wrongObserver.GetProperty("expected"), "ObserverMismatch");

        var freshness = vectors.GetProperty("freshness");
        Assert.AreEqual(Str(manifest.RootElement, "manifestSha256"), Str(freshness, "manifestSha256"));
        var cutoff = TimeSpan.FromSeconds(Int(freshness, "maxFreshAgeSeconds"));
        var observed = Instant(freshness, "observedAtUtc");
        Assert.IsTrue(observed - Instant(manifest.RootElement, "retrievedUtc") < cutoff);
        foreach (var result in Items(freshness, "expected"))
        {
            var series = Series(payload.RootElement, Str(result, "spkId")!);
            var query = Instant(freshness, "queryUtc");
            Assert.IsTrue(query >= Instant(series, "validFromUtc") && query <= Instant(series, "validUntilUtc"));
            var stale = observed - Instant(series, "seriesRetrievedUtc") > cutoff;
            Assert.AreEqual(stale ? "Stale" : "Fresh", Str(result, "freshness"));
            Assert.AreEqual(stale ? "Withheld" : "Eligible", Str(result, "labelDisposition"));
            if (stale)
                Assert.AreEqual("StaleSnapshot", Str(result, "reasonCode"));
        }

        var validity = vectors.GetProperty("validity");
        var target = Series(payload.RootElement, Str(validity, "spkId")!);
        var start = Instant(target, "validFromUtc");
        var end = Instant(target, "validUntilUtc");
        foreach (var result in Items(validity, "cases"))
        {
            var query = Instant(result, "queryUtc");
            var now = Instant(result, "nowUtc");
            var reason = now > end ? "Expired" : query < start || query > end ? "OutOfRange" : null;
            Assert.AreEqual(reason, Str(result, "reasonCode"));
            if (reason != null)
            {
                Assert.IsTrue(query >= Instant(target, "paddedStartUtc") && query <= Instant(target, "paddedEndUtc"));
                Assert.AreEqual(JsonValueKind.Null, result.GetProperty("position").ValueKind);
            }
        }

        var unverified = vectors.GetProperty("unverified");
        using var unsupported = Open(Str(unverified, "seriesPath")!);
        var item = unsupported.RootElement;
        ValidateSeries(item);
        Assert.AreEqual("Unverified", Str(item, "interpolationVerificationMethod"));
        Assert.IsFalse(item.GetProperty("interpolationBoundVerified").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, item.GetProperty("maxVerifiedMidpointResidualArcsec").ValueKind);
        CollectionAssert.AreEqual(SmallBodyFixtureCanonicalJson.Encode(Series(original.RootElement, CometSpkId),
                "recordSha256", "interpolationVerificationMethod", "interpolationBoundVerified", "maxVerifiedMidpointResidualArcsec"),
            SmallBodyFixtureCanonicalJson.Encode(item,
                "recordSha256", "interpolationVerificationMethod", "interpolationBoundVerified", "maxVerifiedMidpointResidualArcsec"));
        Assert.IsTrue(Instant(unverified, "queryUtc") > Instant(item, "validFromUtc") &&
            Instant(unverified, "queryUtc") < Instant(item, "validUntilUtc"));
        AssertWithheld(unverified.GetProperty("expected"), "InterpolationBoundExceeded");

        var integrity = vectors.GetProperty("corruptSha256");
        using var corrupt = Open(Str(integrity, "manifestPath")!);
        var bad = corrupt.RootElement;
        AssertDigest(bad, "manifestSha256");
        CollectionAssert.AreEqual(SmallBodyFixtureCanonicalJson.Encode(manifest.RootElement, "manifestSha256", "normalizedPayloadSha256"),
            SmallBodyFixtureCanonicalJson.Encode(bad, "manifestSha256", "normalizedPayloadSha256"));
        var bytes = Read(Str(integrity, "payloadPath")!);
        Assert.AreEqual(Int(bad, "normalizedPayloadBytes"), bytes.Length);
        var mismatched = Str(bad, "normalizedPayloadSha256")!;
        var correct = Sha256(bytes);
        Assert.IsTrue(mismatched.Length == 64);
        Assert.AreEqual(1, mismatched.Zip(correct).Count(pair => pair.First != pair.Second));
        Assert.AreEqual("IntegrityFailure", Str(integrity.GetProperty("expected"), "reasonCode"));
        Assert.AreEqual(JsonValueKind.Null, integrity.GetProperty("expected").GetProperty("snapshot").ValueKind);
    }

    [TestMethod]
    public void SyntheticTimeInputsVerifyMissingEopLeapCrossingAndPrunedReplay()
    {
        using var cases = Open("evaluation/cases.json");
        using var exactManifest = Open("snapshots/manifest-v1.json");
        using var activeManifest = Open("snapshots/manifest-v2.json");
        using var exactPayload = Open("snapshots/payload-v1.json");
        var vectors = cases.RootElement.GetProperty("timeData");
        var leapFixture = vectors.GetProperty("leapSeconds");
        var leaps = EmbeddedUtf8(leapFixture);
        var bundles = Items(vectors, "eops").ToDictionary(input => Str(input, "id")!,
            input => OrbitalTimeData.FromFinals2000A(EmbeddedUtf8(input), leaps), StringComparer.Ordinal);

        var missing = vectors.GetProperty("missingEop");
        var result = Assert.ThrowsExactly<ArgumentException>(() =>
            bundles[Str(missing, "availableEopId")!].Evaluate(Instant(missing, "queryUtc")));
        Assert.AreEqual("eop-outside-coverage", result.Message);
        var fallback = Items(missing, "cases").Single(item => Str(item, "use") == "Planning").GetProperty("expected");
        Assert.AreEqual("ut1-approximated-by-utc-v1", Str(fallback, "basis"));
        Assert.IsNull(Str(fallback, "earthOrientationSha256"));
        Assert.IsTrue(fallback.GetProperty("degraded").GetBoolean());
        Assert.AreEqual(15, Int(fallback, "timingBasisUncertaintyArcsec"));
        var measured = Items(missing, "cases").Single(item => Str(item, "use") == "MeasuredAssociation").GetProperty("expected");
        Assert.IsFalse(measured.GetProperty("fallbackAllowed").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, measured.GetProperty("position").ValueKind);

        var crossing = vectors.GetProperty("leapCrossing");
        var before = Instant(crossing, "startUtc");
        var after = Instant(crossing, "endUtc");
        var leap = OrbitalTimeData.CreateNominalZeroDiagnostic(leaps);
        Assert.AreEqual(Int(crossing, "taiMinusUtcBeforeSeconds"), leap.Evaluate(before).TaiMinusUtcSeconds);
        Assert.AreEqual(Int(crossing, "taiMinusUtcAfterSeconds"), leap.Evaluate(after).TaiMinusUtcSeconds);
        Assert.AreEqual("2026-11-30T23:59:60Z", Str(crossing, "leapSecondUtc"));
        var unsupported = Assert.ThrowsExactly<ArgumentException>(() =>
            leap.ValidatePropagationInterval(before, new(before, after)));
        Assert.AreEqual("unsupported-leap-crossing", unsupported.Message);
        AssertWithheld(crossing.GetProperty("expected"), "UnsupportedLeapCrossing");

        var replay = vectors.GetProperty("replay");
        var artifact = replay.GetProperty("artifact");
        var expected = replay.GetProperty("expected");
        var exactAsteroid = Series(exactPayload.RootElement, AsteroidSpkId);
        Assert.AreEqual(Str(exactAsteroid, "recordSha256"), Str(artifact, "recordSha256"));
        Assert.AreEqual(Str(exactManifest.RootElement, "manifestSha256"), Str(artifact, "manifestSha256"));
        Assert.AreNotEqual(Str(activeManifest.RootElement, "manifestSha256"), Str(artifact, "manifestSha256"));
        Assert.AreEqual(Str(exactPayload.RootElement.GetProperty("observerBinding"), "observerBindingSha256"),
            Str(artifact, "observerBindingSha256"));
        var pinnedBundle = bundles[Str(artifact, "timeDataBundleId")!];
        Assert.AreEqual(pinnedBundle.EarthOrientationSha256, Str(artifact, "earthOrientationSha256"));
        Assert.AreEqual(pinnedBundle.LeapSecondsSha256, Str(artifact, "leapSecondsSha256"));
        var query = Instant(replay, "queryUtc");
        Assert.AreEqual(pinnedBundle.Evaluate(query).Status.ToString(), Str(artifact, "earthOrientationStatus"));

        var available = StringArray(replay, "availableEopIds").Select(id => bundles[id!]).ToArray();
        Assert.IsFalse(available.Any(bundle => bundle.EarthOrientationSha256 == pinnedBundle.EarthOrientationSha256));
        Assert.IsTrue(expected.GetProperty("inspectRetainedOutputs").GetBoolean());
        Assert.AreEqual(12.75, Num(artifact.GetProperty("geometricDirection"), "altitudeDegrees"));
        Assert.AreEqual(310.25, Num(artifact.GetProperty("geometricDirection"), "azimuthDegrees"));
        Assert.AreEqual(12.82, Num(artifact.GetProperty("apparentDirection"), "altitudeDegrees"));
        Assert.AreEqual(32.5, Num(artifact.GetProperty("pixel"), "x"));
        Assert.AreEqual(41.25, Num(artifact.GetProperty("pixel"), "y"));
        Assert.AreEqual("TimeDataUnavailable", Str(expected, "exactRecomputeReasonCode"));
        Assert.AreEqual(JsonValueKind.Null, expected.GetProperty("exactRecomputedPosition").ValueKind);

        var explicitEvaluation = expected.GetProperty("explicitReevaluation");
        var replacement = bundles[Str(explicitEvaluation, "timeDataBundleId")!];
        Assert.IsTrue(available.Contains(replacement));
        Assert.AreEqual(Str(artifact, "manifestSha256"), Str(explicitEvaluation, "manifestSha256"));
        Assert.AreEqual(replacement.EarthOrientationSha256, Str(explicitEvaluation, "earthOrientationSha256"));
        Assert.AreEqual(replacement.LeapSecondsSha256, Str(explicitEvaluation, "leapSecondsSha256"));
        Assert.AreNotEqual(pinnedBundle.Evaluate(query).Dut1Seconds, replacement.Evaluate(query).Dut1Seconds);
    }

    [TestMethod]
    public void HorizonFixtureSeparatesObstructionUnknownTerrainAndSensorClipping()
    {
        using var document = Open("evaluation/horizon-clipping.json");
        var vectors = document.RootElement;
        var site = vectors.GetProperty("observerBinding");
        AssertDigest(site, "observerBindingSha256");
        var observer = new ObserverLocation(Num(site, "latitudeDegrees"), Num(site, "longitudeDegrees"), Num(site, "elevationMeters"));
        var rigInputs = vectors.GetProperty("rig");
        var size = Int(rigInputs, "sensorSizePixels");
        var rig = new CameraRigConfig(
            new("synthetic", size, size, 2, SensorColorMode.Mono, CameraPixelFormat.Mono16),
            new(Str(rigInputs, "projection")!, 0, Num(rigInputs, "fieldOfViewDegrees"), 0, LensKind.Fisheye),
            new(Num(rigInputs, "boresightAltitudeDegrees"), Num(rigInputs, "boresightAzimuthDegrees"), 0),
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1));
        var horizon = Items(vectors, "profileSamples").Select(sample =>
            new HorizonObstructionSample(Num(sample, "azimuthDegrees"), sample.GetProperty("elevationDegrees").ValueKind == JsonValueKind.Null ?
                null : Num(sample, "elevationDegrees"))).ToArray();
        var profile = new HorizonObstructionProfile(HorizonObstructionProfile.CurrentSchemaVersion, "synthetic-profile", 1, "synthetic-installation",
            HorizonObstructionProfile.ComputeSiteIdentitySha256(observer.LatitudeDegrees, observer.LongitudeDegrees, observer.ElevationMeters),
            HorizonObstructionProfile.ComputeGeometryIdentitySha256(rig), HorizonObstructionSource.Synthetic,
            Num(vectors, "maximumInterpolationGapDegrees"), horizon);
        var evaluator = new HorizonObstructionEvaluator(profile, rig, observer);
        Assert.IsTrue(evaluator.IsCompatible);
        var projector = ProjectorFactory.Create(evaluator.Projection);

        List<List<int>> visibleSegments = [];
        AltAzPoint? previous = null;
        var samples = Items(vectors, "samples");
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = samples[i];
            var direction = new AltAzPoint(Num(sample, "geometricAltitudeDegrees"), Num(sample, "azimuthDegrees"));
            var status = evaluator.Evaluate(direction);
            var reason = status switch
            {
                HorizonObstructionEligibility.Obstructed when direction.AltitudeDegrees < 0 => "BelowHorizon",
                HorizonObstructionEligibility.Obstructed => "HorizonObstructed",
                HorizonObstructionEligibility.Unknown => "UnknownTerrain",
                HorizonObstructionEligibility.Clear when projector.Project(direction) == null => "OutsideSensorBounds",
                HorizonObstructionEligibility.Clear => null,
                _ => throw new InvalidOperationException($"Unexpected horizon eligibility {status}.")
            };
            Assert.AreEqual(Str(sample, "reasonCode"), reason, $"Sample {i}");
            if (sample.TryGetProperty("refractedDisplayAltitudeDegrees", out var refracted))
            {
                var displayAltitude = AtmosphericRefraction.Apply(direction.AltitudeDegrees, new(true));
                Assert.AreEqual(refracted.GetDouble(), displayAltitude, 1e-12);
                Assert.AreEqual(HorizonObstructionEligibility.Obstructed, status);
                Assert.AreEqual(HorizonObstructionEligibility.Clear,
                    evaluator.Evaluate(direction with { AltitudeDegrees = displayAltitude }));
            }

            if (reason != null)
            {
                previous = null;
                continue;
            }
            if (previous is not { } from || !evaluator.IsSegmentClear(from, direction))
                visibleSegments.Add([]);
            visibleSegments[^1].Add(i);
            previous = direction;
        }
        var expected = Items(vectors, "expectedVisibleSegments");
        Assert.HasCount(expected.Length, visibleSegments);
        for (var i = 0; i < expected.Length; i++)
            CollectionAssert.AreEqual(expected[i].EnumerateArray().Select(value => value.GetInt32()).ToArray(), visibleSegments[i]);
    }

    private static void AssertWithheld(JsonElement expected, string reason)
    {
        Assert.AreEqual(reason, Str(expected, "reasonCode"));
        Assert.AreEqual(JsonValueKind.Null, expected.GetProperty("position").ValueKind);
    }

    private static byte[] EmbeddedUtf8(JsonElement input)
    {
        var bytes = SmallBodyFixtureCanonicalJson.Utf8.GetBytes(Str(input, "bytes")!);
        Assert.AreEqual(Str(input, "sha256"), Sha256(bytes));
        return bytes;
    }
}
