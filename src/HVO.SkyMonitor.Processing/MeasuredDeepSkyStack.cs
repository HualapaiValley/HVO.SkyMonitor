using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

public enum DeepSkyStackStatus { Insufficient, AssessedEmpty, Qualified }
public enum DeepSkyStackReason
{
    NoRegisteredStack, InvalidRegistration, InsufficientLocalSupport, SearchLimit,
    ReferenceMappingUnavailable, ReferenceMappingMismatch, CatalogUnavailable, CatalogLimit,
    NoEligibleSources, NoUniqueAssociation, QualifiedAssociations, InsufficientStellarProfile
}

public enum DeepSkyIdentificationStatus
{
    NoCatalogObject, CompetingObjects, CompetingDetections, InsufficientLocalSupport, IncompatibleExtent, Qualified,
    InsufficientStellarProfile
}

public sealed record DeepSkyStackIdentification(int DetectionId, DeepSkyIdentificationStatus Status,
    string? CanonicalId = null, string? Designation = null, PixelPoint? ExpectedPixel = null,
    double? ResidualPixels = null, int PlausibleObjectCount = 0);

public sealed record MeasuredDeepSkyStackV1(string SchemaVersion, CenteredStackOutputPin Stack,
    string StackRecipeSha256, string AssessmentRecipeSha256, string PlanSha256,
    Guid? ReferenceCaptureId, DateTimeOffset? ReferenceMidpointUtc, string? ReferenceGeometrySha256,
    IReadOnlyList<Guid> Contributors, IReadOnlyList<CenteredStackOutputPin> Planes,
    int Width, int Height, string Units, CatalogSnapshotIdentity Catalog,
    string? MappingSolutionSha256, string? MappingAssessmentSha256, double? MappingVerificationRmsPixels,
    DeepSkyStackStatus Status, DeepSkyStackReason Reason, ExtendedStackExtraction? Extraction,
    int CatalogCentersInField, int CatalogCentersWithLocalSupport, IReadOnlyList<DeepSkyStackIdentification> Identifications)
{
    public const string CurrentSchemaVersion = "measured-deep-sky-stack-v1";
    public const string MediaType = "application/vnd.hvo.measured-deep-sky-stack+json";
    public const int MaximumBytes = 256 * 1024;

    public static MeasuredDeepSkyStackV1 Read(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is <= 0 or > MaximumBytes)
        {
            throw new ArgumentException("The assessment exceeds its payload bound.", nameof(bytes));
        }
        var data = JsonSerializer.Deserialize<MeasuredDeepSkyStackV1>(bytes.Span, CenteredStackPlanV1.Json);
        if (data is null || data.SchemaVersion != CurrentSchemaVersion ||
            !CenteredStackPlanV1.Serialize(data).AsSpan().SequenceEqual(bytes.Span))
        {
            throw new ArgumentException("Invalid canonical measured stack assessment.", nameof(bytes));
        }
        return data;
    }
}

/// <summary>Measures a selected, finished registered stack before accessing celestial catalog positions.</summary>
public sealed class MeasuredDeepSkyStackRecipe(IDeepSkyCatalog? catalog) : IProcessingRecipe
{
    public const string RecipeName = "measured-deep-sky-stack";
    public const string AlgorithmVersion = "measured-deep-sky-stack-qualification-v2";
    public const string OutputVariant = "measured-deep-sky-stack";
    private const int MaximumCatalogObjects = 32_768;
    private const double MatchRadius = 2;
    private const double StellarVetoRadius = ExtendedStackSourceExtractor.ApertureRadius;
    private const double StellarProfileRadius = 160;
    private const int MinimumNearbyStellarProfiles = 2;
    private const double MinimumResolvedWidthRatio = 1.2;
    private const double MinimumObjectMajorPixels = 3;
    private const double MaximumObjectMajorPixels = 30;

    public ProcessingRecipeDefinition Definition { get; } =
        new(RecipeName, "1.0.0", AlgorithmVersion, ProcessingOperationKind.Analyzer);

