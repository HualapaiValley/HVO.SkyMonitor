using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class MeasuredDeepSkyStackTests
{
    private static readonly JsonSerializerOptions RegistrationJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    [TestMethod]
    [DataRow("diffuse", 1, DeepSkyStackStatus.Qualified, DeepSkyStackReason.QualifiedAssociations)]
    [DataRow("diffuse", 2, DeepSkyStackStatus.AssessedEmpty, DeepSkyStackReason.NoUniqueAssociation)]
    [DataRow("diffuse", 0, DeepSkyStackStatus.Insufficient, DeepSkyStackReason.CatalogUnavailable)]
    [DataRow("blank", 1, DeepSkyStackStatus.AssessedEmpty, DeepSkyStackReason.NoEligibleSources)]
    [DataRow("blank-edge", 1, DeepSkyStackStatus.Insufficient, DeepSkyStackReason.InsufficientLocalSupport)]
    [DataRow("diffuse-no-profile", 1, DeepSkyStackStatus.Insufficient, DeepSkyStackReason.InsufficientStellarProfile)]
    [SuppressMessage("Reliability", "CA2007", Justification = "MSTest handles async tests.")]
    public async Task PinnedRecipeReopensExactPixelsAndIdentifiesOnlyUniqueSupportedSources(string signal,
        int canonicalObjects, DeepSkyStackStatus expectedStatus, DeepSkyStackReason reason)
    {
        var plate = await PlateAsync();
        var truth = new PixelPoint(42.5, 45.5);
        var samples = Combine(RawOracle(signal == "diffuse-no-profile" ? "diffuse" : signal, truth, 193));
        var stack = BuildRetainedRaster(plate, samples, includeStellarField: signal is not ("blank-edge" or "diffuse-no-profile"));
        var target = new PixelPoint(200 + truth.X, 200 + truth.Y);
        if (signal == "blank-edge")
        {
            // The central aperture and annulus are supported, but one whole column
            // of displaced noise apertures is outside the contributed footprint.
            target = target with { X = target.X + 32 };
        }
        var catalog = Catalog(plate.Mapping, plate.Solution.Assessment.Frame.MidpointUtc, target, canonicalObjects);
        var executor = new ProcessingRecipeExecutor([new MeasuredDeepSkyStackRecipe(catalog)]);
        var snapshot = new CatalogSnapshotIdentity("openngc-oracle", "1", "1", "1", MeasuredAstrometryFixture.Hash("oracle"));
        var inputs = stack.Planes.Concat(plate.Artifacts).ToArray();
        _ = MeasuredDeepSkyStackRecipe.ReadStackEvidence(stack.Evidence);
        var request = MeasuredDeepSkyStackRecipe.CreateRequest(stack.Evidence, inputs, snapshot);
        var outcome = await executor.ExecuteAsync(request);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = Assert.ContainsSingle(outcome.Products);
        var assessment = MeasuredDeepSkyStackV1.Read(product.Payload);
        TestContext.WriteLine(JsonSerializer.Serialize(assessment));
        Assert.AreEqual(expectedStatus, assessment.Status);
        Assert.AreEqual(reason, assessment.Reason);
        Assert.AreEqual(plate.Solution.SolutionIdentitySha256, assessment.MappingSolutionSha256);
        Assert.AreEqual(5, assessment.Contributors.Count);
        Assert.AreEqual(snapshot, assessment.Catalog);
        Assert.AreEqual(4, assessment.Planes.Count);
        if (signal == "blank-edge")
        {
            Assert.IsGreaterThan(0, assessment.Extraction!.SupportedPositions);
            Assert.AreEqual(1, assessment.CatalogCentersInField);
            Assert.AreEqual(0, assessment.CatalogCentersWithLocalSupport);
        }
        if (canonicalObjects == 2)
        {
            Assert.AreEqual(DeepSkyIdentificationStatus.CompetingObjects, Assert.ContainsSingle(assessment.Identifications).Status);
        }
        if (expectedStatus == DeepSkyStackStatus.Qualified)
        {
            var hit = Assert.ContainsSingle(assessment.Identifications);
            var detection = assessment.Extraction!.Detections.Single(item => item.Id == hit.DetectionId);
            Assert.IsNotNull(detection.StellarProfile);
            Assert.IsGreaterThanOrEqualTo(2, detection.StellarProfile.SampleCount);
            Assert.AreEqual("NGC0224", hit.CanonicalId);
            Assert.AreEqual("M31", hit.Designation);
            Assert.AreEqual(target.X, hit.ExpectedPixel!.Value.X, .01);
            Assert.AreEqual(target.Y, hit.ExpectedPixel!.Value.Y, .01);
            Assert.AreEqual(2, catalog?.FindByAlias("NGC 224").Count + catalog?.FindByAlias("M31").Count);
        }

        // An archive restores immutable bytes and the original recipe, independently of discovery order.
        var restored = await executor.ExecuteAsync(MeasuredDeepSkyStackRecipe.CreateRequest(
            stack.Evidence with { Payload = stack.Evidence.Payload.ToArray() },
            inputs.Select(input => input with { Payload = input.Payload.ToArray() }).ToArray(), snapshot));
        var repeated = Assert.ContainsSingle(restored.Products);
        Assert.AreEqual(product.OutputIdentitySha256, repeated.OutputIdentitySha256);
        CollectionAssert.AreEqual(product.Payload.ToArray(), repeated.Payload.ToArray());

        var altered = request.Inputs.Select(input => input.MediaType == CenteredRegisteredStackEvidence.SupportMediaType
            ? input with { Payload = new byte[input.Payload.Length] } : input).ToArray();
        var corrupt = await executor.ExecuteAsync(request with { Inputs = altered });
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, corrupt.Status);
        Assert.IsEmpty(corrupt.Products);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => executor.ExecuteAsync(request, new CancellationToken(true)).AsTask());
    }

    [TestMethod]
    [SuppressMessage("Reliability", "CA2007", Justification = "MSTest handles async tests.")]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnIndependentlyMeasuredStarVetoesACoincidentBroadPeakEvenWithAnOpenNgcMatch(bool omitFromPlate)
    {
        var plate = await PlateAsync();
        var stellar = MeasuredStellarAssociationJson.Parse(plate.Artifacts[1].Payload).Associations!;
        var star = plate.Solution.Residuals.NativeAssociations
            .Join(stellar.Associations, fit => fit.DetectionIndex, association => association.DetectionIndex,
                (_, association) => association)
            .First(association => association.MeasuredX is > 96 and < 416 && association.MeasuredY is > 96 and < 416);
        if (omitFromPlate)
        {
            plate = await PlateAsync(star.DetectionIndex);
            Assert.IsFalse(plate.Solution.Residuals.NativeAssociations.Any(pair => pair.DetectionIndex == star.DetectionIndex));
        }
        var measured = new PixelPoint(star.MeasuredX, star.MeasuredY);
        var originX = (int)measured.X - Width / 2;
        var originY = (int)measured.Y - Height / 2;
        var samples = Combine(RawOracle("broad-psf", new(measured.X - originX, measured.Y - originY), 907));
        Assert.ContainsSingle(Extract(samples).Detections.Where(detection => detection.Rejection is null));

        var stack = BuildRetainedRaster(plate, samples, originX: originX, originY: originY);
        var catalog = Catalog(plate.Mapping, plate.Solution.Assessment.Frame.MidpointUtc, measured, 1)!;
        var result = await new ProcessingRecipeExecutor([new MeasuredDeepSkyStackRecipe(catalog)]).ExecuteAsync(
            MeasuredDeepSkyStackRecipe.CreateRequest(stack.Evidence, stack.Planes.Concat(plate.Artifacts).ToArray(),
                catalog.Semantics.SnapshotIdentity!));
        var assessment = MeasuredDeepSkyStackV1.Read(Assert.ContainsSingle(result.Products).Payload);
        Assert.AreEqual(DeepSkyStackStatus.AssessedEmpty, assessment.Status);
        Assert.AreEqual(DeepSkyStackReason.NoEligibleSources, assessment.Reason);
        Assert.IsNotNull(assessment.Extraction);
        Assert.IsTrue(assessment.Extraction.Detections.Any(detection =>
            detection.Rejection == ExtendedStackRejection.StellarStructure &&
            double.Hypot(detection.Centroid.X - measured.X, detection.Centroid.Y - measured.Y) < 2));
        Assert.IsEmpty(assessment.Identifications);
    }

    [TestMethod]
    [SuppressMessage("Reliability", "CA2007", Justification = "MSTest handles async tests.")]
    public async Task RejectedRegistrationAndStaleMappingCannotNameTheSameDiffusion()
    {
        var plate = await PlateAsync();
        var samples = Combine(RawOracle("diffuse", new(42.5, 45.5), 193));
        var snapshot = new CatalogSnapshotIdentity("openngc-oracle", "1", "1", "1", MeasuredAstrometryFixture.Hash("oracle"));
        var catalog = Catalog(plate.Mapping, plate.Solution.Assessment.Frame.MidpointUtc, new(242.5, 245.5), 1);
        var executor = new ProcessingRecipeExecutor([new MeasuredDeepSkyStackRecipe(catalog)]);

        var rejected = BuildRetainedRaster(plate, samples, wrongRegistration: true);
        var invalid = Assert.ThrowsExactly<InvalidDataException>(() => MeasuredDeepSkyStackRecipe.CreateRequest(
            rejected.Evidence, rejected.Planes.Concat(plate.Artifacts).ToArray(), snapshot));
        StringAssert.Contains(invalid.Message, "Registered contributor does not have a verified map.", StringComparison.Ordinal);

        var stack = BuildRetainedRaster(plate, samples);
        var staleInputs = plate.Artifacts.Select(artifact => artifact.SchemaVersion == MeasuredAstrometrySolutionV1.CurrentSchemaVersion
            ? artifact with { CaptureId = Guid.NewGuid() } : artifact);
        var stale = await executor.ExecuteAsync(MeasuredDeepSkyStackRecipe.CreateRequest(
            stack.Evidence, stack.Planes.Concat(staleInputs).ToArray(), snapshot));
        var assessment = MeasuredDeepSkyStackV1.Read(Assert.ContainsSingle(stale.Products).Payload);
        Assert.AreEqual(DeepSkyStackReason.ReferenceMappingMismatch, assessment.Reason);
        Assert.IsNotNull(assessment.Extraction);
        Assert.IsEmpty(assessment.Identifications);
    }

    private static DeepSkyCatalog? Catalog(AstrometricMapping mapping, DateTimeOffset midpoint, PixelPoint expected, int count)
    {
        if (count == 0)
        {
            return null;
        }
        var pin = new CatalogSnapshotIdentity("openngc-oracle", "1", "1", "1", MeasuredAstrometryFixture.Hash("oracle"));
        var semantics = new DeepSkySemantics("OpenNGC", "fixture", new string('a', 40), new("https://example.test/openngc"),
            "test", "equatorial-j2000-icrs-aligned", "J2000.0", "arcminute", "north-through-east", "test", "test", pin);
        var sky = EquatorialPrecession.PrecessToJ2000(mapping.PixelToSky(expected)!.EquatorialMeanOfDate, midpoint);
        var scale = mapping.LocalPixelScale(expected);
        var major = 7 * Math.Sqrt(scale.XArcsecondsPerPixel!.Value * scale.YArcsecondsPerPixel!.Value) / 60;
        var row = new DeepSkyObject("NGC0224", "NGC 224", "M31", "G", sky.RightAscensionHours,
            sky.DeclinationDegrees, "And", major, major / 2, 0, null, null, null, null, 31, null, null, null);
        DeepSkyObject[] rows = count == 1 ? [row] :
            [row, row with { Id = "NGC0225", Designation = "NGC 225", DisplayName = "NGC 225", MessierNumber = null }];
        var aliases = rows.Select(item => new DeepSkyAlias(item.Designation, item.Id, DeepSkyAliasKinds.Designation))
            .Append(new("M31", row.Id, DeepSkyAliasKinds.Messier));
        return new DeepSkyCatalog(semantics, rows, aliases, [], []);
    }

    [SuppressMessage("Reliability", "CA2007", Justification = "MSTest fixture.")]
    private static async Task<Plate> PlateAsync(int? omitFromPlate = null)
    {
        var fixture = await MeasuredAstrometryFixture.CreateAsync();
        if (omitFromPlate is { } omit)
        {
            fixture = fixture.WithAssociations(fixture.Associations with
            {
                Associations = fixture.Associations.Associations.Select(star => star.DetectionIndex != omit ? star :
                    star with
                    {
                        SignalToNoise = null,
                        LabelEligible = false,
                        LabelRejectionReason = MeasuredStellarAssociationReasonCodes.LabelSignalToNoiseUnavailable
                    }).ToArray()
            });
        }
        var executor = new ProcessingRecipeExecutor();
        var scene = ToArtifact(Assert.ContainsSingle((await executor.ExecuteAsync(new(BuiltInProcessingRecipes.ProjectedScene,
            default, ProcessingInputSelector.Raw("source"), [fixture.Raw], "projected-scene",
            AuxiliaryInputs: [MeasuredStellarAssociationFixtures.SceneAuxiliary(fixture.Scene)]))).Products), fixture.Raw);
        var context = ToArtifact(RetainedAstrometryProducts.CreateContextProduct(fixture.Context, fixture.Raw,
            fixture.Measured, scene, "astrometry-context"), fixture.Raw);
        var request = RetainedAstrometryProducts.Request(fixture.Raw, fixture.Measured, scene, context,
            MeasuredAstrometryFixture.Options, "astrometry");
        var solutionOutcome = await executor.ExecuteAsync(request);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, solutionOutcome.Status, solutionOutcome.ReasonCode);
        var product = Assert.ContainsSingle(solutionOutcome.Products);
        var solution = MeasuredAstrometryJson.Parse(product.Payload);
        Assert.IsTrue(solution.Assessment.HasMeasuredMapping, solution.Assessment.Reason);
        return new(fixture.Raw, [fixture.Raw, fixture.Measured, scene, context, ToArtifact(product, fixture.Raw)], solution,
            MeasuredAstrometryJson.ReconstructMapping(solution, fixture.Calibration, fixture.Associations));
    }

    // Scientific fixture pixels and the independently fitted coordinate fixture are composed at the contract boundary.
    // The registration producer itself is covered by the #1169 geometry integration test; this isolates a replay.
    private static (ProcessingArtifact Evidence, ProcessingArtifact[] Planes) BuildRetainedRaster(
        Plate plate, RegisteredLinearSamples samples, bool wrongRegistration = false, int originX = 200, int originY = 200,
        bool includeStellarField = true)
    {
        const int side = 512;
        var camera = new CameraKey("oracle", "oracle");
        var run = Guid.NewGuid();
        var time = plate.Raw.ObservationStartedUtc!.Value;
        var pins = plate.Artifacts.Select(CenteredStackArtifactPin.From).ToArray();
        var hash = MeasuredAstrometryFixture.Hash("fixture");
        var linearLayout = new FrameLayoutDescriptor(side, side, side * 2, CameraPixelFormat.BayerRggb16,
            FrameByteOrder.LittleEndian, 16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.Rggb, 0,
            ushort.MaxValue, side * side * 2);
        var slots = Enumerable.Range(-2, 5).Select(offset =>
        {
            var start = time.AddSeconds(30 * offset);
            var capture = offset == 0 ? plate.Raw.CaptureId!.Value : Guid.NewGuid();
            var raw = offset == 0 ? pins[0] : FakePin();
            return new CenteredStackSlot(offset, start.AddSeconds(.5), CenteredSlotState.Ready, capture,
                new(new(camera, run, capture), offset + 3, start, start.AddSeconds(1), TimeSpan.TicksPerSecond,
                    raw, FakePin(), FakePin(), null,
                    offset == 0 ? new(pins[1], pins[2], pins[3], pins[4]) : null, hash, hash, hash, linearLayout));
        }).ToArray();

        var deadline = time.AddSeconds(65);
        var plan = new CenteredStackPlanV1(camera, run, "oracle-generation", plate.Raw.CaptureId!.Value,
            slots[2].Exposure!.Linear.ArtifactId, deadline, deadline, "oracle-closure", slots);
        var recipe = CenteredRegisteredStackRecipe.ExecutionIdentity(plan, "linear");
        var evidenceLineage = slots.SelectMany(slot => RetainedIds(slot.Exposure!)).ToArray();
        var planeLineage = slots.Select(slot => slot.Exposure!.Linear.ArtifactId).ToArray();
        var planes = new List<ProcessingArtifact>();
        var planePins = new List<CenteredStackOutputPin>();
        var integration = TimeSpan.FromSeconds(5);
        var support = new byte[side * side];
        var channels = Enumerable.Range(0, 3).Select(_ => new byte[side * side * 2]).ToArray();
        if (includeStellarField)
        {
            // The independent reference image supplies stellar comparison samples outside
            // the oracle patch. Its pixels are identical in all three composed channels.
            Array.Fill(support, (byte)31);
            foreach (var channel in channels)
            {
                plate.Raw.Payload.Span.CopyTo(channel);
            }
        }
        for (var index = 0; index < Pixels; index++)
        {
            var target = (originY + index / Width) * side + originX + index % Width;
            support[target] = samples.Support.Span[index];
            for (var channel = 0; channel < channels.Length; channel++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(channels[channel].AsSpan(target * 2), samples.Codes.Span[index * 3 + channel]);
            }
        }

        AddPlane("red", channels[0]);
        AddPlane("green", channels[1]);
        AddPlane("blue", channels[2]);
        AddPlane("support", support);
        var valid = support.Count(value => CenteredRegisteredStackEvidence.ContributorsAt(value) >= 2);
        var checkedPoints = Enumerable.Range(0, 5).Select(index => new RegistrationCentroidMatch(index, index, .05)).ToArray();
        var checkedHoldout = Enumerable.Range(5, 3).Select(index => new RegistrationCentroidMatch(index, index, .07)).ToArray();
        var decisions = slots.Select((slot, index) => new CenteredStackSourceEvidence(slot.Offset, slot.CaptureId,
            CenteredRegistrationReason.Contributed, slot.Offset == 0 ? CenteredRegistrationMethod.ReferenceIdentity :
                CenteredRegistrationMethod.VerifiedCentroidSimilarity, slot.Offset == 0 ? null : new ImageSimilarity(1, 0, 0, 0),
            support.Count(value => (value & (1 << index)) != 0),
            slot.Offset == 0 ? null : new(wrongRegistration ? DirectRegistrationStatus.ResidualExceeded :
                DirectRegistrationStatus.Verified, checkedPoints, checkedHoldout),
            slot.Offset == 0 ? new(plate.Solution.SolutionIdentitySha256, plate.Solution.Assessment.IdentitySha256,
                plate.Solution.Assessment.ReasonCode, plate.Solution.UncertaintyReasonCode) : null)).ToArray();
        var evidence = new CenteredRegisteredStackEvidence(CenteredRegisteredStackEvidence.CurrentSchemaVersion,
            plan.ToAuxiliaryInput().IdentitySha256!, plan, CenteredStackDisposition.Registered, time.AddSeconds(.5),
            time.AddSeconds(-60), time.AddSeconds(61), integration.Ticks, side, side, valid,
            CenteredRegisteredStackEvidence.LinearUnits, decisions, planePins);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(
            JsonSerializer.SerializeToElement(evidence, RegistrationJson)));
        var checksum = ProcessingIdentity.ComputePayloadSha256(bytes);
        var evidenceVariant = "oracle-" + CenteredRegisteredStackRecipe.EvidenceVariant;
        var evidenceOutput = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, evidenceVariant,
            recipe.IdentitySha256, evidenceLineage);
        var metadata = new ProcessingProduct(FrameArtifactRole.Metadata, evidenceVariant, evidenceOutput,
            CenteredRegisteredStackEvidence.MediaType, null, bytes, checksum, recipe, [], evidenceLineage,
            integration, plate.Raw.Compatibility)
        {
            Kind = ProcessingProductKind.Metadata,
            SchemaVersion = CenteredRegisteredStackEvidence.CurrentSchemaVersion,
            ContentIdentitySha256 = checksum
        };
        return (ToArtifact(metadata, plate.Raw), planes.ToArray());

        CenteredStackArtifactPin FakePin() => new(Guid.NewGuid(), hash, hash, hash, null, hash);

        static IEnumerable<Guid> RetainedIds(CenteredStackExposurePin exposure)
        {
            yield return exposure.Raw.ArtifactId;
            yield return exposure.Linear.ArtifactId;
            yield return exposure.ValidityMask.ArtifactId;
            if (exposure.Plate is { } plate)
            {
                yield return plate.Associations.ArtifactId;
                yield return plate.Scene.ArtifactId;
                yield return plate.Context.ArtifactId;
                yield return plate.Solution.ArtifactId;
            }
        }

        void AddPlane(string channel, byte[] payload)
        {
            var isSupport = channel == "support";
            var variant = "oracle-" + channel;
            var identity = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Combined, variant,
                recipe.IdentitySha256, planeLineage);
            var id = ProcessingIdentity.CreateArtifactId(identity);
            var media = isSupport ? CenteredRegisteredStackEvidence.SupportMediaType : "application/x-hvo-linear-frame";
            var layout = new FrameLayoutDescriptor(side, side, side * (isSupport ? 1 : 2),
                isSupport ? CameraPixelFormat.Mono8 : CameraPixelFormat.Mono16,
                isSupport ? FrameByteOrder.NotApplicable : FrameByteOrder.LittleEndian, isSupport ? 8 : 16,
                isSupport ? 8 : 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, isSupport ? 31 : ushort.MaxValue,
                payload.Length);
            planes.Add(new(id, FrameArtifactRole.Combined, variant, recipe.IdentitySha256, media, layout,
                payload, time, integration, plate.Raw.Compatibility, SourceArtifactIds: planeLineage)
            { CaptureId = plate.Raw.CaptureId, RecipeDescriptor = recipe.Descriptor });
            planePins.Add(new(channel, id, identity, ProcessingIdentity.ComputePayloadSha256(payload)));
        }
    }

    private static ProcessingArtifact ToArtifact(ProcessingProduct product, ProcessingArtifact raw) =>
        new(ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256), product.Role, product.Variant,
            product.Recipe.IdentitySha256, product.MediaType, product.Layout, product.Payload, raw.CreatedUtc,
            product.TotalIntegration, product.Compatibility, SourceArtifactIds: product.SourceArtifactIds)
        {
            CaptureId = raw.CaptureId,
            ProductKind = product.Kind,
            SchemaVersion = product.SchemaVersion,
            ContentIdentitySha256 = product.ContentIdentitySha256,
            RecipeDescriptor = product.Recipe.Descriptor
        };

    private sealed record Plate(ProcessingArtifact Raw, ProcessingArtifact[] Artifacts, MeasuredAstrometrySolutionV1 Solution,
        AstrometricMapping Mapping);
}
