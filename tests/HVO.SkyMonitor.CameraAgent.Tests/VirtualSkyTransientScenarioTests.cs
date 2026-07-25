using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class VirtualSkyTransientScenarioTests
{
    private static readonly DateTimeOffset FixtureUtc = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions FixtureSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly Dictionary<CameraPixelFormat, (string First, string Later)> ExpectedChecksums =
        new Dictionary<CameraPixelFormat, (string First, string Later)>
        {
            [CameraPixelFormat.Mono16] = (
                "F703A678B0F2D6C4133D216E1AED8F06DD57E287A6A06F4DC17DC890788AF77A",
                "FFC5B7A47D26EBE277FCC8F6C23389615EEB31845459E499CB7A4390B544DCE4"),
            [CameraPixelFormat.Rgb24] = (
                "3BD387EDC357C1C5B19129EA8DAB1329103D620DE20794D8600C1ABACDD9D22A",
                "1035AB2C2CFA287BDC71D8F14F591027F0B6A4026517A7F6018BF74C44AB1265"),
            [CameraPixelFormat.BayerRggb16] = (
                "34DA48AE39F31F5F1549E5BA86654E48F973295B75992A48989C40752DA6E8C3",
                "B5E0939AF4B4122FFCA4C892DC9AC492179B65D01483589CB141893DB323813A")
        };
    private const string FixtureFileName = "transient-scenarios-v1.json";
    private const string OracleFileName = "transient-detection-oracle-v1.json";
    private static readonly string[] ExpectedSupplementalCoverage =
        ["stable-cloud", "persistent-star-residual", "persistent-mask-kinds"];

    [TestMethod]
    public async Task FixtureMatrix_HasStableOpaqueRawEvidence()
    {
        var (manifestBytes, manifest) = await LoadManifestAsync().ConfigureAwait(false);
        Assert.AreEqual("virtual-transient-fixture-v1", manifest.SchemaVersion);
        var actualByCase = new Dictionary<string, IReadOnlyList<RawEvidence>>(StringComparer.Ordinal);
        foreach (var scenario in manifest.Cases)
        {
            var module = Module();
            await module.InitializeAsync(
                Config(CameraPixelFormat.Mono16, scenario.Definition), CancellationToken.None).ConfigureAwait(false);
            var captures = new List<RawEvidence>();
            foreach (var offset in scenario.RawCaptureOffsetsSeconds)
            {
                var capture = await module.CaptureAsync(
                    new CaptureRequest(
                        manifest.Utc.AddSeconds(offset),
                        TimeSpan.FromSeconds(5),
                        CaptureMode.Still,
                        new CaptureSetpoint(TimeSpan.FromSeconds(manifest.ExposureSeconds), 1, null, null)),
                    CancellationToken.None).ConfigureAwait(false);
                var checksum = Convert.ToHexString(SHA256.HashData(capture.Frame!.PixelData.Span));
                var statistics = RawStatistics(capture.Frame.PixelData.Span);
                captures.Add(new RawEvidence(offset, statistics.Minimum, statistics.Maximum, statistics.Mean, checksum));
                TestContext.WriteLine(
                    $"{scenario.Id}@{offset:R}: min={statistics.Minimum}, max={statistics.Maximum}, " +
                    $"mean={statistics.Mean:R}, sha256={checksum}");
            }
            actualByCase.Add(scenario.Id, captures);
        }

        var oracle = await LoadOracleAsync().ConfigureAwait(false);
        Assert.AreEqual("virtual-transient-detection-oracle-v1", oracle.SchemaVersion);
        Assert.IsTrue(oracle.VirtualOnly);
        Assert.AreEqual(CaptureContractJson.ComputeCanonicalJsonSha256(manifest), oracle.InputManifestSha256);
        var manifestJson = Encoding.UTF8.GetString(manifestBytes);
        Assert.IsFalse(manifestJson.Contains(oracle.OracleSentinel, StringComparison.Ordinal));
        CollectionAssert.AreEquivalent(
            manifest.Cases.Select(static value => value.Id).ToArray(),
            oracle.Cases.Select(static value => value.Id).ToArray());
        foreach (var expected in oracle.Cases)
        {
            var scenario = manifest.Cases.Single(value => value.Id == expected.Id);
            var detectorConfiguration = JsonSerializer.Serialize(scenario.Definition);
            Assert.IsFalse(detectorConfiguration.Contains(oracle.OracleSentinel, StringComparison.Ordinal));
            Assert.IsFalse(detectorConfiguration.Contains(expected.Label, StringComparison.OrdinalIgnoreCase));
            var actual = actualByCase[expected.Id];
            Assert.HasCount(expected.Raw.Count, actual, expected.Id);
            for (var index = 0; index < expected.Raw.Count; index++)
            {
                Assert.AreEqual(expected.Raw[index].OffsetSeconds, actual[index].OffsetSeconds, 1e-12, expected.Id);
                Assert.AreEqual(expected.Raw[index].MinimumAdu, actual[index].MinimumAdu, expected.Id);
                Assert.AreEqual(expected.Raw[index].MaximumAdu, actual[index].MaximumAdu, expected.Id);
                Assert.AreEqual(expected.Raw[index].MeanAdu, actual[index].MeanAdu, 1e-12, expected.Id);
                Assert.AreEqual(expected.Raw[index].Sha256, actual[index].Sha256, expected.Id);
            }
        }
    }

    [TestMethod]
    public async Task FixtureMatrix_ProducesMeasuredResidualGeometryWithoutOracleInputs()
    {
        var (_, manifest) = await LoadManifestAsync().ConfigureAwait(false);
        var options = new TransientCandidateExtractionOptionsV1(
            MinimumResidualAdu: 1,
            MinimumComponentPixels: 2,
            MinimumIntegratedSignalAdu: 2,
            MaximumCandidates: 32,
            ProfileSampleCount: 8,
            MaximumSaturationBridgePixels: 256,
            MaximumForegroundPixels: 100_000,
            MaximumFragmentGapPixels: 3,
            MinimumFragmentAlignmentCosine: 0.85);
        var actualCases = new List<ScenarioEvidence>();
        foreach (var scenario in manifest.Cases)
        {
            var module = Module();
            var config = Config(CameraPixelFormat.Mono16, scenario.Definition);
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var offsets = scenario.AssessmentOffsetsSeconds.Append(scenario.CaptureOffsetSeconds).Distinct().ToArray();
            var extractions = new Dictionary<double, ScenarioExtraction>();
            foreach (var offset in offsets)
            {
                var extraction = await ExtractAtAsync(module, config, manifest, scenario.Id, offset, options)
                    .ConfigureAwait(false);
                extractions.Add(offset, extraction);
                TestContext.WriteLine(
                    $"{scenario.Id}@{offset:R}: components={extraction.Candidates.Count}; " +
                    string.Join(';', extraction.Candidates.Select(static candidate =>
                        $"length={candidate.Features!.LengthPixels:F2},width={candidate.Features.MeanWidthPixels:F2}," +
                        $"signal={candidate.Features.IntegratedSignalAdu},sat={candidate.Features.SaturatedSampleCount}," +
                        $"fragments={candidate.Features.FragmentCount}")));
            }
            var primary = extractions[scenario.CaptureOffsetSeconds];
            var geometryIdentities = offsets.ToDictionary(
                static offset => offset,
                offset => CaptureContractJson.ComputeCanonicalJsonSha256(extractions[offset].Candidates.Select(
                    static candidate => new { candidate.Geometry, candidate.Features })));
            var extractionReceiptIdentities = offsets.ToDictionary(
                static offset => offset,
                offset => Convert.ToHexString(SHA256.HashData(
                    TransientCandidateExtractionJson.Serialize(extractions[offset].Descriptor))));
            if (scenario.AssessmentOffsetsSeconds.Count == 0)
            {
                actualCases.Add(new ScenarioEvidence(
                    scenario.Id,
                    primary.Candidates.Count,
                    geometryIdentities,
                    extractionReceiptIdentities,
                    extractions.ToDictionary(
                        static value => value.Key,
                        static value => value.Value.Candidates.Select(candidate => candidate.CandidateId).ToArray()),
                    extractions.ToDictionary(
                        static value => value.Key,
                        static value => value.Value.DetectorBoundaryJson),
                    null,
                    null,
                    null,
                    null));
                continue;
            }
            var assessmentCandidates = scenario.AssessmentOffsetsSeconds.Select(offset =>
            {
                var extraction = extractions[offset];
                Assert.IsNotEmpty(extraction.Candidates, $"{scenario.Id}@{offset:R}");
                return (Extraction: extraction, Candidate: extraction.Candidates
                    .OrderByDescending(static candidate => candidate.Features!.LengthPixels)
                    .First());
            }).ToArray();
            var observations = assessmentCandidates.Select((value, ordinal) =>
                TransientObservationFactory.CreateAssessmentObservation(new TransientObservationPromotionRequest(
                    value.Candidate.CandidateId,
                    FixtureGuid(
                        "observation",
                        scenario.Id,
                        scenario.AssessmentOffsetsSeconds[ordinal].ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
                    ordinal,
                    value.Extraction.Descriptor))).ToArray();
            var eventId = observations[0].EventId;
            Assert.IsTrue(observations.All(observation => observation.EventId == eventId), scenario.Id);
            var assessment = TransientAssessmentFactory.Create(new TransientAssessmentExecutionRequest(
                eventId,
                FixtureGuid("assessment", scenario.Id),
                manifest.Utc.AddMinutes(10),
                TransientAssessmentAuthority.Provisional,
                observations,
                AssessmentOptions(),
                []));
            Assert.AreEqual(TransientAssessmentExecutionStatus.Produced, assessment.Status, assessment.ReasonCode);
            actualCases.Add(new ScenarioEvidence(
                scenario.Id,
                primary.Candidates.Count,
                geometryIdentities,
                extractionReceiptIdentities,
                extractions.ToDictionary(
                    static value => value.Key,
                    static value => value.Value.Candidates.Select(candidate => candidate.CandidateId).ToArray()),
                extractions.ToDictionary(
                    static value => value.Key,
                    static value => value.Value.DetectorBoundaryJson),
                eventId,
                assessment.Descriptor!.Assessment.Classification.ToString(),
                assessment.Descriptor.Assessment.MeteorSeverity?.ToString(),
                Convert.ToHexString(SHA256.HashData(TransientAssessmentJson.Serialize(assessment.Descriptor)))));
        }
        var cloudConfig = Config(CameraPixelFormat.Mono16, null, cloud: StableCloud());
        var cloudModule = Module();
        await cloudModule.InitializeAsync(cloudConfig, CancellationToken.None).ConfigureAwait(false);
        var cloudResult = await ExtractAtAsync(
            cloudModule,
            cloudConfig,
            manifest,
            "stable-cloud",
            0,
            options).ConfigureAwait(false);
        Assert.IsEmpty(cloudResult.Candidates, "stable-cloud");
        var cloudFrame = await CaptureAtAsync(cloudModule, manifest, 0).ConfigureAwait(false);
        var cloudRawStatistics = RawStatistics(cloudFrame.PixelData.Span);
        var cloudRaw = new RawEvidence(
            0,
            cloudRawStatistics.Minimum,
            cloudRawStatistics.Maximum,
            cloudRawStatistics.Mean,
            Convert.ToHexString(SHA256.HashData(cloudFrame.PixelData.Span)));
        var cloudGeometryIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(cloudResult.Candidates.Select(
            static candidate => new { candidate.Geometry, candidate.Features }));
        var cloudExtractionIdentity = Convert.ToHexString(SHA256.HashData(
            TransientCandidateExtractionJson.Serialize(cloudResult.Descriptor)));

        var oracle = await LoadOracleAsync().ConfigureAwait(false);
        Assert.IsTrue(oracle.VirtualOnly);
        var manifestIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(manifest);
        TestContext.WriteLine($"input-manifest-identity={manifestIdentity}");
        Assert.AreEqual(manifestIdentity, oracle.InputManifestSha256);
        CollectionAssert.AreEquivalent(
            ExpectedSupplementalCoverage,
            oracle.SupplementalCoverage.Select(static value => value.Id).ToArray());
        var cloudExpectation = oracle.SupplementalCoverage.Single(static value => value.Id == "stable-cloud");
        Assert.AreEqual("TrueNegative", cloudExpectation.ExpectedDisposition);
        Assert.AreEqual(cloudExpectation.ExpectedCandidateCount, cloudResult.Candidates.Count);
        TestContext.WriteLine("cloud-evidence=" + JsonSerializer.Serialize(new
        {
            Raw = cloudRaw,
            GeometryFeaturesIdentitySha256 = cloudGeometryIdentity,
            ExtractionReceiptSha256 = cloudExtractionIdentity
        }));
        Assert.IsNotNull(cloudExpectation.Raw);
        Assert.IsNotNull(cloudExpectation.GeometryFeaturesIdentitySha256);
        Assert.IsNotNull(cloudExpectation.ExtractionReceiptSha256);
        Assert.AreEqual(cloudExpectation.Raw, cloudRaw);
        Assert.AreEqual(cloudExpectation.GeometryFeaturesIdentitySha256, cloudGeometryIdentity);
        Assert.AreEqual(cloudExpectation.ExtractionReceiptSha256, cloudExtractionIdentity);
        var starExpectation = oracle.SupplementalCoverage.Single(static value => value.Id == "persistent-star-residual");
        Assert.AreEqual("StarResidual", starExpectation.Category);
        Assert.AreEqual("SuppressedByPersistentMask", starExpectation.ExpectedDisposition);
        Assert.AreEqual(0, starExpectation.ExpectedCandidateCount);
        Assert.AreEqual(
            "TransientStarMaskStrategyTests.W1W2PersistentProjectedStarMaskEvidence",
            starExpectation.Evidence);
        var maskExpectation = oracle.SupplementalCoverage.Single(static value => value.Id == "persistent-mask-kinds");
        Assert.AreEqual("Masks", maskExpectation.Category);
        Assert.AreEqual("SuppressedByPersistentMask", maskExpectation.ExpectedDisposition);
        Assert.AreEqual(0, maskExpectation.ExpectedCandidateCount);
        Assert.AreEqual(
            "TransientCandidateExtractionTests.CausalExtractionIsProvisionalAndNoEventReturnsAuditableNoCandidate",
            maskExpectation.Evidence);
        var forbiddenOracleValues = oracle.Cases
            .Where(static value => value.Label != "None")
            .Select(static value => value.Label)
            .Append(oracle.OracleSentinel)
            .ToArray();
        foreach (var actual in actualCases)
        {
            foreach (var detectorBoundary in actual.DetectorBoundaryJsonByOffset.Values)
            {
                foreach (var forbidden in forbiddenOracleValues)
                {
                    Assert.IsFalse(
                        detectorBoundary.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                        $"{actual.Id} detector boundary exposed {forbidden}.");
                }
            }
        }
        TestContext.WriteLine("matrix-evidence=" + JsonSerializer.Serialize(actualCases));
        var confusion = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var expected in oracle.Cases)
        {
            var actual = actualCases.Single(value => value.Id == expected.Id);
            Assert.AreEqual(expected.CandidateCount, actual.CandidateCount, expected.Id);
            AssertOffsetMap(expected.GeometryFeaturesIdentitySha256s, actual.GeometryFeaturesIdentities, expected.Id);
            AssertOffsetMap(expected.ExtractionReceiptSha256s, actual.ExtractionReceiptIdentities, expected.Id);
            AssertCandidateMap(expected.CandidateIdsByOffset, actual.CandidateIdsByOffset, expected.Id);
            Assert.AreEqual(expected.Classification, actual.Classification, expected.Id);
            Assert.AreEqual(expected.MeteorSeverity, actual.MeteorSeverity, expected.Id);
            Assert.AreEqual(expected.EventId, actual.EventId, expected.Id);
            Assert.AreEqual(expected.AssessmentReceiptSha256, actual.AssessmentReceiptSha256, expected.Id);
            var actualLabel = actual.Classification ?? "None";
            var key = $"{expected.Label}/{actualLabel}";
            confusion[key] = confusion.GetValueOrDefault(key) + 1;
            var disposition = string.Equals(expected.Label, actualLabel, StringComparison.Ordinal)
                || expected.Label is "Fireball" or "BoundaryMeteor" && actualLabel == "Meteor"
                    ? expected.CandidateCount == 0 ? "TrueNegative" : "Scored"
                    : actual.CandidateCount == 0 ? "Miss" : "Mismatch";
            Assert.AreEqual(expected.ScoringDisposition, disposition, expected.Id);
        }
        Assert.AreEqual(7, confusion.Values.Sum());
        TestContext.WriteLine("virtual-confusion=" + JsonSerializer.Serialize(confusion) +
            "; stable-cloud=None/None; limitations=virtual deterministic matrix, no physical sensitivity claim");
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16, true)]
    [DataRow(CameraPixelFormat.Rgb24, false)]
    [DataRow(CameraPixelFormat.BayerRggb16, false)]
    public async Task CaptureAsync_TransientScenarioRunsThroughEveryRawFormat(
        CameraPixelFormat format,
        bool physicalMono)
    {
        var definition = Definition();
        var config = Config(format, definition, physicalMono);
        var firstModule = Module();
        var repeatedModule = Module();
        await firstModule.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        await repeatedModule.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var request = new CaptureRequest(
            FixtureUtc,
            TimeSpan.FromSeconds(5),
            CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(1), physicalMono || format == CameraPixelFormat.BayerRggb16 ? 100 : 1, null, null));

        var first = await firstModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var repeated = await repeatedModule.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        var later = await repeatedModule.CaptureAsync(
            request with { RequestedStartUtc = FixtureUtc.AddSeconds(1) }, CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreEqual(first.Frame!.PixelData.ToArray(), repeated.Frame!.PixelData.ToArray());
        CollectionAssert.AreNotEqual(first.Frame.PixelData.ToArray(), later.Frame!.PixelData.ToArray());
        Assert.AreEqual(format, first.Frame.PixelFormat);
        var provenance = first.Frame.Metadata.Scene!.TransientScenario!;
        var canonical = definition with { ScenarioId = definition.ComputeCanonicalScenarioId() };
        Assert.AreEqual(canonical.ScenarioId, provenance.ScenarioId);
        Assert.AreEqual(canonical.ComputeParametersSha256(), provenance.ParametersSha256);
        Assert.AreEqual(FixtureUtc, provenance.IntegrationStartUtc);
        Assert.AreEqual(FixtureUtc.AddSeconds(1), provenance.IntegrationEndUtc);
        Assert.AreEqual(1, provenance.SkyPrimitiveCount);
        Assert.AreEqual(1, provenance.SensorPrimitiveCount);
        Assert.Contains(VirtualTransientScenarioDefinition.CurrentAlgorithmVersion,
            first.Frame.Metadata.Extra!["renderAlgorithm"]);
        Assert.IsFalse(provenance.Parameters.GetRawText().Contains("meteor", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(provenance.Parameters.GetRawText().Contains("aircraft", StringComparison.OrdinalIgnoreCase));
        var firstChecksum = Convert.ToHexString(SHA256.HashData(first.Frame.PixelData.Span));
        var laterChecksum = Convert.ToHexString(SHA256.HashData(later.Frame.PixelData.Span));
        Assert.AreEqual(ExpectedChecksums[format].First, firstChecksum);
        Assert.AreEqual(ExpectedChecksums[format].Later, laterChecksum);
        TestContext.WriteLine(
            $"{format}: first={firstChecksum}, later={laterChecksum}");
    }

    [TestMethod]
    public async Task CaptureAsync_PhysicalFramesAreStableAcrossRestartAndCaptureOrder()
    {
        var config = Config(CameraPixelFormat.Mono16, Definition(), physicalMono: true);
        var ordered = Module();
        var reversed = Module();
        await ordered.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        await reversed.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 100, null, null);
        var firstRequest = new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(5), CaptureMode.Still, setpoint);
        var secondRequest = firstRequest with { RequestedStartUtc = FixtureUtc.AddSeconds(1) };

        var orderedFirst = await ordered.CaptureAsync(firstRequest, CancellationToken.None).ConfigureAwait(false);
        var orderedSecond = await ordered.CaptureAsync(secondRequest, CancellationToken.None).ConfigureAwait(false);
        var reversedSecond = await reversed.CaptureAsync(secondRequest, CancellationToken.None).ConfigureAwait(false);
        var reversedFirst = await reversed.CaptureAsync(firstRequest, CancellationToken.None).ConfigureAwait(false);

        CollectionAssert.AreEqual(orderedFirst.Frame!.PixelData.ToArray(), reversedFirst.Frame!.PixelData.ToArray());
        CollectionAssert.AreEqual(orderedSecond.Frame!.PixelData.ToArray(), reversedSecond.Frame!.PixelData.ToArray());
        Assert.AreEqual(
            orderedFirst.Frame.Metadata.Extra!["captureSequence"],
            reversedFirst.Frame.Metadata.Extra!["captureSequence"]);
    }

    [TestMethod]
    public async Task InitializeAsync_RejectsUnknownAndOutOfBoundsTransientConfiguration()
    {
        using var unknown = JsonDocument.Parse("""
            {
              "transientScenario": {
                "schemaVersion": "virtual-transient-scenario-v1",
                "scenarioId": "scenario-61-a",
                "scenarioVersion": "1",
                "seed": 61,
                "epochUtc": "2025-01-15T08:00:00Z",
                "temporalSampleCount": 8,
                "unknown": true,
                "skyTracks": [],
                "sensorTracks": []
              }
            }
            """);
        var unknownConfig = Config(CameraPixelFormat.Mono16, null) with
        {
            Module = new CameraModuleDescriptor("VirtualSky", unknown.RootElement.Clone())
        };
        var invalidConfig = Config(CameraPixelFormat.Mono16, Definition() with
        {
            SensorTracks =
            [
                Definition().SensorTracks[0] with
                {
                    Keyframes = Definition().SensorTracks[0].Keyframes.Select(
                        static keyframe => keyframe with { PixelX = 64 }).ToArray()
                }
            ]
        });

        await Assert.ThrowsAsync<JsonException>(() => Module().InitializeAsync(unknownConfig, CancellationToken.None))
            .ConfigureAwait(false);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Module().InitializeAsync(invalidConfig, CancellationToken.None))
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RecipeAdapter_DoesNotExposeScenarioProvenanceAsDetectorInput()
    {
        var config = Config(CameraPixelFormat.Mono16, Definition());
        var module = Module();
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
        var result = await module.CaptureAsync(
            new CaptureRequest(FixtureUtc, TimeSpan.FromSeconds(5), CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null)),
            CancellationToken.None).ConfigureAwait(false);
        var artifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            config,
            new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, result.Frame!),
            "source");

        var serialized = JsonSerializer.Serialize(artifact);
        Assert.IsFalse(serialized.Contains("transientScenario", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(serialized.Contains(result.Frame!.Metadata.Scene!.TransientScenario!.ScenarioId,
            StringComparison.Ordinal));
    }

    public TestContext TestContext { get; set; } = null!;

    private static VirtualSkyCameraModule Module()
        => new(TimeProvider.System, new InMemoryCelestialCatalog([]), new ProjectedSceneStore());

    private static async Task<CameraFrame> CaptureAtAsync(
        VirtualSkyCameraModule module,
        TransientFixtureManifest manifest,
        double offsetSeconds)
    {
        var capture = await module.CaptureAsync(
            new CaptureRequest(
                manifest.Utc.AddSeconds(offsetSeconds),
                TimeSpan.FromSeconds(5),
                CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(manifest.ExposureSeconds), 1, null, null)),
            CancellationToken.None).ConfigureAwait(false);
        return capture.Frame!;
    }

    private static async Task<ScenarioExtraction> ExtractAtAsync(
        VirtualSkyCameraModule module,
        CameraModuleConfig config,
        TransientFixtureManifest manifest,
        string scenarioId,
        double targetOffset,
        TransientCandidateExtractionOptionsV1 options)
    {
        var positions = new[]
        {
            TransientTemporalPosition.NMinus2,
            TransientTemporalPosition.NMinus1,
            TransientTemporalPosition.N,
            TransientTemporalPosition.NPlus1,
            TransientTemporalPosition.NPlus2
        };
        var window = new Dictionary<TransientTemporalPosition, TransientTemporalSource>();
        var offsetKey = targetOffset.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var sequenceBase = FixtureSequence("sequence", scenarioId, offsetKey);
        foreach (var position in positions)
        {
            var offset = targetOffset + (int)position;
            var frame = await CaptureAtAsync(module, manifest, offset).ConfigureAwait(false);
            window[position] = CreateTemporalSource(
                config, frame, position, scenarioId, offsetKey, sequenceBase + (int)position);
        }
        var target = window[TransientTemporalPosition.N];
        var background = TransientTemporalBackgroundFactory.Create(new TransientTemporalBackgroundRequest(
            TransientTemporalBackgroundKind.CenteredFinal,
            target,
            positions.Where(static position => position != TransientTemporalPosition.N)
                .Select(position => window[position]).ToArray(),
            [],
            TimeSpan.FromSeconds(30)));
        Assert.AreEqual(TransientTemporalBackgroundStatus.Produced, background.Status, background.ReasonCode);
        var byEvidence = window.Values.ToDictionary(static source => source.Input.Descriptor.Source.EvidenceId);
        var orderedSources = background.Product!.Descriptor.Sources.Select(source => byEvidence[source.EvidenceId]).ToArray();
        var identitySlots = Enumerable.Range(1, options.MaximumCandidates).Select(index =>
            new TransientCandidateIdentitySlot(
                FixtureGuid("candidate", scenarioId, offsetKey, index.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                FixtureGuid("event", scenarioId, index.ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToArray();
        var extraction = TransientCandidateExtractionFactory.Create(new TransientCandidateExtractionRequest(
            "virtual-sky-scenario-agent",
            manifest.Utc.AddMinutes(10),
            target,
            background.Product,
            orderedSources,
            identitySlots,
            options,
            CenteredContextConverged: true));
        Assert.IsTrue(
            extraction.Status is TransientCandidateExtractionStatus.Produced or TransientCandidateExtractionStatus.NoCandidate,
            extraction.ReasonCode);
        Assert.IsNotNull(extraction.Descriptor);
        var detectorBoundaryJson = JsonSerializer.Serialize(new
        {
            Target = target.Input.Descriptor,
            Background = background.Product.Descriptor,
            Extraction = extraction.Descriptor,
            extraction.Candidates
        });
        return new ScenarioExtraction(extraction.Descriptor, extraction.Candidates, detectorBoundaryJson);
    }

    private static TransientTemporalSource CreateTemporalSource(
        CameraModuleConfig config,
        CameraFrame frame,
        TransientTemporalPosition position,
        string scenarioId,
        string targetOffsetKey,
        int captureSequence)
    {
        var positionKey = ((int)position).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var artifactId = FixtureGuid("artifact", scenarioId, targetOffsetKey, positionKey);
        var artifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            config,
            new FrameArtifact(artifactId, FrameArtifactRole.Raw, frame),
            "virtual-transient-v1");
        artifact = artifact with
        {
            CaptureSequence = captureSequence,
            Layout = artifact.Layout! with
            {
                BlackLevel = 0,
                WhiteLevel = ushort.MaxValue
            }
        };
        var source = new TransientSourceEvidenceReferenceV1(
            TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
            FixtureGuid("evidence", scenarioId, targetOffsetKey, positionKey),
            new TransientWholeArtifactLocatorV1(
                TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                TransientSourceLocatorKind.WholeArtifact,
                new TransientArtifactReferenceV1(
                    artifact.ArtifactId,
                    artifact.Role,
                    artifact.Variant,
                    artifact.RecipeIdentitySha256,
                    Convert.ToHexString(SHA256.HashData(artifact.Payload.Span)))),
            artifact.ObservationStartedUtc!.Value,
            artifact.ObservationEndedUtc!.Value,
            TransientTimingQuality.Reported,
            new TransientTimingProvenanceV1("virtual-sky", "v1"));
        var input = TransientDetectorInputFactory.Create(
            artifact,
            source,
            new TransientLinearLevelsV1(
                0,
                ushort.MaxValue,
                ushort.MaxValue));
        Assert.IsTrue(input.Validation.IsValid, input.Validation.ReasonCode);
        var masks = new[]
        {
            TransientDetectorMaskKind.Sky,
            TransientDetectorMaskKind.ImageCircle,
            TransientDetectorMaskKind.Horizon,
            TransientDetectorMaskKind.Obstruction,
            TransientDetectorMaskKind.BadPixel,
            TransientDetectorMaskKind.Star
        }.Select(kind => TransientDetectorMask.Create(
            kind,
            new ProcessingAlgorithmIdentity($"virtual-{kind}-mask", "v1"),
            Linear16MaskOperations.Empty(input.Input!.Descriptor.Layout.Width, input.Input.Descriptor.Layout.Height))).ToArray();
        return new TransientTemporalSource(
            position,
            captureSequence,
            input.Input!,
            new TransientSensitivityV1("virtual-response-v1", 1, 1),
            masks);
    }

    private static TransientDeterministicAssessmentOptionsV1 AssessmentOptions()
        => new(
            MinimumMeteorLengthPixels: 2.5,
            MinimumMeteorElongation: 1.3,
            MaximumMeteorMeanWidthPixels: 10,
            CompactSensorMaximumLengthPixels: 2.5,
            SensorArtifactMaximumMeanWidthPixels: 1.8,
            StationaryMaximumDisplacementPixels: 0.5,
            EnvironmentalMinimumMeanWidthPixels: 12,
            FireballMinimumIntegratedSignalAdu: 100_000,
            FlareMinimumPeakToEndpointRatio: 3,
            PersistentTrackMinimumObservations: 3,
            AircraftMinimumBrightnessRatio: 3,
            AircraftMinimumIntegratedSignalAdu: 1_000,
            SmoothMotionMaximumTurnDegrees: 180,
            SmoothMotionMaximumStepRatio: 10);

    private static CameraModuleConfig Config(
        CameraPixelFormat format,
        VirtualTransientScenarioDefinition? definition,
        bool physicalMono = false,
        VirtualCloudScenarioDefinition? cloud = null)
    {
        var options = JsonSerializer.SerializeToElement(new
        {
            seed = 2025,
            magnitudeZeroElectronsPerSecond = 1000d,
            backgroundElectronsPerSecond = 1d,
            psfSigmaPixels = 1d,
            psfRadiusPixels = 4d,
            vignettingStrength = 0.1,
            bias = 0d,
            readNoiseStandardDeviation = 0d,
            shotNoiseEnabled = false,
            transientScenario = definition,
            cloudScenario = cloud,
            asi174Sensor = new { enabled = physicalMono, blackLevelAdu = 64d },
            asi178Sensor = new { enabled = format == CameraPixelFormat.BayerRggb16, blackLevelContainerAdu = 64d }
        });
        var color = format == CameraPixelFormat.Mono16 ? SensorColorMode.Mono : SensorColorMode.Color;
        var response = format switch
        {
            CameraPixelFormat.Mono16 => SensorResponseMode.Monochrome,
            CameraPixelFormat.Rgb24 => SensorResponseMode.RenderedRgb,
            CameraPixelFormat.BayerRggb16 => SensorResponseMode.BayerRaw,
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        return new CameraModuleConfig(
            new ObservatoryLocation(35.347, -113.878, 0, "America/Phoenix"),
            new CameraModuleDescriptor("VirtualSky", options),
            new CameraRigConfig(
                new SensorProfile(
                    "TransientFixture", 64, 48, 5.86, color, format, response,
                    SensorRecipeVersion: "transient-fixture-v1"),
                new OpticsProfile(
                    "EquidistantFisheye", 0, 180, 0, LensKind.Fisheye,
                    32, 24, 23, CalibrationVersion: "transient-fixture-optics-v1"),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)));
    }

    private static VirtualTransientScenarioDefinition Definition() => new()
    {
        ScenarioId = "scenario-61-a",
        ScenarioVersion = "1",
        Seed = 61,
        EpochUtc = FixtureUtc,
        TemporalSampleCount = 8,
        SkyTracks =
        [
            new VirtualTransientSkyTrack
            {
                PrimitiveId = "p-001",
                Keyframes =
                [
                    new VirtualTransientSkyKeyframe
                    {
                        OffsetSeconds = 0,
                        AltitudeDegrees = 65,
                        AzimuthDegrees = 270,
                        Magnitude = -4,
                        AngularWidthDegrees = 0.25
                    },
                    new VirtualTransientSkyKeyframe
                    {
                        OffsetSeconds = 2,
                        AltitudeDegrees = 65,
                        AzimuthDegrees = 90,
                        Magnitude = -1,
                        AngularWidthDegrees = 0.4
                    }
                ]
            }
        ],
        SensorTracks =
        [
            new VirtualTransientSensorTrack
            {
                PrimitiveId = "s-001",
                Keyframes =
                [
                    new VirtualTransientSensorKeyframe
                    {
                        OffsetSeconds = 0,
                        PixelX = 1.5,
                        PixelY = 1.5,
                        ElectronsPerSecond = 10_000
                    },
                    new VirtualTransientSensorKeyframe
                    {
                        OffsetSeconds = 3,
                        PixelX = 1.5,
                        PixelY = 1.5,
                        ElectronsPerSecond = 10_000
                    }
                ]
            }
        ]
    };

    private static VirtualCloudScenarioDefinition StableCloud() => new()
    {
        ScenarioId = "issue-121-stable-cloud",
        ScenarioVersion = "1",
        Seed = 121,
        EpochUtc = FixtureUtc,
        SpatialFrequency = 3,
        DriftEastCellsPerSecond = 0,
        DriftNorthCellsPerSecond = 0,
        EvolutionCellsPerSecond = 0,
        Octaves = 3,
        EdgeSoftness = 0.5,
        HorizonFadeDegrees = 5,
        TemporalSampleCount = 2,
        Keyframes = [new() { Coverage = 0.55, MaximumOpacity = 0.8, ScatterFraction = 0.1 }]
    };

    private static (ushort Minimum, ushort Maximum, double Mean) RawStatistics(ReadOnlySpan<byte> pixels)
    {
        var minimum = ushort.MaxValue;
        ushort maximum = 0;
        long sum = 0;
        for (var offset = 0; offset < pixels.Length; offset += sizeof(ushort))
        {
            var value = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(pixels[offset..]);
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
            sum += value;
        }
        return (minimum, maximum, sum / (pixels.Length / (double)sizeof(ushort)));
    }

    private static async Task<(byte[] Bytes, TransientFixtureManifest Manifest)> LoadManifestAsync()
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", FixtureFileName))
            .ConfigureAwait(false);
        return (bytes, JsonSerializer.Deserialize<TransientFixtureManifest>(bytes, FixtureSerializerOptions)!);
    }

    private static async Task<TransientDetectionOracle> LoadOracleAsync()
        => JsonSerializer.Deserialize<TransientDetectionOracle>(
            await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", OracleFileName))
                .ConfigureAwait(false),
            FixtureSerializerOptions)!;

    private static Guid FixtureGuid(params string[] parts)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts)));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static int FixtureSequence(params string[] parts)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001f', parts)));
        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(hash) & 0x3fffffff) + 10;
    }

    private static void AssertOffsetMap(
        Dictionary<string, string> expected,
        IReadOnlyDictionary<double, string> actual,
        string scenarioId)
    {
        Assert.HasCount(expected.Count, actual, scenarioId);
        foreach (var pair in expected)
        {
            var offset = double.Parse(pair.Key, System.Globalization.CultureInfo.InvariantCulture);
            Assert.IsTrue(actual.TryGetValue(offset, out var identity), $"{scenarioId}@{pair.Key}");
            Assert.AreEqual(pair.Value, identity, $"{scenarioId}@{pair.Key}");
        }
    }

    private static void AssertCandidateMap(
        Dictionary<string, Guid[]> expected,
        IReadOnlyDictionary<double, Guid[]> actual,
        string scenarioId)
    {
        Assert.HasCount(expected.Count, actual, scenarioId);
        foreach (var pair in expected)
        {
            var offset = double.Parse(pair.Key, System.Globalization.CultureInfo.InvariantCulture);
            Assert.IsTrue(actual.TryGetValue(offset, out var candidateIds), $"{scenarioId}@{pair.Key}");
            CollectionAssert.AreEqual(pair.Value, candidateIds, $"{scenarioId}@{pair.Key}");
        }
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json fixture deserialization.")]
    private sealed record TransientFixtureManifest(
        string SchemaVersion,
        DateTimeOffset Utc,
        double ExposureSeconds,
        IReadOnlyList<TransientFixtureCase> Cases);

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json fixture deserialization.")]
    private sealed record TransientFixtureCase(
        string Id,
        double CaptureOffsetSeconds,
        IReadOnlyList<double> RawCaptureOffsetsSeconds,
        IReadOnlyList<double> AssessmentOffsetsSeconds,
        VirtualTransientScenarioDefinition Definition);

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json fixture deserialization.")]
    private sealed record TransientDetectionOracle(
        string SchemaVersion,
        string OracleSentinel,
        string InputManifestSha256,
        bool VirtualOnly,
        IReadOnlyList<SupplementalOracleCase> SupplementalCoverage,
        IReadOnlyList<TransientOracleCase> Cases);

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json fixture deserialization.")]
    private sealed record SupplementalOracleCase(
        string Id,
        string Category,
        string ExpectedDisposition,
        int ExpectedCandidateCount,
        string Evidence,
        RawEvidence? Raw,
        string? GeometryFeaturesIdentitySha256,
        string? ExtractionReceiptSha256);

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json fixture deserialization.")]
    private sealed record TransientOracleCase(
        string Id,
        string Label,
        IReadOnlyList<RawEvidence> Raw,
        int CandidateCount,
        Dictionary<string, string> GeometryFeaturesIdentitySha256s,
        string? Classification,
        string? MeteorSeverity,
        string ScoringDisposition,
        Dictionary<string, Guid[]> CandidateIdsByOffset,
        Guid? EventId,
        Dictionary<string, string> ExtractionReceiptSha256s,
        string? AssessmentReceiptSha256);

    private sealed record RawEvidence(
        double OffsetSeconds,
        ushort MinimumAdu,
        ushort MaximumAdu,
        double MeanAdu,
        string Sha256);

    private sealed record ScenarioExtraction(
        TransientCandidateExtractionDescriptorV1 Descriptor,
        IReadOnlyList<TransientCandidateV1> Candidates,
        string DetectorBoundaryJson);

    private sealed record ScenarioEvidence(
        string Id,
        int CandidateCount,
        IReadOnlyDictionary<double, string> GeometryFeaturesIdentities,
        IReadOnlyDictionary<double, string> ExtractionReceiptIdentities,
        IReadOnlyDictionary<double, Guid[]> CandidateIdsByOffset,
        IReadOnlyDictionary<double, string> DetectorBoundaryJsonByOffset,
        Guid? EventId,
        string? Classification,
        string? MeteorSeverity,
        string? AssessmentReceiptSha256);
}
