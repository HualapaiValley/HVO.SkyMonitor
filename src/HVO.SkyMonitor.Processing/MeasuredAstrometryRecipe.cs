using System.Diagnostics;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>
/// Explicit post-admission analyzer. Registration makes the recipe available, never active in a host/live graph.
/// It consumes retained measured evidence without decoding or measuring pixels and never writes a calibration.
/// </summary>
internal sealed class MeasuredAstrometryRecipe : IProcessingRecipe
{
    internal const string AlgorithmVersion = "measured-astrometry-v1";
    public ProcessingRecipeDefinition Definition { get; } = new(BuiltInProcessingRecipes.MeasuredAstrometry,
        "1.0.0", AlgorithmVersion, ProcessingOperationKind.Analyzer);
    internal static readonly ProcessingAlgorithmIdentity[] Algorithms =
    [
        new("measured-astrometry", AlgorithmVersion),
        new("astrometric-solver", AstrometricConventions.SolverVersion),
        new("astrometric-residual-diagnostics", AstrometricResidualDiagnostics.CurrentSchemaVersion),
        new("astrometric-uncertainty", AstrometricUncertaintyOptions.EstimatorVersion)
    ];

    public JsonElement NormalizeOptions(JsonElement options)
    {
        var parsed = ProcessingRecipeSupport.ParseOptions<MeasuredAstrometryOptions>(options);
        var normalized = parsed with { Solver = parsed.Solver ?? new(), Diagnostics = parsed.Diagnostics ?? new(), Uncertainty = parsed.Uncertainty ?? new() };
        normalized.Solver!.Validate(); normalized.Diagnostics!.Validate(); normalized.Uncertainty!.Validate();
        if (!double.IsFinite(parsed.MaximumExecutionMilliseconds) || parsed.MaximumExecutionMilliseconds is <= 0 or > 60000)
            throw new ArgumentException("Astrometry execution budget must be in (0,60000] milliseconds.", nameof(options));
        return ProcessingRecipeSupport.Normalize(normalized);
    }

    internal static ProcessingArtifact ResolveMeasured(ProcessingExecutionRequest request)
    {
        var aux = request.AuxiliaryInputs?.SingleOrDefault(a => a.Name == BuiltInProcessingRecipes.MeasuredStellarAssociationsInputName);
        if (aux?.Kind != ProcessingAuxiliaryInputKind.Artifact || aux.ArtifactId is null || aux.Selector is null)
            throw new ArgumentException("An explicit retained measured artifact is required.", nameof(request));
        var matches = request.Inputs.Where(i => i.ArtifactId == aux.ArtifactId && ProcessingRecipeSupport.Matches(i, aux.Selector)).ToArray();
        if (matches.Length != 1) throw new ArgumentException("The retained measured artifact is unavailable or ambiguous.", nameof(request));
        return matches[0];
    }

    internal static bool MeasuredInputIsValid(ProcessingArtifact measuredArtifact, ProcessingArtifact raw)
    {
        return measuredArtifact.ProductKind == ProcessingProductKind.Metadata && measuredArtifact.Role == FrameArtifactRole.Metadata &&
            measuredArtifact.MediaType == StructuredProcessingProductContracts.MeasuredStellarAssociationsMediaType &&
            measuredArtifact.SchemaVersion == MeasuredStellarAssociationsV1.CurrentSchemaVersion &&
            measuredArtifact.ContentIdentitySha256 is not null && measuredArtifact.SourceArtifactIds is { Count: 1 } &&
            measuredArtifact.SourceArtifactIds[0] == raw.ArtifactId &&
            measuredArtifact.RecipeDescriptor is
            {
                Name: BuiltInProcessingRecipes.MeasuredStellarAssociations,
                SemanticVersion: "1.0.0", ImplementationVersion: "measured-stellar-associations-v1"
            } &&
            MeasuredAstrometryJson.Same(ProcessingIdentity.CreateRecipeIdentity(measuredArtifact.RecipeDescriptor).IdentitySha256,
                measuredArtifact.RecipeIdentitySha256);
    }

