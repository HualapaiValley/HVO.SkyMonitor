using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing;

/// <summary>
/// Explicit, immutable solve inputs supplied after raw admission. Catalog rows, scene and calibration are retained
/// once by their owners; this transport envelope is never copied into the compact solution.
/// </summary>
public sealed record MeasuredAstrometryContextV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] AstrometricFrameContext Frame,
    [property: JsonRequired] ProjectionContext Projection,
    [property: JsonRequired] string CalibrationVersion,
    [property: JsonRequired] string ReadoutIdentitySha256,
    [property: JsonRequired] CatalogMetadata CatalogMetadata,
    [property: JsonRequired] IReadOnlyList<CelestialCatalogObject> CatalogStars,
    [property: JsonRequired] bool CatalogComplete,
    [property: JsonRequired] double CompletenessMagnitudeLimit,
    [property: JsonRequired] string CatalogCoordinateModel,
    [property: JsonRequired] AstrometricCatalogProvenance CatalogPackage,
    [property: JsonRequired] Guid MeasuredArtifactId,
    [property: JsonRequired] string MeasuredIdentitySha256,
    [property: JsonRequired] string MeasuredRecipeIdentitySha256,
    [property: JsonRequired] AstrometricClockFacts Clock,
    [property: JsonRequired] AstrometricFrameAssessment? PreviousAccepted)
{
    public const string CurrentSchemaVersion = "measured-astrometry-context-v1";
}

/// <summary>Fixed bounds and engineering settings; fitting must be explicitly requested after durable admission.</summary>
public sealed record MeasuredAstrometryOptions(
    AstrometricSolverOptions? Solver = null,
    AstrometricDiagnosticsOptions? Diagnostics = null,
    AstrometricUncertaintyOptions? Uncertainty = null,
    double MaximumExecutionMilliseconds = 20000);

/// <summary>Compact residual evidence; catalog/centroid rows remain in the separately retained measured product.</summary>
public sealed record MeasuredAstrometryResidualSummaryV1(
    [property: JsonRequired] int QualifiedCount,
    [property: JsonRequired] IReadOnlyList<int> FittingDetectionIndexes,
    [property: JsonRequired] IReadOnlyList<int> VerificationDetectionIndexes,
    [property: JsonRequired] IReadOnlyList<int> OutlierDetectionIndexes,
    [property: JsonRequired] double? FittingRmsPixels,
    [property: JsonRequired] double? VerificationRmsPixels,
    [property: JsonRequired] double? MaximumResidualPixels,
    [property: JsonRequired] AstrometricConditioning? Conditioning,
    [property: JsonRequired] string ReasonCode,
    [property: JsonRequired] IReadOnlyList<AstrometricAssociation> NativeAssociations);

/// <summary>
/// A compact retained proposal, including failures. No calibration is changed. Native assessment bytes are validated
/// by AstrometricEvidenceJson; accepted mapping reconstruction requires the separately retained immutable calibration
/// and measured product. Timings are deliberately excluded from semantic/content identity.
/// </summary>
public sealed record MeasuredAstrometrySolutionV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string RecipeIdentitySha256,
    [property: JsonRequired] string ContextIdentitySha256,
    [property: JsonRequired] Guid MeasuredArtifactId,
    [property: JsonRequired] string MeasuredIdentitySha256,
    [property: JsonRequired] string MeasuredRecipeIdentitySha256,
    [property: JsonRequired] string SceneIdentitySha256,
    [property: JsonRequired] string ReadoutIdentitySha256,
    [property: JsonRequired] AstrometricCatalogProvenance CatalogPackage,
    [property: JsonRequired] AstrometricFrameAssessment Assessment,
    [property: JsonRequired] MeasuredAstrometryResidualSummaryV1 Residuals,
    [property: JsonRequired] AstrometricFrameUncertainty? Uncertainty,
    [property: JsonRequired] string UncertaintyReasonCode)
{
    public const string CurrentSchemaVersion = "measured-astrometry-solution-v1";
    [JsonIgnore] public string SolutionIdentitySha256 => MeasuredAstrometryJson.Identity(this);
}

