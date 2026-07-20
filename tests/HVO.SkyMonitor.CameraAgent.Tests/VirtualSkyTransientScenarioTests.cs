using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
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
    private static readonly JsonSerializerOptions FixtureSerializerOptions = new(JsonSerializerDefaults.Web);
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
                "32DCB2000A7B01EC85D92C33B2FA70AE1DA74BA6365522282DA8035C080140F6",
                "D9D898DFC347F9C8272C6B31338AD76ED2900E288A3A761FC1C0DCBB55FEFEAB")
        };
    private static readonly Dictionary<string, string> ExpectedComponentIdentities = new(StringComparer.Ordinal)
    {
        ["no-event"] = "4F53CDA18C2BAA0C0354BB5F9A3ECBE5ED12AB4D8E11BA873C2F11161202B945",
        ["short-track"] = "B560555D29A89A08C2F0CDDD3BE4061E677FAB84DB1E701E803EE427195436C8",
        ["fragmented-flare"] = "54F7DB647708134A68908FBFC3FB5CBFCB3BBF02E3526FF35674B4F5C918E43C",
        ["boundary-crossing"] = "963EB61991EC5D053EDCD6278CB82ABC79600728BE8C40D8BD8045093FB74CD5",
        ["long-shadow-track"] = "6548D195C6544BB40262C552E5E5BC1B927D9C67C7E3051EBE6E6A7B96075664",
        ["blinking-track"] = "FD222CC77C931A8ADB6FE90071EB480B427359DDB7FA7F70FD2F148D73B83792",
        ["sensor-artifacts"] = "60DA335E06DC1D8E2B9CD9DB0EA7F89BFF1FB4DC608DFF17FEBB17323B80A1B6"
    };
    private const string BoundarySecondComponentIdentity = "6216EB6FE3C5EDCDE44709EB01EF7B651343A142414ACDE4836AB992BFFDC392";
    private static readonly Dictionary<string, (TransientClassification Classification, TransientMeteorSeverity? Severity)>
        ExpectedAssessments = new(StringComparer.Ordinal)
        {
            ["short-track"] = (TransientClassification.Meteor, TransientMeteorSeverity.Meteor),
            ["fragmented-flare"] = (TransientClassification.Meteor, TransientMeteorSeverity.Fireball),
            ["boundary-crossing"] = (TransientClassification.Meteor, TransientMeteorSeverity.Meteor),
            ["long-shadow-track"] = (TransientClassification.Satellite, null),
            ["blinking-track"] = (TransientClassification.Aircraft, null),
            ["sensor-artifacts"] = (TransientClassification.SensorArtifact, null)
        };

    [TestMethod]
    public async Task FixtureMatrix_HasStableOpaqueRawEvidence()
    {
        var manifest = JsonSerializer.Deserialize<TransientFixtureManifest>(
            await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "transient-scenarios-v1.json"))
                .ConfigureAwait(false),
            FixtureSerializerOptions)!;
        Assert.AreEqual("virtual-transient-fixture-v1", manifest.SchemaVersion);
        for (var scenarioIndex = 0; scenarioIndex < manifest.Cases.Count; scenarioIndex++)
        {
            var scenario = manifest.Cases[scenarioIndex];
            var serializedDefinition = JsonSerializer.Serialize(scenario.Definition);
            Assert.IsFalse(serializedDefinition.Contains(scenario.OracleLabel, StringComparison.OrdinalIgnoreCase));
            var module = Module();
            await module.InitializeAsync(
                Config(CameraPixelFormat.Mono16, scenario.Definition), CancellationToken.None).ConfigureAwait(false);
            var capture = await module.CaptureAsync(
                new CaptureRequest(
                    manifest.Utc.AddSeconds(scenario.CaptureOffsetSeconds),
                    TimeSpan.FromSeconds(5),
                    CaptureMode.Still,
                    new CaptureSetpoint(TimeSpan.FromSeconds(manifest.ExposureSeconds), 1, null, null)),
                CancellationToken.None).ConfigureAwait(false);
            var checksum = Convert.ToHexString(SHA256.HashData(capture.Frame!.PixelData.Span));
            var statistics = RawStatistics(capture.Frame.PixelData.Span);
            var parameters = capture.Frame.Metadata.Scene!.TransientScenario!.Parameters.GetRawText();
            Assert.IsFalse(parameters.Contains(scenario.OracleLabel, StringComparison.OrdinalIgnoreCase));
            TestContext.WriteLine(
                $"{scenario.Id}: min={statistics.Minimum}, max={statistics.Maximum}, " +
                $"mean={statistics.Mean:R}, sha256={checksum}");
            if (scenario.ExpectedMono16Sha256.Length > 0)
            {
                Assert.AreEqual(scenario.ExpectedMono16Sha256, checksum, scenario.Id);
                Assert.AreEqual(scenario.ExpectedRawMinimum, statistics.Minimum, scenario.Id);
                Assert.AreEqual(scenario.ExpectedRawMaximum, statistics.Maximum, scenario.Id);
                Assert.AreEqual(scenario.ExpectedRawMean, statistics.Mean, 1e-12, scenario.Id);
            }
            if (scenario.ExpectedSecondMono16Sha256 is not null)
            {
                var second = await module.CaptureAsync(
                    new CaptureRequest(
                        manifest.Utc.AddSeconds(scenario.CaptureOffsetSeconds + manifest.ExposureSeconds),
                        TimeSpan.FromSeconds(5),
                        CaptureMode.Still,
                        new CaptureSetpoint(TimeSpan.FromSeconds(manifest.ExposureSeconds), 1, null, null)),
                    CancellationToken.None).ConfigureAwait(false);
                var secondChecksum = Convert.ToHexString(SHA256.HashData(second.Frame!.PixelData.Span));
                TestContext.WriteLine($"{scenario.Id}: second-sha256={secondChecksum}");
                if (scenario.ExpectedSecondMono16Sha256.Length > 0)
                {
                    Assert.AreEqual(scenario.ExpectedSecondMono16Sha256, secondChecksum, scenario.Id);
                }
            }
        }
    }

    [TestMethod]
    public async Task FixtureMatrix_ProducesMeasuredResidualGeometryWithoutOracleInputs()
    {
        var manifest = JsonSerializer.Deserialize<TransientFixtureManifest>(
            await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "transient-scenarios-v1.json"))
                .ConfigureAwait(false),
            FixtureSerializerOptions)!;
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
        var confusion = new Dictionary<string, int>(StringComparer.Ordinal);
        var extractedEventCases = 0;
        var falsePositiveCases = 0;
        for (var scenarioIndex = 0; scenarioIndex < manifest.Cases.Count; scenarioIndex++)
        {
            var scenario = manifest.Cases[scenarioIndex];
            var module = Module();
            var config = Config(CameraPixelFormat.Mono16, scenario.Definition);
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var targetOffset = scenario.CaptureOffsetSeconds;
            var result = await ExtractAtAsync(module, config, manifest, scenarioIndex, targetOffset, options)
                .ConfigureAwait(false);
            var componentIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(result.Candidates.Select(
                static candidate => new { candidate.Geometry, candidate.Features }));
            TestContext.WriteLine(
                $"{scenario.Id}: identity={componentIdentity}, components={result.Candidates.Count}; " +
                string.Join(';', result.Candidates.Select(static candidate =>
                    $"length={candidate.Features!.LengthPixels:F2},width={candidate.Features.MeanWidthPixels:F2}," +
                    $"signal={candidate.Features.IntegratedSignalAdu},sat={candidate.Features.SaturatedSampleCount}," +
                    $"fragments={candidate.Features.FragmentCount}")));
            Assert.AreEqual(ExpectedComponentIdentities[scenario.Id], componentIdentity, scenario.Id);

            if (scenario.Id == "no-event")
            {
                Assert.IsEmpty(result.Candidates, scenario.Id);
                falsePositiveCases += result.Candidates.Count > 0 ? 1 : 0;
                continue;
            }
            Assert.IsNotEmpty(result.Candidates, scenario.Id);
            extractedEventCases++;
            if (scenario.Id is "short-track" or "long-shadow-track" or "blinking-track")
            {
                Assert.IsTrue(result.Candidates.Any(static candidate =>
                    candidate.Features!.LengthPixels / candidate.Features.MeanWidthPixels >= 2), scenario.Id);
            }
            else if (scenario.Id == "fragmented-flare")
            {
                var features = result.Candidates[0].Features!;
                var endpoint = (features.BrightnessProfile[0].Value + features.BrightnessProfile[^1].Value) / 2;
                Assert.IsGreaterThan(0, features.SaturatedSampleCount, scenario.Id);
                Assert.HasCount(options.ProfileSampleCount, features.BrightnessProfile, scenario.Id);
                Assert.IsGreaterThanOrEqualTo(
                    3,
                    features.BrightnessProfile.Max(static sample => sample.Value) / Math.Max(endpoint, 1),
                    scenario.Id);
            }
            else if (scenario.Id == "sensor-artifacts")
            {
                Assert.IsTrue(result.Candidates.Any(static candidate => candidate.Features!.LengthPixels <= 4), scenario.Id);
            }
            var assessmentCandidates = new List<(ScenarioExtraction Extraction, TransientCandidateV1 Candidate)>();
            if (scenario.Id == "long-shadow-track")
            {
                foreach (var relative in new[] { -2d, -1d })
                {
                    var prior = await ExtractAtAsync(
                        module, config, manifest, scenarioIndex, targetOffset + relative, options).ConfigureAwait(false);
                    TestContext.WriteLine($"{scenario.Id}: relative={relative}, components={prior.Candidates.Count}; " +
                        string.Join(';', prior.Candidates.Select(static candidate =>
                            $"x={candidate.Geometry!.Bounds.X:F1},y={candidate.Geometry.Bounds.Y:F1}," +
                            $"length={candidate.Features!.LengthPixels:F2},width={candidate.Features.MeanWidthPixels:F2}," +
                            $"signal={candidate.Features.IntegratedSignalAdu}")));
                    assessmentCandidates.Add((prior, prior.Candidates.Single()));
                }
            }
            assessmentCandidates.Add((result, scenario.Id == "sensor-artifacts"
                ? result.Candidates.OrderByDescending(static candidate => candidate.Features!.LengthPixels).First()
                : result.Candidates[0]));
            if (scenario.Id == "blinking-track")
            {
                foreach (var relative in new[] { 0.5d, 1d })
                {
                    var later = await ExtractAtAsync(
                        module, config, manifest, scenarioIndex, targetOffset + relative, options).ConfigureAwait(false);
                    TestContext.WriteLine($"{scenario.Id}: relative={relative}, components={later.Candidates.Count}; " +
                        string.Join(';', later.Candidates.Select(static candidate =>
                            $"x={candidate.Geometry!.Bounds.X:F1},y={candidate.Geometry.Bounds.Y:F1}," +
                            $"length={candidate.Features!.LengthPixels:F2},width={candidate.Features.MeanWidthPixels:F2}," +
                            $"signal={candidate.Features.IntegratedSignalAdu}")));
                    assessmentCandidates.Add((later, later.Candidates.Single()));
                }
            }
            if (scenario.ExpectedSecondMono16Sha256 is not null)
            {
                var second = await ExtractAtAsync(module, config, manifest, scenarioIndex, targetOffset + 1, options)
                    .ConfigureAwait(false);
                var secondIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(second.Candidates.Select(
                    static candidate => new { candidate.Geometry, candidate.Features }));
                TestContext.WriteLine($"{scenario.Id}: second-identity={secondIdentity}");
                Assert.AreEqual(BoundarySecondComponentIdentity, secondIdentity, $"{scenario.Id} second observation");
                Assert.IsNotEmpty(second.Candidates, $"{scenario.Id} second observation");
                assessmentCandidates.Add((second, second.Candidates[0]));
            }

            var observations = assessmentCandidates.Select((value, ordinal) =>
                TransientObservationFactory.CreateAssessmentObservation(new TransientObservationPromotionRequest(
                    value.Candidate.CandidateId,
                    Guid.Parse($"a4000000-0000-0000-0000-{scenarioIndex * 10 + ordinal + 1:D12}"),
                    ordinal,
                    value.Extraction.Descriptor))).ToArray();
            var eventId = observations[0].EventId;
            Assert.IsTrue(observations.All(observation => observation.EventId == eventId), scenario.Id);
            var assessment = TransientAssessmentFactory.Create(new TransientAssessmentExecutionRequest(
                eventId,
                Guid.Parse($"a2000000-0000-0000-0000-{scenarioIndex + 1:D12}"),
                manifest.Utc.AddMinutes(10),
                TransientAssessmentAuthority.Provisional,
                observations,
                AssessmentOptions(),
                []));
            Assert.AreEqual(TransientAssessmentExecutionStatus.Produced, assessment.Status, assessment.ReasonCode);
            var expected = ExpectedAssessments[scenario.Id];
            Assert.AreEqual(expected.Classification, assessment.Descriptor!.Assessment.Classification, scenario.Id);
            Assert.AreEqual(expected.Severity, assessment.Descriptor.Assessment.MeteorSeverity, scenario.Id);
            var key = $"{expected.Classification}/{assessment.Descriptor.Assessment.Classification}";
            confusion[key] = confusion.GetValueOrDefault(key) + 1;
        }
        var cloudConfig = Config(CameraPixelFormat.Mono16, null, cloud: StableCloud());
        var cloudModule = Module();
        await cloudModule.InitializeAsync(cloudConfig, CancellationToken.None).ConfigureAwait(false);
        var cloudResult = await ExtractAtAsync(
            cloudModule,
            cloudConfig,
            manifest,
            manifest.Cases.Count,
            0,
            options).ConfigureAwait(false);
        Assert.IsEmpty(cloudResult.Candidates, "stable-cloud");
        Assert.AreEqual(
            ExpectedComponentIdentities["no-event"],
            CaptureContractJson.ComputeCanonicalJsonSha256(cloudResult.Candidates.Select(
                static candidate => new { candidate.Geometry, candidate.Features })),
            "stable-cloud");
        Assert.AreEqual(6, extractedEventCases);
        Assert.AreEqual(0, falsePositiveCases);
        Assert.AreEqual(6, confusion.Values.Sum());
        TestContext.WriteLine(
            "virtual-confusion=" + JsonSerializer.Serialize(confusion) +
            $"; attribution=background/extraction:6/6 events,0/2 no-event/cloud false positives; assessment:{confusion.Values.Sum()}/6");
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
        int scenarioIndex,
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
        var sequenceBase = scenarioIndex * 100_000 + (int)Math.Round((targetOffset + 100) * 10);
        foreach (var position in positions)
        {
            var offset = targetOffset + (int)position;
            var frame = await CaptureAtAsync(module, manifest, offset).ConfigureAwait(false);
            window[position] = CreateTemporalSource(
                config, frame, position, scenarioIndex, offset, sequenceBase + (int)position);
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
        var identityBase = scenarioIndex * 100_000 + ((int)Math.Round(targetOffset) + 100) * 100;
        var identitySlots = Enumerable.Range(1, options.MaximumCandidates).Select(index =>
            new TransientCandidateIdentitySlot(
                Guid.Parse($"b3000000-0000-0000-0000-{identityBase + index:D12}"),
                Guid.Parse($"b4000000-0000-0000-0000-{(index == 1 ? scenarioIndex + 1 : identityBase + index):D12}"))).ToArray();
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
        return new ScenarioExtraction(extraction.Descriptor, extraction.Candidates);
    }

    private static TransientTemporalSource CreateTemporalSource(
        CameraModuleConfig config,
        CameraFrame frame,
        TransientTemporalPosition position,
        int scenarioIndex,
        double offset,
        int captureSequence)
    {
        var suffix = scenarioIndex * 100_000 + (int)Math.Round((offset + 100) * 10);
        var artifactId = Guid.Parse($"b1000000-0000-0000-0000-{suffix:D12}");
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
            Guid.Parse($"b2000000-0000-0000-0000-{suffix:D12}"),
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

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json fixture deserialization.")]
    private sealed record TransientFixtureManifest(
        string SchemaVersion,
        DateTimeOffset Utc,
        double ExposureSeconds,
        IReadOnlyList<TransientFixtureCase> Cases);

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by System.Text.Json fixture deserialization.")]
    private sealed record TransientFixtureCase(
        string Id,
        string OracleLabel,
        double CaptureOffsetSeconds,
        ushort ExpectedRawMinimum,
        ushort ExpectedRawMaximum,
        double ExpectedRawMean,
        string ExpectedMono16Sha256,
        string? ExpectedSecondMono16Sha256,
        VirtualTransientScenarioDefinition Definition);

    private sealed record ScenarioExtraction(
        TransientCandidateExtractionDescriptorV1 Descriptor,
        IReadOnlyList<TransientCandidateV1> Candidates);
}