    public ValueTask<ProcessingOutcome> ExecuteAsync(ProcessingExecutionRequest request, ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var raw = ProcessingRecipeSupport.ResolveSingle(request, out var failure);
        if (raw is null) return ValueTask.FromResult(failure!);
        if (raw.Role != FrameArtifactRole.Raw || !ProcessingRecipeSupport.TryValidateFrame(raw, out var layout, out _) ||
            (long)layout.Width * layout.Height > 16_777_216)
            return Fail("astrometry.raw-required");
        MeasuredAstrometryContextV1 context;
        MeasuredStellarAssociationsV1 measured;
        ProcessingArtifact measuredArtifact;
        ProjectedSceneV1 scene;
        ProcessingAuxiliaryInput contextInput;
        try
        {
            if (request.Annotation is not null || request.AuxiliaryInputs?.Count != 3) return Fail("astrometry.inputs-required");
            contextInput = request.AuxiliaryInputs.Single(a => a.Name == MeasuredAstrometryJson.ContextInputName);
            if (contextInput.Kind != ProcessingAuxiliaryInputKind.CanonicalJson || contextInput.SchemaVersion != MeasuredAstrometryContextV1.CurrentSchemaVersion ||
                !MeasuredAstrometryJson.Same(contextInput.IdentitySha256, ProcessingIdentity.ComputePayloadSha256(contextInput.Payload)))
                return Fail("astrometry.invalid-context");
            context = MeasuredAstrometryJson.ParseContext(contextInput.Payload);
            measuredArtifact = ResolveMeasured(request);
            if (!MeasuredInputIsValid(measuredArtifact, raw))
                return Fail("astrometry.invalid-measured-input");
            measured = MeasuredStellarAssociationJson.Parse(measuredArtifact.Payload).Associations
                ?? throw new ArgumentException("Invalid measured associations.");
            var sceneInput = request.AuxiliaryInputs.Single(a => a.Name == BuiltInProcessingRecipes.MeasuredStellarAssociationsSceneInputName);
            scene = ProjectedSceneJson.Parse(sceneInput.Payload).Scene ?? throw new ArgumentException("Invalid scene.");
            if (sceneInput.Kind != ProcessingAuxiliaryInputKind.CanonicalJson || sceneInput.SchemaVersion != scene.SchemaVersion ||
                !MeasuredAstrometryJson.Same(sceneInput.IdentitySha256, scene.SceneIdentitySha256)) return Fail("astrometry.scene-mismatch");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        {
            return Fail("astrometry.invalid-input");
        }
        var options = ProcessingRecipeSupport.ParseOptions<MeasuredAstrometryOptions>(identity.Descriptor.Options.GetProperty("parameters"));
        var calibration = new AstrometricCalibration(context.Projection, context.CalibrationVersion, context.ReadoutIdentitySha256);
        var catalog = MeasuredAstrometryJson.Catalog(context);
        var lineageFailure = LineageFailure(raw, measuredArtifact, measured, scene, context, calibration, catalog);
        if (lineageFailure is not null) return Fail(lineageFailure);

        // A catalog label is eligibility evidence, not a solver hint. Exclude saturation/trails from this first
        // fixed-centroid slice even when a presentation policy allows their labels. Preserve original detection IDs.
        cancellationToken.ThrowIfCancellationRequested();
        var qualified = Qualified(measured);
        var detections = Detections(qualified);
        // The retained producer rounds covariance to six decimals. A rounded zero/singular matrix is missing
        // covariance, never a manufactured positive floor. The shared estimator explicitly withholds incomplete data.
        var covariances = Covariances(qualified);
        var solver = options.Solver!;
        AstrometricFrameAssessment assessment;
        MeasuredAstrometryResidualSummaryV1 residuals;
        AstrometricFrameUncertainty? uncertainty = null;
        var uncertaintyReason = "not-accepted";
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = options.MaximumExecutionMilliseconds - Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        budget.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(0, remaining)));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining <= 0)
                assessment = FailureAssessment(context, calibration, catalog, solver, AstrometricAssessmentStatus.BudgetExceeded, "execution-budget");
            else if (measured.Measurement.Status != StellarMeasurementStatus.Completed)
                assessment = FailureAssessment(context, calibration, catalog, solver, AstrometricAssessmentStatus.Unavailable, "measurement-incomplete");
            else if (detections.Length > AstrometricCatalogData.MaximumEntries)
                assessment = FailureAssessment(context, calibration, catalog, solver, AstrometricAssessmentStatus.BudgetExceeded, "measured-entry-bound");
            else
            {
                var result = context.PreviousAccepted is { } prior
                    ? AstrometricSolver.Refine(context.Frame, calibration, catalog, detections, prior, solver, budget.Token)
                    : AstrometricSolver.Solve(context.Frame, calibration, catalog, detections, solver, budget.Token);
                assessment = result.Assessment;
                if (assessment.HasMeasuredMapping && result.Associations.Any(a =>
                    !qualified.Any(m => m.DetectionIndex == a.DetectionIndex && m.CatalogId == a.CatalogId)))
                    assessment = FailureAssessment(context, calibration, catalog, solver, AstrometricAssessmentStatus.Rejected, "measured-catalog-correspondence-mismatch");
                if (assessment.HasMeasuredMapping)
                {
                    var diagnostics = AstrometricResidualAnalyzer.Analyze(calibration, catalog, solver, result, detections,
                        covariances: covariances, options: options.Diagnostics, cancellationToken: budget.Token);
                    uncertainty = AstrometricUncertaintyEstimator.Estimate(calibration, catalog, solver, result, detections,
                        covariances, context.Clock, raw.Layout!.Readout, options: options.Uncertainty, cancellationToken: budget.Token);
                    uncertaintyReason = uncertainty.ReasonCode;
                    var fitting = result.Associations.Where(a => !a.Verification).Select(a => a.DetectionIndex).Order().ToArray();
                    var verification = result.Associations.Where(a => a.Verification).Select(a => a.DetectionIndex).Order().ToArray();
                    var used = fitting.Concat(verification).ToHashSet();
                    residuals = new(detections.Length, fitting, verification, detections.Where(d => !used.Contains(d.Index)).Select(d => d.Index).ToArray(),
                        assessment.Quality!.FittingRmsPixels, assessment.Quality.VerificationRmsPixels,
                        diagnostics.Residuals.Max(r => r.ResidualPixels), diagnostics.Conditioning, "available", result.Associations);
                    budget.Token.ThrowIfCancellationRequested();
                    return Produce();
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            assessment = FailureAssessment(context, calibration, catalog, solver, AstrometricAssessmentStatus.BudgetExceeded, "execution-budget");
            uncertainty = null; uncertaintyReason = "execution-budget";
        }
        cancellationToken.ThrowIfCancellationRequested();
        residuals = new(Math.Min(detections.Length, AstrometricCatalogData.MaximumEntries), [], [],
            detections.Take(AstrometricCatalogData.MaximumEntries).Select(d => d.Index).ToArray(), null, null, null, null, assessment.ReasonCode, []);
        // Exceeding the input bound never retains a truncated set as a complete measurement.
        if (detections.Length > AstrometricCatalogData.MaximumEntries)
            residuals = new(0, [], [], [], null, null, null, null, "measured-entry-bound", []);
        return Produce();

        ValueTask<ProcessingOutcome> Produce()
        {
            var solution = new MeasuredAstrometrySolutionV1(MeasuredAstrometrySolutionV1.CurrentSchemaVersion,
                identity.IdentitySha256, contextInput.IdentitySha256!, measuredArtifact.ArtifactId, measured.AssociationIdentitySha256,
                measuredArtifact.RecipeIdentitySha256, scene.SceneIdentitySha256, calibration.ReadoutIdentitySha256,
                context.CatalogPackage, assessment, residuals, uncertainty, uncertaintyReason);
            var payload = MeasuredAstrometryJson.Serialize(solution);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ProcessingOutcome.Produced(ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata,
                request.OutputVariant, MeasuredAstrometryJson.MediaType, null, payload, identity, Algorithms,
                [raw, measuredArtifact], raw.Integration, raw.Compatibility, ProcessingProductKind.Metadata,
                MeasuredAstrometrySolutionV1.CurrentSchemaVersion, solution.SolutionIdentitySha256)));
        }
    }

    internal static MeasuredStellarAssociationV1[] Qualified(MeasuredStellarAssociationsV1 measured) => measured.Associations
        .Where(a => a.LabelEligible && !a.Saturated && !a.Trailed && a.Flux > 0).ToArray();

    internal static AstrometricDetection[] Detections(IEnumerable<MeasuredStellarAssociationV1> qualified) => qualified
        .Select(a => new AstrometricDetection(a.DetectionIndex, new(a.MeasuredX, a.MeasuredY), a.Flux)).OrderBy(a => a.Index).ToArray();

    internal static AstrometricPixelCovariance[] Covariances(IEnumerable<MeasuredStellarAssociationV1> qualified) => qualified
        .Where(a => a.CovarianceXX is > 0 && a.CovarianceYY is > 0 &&
            a.CovarianceXX * a.CovarianceYY - a.CovarianceXY * a.CovarianceXY > 0)
        .Select(a => new AstrometricPixelCovariance(a.DetectionIndex, a.CovarianceXX!.Value, a.CovarianceXY!.Value, a.CovarianceYY!.Value))
        .OrderBy(a => a.DetectionIndex).ToArray();

    internal static string? LineageFailure(ProcessingArtifact raw, ProcessingArtifact measuredArtifact,
        MeasuredStellarAssociationsV1 measured, ProjectedSceneV1 scene, MeasuredAstrometryContextV1 context,
        AstrometricCalibration calibration, AstrometricCatalogData catalog)
    {
        if (measured.Measurement.AlgorithmVersion != StellarSourceMeasurer.AlgorithmVersion ||
            measured.Method.AlgorithmVersion != MeasuredStellarAssociator.AlgorithmVersion || measured.Method.LabelPolicyVersion != StellarLabelPolicy.Version)
            return "astrometry.unsupported-measurement";
        var frame = context.Frame;
        var source = measured.Source;
        if (context.MeasuredArtifactId != measuredArtifact.ArtifactId ||
            !MeasuredAstrometryJson.Same(context.MeasuredIdentitySha256, measured.AssociationIdentitySha256) ||
            !MeasuredAstrometryJson.Same(measuredArtifact.ContentIdentitySha256, measured.AssociationIdentitySha256) ||
            !MeasuredAstrometryJson.Same(context.MeasuredRecipeIdentitySha256, measuredArtifact.RecipeIdentitySha256) ||
            raw.CaptureId != frame.CaptureId || raw.ArtifactId != frame.SourceArtifactId || source.CaptureId != frame.CaptureId ||
            source.ArtifactId != raw.ArtifactId || source.Variant != raw.Variant || source.PixelFormat != raw.Layout!.PixelFormat ||
            !MeasuredAstrometryJson.Same(source.RecipeIdentitySha256, raw.RecipeIdentitySha256) ||
            !MeasuredAstrometryJson.Same(raw.DescriptorIdentitySha256, frame.SourceDescriptorSha256) ||
            !MeasuredAstrometryJson.Same(source.DescriptorIdentitySha256, frame.SourceDescriptorSha256) ||
            !MeasuredAstrometryJson.Same(ProcessingIdentity.ComputePayloadSha256(raw.Payload), frame.SourcePayloadSha256) ||
            raw.ObservationStartedUtc != frame.ExposureStartUtc || raw.ObservationEndedUtc is not { } observationEnd ||
            ProcessingArtifact.ResolveObservationEndedUtc(frame.ExposureStartUtc, observationEnd, raw.Integration) != frame.ExposureEndUtc ||
            raw.Integration != frame.ExposureEndUtc - frame.ExposureStartUtc ||
            frame.DetectionAlgorithmVersion != measured.Measurement.AlgorithmVersion ||
            !MeasuredAstrometryJson.Same(frame.DetectionSettingsIdentitySha256, measured.Measurement.SettingsIdentitySha256))
            return "astrometry.source-lineage-mismatch";
        if (scene.Source.CaptureId != frame.CaptureId || scene.Source.ArtifactId != raw.ArtifactId ||
            !MeasuredAstrometryJson.Same(scene.Source.ArtifactIdentitySha256, frame.SourceDescriptorSha256) ||
            !MeasuredAstrometryJson.Same(scene.SceneIdentitySha256, measured.Scene.SceneIdentitySha256) ||
            measured.Scene.Kind != scene.Kind || scene.Kind != ProjectedSceneKind.Predicted ||
            scene.Observer != frame.Observer || scene.EffectiveUtc != frame.MidpointUtc || scene.Refraction.Enabled ||
            scene.CoordinateConvention != ProjectedSceneCoordinateConvention.ContinuousTopLeftPixelEdge ||
            scene.ImageTransform != ProjectedSceneImageTransformV1.Identity(raw.Layout!.Width, raw.Layout.Height) ||
            scene.Projection.CalibrationVersion != calibration.CalibrationVersion || measured.Scene.CalibrationVersion != calibration.CalibrationVersion ||
            SceneProjection(scene.Projection) != calibration.Projection || source.WidthPixels != raw.Layout.Width || source.HeightPixels != raw.Layout.Height ||
            !MeasuredAstrometryJson.Same(context.ReadoutIdentitySha256, MeasuredAstrometryJson.ComputeReadoutIdentity(raw.Layout)) ||
            raw.Compatibility.LocationIdentitySha256 is { } location && !MeasuredAstrometryJson.Same(location, frame.ObserverIdentitySha256))
            return "astrometry.scene-time-calibration-mismatch";
        if (scene.Catalog.Name != catalog.Metadata.Name || scene.Catalog.Version != catalog.Metadata.Version ||
            !MeasuredAstrometryJson.Same(scene.Catalog.ChecksumSha256, catalog.Metadata.Checksum) ||
            scene.Catalog.SourceUrl != catalog.Metadata.SourceUrl || scene.Catalog.License != catalog.Metadata.License ||
            scene.Catalog.SchemaVersion != catalog.Metadata.SchemaVersion || scene.Catalog.PreprocessingVersion != context.CatalogPackage.PreprocessingVersion ||
            measured.Scene.CatalogName != scene.Catalog.Name || measured.Scene.CatalogVersion != scene.Catalog.Version ||
            !MeasuredAstrometryJson.Same(measured.Scene.CatalogChecksumSha256, catalog.Metadata.Checksum) ||
            measured.Scene.ProjectionAlgorithmVersion != scene.Projection.AlgorithmVersion || measured.Scene.AstronomyAlgorithmVersion != scene.AstronomyAlgorithmVersion ||
            measured.Associations.Any(a => !catalog.Stars.Any(s => s.Id == a.CatalogId && Math.Abs(s.Magnitude - a.Magnitude) < 1e-5)))
            return "astrometry.catalog-lineage-mismatch";
        var stars = catalog.Stars.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var predictions = scene.Objects.Where(o => o.Kind == CelestialObjectKind.Star).ToDictionary(o => o.Id, StringComparer.Ordinal);
        if (predictions.Count != measured.Associations.Count + measured.UnmatchedPredictions.Count ||
            measured.Associations.Any(a => !predictions.ContainsKey(a.CatalogId)) || measured.UnmatchedPredictions.Any(a => !predictions.ContainsKey(a.CatalogId)) ||
            predictions.Values.Any(p => !stars.TryGetValue(p.Id, out var star) || Math.Abs(star.RightAscensionHours - p.J2000Equatorial.RightAscensionHours) > 1e-8 ||
                Math.Abs(star.DeclinationDegrees - p.J2000Equatorial.DeclinationDegrees) > 1e-8 || Math.Abs(star.Magnitude - p.Magnitude) > 1e-6))
            return "astrometry.catalog-selection-mismatch";
        return null;
    }

    internal static ProjectionContext SceneProjection(ProjectedSceneProjection p) => new(p.Model, p.PrincipalPointX, p.PrincipalPointY,
        p.FocalLengthXPixels, p.FocalLengthYPixels, p.WidthPixels, p.HeightPixels, p.Aperture, p.ImageCircleRadiusPixels,
        p.BoresightAltitudeDegrees, p.BoresightAzimuthDegrees, p.RollDegrees, p.HorizontalFlip, p.EnforceSensorBounds, p.RadialDistortionK1);

    private static AstrometricFrameAssessment FailureAssessment(MeasuredAstrometryContextV1 context, AstrometricCalibration calibration,
        AstrometricCatalogData catalog, AstrometricSolverOptions solver, AstrometricAssessmentStatus status, string reason)
    {
        var value = new AstrometricFrameAssessment(AstrometricFrameAssessment.CurrentSchemaVersion, string.Empty, context.Frame,
            calibration.IdentitySha256, catalog.IdentitySha256, catalog.SelectionIdentitySha256, AstrometricConventions.SolverVersion,
            solver.IdentitySha256, AstrometricConventions.CoordinateModel, AstrometricConventions.Refraction, AstrometricConventions.PixelCoordinates,
            context.PreviousAccepted is null ? AstrometricSolveMode.Blind : AstrometricSolveMode.Warm, status, reason, reason,
            null, null, null, context.PreviousAccepted?.IdentitySha256);
        value = value with { IdentitySha256 = MeasuredAstrometryJson.NativeIdentity(value) };
        AstrometricEvidenceJson.Validate(value);
        return value;
    }

    private static ValueTask<ProcessingOutcome> Fail(string reason) => ValueTask.FromResult(ProcessingOutcome.TerminalFailure(reason));
}