    public JsonElement NormalizeOptions(JsonElement options)
    {
        var pinned = options.Deserialize<CatalogSnapshotIdentity>(CenteredStackPlanV1.Json);
        if (pinned is null || string.IsNullOrWhiteSpace(pinned.CatalogId) ||
            string.IsNullOrWhiteSpace(pinned.PackageVersion) || string.IsNullOrWhiteSpace(pinned.SchemaVersion) ||
            string.IsNullOrWhiteSpace(pinned.PreprocessingVersion) ||
            pinned.DatabaseSha256 is not { Length: 64 } || !pinned.DatabaseSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The assessment requires an exact catalog snapshot.", nameof(options));
        }

        return ProcessingRecipeSupport.Normalize(new
        {
            catalog = pinned,
            extraction = ExtendedStackSourceExtractor.AlgorithmVersion,
            coordinates = "delivered-pixel-edges-v1",
            luminance = "linear-rec709-v1",
            minimumContributors = ExtendedStackSourceExtractor.MinimumContributors,
            apertureRadius = ExtendedStackSourceExtractor.ApertureRadius,
            backgroundRadius = ExtendedStackSourceExtractor.BackgroundRadius,
            searchStride = ExtendedStackSourceExtractor.SearchStride,
            peakLimit = ExtendedStackSourceExtractor.MaximumEvaluatedPeaks,
            detectionLimit = ExtendedStackSourceExtractor.MaximumDetections,
            significance = ExtendedStackSourceExtractor.MinimumSignificance,
            matchRadius = MatchRadius,
            measuredStellarVetoRadius = StellarVetoRadius,
            stellarProfileRadius = StellarProfileRadius,
            minimumNearbyStellarProfiles = MinimumNearbyStellarProfiles,
            minimumResolvedWidthRatio = MinimumResolvedWidthRatio,
            catalogSupport = "sampled-aperture-and-same-slot-noise-v1",
            objectMajorAxisRangePixels = new[] { MinimumObjectMajorPixels, MaximumObjectMajorPixels },
            maximumCatalogObjects = MaximumCatalogObjects
        });
    }

    public static string ComputeGeometryIdentity(ExposureGeometryV1 geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return ProcessingIdentity.ComputePayloadSha256(CenteredStackPlanV1.Serialize(geometry));
    }

    public static CenteredRegisteredStackEvidence ReadStackEvidence(ProcessingArtifact evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return RegisteredDeepSkyInput.Read(evidence);
    }

