using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Maintainability", "CA1507:Use nameof to express symbol names", Justification = "Token keys are a versioned external contract.")]
[SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "Single-use fixture expectations are clearer inline.")]
public sealed class FrameFactsRetainedContextTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 10, 22, 23, 30, 0, 123, TimeSpan.Zero);
    private static readonly Guid CaptureId = Guid.Parse("15510000-0000-0000-0000-000000000001");
    private static readonly Guid ArtifactId = Guid.Parse("15510000-0000-0000-0000-000000000002");
    private static readonly string Hash = new('A', 64);

    [TestMethod]
    public void CatalogueDocumentsEveryScalarAndProvidesOnlyClosedFormatsAndStatusCompanions()
    {
        Assert.IsTrue(FrameFactsRetainedCatalog.Entries.Length < FrameFactsTokenContext.MaximumFacts);
        foreach (var entry in FrameFactsRetainedCatalog.Entries)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Source));
            Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Unit));
            Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Provenance));
            Assert.IsNotNull(FrameFactsRetainedCatalog.Catalog.Find(entry.Definition.Key + ".status"));
            Assert.IsFalse(entry.Definition.Key.StartsWith("spaceWeather.", StringComparison.Ordinal));
        }
        Assert.AreEqual("frame-facts.reserved-namespace", FrameFactsTemplate.Parse("{{spaceWeather.kp}}", FrameFactsRetainedCatalog.Catalog).Error?.Code);
        Assert.AreEqual("frame-facts.unknown-format", FrameFactsTemplate.Parse("{{time.captureUtc|O}}", FrameFactsRetainedCatalog.Catalog).Error?.Code);
        Assert.AreEqual("frame-facts.unknown-token", FrameFactsTemplate.Parse("{{environment.cloudPercent}}", FrameFactsRetainedCatalog.Catalog).Error?.Code);
    }

    [TestMethod]
    public void CaptureAcquisitionStackAndLineageUseRetainedValuesAndExplicitUnits()
    {
        var descriptor = Descriptor();
        var identity = CaptureContractJson.ComputeDescriptorSha256(descriptor);
        var context = FrameFactsRetainedContext.Create(new(descriptor,
            new(identity, CadenceSeconds: 8), new(identity, Hash, [ArtifactId, Guid.Parse("15510000-0000-0000-0000-000000000004")], TimeSpan.FromSeconds(5))));
        Assert.AreEqual("fixture-agent rig-1 42", Evaluate("{{capture.agentId}} {{capture.rigId}} {{capture.sequence}}", context));
        Assert.AreEqual("1936 × 1216", Evaluate("{{capture.width}} × {{capture.height}}", context));
        Assert.AreEqual("2.500 7.250 3.0 -10.0 -9.5", Evaluate("{{acquisition.exposureSeconds|fixed3}} {{acquisition.gain|fixed3}} {{acquisition.offset|fixed1}} {{acquisition.sensorSetpointC|fixed1}} {{acquisition.sensorTemperatureC|fixed1}}", context));
        Assert.AreEqual("2 5.000 8", Evaluate("{{stack.count}} {{stack.integrationSeconds|fixed3}} {{acquisition.cadenceSeconds}}", context));
        Assert.AreEqual("cal-v1 AAAAAAAAAAAA proc-v1", Evaluate("{{lineage.calibration.version}} {{lineage.calibration.shortHash}} {{lineage.processing.version}}", context));
        Assert.AreEqual("Configured setpoint", context.Provenance["acquisition.sensorSetpointC"].Nature);
        Assert.AreNotEqual("Measured", context.Provenance["acquisition.sensorTemperatureC"].Nature);
    }

    [TestMethod]
    public void ScheduleAndCadenceComeFromTheCaptureTimeAdmission()
    {
        var location = Location();
        var descriptor = Descriptor() with { Location = location.ToProvenance() };
        var controls = descriptor.Controls;
        descriptor = descriptor with
        {
            CycleEvidence = new(CaptureCadenceMode.MinimumStartInterval, CaptureStartReason.Initial,
                AutomaticControlOwnership.Disabled, AutomaticControlOwnership.Disabled, null, Epoch, null, null,
                new(Epoch.AddSeconds(3.1), Epoch.AddSeconds(3.2), controls.EffectiveExposure, controls.EffectiveGain,
                    controls.EffectiveExposure, controls.EffectiveGain, CaptureControlDecisionReason.Disabled), Epoch.AddSeconds(3.5))
            {
                ScheduleAdmission = new(CaptureScheduleAdmissionEvidence.CurrentSchemaVersion, "revision-1", Hash, Hash,
                    "night-retained", CaptureScheduleAdmissionReason.WeeklyWindow, CaptureScheduleIntervalSource.WeeklyWindow,
                    Epoch.AddSeconds(-1), Epoch.AddHours(-1), Epoch.AddHours(1), "interval-1", "fixture-v1", Hash, Hash,
                    location.LocationId, location.Version)
            }
        };
        var context = FrameFactsRetainedContext.Create(new(descriptor));
        Assert.AreEqual("night-retained MinimumStartInterval", Evaluate("{{acquisition.scheduleProfile}} {{acquisition.cadenceMode}}", context));
        Assert.AreEqual("MISSING", Evaluate("{{acquisition.cadenceSeconds.status}}", context));
    }

    [TestMethod]
    public void OldRecordsDoNotAcquireMissingFieldsFromPresentDayDefaults()
    {
        var descriptor = Descriptor() with { Location = null, Controls = Descriptor().Controls with { EffectiveOffset = null, TemperatureSetpointC = null, EffectiveTemperatureC = null } };
        var context = FrameFactsRetainedContext.Create(new(descriptor));
        foreach (var key in new[] { "acquisition.offset", "acquisition.sensorSetpointC", "acquisition.sensorTemperatureC", "acquisition.scheduleProfile", "acquisition.cadenceMode", "acquisition.cadenceSeconds", "stack.count", "time.sourceUtc", "time.processingUtc", "time.captureLocal", "location.latitude", "astronomy.kind", "environment.cloudCover" })
            Assert.AreEqual("UNAVAILABLE MISSING", Evaluate("{{" + key + "}} {{" + key + ".status}}", context), key);
        Assert.AreEqual("2026-10-22T23:30:05.123+00:00", Evaluate("{{time.artifactCreatedUtc}}", context));
    }

    [TestMethod]
    public void SourceCaptureArtifactAndProcessingTimesRemainDistinctAndUtcIsExplicit()
    {
        var descriptor = Descriptor();
        var context = FrameFactsRetainedContext.Create(new(descriptor, new(CaptureContractJson.ComputeDescriptorSha256(descriptor),
            Epoch.AddMinutes(-1).ToOffset(TimeSpan.FromHours(3)), ProcessingCompletedUtc: Epoch.AddMinutes(2).ToOffset(TimeSpan.FromHours(-7)))));
        Assert.AreEqual("2026-10-22T23:29:00.123+00:00", Evaluate("{{time.sourceUtc}}", context));
        Assert.AreEqual("2026-10-22T23:30:00.123+00:00", Evaluate("{{time.captureUtc}}", context));
        Assert.AreEqual("2026-10-22T23:30:02.623+00:00", Evaluate("{{time.captureEndUtc}}", context));
        Assert.AreEqual("2026-10-22T23:30:05.123+00:00", Evaluate("{{time.artifactCreatedUtc}}", context));
        Assert.AreEqual("2026-10-22T23:32:00.123+00:00", Evaluate("{{time.processingUtc}}", context));
    }

    [TestMethod]
    [DataRow(-420, "2026-10-22T16:30:00.123-07:00")]
    [DataRow(330, "2026-10-23T05:00:00.123+05:30")]
    public void LocalTimeRequiresFrozenZoneRulesAndOffset(int offsetMinutes, string expected)
    {
        var descriptor = Descriptor();
        var context = FrameFactsRetainedContext.Create(new(descriptor, LocalTime: new(CaptureContractJson.ComputeDescriptorSha256(descriptor), Epoch,
            "explicit-fixture-zone", TimeSpan.FromMinutes(offsetMinutes), "retained-zone-rules-v1")));
        Assert.AreEqual(expected, Evaluate("{{time.captureLocal}}", context));
        Assert.AreEqual("explicit-fixture-zone retained-zone-rules-v1", Evaluate("{{time.zoneId}} {{time.zoneRuleIdentity}}", context));
    }

    [TestMethod]
    public void MatchingLocationSuppliesCoordinatesButZoneNameAloneDoesNotSupplyLocalTime()
    {
        var location = Location();
        var descriptor = Descriptor() with { Location = location.ToProvenance() };
        var context = FrameFactsRetainedContext.Create(new(descriptor, Location: location));
        Assert.AreEqual("35.2 -113.9 1000.0", Evaluate("{{location.latitude|fixed1}} {{location.longitude|fixed1}} {{location.elevation|fixed1}}", context));
        Assert.AreEqual("site-fixture 1 manual America/Phoenix", Evaluate("{{location.id}} {{location.version}} {{location.source}} {{location.timeZone}}", context));
        Assert.AreEqual("MISSING", Evaluate("{{time.captureLocal.status}}", context));
    }

    [TestMethod]
    public void WrongLocationProvenanceAndCorruptedCoordinatesAreUnavailable()
    {
        var location = Location();
        var descriptor = Descriptor() with { Location = location.ToProvenance() };
        Assert.AreEqual("CONFLICTING", Evaluate("{{location.latitude.status}}", FrameFactsRetainedContext.Create(new(descriptor, Location: location with { Version = 2 }))));
        Assert.AreEqual("UNSUPPORTED", Evaluate("{{location.latitude.status}}", FrameFactsRetainedContext.Create(new(descriptor, Location: location with { LatitudeDegrees = 1 }))));
        Assert.AreEqual("MISSING", Evaluate("{{location.latitude.status}}", FrameFactsRetainedContext.Create(new(Descriptor(), Location: location))));
    }

    [TestMethod]
    public void ConflictingLocalInstantOrZoneAndUnsupportedOffsetsAreUnavailable()
    {
        var location = Location();
        var descriptor = Descriptor() with { Location = location.ToProvenance() };
        var local = new FrameFactsRetainedLocalTimeV1(CaptureContractJson.ComputeDescriptorSha256(descriptor), Epoch, location.TimeZoneId, TimeSpan.FromHours(-7), "rules-v1");
        foreach (var conflicting in new[] { local with { CaptureUtc = Epoch.AddSeconds(1) }, local with { ZoneId = "Europe/London" }, local with { DescriptorIdentitySha256 = Hash } })
            Assert.AreEqual("CONFLICTING", Evaluate("{{time.captureLocal.status}}", FrameFactsRetainedContext.Create(new(descriptor, Location: location, LocalTime: conflicting))));
        foreach (var unsupported in new[] { local with { Offset = TimeSpan.FromHours(15) }, local with { Offset = TimeSpan.FromSeconds(1) }, local with { RuleIdentity = "" } })
            Assert.AreEqual("UNSUPPORTED", Evaluate("{{time.captureLocal.status}}", FrameFactsRetainedContext.Create(new(descriptor, LocalTime: unsupported))));
    }

    [TestMethod]
    public void UnrelatedExtrasAndStackAreConflictingRatherThanLastWriterWins()
    {
        var context = FrameFactsRetainedContext.Create(new(Descriptor(), new(Hash, Epoch, 4, Epoch), new(Hash, Hash, [ArtifactId], TimeSpan.FromSeconds(1))));
        foreach (var key in new[] { "time.sourceUtc", "time.processingUtc", "acquisition.cadenceSeconds", "stack.count" })
            Assert.AreEqual("UNAVAILABLE CONFLICTING", Evaluate("{{" + key + "}} {{" + key + ".status}}", context));
    }

    [TestMethod]
    public void UnsupportedStackEvidenceAndImpossibleCaptureExtrasAreUnavailable()
    {
        var descriptor = Descriptor();
        var identity = CaptureContractJson.ComputeDescriptorSha256(descriptor);
        var stack = new FrameFactsRetainedStackV1(identity, Hash, [ArtifactId], TimeSpan.FromSeconds(2.5));
        foreach (var invalid in new[] { stack with { SourceArtifactIds = [] }, stack with { SourceArtifactIds = [ArtifactId, ArtifactId] },
            stack with { SourceArtifactIds = [CaptureId] }, stack with { Identity = "unknown" }, stack with { TotalIntegration = TimeSpan.FromSeconds(-1) } })
            Assert.AreEqual("UNSUPPORTED", Evaluate("{{stack.count.status}}", FrameFactsRetainedContext.Create(new(descriptor, Stack: invalid))));
        var context = FrameFactsRetainedContext.Create(new(descriptor, new(identity, CadenceSeconds: -1, ProcessingCompletedUtc: Epoch.AddSeconds(-1))));
        Assert.AreEqual("UNSUPPORTED UNSUPPORTED", Evaluate("{{acquisition.cadenceSeconds.status}} {{time.processingUtc.status}}", context));
    }

    [TestMethod]
    [DataRow(FrameFactsAvailability.Missing, "MISSING")]
    [DataRow(FrameFactsAvailability.Stale, "STALE")]
    [DataRow(FrameFactsAvailability.Conflicting, "CONFLICTING")]
    [DataRow(FrameFactsAvailability.Unsupported, "UNSUPPORTED")]
    public void AssociationStatusCannotBeOverriddenByAnOtherwiseValidObservation(FrameFactsAvailability status, string expected)
    {
        var context = FrameFactsRetainedContext.Create(new(Descriptor(), Environment: [Association(Observation()) with { Availability = status }]));
        Assert.AreEqual("UNAVAILABLE " + expected, Evaluate("{{environment.airTemperature}} {{environment.airTemperature.status}}", context));
        Assert.AreEqual("Measured", Evaluate("{{environment.airTemperature.sourceKind}}", context));
        Assert.AreEqual("UNAVAILABLE " + expected, Evaluate("{{environment.airTemperature}} {{environment.airTemperature.status}}", context, FrameFactsMissingPolicy.DropLine));
    }

    [TestMethod]
    [DataRow(EnvironmentalObservationSourceKind.Measured)]
    [DataRow(EnvironmentalObservationSourceKind.Simulated)]
    [DataRow(EnvironmentalObservationSourceKind.Imported)]
    [DataRow(EnvironmentalObservationSourceKind.Manual)]
    [DataRow(EnvironmentalObservationSourceKind.Derived)]
    public void EnvironmentalSourceKindsArePreservedWithoutPromotion(EnvironmentalObservationSourceKind kind)
    {
        var observation = Observation(kind: kind);
        var context = FrameFactsRetainedContext.Create(new(Descriptor(), Environment: [Association(observation)]));
        Assert.AreEqual("12.5 " + kind, Evaluate("{{environment.airTemperature|fixed1}} {{environment.airTemperature.sourceKind}}", context));
        Assert.AreEqual(kind.ToString(), context.Provenance["environment.airTemperature"].Nature);
        StringAssert.Contains(context.Provenance["environment.airTemperature"].SourceIdentity, EnvironmentalObservationFactJson.ComputeContentSha256(observation), StringComparison.Ordinal);
    }

    [TestMethod]
    public void AllEnvironmentalQuantitiesUseRetainedCanonicalUnitsAndCloudCoverStaysAFraction()
    {
        var facts = Enum.GetValues<EnvironmentalObservationKind>().Select(kind => Association(Observation(valueKind: kind))).ToArray();
        var context = FrameFactsRetainedContext.Create(new(Descriptor(), Environment: facts));
        foreach (var entry in FrameFactsRetainedCatalog.Entries.Where(entry => entry.Definition.Key.StartsWith("environment.", StringComparison.Ordinal)))
            Assert.AreEqual(FrameFactsAvailability.Available, context.Tokens.Get(entry.Definition.Key).Availability, entry.Definition.Key);
        Assert.AreEqual("0.25 TRUE 101325", Evaluate("{{environment.cloudCover}} {{environment.rainState}} {{environment.atmosphericPressure}}", context));
        Assert.AreEqual("21.0", Evaluate("{{environment.cameraSensorTemperature|fixed1}}", context));
        Assert.AreEqual("-9.5", Evaluate("{{acquisition.sensorTemperatureC|fixed1}}", context));
    }

    [TestMethod]
    public void StaleAfterValidityQualityAndWrongUnitsFailClosed()
    {
        var observation = Observation();
        foreach (var stale in new[] { observation with { StaleAfterUtc = Epoch.AddSeconds(1) }, observation with { ValidFromUtc = Epoch.AddSeconds(1) }, observation with { ValidThroughUtc = Epoch.AddSeconds(1), StaleAfterUtc = Epoch.AddSeconds(1) } })
            Assert.AreEqual("STALE", Evaluate("{{environment.airTemperature.status}}", FrameFactsRetainedContext.Create(new(Descriptor(), Environment: [Association(stale)]))));
        foreach (var invalid in new[] { observation with { Value = observation.Value with { Quality = EnvironmentalObservationQuality.Suspect } },
            observation with { Value = observation.Value with { Unit = EnvironmentalObservationUnit.Percent } }, observation with { SchemaVersion = "future-v99" } })
            Assert.AreEqual("UNSUPPORTED", Evaluate("{{environment.airTemperature.status}}", FrameFactsRetainedContext.Create(new(Descriptor(), Environment: [Association(invalid)]))));
    }

    [TestMethod]
    public void EnvironmentalAssociationMustMatchCaptureIntervalAndRigAndDuplicateKindsAreRejected()
    {
        var association = Association(Observation());
        foreach (var wrong in new[] { association with { CaptureId = ArtifactId }, association with { ExposureFromUtc = Epoch.AddSeconds(-1) },
            association with { Observation = association.Observation! with { RigId = "another-rig" } } })
            Assert.AreEqual("CONFLICTING", Evaluate("{{environment.airTemperature.status}}", FrameFactsRetainedContext.Create(new(Descriptor(), Environment: [wrong]))));
        Assert.ThrowsExactly<ArgumentException>(() => FrameFactsRetainedContext.Create(new(Descriptor(), Environment: [association, association])));
    }

    [TestMethod]
    public void LegacyDisplayTextAndVirtualSceneNeverInventAnEnvironmentalReading()
    {
        var context = FrameFactsRetainedContext.Create(new(Descriptor()));
        var legacy = new PresentationMetadataFactsV1(Hash, [], [], ["ENVIRONMENT", "CLOUDCOVER 42 PERCENT MEASURED"], []);
        Assert.IsTrue(FrameFactsCornerPresets.ConvertLegacy(legacy).IsValid);
        Assert.AreEqual("UNAVAILABLE MISSING", Evaluate("{{environment.cloudCover}} {{environment.cloudCover.status}}", context));
    }

    [TestMethod]
    public void OlderEnvironmentalSchemaSupportsItsOriginalQuantitiesButNotNewSensorTemperature()
    {
        var original = Observation() with { SchemaVersion = EnvironmentalObservationSchemaVersions.V1 };
        var newQuantity = Observation(valueKind: EnvironmentalObservationKind.CameraSensorTemperature) with { SchemaVersion = EnvironmentalObservationSchemaVersions.V1 };
        var context = FrameFactsRetainedContext.Create(new(Descriptor(), Environment: [Association(original), Association(newQuantity)]));
        Assert.AreEqual("12.5 AVAILABLE", Evaluate("{{environment.airTemperature}} {{environment.airTemperature.status}}", context));
        Assert.AreEqual("UNSUPPORTED", Evaluate("{{environment.cameraSensorTemperature.status}}", context));
    }

    [TestMethod]
    [DataRow(ProjectedSceneKind.Predicted)]
    [DataRow(ProjectedSceneKind.VirtualRenderAuthoritative)]
    [DataRow(ProjectedSceneKind.ImageRegistered)]
    public async Task AstronomyKeepsConfiguredGeometryAndDerivedSceneKindDistinct(ProjectedSceneKind kind)
    {
        var descriptor = Descriptor();
        var scene = await SceneAsync(descriptor, kind).ConfigureAwait(false);
        var context = FrameFactsRetainedContext.Create(new(descriptor, Astronomy: scene));
        Assert.AreEqual(kind.ToString(), Evaluate("{{astronomy.kind}}", context));
        Assert.AreEqual("fixture 1", Evaluate("{{lineage.catalog.name}} {{lineage.catalog.version}}", context));
        Assert.AreEqual("90.0 0.0 FALSE", Evaluate("{{astronomy.boresightAltitude|fixed1}} {{astronomy.observerLatitude|fixed1}} {{astronomy.refraction}}", context));
        Assert.AreEqual("AVAILABLE", Evaluate("{{astronomy.sun.geometricAltitude.status}}", context));
        Assert.AreEqual("MISSING", Evaluate("{{astronomy.moon.geometricAltitude.status}}", context));
        Assert.AreEqual("Configured scene observer and projection", context.Provenance["astronomy.boresightAltitude"].Nature);
        StringAssert.StartsWith(context.Provenance["astronomy.sun.geometricAltitude"].Nature, kind.ToString(), StringComparison.Ordinal);
        Assert.AreEqual("MISSING", Evaluate("{{environment.cloudCover.status}}", context));
    }

    [TestMethod]
    public async Task UnsupportedOrUnrelatedScenesAreUnavailableAndNeverRecomputed()
    {
        var descriptor = Descriptor();
        var scene = await SceneAsync(descriptor).ConfigureAwait(false);
        Assert.AreEqual("CONFLICTING", Evaluate("{{astronomy.kind.status}}", FrameFactsRetainedContext.Create(new(descriptor, Astronomy: scene with { Source = scene.Source with { CaptureId = ArtifactId } }))));
        Assert.AreEqual("UNSUPPORTED", Evaluate("{{lineage.catalog.name.status}}", FrameFactsRetainedContext.Create(new(descriptor, Astronomy: scene with { SchemaVersion = "future-v99" }))));
        Assert.AreEqual("UNSUPPORTED", Evaluate("{{astronomy.kind.status}}", FrameFactsRetainedContext.Create(new(descriptor, Astronomy: scene with { SceneIdentitySha256 = Hash }))));
    }

    [TestMethod]
    public async Task FrozenSnapshotDoesNotRetainMutableInputsAndIsIdenticalForEitherConsumer()
    {
        var descriptor = Descriptor();
        var sourceIds = new List<Guid> { ArtifactId };
        var environment = new List<FrameFactsRetainedEnvironmentV1> { Association(Observation()) };
        var scene = await SceneAsync(descriptor).ConfigureAwait(false);
        var input = new FrameFactsRetainedInputV1(descriptor, Stack: new(CaptureContractJson.ComputeDescriptorSha256(descriptor), Hash, sourceIds, TimeSpan.FromSeconds(2.5)), Astronomy: scene, Environment: environment);
        var context = FrameFactsRetainedContext.Create(input);
        string[] AllTokens(FrameFactsRetainedContext snapshot) => FrameFactsRetainedCatalog.Catalog.Definitions
            .Select(definition => Evaluate("{{" + definition.Key + "}}", snapshot)).ToArray();
        var before = AllTokens(context);
        CollectionAssert.AreEqual(before, AllTokens(FrameFactsRetainedContext.Create(input)));
        sourceIds.Clear();
        environment.Clear();
        // A later configuration/profile object cannot influence the previously constructed context.
        input = input with { Descriptor = descriptor with { Controls = descriptor.Controls with { EffectiveGain = 999 }, Profiles = descriptor.Profiles with { Processing = descriptor.Profiles.Processing with { Version = "current-unrelated" } } } };
        CollectionAssert.AreEqual(before, AllTokens(context));
        Assert.AreEqual("proc-v1", Evaluate("{{lineage.processing.version}}", context));
        Assert.AreEqual(999, input.Descriptor.Controls.EffectiveGain);
    }

    internal static ReconstructionDescriptor Descriptor()
    {
        var profile = new ProfileIdentityDescriptor("fixture", "1", Hash);
        var recipe = RecipeIdentityDescriptor.Create("capture", "1", "fixture-v1", JsonSerializer.SerializeToElement(new { }));
        return new(new("fixture-agent", "rig-1", 42, CaptureId), new(Epoch, Epoch, Epoch.AddSeconds(2.5), Epoch.AddSeconds(3), Epoch.AddSeconds(4)),
            new(TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(2.5), 7.25, 7.25, 3, 3, -10, -9.5),
            new(profile, profile with { Version = "cal-v1" }, profile, profile, profile with { Version = "proc-v1" }),
            new(1936, 1216, 1936, CameraPixelFormat.Mono8, FrameByteOrder.NotApplicable, 8, 8, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 255, 1936L * 1216),
            new(ArtifactId, FrameArtifactRole.Raw, "fixture-camera", "raw", Epoch.AddSeconds(5), [], recipe, "application/x-hvo-frame", Hash));
    }

    private static DeploymentLocationSnapshot Location() => DeploymentLocationSnapshot.Create("site-fixture", 1, "manual", null, Epoch.AddDays(-1), Epoch.AddDays(1), 35.2, -113.9, 1000, "America/Phoenix");

    private static FrameFactsRetainedEnvironmentV1 Association(EnvironmentalObservationFactV1 fact)
        => new(CaptureId, fact.Value.Kind, FrameFactsAvailability.Available, Hash, Epoch, Epoch.AddSeconds(2.5), fact);

    private static EnvironmentalObservationFactV1 Observation(EnvironmentalObservationSourceKind kind = EnvironmentalObservationSourceKind.Measured,
        EnvironmentalObservationKind valueKind = EnvironmentalObservationKind.AirTemperature)
    {
        var parameters = JsonSerializer.SerializeToElement(new { model = "retained-fixture-v1" });
        var (unit, numeric) = valueKind switch
        {
            EnvironmentalObservationKind.RelativeHumidity => (EnvironmentalObservationUnit.Percent, 50d),
            EnvironmentalObservationKind.AtmosphericPressure => (EnvironmentalObservationUnit.Pascals, 101325d),
            EnvironmentalObservationKind.WindSpeed or EnvironmentalObservationKind.WindGust => (EnvironmentalObservationUnit.MetersPerSecond, 2d),
            EnvironmentalObservationKind.WindDirection => (EnvironmentalObservationUnit.DegreesTrue, 270d),
            EnvironmentalObservationKind.PrecipitationRate => (EnvironmentalObservationUnit.MillimetersPerHour, 0d),
            EnvironmentalObservationKind.RainState => (EnvironmentalObservationUnit.Boolean, 0d),
            EnvironmentalObservationKind.SkyBrightness or EnvironmentalObservationKind.SkyQuality => (EnvironmentalObservationUnit.MagnitudesPerSquareArcsecond, 20d),
            EnvironmentalObservationKind.CloudCover => (EnvironmentalObservationUnit.Fraction, 0.25),
            EnvironmentalObservationKind.CameraSensorTemperature => (EnvironmentalObservationUnit.DegreesCelsius, 21d),
            _ => (EnvironmentalObservationUnit.DegreesCelsius, 12.5)
        };
        return new(EnvironmentalObservationSchemaVersions.V2, Guid.Parse("15510000-0000-0000-0000-000000000003"),
            new("fixture", "weather", "1", kind, new(new("fixture", "1"), parameters, CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
            Epoch.AddSeconds(-10), null, null, Epoch.AddMinutes(-1), Epoch.AddMinutes(5), Epoch.AddMinutes(4),
            new(valueKind, unit, valueKind == EnvironmentalObservationKind.RainState ? null : numeric, valueKind == EnvironmentalObservationKind.RainState ? true : null, EnvironmentalObservationQuality.Good),
            kind == EnvironmentalObservationSourceKind.Derived ? [new(Hash, ArtifactId)] : [], "rig-1");
    }

    internal static async Task<ProjectedSceneV1> SceneAsync(ReconstructionDescriptor descriptor, ProjectedSceneKind kind = ProjectedSceneKind.Predicted)
    {
        var sidereal = AstronomyTime.LocalMeanSiderealDegrees(Epoch, 0) / 15;
        var ephemeris = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition> { [SolarSystemBody.Sun] = new(new(sidereal, 0), -26) });
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([]), null, ephemeris).BuildAsync(new VisibleSceneRequest(Epoch,
            new ObserverLocation(0, 0, 0), new ProjectionContext(ProjectionModel.EquidistantFisheye, 968, 608, 500, 500, 1936, 1216,
                ProjectionAperture.Circular, ImageCircleRadiusPixels: 600, BoresightAltitudeDegrees: 90), new CatalogQuery(6, 10),
            new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), Hash, "test", "v1"),
            projectionVersion: "fixture-projection-v1", solarSystemBodies: [SolarSystemBody.Sun])).ConfigureAwait(false);
        return ProjectedSceneJson.Create(kind, visible, ProjectedSceneImageTransformV1.Identity(1936, 1216),
            new(descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(descriptor)), "cal-v1", "fixture-projection-v1");
    }

    internal static string Evaluate(string source, FrameFactsRetainedContext context, FrameFactsMissingPolicy policy = FrameFactsMissingPolicy.Placeholder)
    {
        var parsed = FrameFactsTemplate.Parse(source, FrameFactsRetainedCatalog.Catalog);
        Assert.IsTrue(parsed.IsValid, parsed.Error?.ToString());
        var result = FrameFactsEvaluator.Evaluate(parsed.Template!, context.Tokens, policy);
        Assert.IsTrue(result.IsValid, result.Error?.ToString());
        return result.Text;
    }
}