/// <summary>Bounded canonical transport and retained-solution validation, preserving native Astronomy identities.</summary>
public static class MeasuredAstrometryJson
{
    public const string MediaType = "application/vnd.hvo.measured-astrometry-solution+json";
    public const string ContextMediaType = "application/vnd.hvo.measured-astrometry-context+json";
    public const string ContextInputName = "astrometry-context";
    public const int MaximumContextBytes = 2 * 1024 * 1024;
    public const int MaximumSolutionBytes = 128 * 1024;
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    internal static string NativeIdentity<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    internal static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal static byte[] Canonical<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(
        CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(value, Options)));
    internal static string Identity<T>(T value) => ProcessingIdentity.ComputePayloadSha256(Canonical(value));

    /// <summary>Pins the exact raw layout, stored-code convention and readout, including an explicit absent readout.</summary>
    public static string ComputeReadoutIdentity(FrameLayoutDescriptor layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!layout.Validate().IsValid) throw new ArgumentException("Invalid raw layout.", nameof(layout));
        return Identity(new { schema = "measured-astrometry-readout-v1", layout });
    }

    public static ProcessingAuxiliaryInput ContextInput(MeasuredAstrometryContextV1 context)
    {
        ValidateContext(context);
        var bytes = Canonical(context);
        if (bytes.Length > MaximumContextBytes) throw new ArgumentException("Astrometry context exceeds its byte bound.", nameof(context));
        return new(ContextInputName, ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: MeasuredAstrometryContextV1.CurrentSchemaVersion,
            IdentitySha256: ProcessingIdentity.ComputePayloadSha256(bytes), Payload: bytes);
    }

    public static MeasuredAstrometryContextV1 ParseContext(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumContextBytes) throw new ArgumentException("Astrometry context exceeds its byte bound.", nameof(bytes));
        var value = JsonSerializer.Deserialize<MeasuredAstrometryContextV1>(bytes.Span, Options)
            ?? throw new ArgumentException("Missing astrometry context.", nameof(bytes));
        ValidateContext(value);
        if (!bytes.Span.SequenceEqual(Canonical(value))) throw new ArgumentException("Noncanonical astrometry context.", nameof(bytes));
        return value;
    }

    private static void ValidateContext(MeasuredAstrometryContextV1 context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.SchemaVersion != MeasuredAstrometryContextV1.CurrentSchemaVersion || context.Frame is null ||
            context.CatalogStars is null || context.CatalogStars.Count > AstrometricCatalogData.MaximumEntries ||
            context.CatalogPackage is null || context.Clock is null || context.MeasuredArtifactId == Guid.Empty ||
            !Hash(context.MeasuredIdentitySha256) || !Hash(context.MeasuredRecipeIdentitySha256))
            throw new ArgumentException("Invalid astrometry context.", nameof(context));
        context.Frame.Validate(); context.Clock.Validate();
        _ = new AstrometricCalibration(context.Projection, context.CalibrationVersion, context.ReadoutIdentitySha256);
        _ = Catalog(context);
        if (context.PreviousAccepted is { } prior)
        {
            AstrometricEvidenceJson.Validate(prior);
            if (!prior.HasMeasuredMapping) throw new ArgumentException("A supplied prior must be accepted.", nameof(context));
        }
    }

    internal static AstrometricCatalogData Catalog(MeasuredAstrometryContextV1 context) => new(context.CatalogMetadata,
        context.CatalogStars, context.CatalogComplete, context.CompletenessMagnitudeLimit, context.CatalogCoordinateModel, context.CatalogPackage);

    public static byte[] Serialize(MeasuredAstrometrySolutionV1 solution)
    {
        Validate(solution);
        var bytes = Canonical(solution);
        if (bytes.Length > MaximumSolutionBytes) throw new ArgumentException("Astrometry solution exceeds its byte bound.", nameof(solution));
        return bytes;
    }

    public static MeasuredAstrometrySolutionV1 Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumSolutionBytes) throw new ArgumentException("Astrometry solution exceeds its byte bound.", nameof(bytes));
        var value = JsonSerializer.Deserialize<MeasuredAstrometrySolutionV1>(bytes.Span, Options)
            ?? throw new ArgumentException("Missing astrometry solution.", nameof(bytes));
        Validate(value);
        if (!bytes.Span.SequenceEqual(Canonical(value))) throw new ArgumentException("Noncanonical astrometry solution.", nameof(bytes));
        return value;
    }

    public static void Validate(MeasuredAstrometrySolutionV1 value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.SchemaVersion != MeasuredAstrometrySolutionV1.CurrentSchemaVersion || value.MeasuredArtifactId == Guid.Empty ||
            value.CatalogPackage is null || value.Residuals is null || string.IsNullOrWhiteSpace(value.UncertaintyReasonCode) ||
            value.UncertaintyReasonCode.Length > 128 || new[] { value.RecipeIdentitySha256, value.ContextIdentitySha256,
                value.MeasuredIdentitySha256, value.MeasuredRecipeIdentitySha256, value.SceneIdentitySha256, value.ReadoutIdentitySha256 }.Any(id => !Hash(id)))
            throw new ArgumentException("Invalid retained astrometry solution.", nameof(value));
        AstrometricEvidenceJson.Validate(value.Assessment);
        if (value.CatalogPackage.PackageKind is not ("fixture" or "production") ||
            new[] { value.CatalogPackage.CatalogId, value.CatalogPackage.PackageVersion, value.CatalogPackage.PreprocessingVersion }
                .Any(text => string.IsNullOrWhiteSpace(text) || text.Length > 256))
            throw new ArgumentException("Invalid catalog package identity.", nameof(value));
        var r = value.Residuals;
        if (r.FittingDetectionIndexes is null || r.VerificationDetectionIndexes is null || r.OutlierDetectionIndexes is null ||
            r.NativeAssociations is null || r.NativeAssociations.Count > AstrometricCatalogData.MaximumEntries ||
            r.QualifiedCount is < 0 or > AstrometricCatalogData.MaximumEntries || string.IsNullOrWhiteSpace(r.ReasonCode) || r.ReasonCode.Length > 128)
            throw new ArgumentException("Invalid residual evidence.", nameof(value));
        var all = r.FittingDetectionIndexes.Concat(r.VerificationDetectionIndexes).Concat(r.OutlierDetectionIndexes).ToArray();
        if (all.Length != r.QualifiedCount || all.Any(i => i is < 0 or >= 4096) || all.Distinct().Count() != all.Length ||
            !r.FittingDetectionIndexes.SequenceEqual(r.FittingDetectionIndexes.Order()) ||
            !r.VerificationDetectionIndexes.SequenceEqual(r.VerificationDetectionIndexes.Order()) ||
            !r.OutlierDetectionIndexes.SequenceEqual(r.OutlierDetectionIndexes.Order()) ||
            new[] { r.FittingRmsPixels, r.VerificationRmsPixels, r.MaximumResidualPixels }.Any(v => v is { } n && (!double.IsFinite(n) || n < 0)))
            throw new ArgumentException("Invalid residual membership or values.", nameof(value));
        if (value.Assessment.HasMeasuredMapping)
        {
            // Keep the native solver records at full residual precision: recomputing residuals from a mapping
            // can change the last bits and cannot reproduce the native association identity.
            var associations = r.NativeAssociations;
            if (associations.Any(a => a is null || string.IsNullOrWhiteSpace(a.CatalogId) || a.CatalogId.Length > 256 ||
                    a.DetectionIndex is < 0 or >= 4096 || !double.IsFinite(a.ResidualPixels) || a.ResidualPixels < 0) ||
                associations.Select(a => a.CatalogId).Distinct(StringComparer.Ordinal).Count() != associations.Count ||
                associations.Select(a => a.DetectionIndex).Distinct().Count() != associations.Count ||
                !associations.Select(a => a.CatalogId).SequenceEqual(associations.Select(a => a.CatalogId).Order(StringComparer.Ordinal)) ||
                NativeIdentity(associations) != value.Assessment.AssociationIdentitySha256 ||
                !r.FittingDetectionIndexes.SequenceEqual(associations.Where(a => !a.Verification).Select(a => a.DetectionIndex).Order()) ||
                !r.VerificationDetectionIndexes.SequenceEqual(associations.Where(a => a.Verification).Select(a => a.DetectionIndex).Order()))
                throw new ArgumentException("Retained membership differs from native associations.", nameof(value));
            var q = value.Assessment.Quality!;
            if (r.FittingDetectionIndexes.Count != q.InlierCount || r.VerificationDetectionIndexes.Count != q.VerificationCount ||
                r.FittingRmsPixels != q.FittingRmsPixels || r.VerificationRmsPixels != q.VerificationRmsPixels ||
                r.MaximumResidualPixels is null || r.ReasonCode != "available" || value.Uncertainty is null)
                throw new ArgumentException("Accepted solution lacks fit/uncertainty evidence.", nameof(value));
        }
        else if (r.FittingDetectionIndexes.Count != 0 || r.VerificationDetectionIndexes.Count != 0 || r.NativeAssociations.Count != 0 || value.Uncertainty is not null)
            throw new ArgumentException("A failed solution cannot retain accepted fit evidence.", nameof(value));
        if (value.Uncertainty is { } u)
        {
            AstrometricUncertaintyEvidence.Validate(u, value.Assessment);
            if (u.ReasonCode != value.UncertaintyReasonCode)
                throw new ArgumentException("Uncertainty reason mismatch.", nameof(value));
        }
    }

    internal static bool MatchesContext(MeasuredAstrometrySolutionV1 value, MeasuredAstrometryContextV1 context,
        MeasuredAstrometryOptions options, string sceneIdentity)
    {
        var calibration = new AstrometricCalibration(context.Projection, context.CalibrationVersion, context.ReadoutIdentitySha256);
        var catalog = Catalog(context);
        return Same(value.ContextIdentitySha256, Identity(context)) && value.Assessment.Frame == context.Frame && value.Assessment.CalibrationIdentitySha256 == calibration.IdentitySha256 &&
            value.Assessment.CatalogIdentitySha256 == catalog.IdentitySha256 && value.Assessment.CatalogSelectionIdentitySha256 == catalog.SelectionIdentitySha256 &&
            value.Assessment.SettingsIdentitySha256 == options.Solver!.IdentitySha256 && value.CatalogPackage == context.CatalogPackage &&
            Same(value.ReadoutIdentitySha256, calibration.ReadoutIdentitySha256) && Same(value.SceneIdentitySha256, sceneIdentity) &&
            value.MeasuredArtifactId == context.MeasuredArtifactId && Same(value.MeasuredIdentitySha256, context.MeasuredIdentitySha256) &&
            Same(value.MeasuredRecipeIdentitySha256, context.MeasuredRecipeIdentitySha256) &&
            value.Assessment.PreviousAssessmentIdentitySha256 == context.PreviousAccepted?.IdentitySha256 &&
            value.Assessment.Mode == (context.PreviousAccepted is null ? AstrometricSolveMode.Blind : AstrometricSolveMode.Warm) &&
            (value.Uncertainty is null || value.Uncertainty.OptionsIdentitySha256 == options.Uncertainty!.IdentitySha256 && value.Uncertainty.Clock == context.Clock);
    }

    /// <summary>Reconstructs only an accepted immutable mapping; no fitting or retained-state mutation occurs.</summary>
    public static AstrometricMapping ReconstructMapping(MeasuredAstrometrySolutionV1 solution,
        AstrometricCalibration calibration, MeasuredStellarAssociationsV1 measured)
    {
        Validate(solution);
        ArgumentNullException.ThrowIfNull(calibration);
        ArgumentNullException.ThrowIfNull(measured);
        if (!MeasuredStellarAssociationJson.Validate(measured).IsValid ||
            !Same(solution.MeasuredIdentitySha256, measured.AssociationIdentitySha256) ||
            measured.Source.CaptureId != solution.Assessment.Frame.CaptureId || measured.Source.ArtifactId != solution.Assessment.Frame.SourceArtifactId ||
            !Same(measured.Source.DescriptorIdentitySha256, solution.Assessment.Frame.SourceDescriptorSha256) ||
            !Same(measured.Scene.SceneIdentitySha256, solution.SceneIdentitySha256) ||
            !Same(calibration.ReadoutIdentitySha256, solution.ReadoutIdentitySha256))
            throw new ArgumentException("Retained mapping input mismatch.", nameof(measured));
        if (!MembershipMatchesMeasured(solution, measured))
            throw new ArgumentException("Retained membership differs from measured evidence.", nameof(measured));
        return new(calibration, solution.Assessment);
    }

    /// <summary>Rebuilds shared residual diagnostics from the exact retained inputs, without executing a solver.</summary>
    public static AstrometricResidualDiagnostics ReconstructDiagnostics(MeasuredAstrometrySolutionV1 solution,
        MeasuredAstrometryContextV1 context, MeasuredStellarAssociationsV1 measured, MeasuredAstrometryOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(options);
        options = options with { Solver = options.Solver ?? new(), Diagnostics = options.Diagnostics ?? new(), Uncertainty = options.Uncertainty ?? new() };
        ValidateContext(context);
        var calibration = new AstrometricCalibration(context.Projection, context.CalibrationVersion, context.ReadoutIdentitySha256);
        _ = ReconstructMapping(solution, calibration, measured);
        if (!MatchesContext(solution, context, options, solution.SceneIdentitySha256))
            throw new ArgumentException("Diagnostic context differs from the retained assessment.", nameof(context));
        var qualified = MeasuredAstrometryRecipe.Qualified(measured);
        return AstrometricResidualAnalyzer.Analyze(calibration, Catalog(context), options.Solver!,
            solution.Assessment, solution.Residuals.NativeAssociations, MeasuredAstrometryRecipe.Detections(qualified),
            covariances: MeasuredAstrometryRecipe.Covariances(qualified), options: options.Diagnostics,
            cancellationToken: cancellationToken);
    }

    internal static bool MembershipMatchesMeasured(MeasuredAstrometrySolutionV1 solution, MeasuredStellarAssociationsV1 measured)
    {
        var qualified = MeasuredAstrometryRecipe.Qualified(measured);
        var r = solution.Residuals;
        if (qualified.Length > AstrometricCatalogData.MaximumEntries)
            return !solution.Assessment.HasMeasuredMapping && r.QualifiedCount == 0 && r.ReasonCode == "measured-entry-bound";
        var retained = r.FittingDetectionIndexes.Concat(r.VerificationDetectionIndexes).Concat(r.OutlierDetectionIndexes).Order();
        var byIndex = qualified.ToDictionary(a => a.DetectionIndex);
        return qualified.Select(a => a.DetectionIndex).Order().SequenceEqual(retained) &&
            r.NativeAssociations.All(a => byIndex.TryGetValue(a.DetectionIndex, out var row) && row.CatalogId == a.CatalogId);
    }

    /// <summary>Separate bounded mapping application for at most 4096 points, with cooperative cancellation.</summary>
    public static IReadOnlyList<AstrometricSkyPoint?> ApplyPixels(AstrometricMapping mapping,
        IReadOnlyList<PixelPoint> pixels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mapping); ArgumentNullException.ThrowIfNull(pixels);
        cancellationToken.ThrowIfCancellationRequested();
        if (pixels.Count > 4096) throw new ArgumentException("Mapping application exceeds 4096 points.", nameof(pixels));
        var output = new AstrometricSkyPoint?[pixels.Count];
        for (var i = 0; i < output.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(pixels[i].X) || !double.IsFinite(pixels[i].Y)) throw new ArgumentException("Invalid pixel.", nameof(pixels));
            output[i] = mapping.PixelToSky(pixels[i]);
        }
        return Array.AsReadOnly(output);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false,
            MaxDepth = 32,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new AssessmentConverter());
        return options;
    }

    private sealed class AssessmentConverter : JsonConverter<AstrometricFrameAssessment>
    {
        public override AstrometricFrameAssessment Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return AstrometricEvidenceJson.Parse(JsonSerializer.SerializeToUtf8Bytes(document.RootElement));
        }
        public override void Write(Utf8JsonWriter writer, AstrometricFrameAssessment value, JsonSerializerOptions options) =>
            writer.WriteRawValue(AstrometricEvidenceJson.Serialize(value));
    }
}