    public static ProcessingExecutionRequest CreateRequest(ProcessingArtifact evidence, IReadOnlyList<ProcessingArtifact> inputs,
        CatalogSnapshotIdentity catalog)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(catalog);
        var stack = ReadStackEvidence(evidence);
        var byId = inputs.ToDictionary(artifact => artifact.ArtifactId);
        var pinnedIds = PinnedInputIds(stack).ToArray();
        if (inputs.Any(input => !pinnedIds.Contains(input.ArtifactId)))
        {
            throw new ArgumentException("The request contains an input outside the frozen stack.", nameof(inputs));
        }
        var ordered = pinnedIds.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
        var primary = stack.Outputs.Count == 0 ? evidence : byId[stack.Outputs[0].ArtifactId];
        return new(RecipeName, JsonSerializer.SerializeToElement(catalog, CenteredStackPlanV1.Json),
            ProcessingInputSelector.RecipeResult(primary.Role, primary.Variant, primary.RecipeIdentitySha256),
            [evidence, .. ordered], OutputVariant, InputArtifactId: primary.ArtifactId);
    }

    public string ExpectedOutputIdentity(ProcessingExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, request.OutputVariant,
            Identity(request).IdentitySha256, request.Inputs.Select(artifact => artifact.ArtifactId).ToArray());
    }

    private ProcessingRecipeIdentity Identity(ProcessingExecutionRequest request) => ProcessingIdentity.CreateRecipeIdentity(
        Definition, ProcessingIdentity.BindExecutionInputs(NormalizeOptions(request.Options), request.Input, request.Annotation,
            request.AuxiliaryInputs));

    private static IEnumerable<Guid> PinnedInputIds(CenteredRegisteredStackEvidence stack)
    {
        foreach (var plane in stack.Outputs)
        {
            yield return plane.ArtifactId;
        }
        if (stack.Plan.Slots[2].Exposure is { Plate: { } plate } reference)
        {
            yield return reference.Raw.ArtifactId;
            yield return plate.Associations.ArtifactId;
            yield return plate.Scene.ArtifactId;
            yield return plate.Context.ArtifactId;
            yield return plate.Solution.ArtifactId;
        }
    }

    public ValueTask<ProcessingOutcome> ExecuteAsync(ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        var expectedIdentity = Identity(request);
        if (request.RecipeName != RecipeName || request.OutputVariant != OutputVariant ||
            request.Input.Kind != ProcessingInputKind.RecipeResult || request.Annotation is not null ||
            request.AuxiliaryInputs is { Count: > 0 } || identity.IdentitySha256 != expectedIdentity.IdentitySha256 ||
            request.Inputs is not [{ } evidence, ..] || request.Inputs.Count > 10 ||
            request.Inputs.Select(x => x.ArtifactId).Distinct().Count() != request.Inputs.Count ||
            ProcessingRecipeSupport.ResolveSingle(request, out _) is not { } selected)
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure("deep-sky.invalid-request"));
        }

        var pinned = request.Options.Deserialize<CatalogSnapshotIdentity>(CenteredStackPlanV1.Json)!;
        try
        {
            var stack = RegisteredDeepSkyInput.Read(evidence);
            var byId = request.Inputs.ToDictionary(input => input.ArtifactId);
            if (!request.Inputs.Skip(1).Select(input => input.ArtifactId)
                .SequenceEqual(PinnedInputIds(stack).Where(byId.ContainsKey)))
            {
                return ValueTask.FromResult(ProcessingOutcome.TerminalFailure("deep-sky.invalid-request-order"));
            }
            var assessment = Assess(stack, evidence, selected, byId, identity, pinned, cancellationToken);
            var bytes = CenteredStackPlanV1.Serialize(assessment);
            if (bytes.Length > MeasuredDeepSkyStackV1.MaximumBytes)
            {
                throw new InvalidDataException("The assessment exceeded its output bound.");
            }
            var product = ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata, request.OutputVariant,
                MeasuredDeepSkyStackV1.MediaType, null, bytes, identity,
                [new(RecipeName, AlgorithmVersion), new("extended-source-extractor", ExtendedStackSourceExtractor.AlgorithmVersion)],
                request.Inputs, evidence.Integration, evidence.Compatibility, ProcessingProductKind.Metadata,
                MeasuredDeepSkyStackV1.CurrentSchemaVersion, ProcessingIdentity.ComputePayloadSha256(bytes));
            return ValueTask.FromResult(ProcessingOutcome.Produced(product));
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or InvalidDataException or
            InvalidOperationException or KeyNotFoundException)
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure("deep-sky.invalid-stack-evidence"));
        }
    }

    private MeasuredDeepSkyStackV1 Assess(CenteredRegisteredStackEvidence stack, ProcessingArtifact evidence,
        ProcessingArtifact selected, Dictionary<Guid, ProcessingArtifact> artifacts, ProcessingRecipeIdentity identity,
        CatalogSnapshotIdentity pinned, CancellationToken cancellationToken)
    {
        var reference = stack.Plan.Slots[2].Exposure;
        var contributors = stack.Sources.Where(source => source.Reason == CenteredRegistrationReason.Contributed).ToArray();
        ExtendedStackExtraction? extraction = null;
        CenteredStackPlate? plate = null;
        var catalogCenters = 0;
        var supportedCatalogCenters = 0;
        if (stack.Disposition != CenteredStackDisposition.Registered)
        {
            return Result(DeepSkyStackStatus.Insufficient, DeepSkyStackReason.NoRegisteredStack);
        }
        if (reference is null || !RegisteredDeepSkyInput.HasVerifiedRegistration(stack, contributors))
        {
            return Result(DeepSkyStackStatus.Insufficient, DeepSkyStackReason.InvalidRegistration);
        }
        var pixels = RegisteredDeepSkyInput.Decode(stack, evidence, selected, artifacts, cancellationToken);
        extraction = ExtendedStackSourceExtractor.Extract(pixels.Luminance, pixels.Support.Span,
            stack.Width, stack.Height, out var searchSupport, out var stellarProfile, cancellationToken);
        if (extraction.LimitExceeded)
        {
            return Result(DeepSkyStackStatus.Insufficient, DeepSkyStackReason.SearchLimit);
        }
        if (extraction.SupportedPositions == 0)
        {
            return Result(DeepSkyStackStatus.Insufficient, DeepSkyStackReason.InsufficientLocalSupport);
        }

        if (reference.Plate is not { } pin || stack.Sources[2].Plate is not { } plateEvidence)
        {
            return Result(DeepSkyStackStatus.Insufficient, DeepSkyStackReason.ReferenceMappingUnavailable);
        }
        if (new[] { reference.Raw, pin.Associations, pin.Scene, pin.Context, pin.Solution }
            .Any(input => !artifacts.ContainsKey(input.ArtifactId)))
        {
            return Result(DeepSkyStackStatus.Insufficient, DeepSkyStackReason.ReferenceMappingUnavailable);
        }

        try
        {
            var associations = Load(pin.Associations);
            if (!CenteredStackPlate.MatchesMeasurementPin(associations, reference.Measurement))
            {
                return Result(DeepSkyStackStatus.Insufficient, DeepSkyStackReason.ReferenceMappingMismatch);
            }
            plate = CenteredStackPlate.Verify(Load(reference.Raw), associations, Load(pin.Scene),
                Load(pin.Context), Load(pin.Solution), cancellationToken);
            if (plate.Solution.SolutionIdentitySha256 != plateEvidence.SolutionSha256 ||
                plate.Solution.Assessment.IdentitySha256 != plateEvidence.AssessmentSha256 ||
                plate.Solution.Assessment.Frame.MidpointUtc != stack.ReferenceMidpointUtc ||
                Load(reference.Raw).Layout is not { } layout || layout.Width != stack.Width || layout.Height != stack.Height)
            {
                return Result(DeepSkyStackStatus.Insufficient, DeepSkyStackReason.ReferenceMappingMismatch);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            KeyNotFoundException or JsonException or InvalidDataException)
        {
            return Result(DeepSkyStackStatus.Insufficient, DeepSkyStackReason.ReferenceMappingMismatch);
        }

        if (plate.Mapping is not { } mapping)
        {
            return Result(DeepSkyStackStatus.Insufficient, DeepSkyStackReason.ReferenceMappingUnavailable);
        }

        // Apply stellar measurements after blind extraction, before considering OpenNGC positions.
        var stellar = MeasuredStellarAssociationJson.Parse(Load(pin.Associations).Payload).Associations!;
        extraction = ApplyStellarProfile(extraction, stellar, plate.Solution, pixels.Luminance, stellarProfile);
        if (catalog?.Semantics.SnapshotIdentity != pinned ||
            catalog.Semantics is not
            {
                SourceName: "OpenNGC", CoordinateEpoch: "J2000.0",
                CoordinateFrame: "equatorial-j2000-icrs-aligned", AxisUnit: "arcminute"
            })
        {
            return Result(DeepSkyStackStatus.Insufficient, DeepSkyStackReason.CatalogUnavailable);
        }
        if (catalog.Objects.Count > MaximumCatalogObjects)
        {
            return Result(DeepSkyStackStatus.Insufficient, DeepSkyStackReason.CatalogLimit);
        }

        var candidates = extraction.Detections.Where(detection =>
            detection.Rejection is null or ExtendedStackRejection.StellarProfileUnavailable).ToArray();
        var (matches, inField, supported) = Identify(candidates, catalog.Objects, mapping, stack.ReferenceMidpointUtc!.Value,
            searchSupport, stack.Width, stack.Height, cancellationToken);
        catalogCenters = inField;
        supportedCatalogCenters = supported;
        if (matches.Any(match => match.Status == DeepSkyIdentificationStatus.Qualified))
        {
            return Result(DeepSkyStackStatus.Qualified, DeepSkyStackReason.QualifiedAssociations, matches);
        }
        if (supported < inField)
        {
            return Result(DeepSkyStackStatus.Insufficient, DeepSkyStackReason.InsufficientLocalSupport, matches);
        }
        if (matches.Any(match => match.Status == DeepSkyIdentificationStatus.InsufficientStellarProfile))
        {
            return Result(DeepSkyStackStatus.Insufficient, DeepSkyStackReason.InsufficientStellarProfile, matches);
        }
        return Result(DeepSkyStackStatus.AssessedEmpty, candidates.Length == 0
            ? DeepSkyStackReason.NoEligibleSources : DeepSkyStackReason.NoUniqueAssociation, matches);

        ProcessingArtifact Load(CenteredStackArtifactPin input) =>
            artifacts.TryGetValue(input.ArtifactId, out var artifact) &&
            artifact.CaptureId == stack.Plan.ReferenceCaptureId && input.Matches(artifact)
                ? artifact : throw new InvalidDataException("The exact retained reference observation is unavailable.");

        MeasuredDeepSkyStackV1 Result(DeepSkyStackStatus status, DeepSkyStackReason reason,
            IReadOnlyList<DeepSkyStackIdentification>? matches = null) =>
            new(MeasuredDeepSkyStackV1.CurrentSchemaVersion,
                new("evidence", evidence.ArtifactId, RegisteredDeepSkyInput.OutputIdentity(evidence),
                    ProcessingIdentity.ComputePayloadSha256(evidence.Payload)),
                evidence.RecipeIdentitySha256, identity.IdentitySha256, stack.PlanSha256, stack.Plan.ReferenceCaptureId,
                stack.ReferenceMidpointUtc, reference?.GeometrySha256, contributors.Select(source => source.CaptureId!.Value).ToArray(),
                stack.Outputs, stack.Width, stack.Height, stack.Units, pinned, plate?.Solution.SolutionIdentitySha256,
                plate?.Solution.Assessment.IdentitySha256, plate?.Solution.Assessment.Quality?.VerificationRmsPixels,
                status, reason, extraction, catalogCenters, supportedCatalogCenters, matches ?? []);
    }

    internal static ExtendedStackExtraction ApplyStellarProfile(ExtendedStackExtraction extraction,
        MeasuredStellarAssociationsV1 stellar, MeasuredAstrometrySolutionV1 solution, ReadOnlySpan<float> luminance,
        StellarStackProfileQuery query)
    {
        if (!extraction.Detections.Any(detection => detection.Rejection is null))
        {
            return extraction;
        }
        var stars = stellar.Associations.Select(star => new PixelPoint(star.MeasuredX, star.MeasuredY)).ToArray();
        var planets = (stellar.PointSourcePlanets?.Associations ?? [])
            .Select(planet => new PixelPoint(planet.MeasuredX, planet.MeasuredY)).ToArray();
        var pointSources = planets.Length == 0 ? stars : [.. stars, .. planets];
        var measuredSources = pointSources.Concat(stellar.UnassociatedDetections.Select(source => new PixelPoint(source.X, source.Y))).ToArray();
        var verified = solution.Residuals.NativeAssociations.Select(pair => pair.DetectionIndex).ToHashSet();
        var profiles = new List<ExtendedStackDetection>();
        foreach (var star in stellar.Associations)
        {
            var center = new PixelPoint(star.MeasuredX, star.MeasuredY);
            if (!star.Saturated && !star.Trailed && verified.Contains(star.DetectionIndex) &&
                measuredSources.Count(other => Separation(center, other) < 2 * StellarVetoRadius) == 1 &&
                query(luminance, center) is { RadiusRmsPixels: > 0 } profile &&
                profile.Rejection is null or ExtendedStackRejection.StellarStructure &&
                Separation(center, profile.Centroid) <= MatchRadius)
            {
                profiles.Add(profile);
            }
        }
        var rejected = 0;
        var detections = extraction.Detections.Select(detection =>
        {
            if (detection.Rejection is not null)
            {
                return detection;
            }
            var classified = Classify(detection);
            rejected += classified.Rejection.HasValue ? 1 : 0;
            return classified;
        }).ToArray();
        return extraction with { Detections = detections, RejectedPeaks = extraction.RejectedPeaks + rejected };

        ExtendedStackDetection Classify(ExtendedStackDetection detection)
        {
            if (pointSources.Any(source => Separation(detection.Centroid, source) <= StellarVetoRadius))
            {
                return detection with { Rejection = ExtendedStackRejection.StellarStructure };
            }
            var nearby = profiles.Where(profile => profile.SupportSlots == detection.SupportSlots &&
                Separation(detection.Centroid, profile.Centroid) <= StellarProfileRadius).ToArray();
            var maximumRadius = nearby.Length == 0 ? (double?)null : nearby.Max(profile => profile.RadiusRmsPixels);
            return detection with
            {
                StellarProfile = new(nearby.Length, maximumRadius),
                Rejection = nearby.Length < MinimumNearbyStellarProfiles
                    ? ExtendedStackRejection.StellarProfileUnavailable
                    : detection.RadiusRmsPixels < MinimumResolvedWidthRatio * maximumRadius
                        ? ExtendedStackRejection.StellarStructure : null
            };
        }

        static double Separation(PixelPoint first, PixelPoint second) =>
            double.Hypot(first.X - second.X, first.Y - second.Y);
    }

    private static (DeepSkyStackIdentification[] Identifications, int CatalogCenters, int SupportedCenters) Identify(
        IReadOnlyList<ExtendedStackDetection> detections, IReadOnlyList<DeepSkyObject> objects, AstrometricMapping mapping,
        DateTimeOffset midpoint, Func<PixelPoint, byte?> searchSupport, int width, int height, CancellationToken cancellationToken)
    {
        var considered = 0;
        var supported = 0;
        var claims = detections.ToDictionary(detection => detection.Id,
            _ => new Dictionary<string, (DeepSkyObject Object, PixelPoint Expected, double Residual, byte? LocalSupport, bool CompatibleExtent)>(StringComparer.Ordinal));
        foreach (var item in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.IsStellar || mapping.SkyToPixel(EquatorialPrecession.PrecessJ2000(
                new(item.RightAscensionHours, item.DeclinationDegrees), midpoint)) is not { } expected ||
                expected.X < 0 || expected.X >= width || expected.Y < 0 || expected.Y >= height ||
                mapping.PixelToSky(expected) is not { AboveGeometricHorizon: true })
            {
                continue;
            }
            var scale = mapping.LocalPixelScale(expected);
            var major = item.MajorAxisArcminutes * 60 / Math.Sqrt(
                (scale.XArcsecondsPerPixel ?? double.NaN) * (scale.YArcsecondsPerPixel ?? double.NaN));
            var compatibleExtent = item.ObjectType is not ("DrkN" or "Nova" or "Other") &&
                major is >= MinimumObjectMajorPixels and <= MaximumObjectMajorPixels;
            var localSupport = searchSupport(expected);
            if (compatibleExtent)
            {
                considered++;
                if (localSupport is not null)
                {
                    supported++;
                }
            }
            foreach (var detection in detections)
            {
                var dx = expected.X - detection.Centroid.X;
                var dy = expected.Y - detection.Centroid.Y;
                var residual = Math.Sqrt(dx * dx + dy * dy);
                if (residual <= MatchRadius)
                {
                    // OpenNGC Objects contains canonical rows only. Alias lookups are display/search concerns.
                    claims[detection.Id].TryAdd(item.Id, (item, expected, residual, localSupport, compatibleExtent));
                }
            }
        }

        var conflicts = claims.Values.SelectMany(matches => matches.Keys).GroupBy(id => id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return (detections.Select(detection => Classify(detection, claims[detection.Id])).ToArray(), considered, supported);

        DeepSkyStackIdentification Classify(ExtendedStackDetection detection,
            Dictionary<string, (DeepSkyObject Object, PixelPoint Expected, double Residual, byte? LocalSupport, bool CompatibleExtent)> candidates)
        {
            if (candidates.Count != 1)
            {
                return new(detection.Id, candidates.Count == 0 ? DeepSkyIdentificationStatus.NoCatalogObject :
                    DeepSkyIdentificationStatus.CompetingObjects, PlausibleObjectCount: candidates.Count);
            }
            var (item, expected, residual, localSupport, compatibleExtent) = candidates.Values.Single();
            var status = !compatibleExtent ? DeepSkyIdentificationStatus.IncompatibleExtent :
                localSupport != detection.SupportSlots ? DeepSkyIdentificationStatus.InsufficientLocalSupport :
                detection.Rejection == ExtendedStackRejection.StellarProfileUnavailable ? DeepSkyIdentificationStatus.InsufficientStellarProfile :
                conflicts[item.Id] > 1 ? DeepSkyIdentificationStatus.CompetingDetections :
                DeepSkyIdentificationStatus.Qualified;
            return new(detection.Id, status, item.Id, item.DisplayName, expected, residual, 1);
        }
    }
}
