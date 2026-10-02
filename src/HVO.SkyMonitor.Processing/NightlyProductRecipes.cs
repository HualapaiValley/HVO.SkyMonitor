using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Bounded options for the nightly keogram recipe.</summary>
public sealed record KeogramRecipeOptions(
    double MaximumGapSeconds = KeogramComposer.DefaultMaximumGapSeconds,
    int MaximumGapColumnCount = KeogramComposer.DefaultMaximumGapColumnCount,
    int MaximumColumnCount = KeogramComposer.DefaultMaximumColumnCount);

/// <summary>
/// The captured rig geometry a keogram samples: the readout-view projection of the source previews, the rig profile it
/// was derived from, and the number of uniformly spaced north-zenith-south meridian rows.
/// </summary>
public sealed record KeogramGeometryV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] string RigProfileSha256,
    [property: JsonRequired] ProjectionContext Projection,
    [property: JsonRequired] int SampleCount)
{
    public const string CurrentSchemaVersion = "keogram-meridian-geometry-v1";
    public const string AuxiliaryInputName = "keogram-geometry";

    /// <summary>Creates geometry with roughly one meridian row per imaged source pixel.</summary>
    public static KeogramGeometryV1 Create(ProjectionContext projection, string rigProfileSha256) =>
        new(CurrentSchemaVersion, rigProfileSha256, projection, MeridianSamplePath.RecommendedSampleCount(projection));
}

/// <summary>Canonical serialization and auxiliary-input binding for <see cref="KeogramGeometryV1"/>.</summary>
public static class KeogramGeometryJson
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions ParserOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static byte[] Serialize(KeogramGeometryV1 geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var element = CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(geometry, SerializerOptions));
        return Encoding.UTF8.GetBytes(element.GetRawText());
    }

    public static string ComputeIdentitySha256(KeogramGeometryV1 geometry) =>
        ProcessingIdentity.ComputePayloadSha256(Serialize(geometry));

    public static ProcessingAuxiliaryInput CreateAuxiliaryInput(KeogramGeometryV1 geometry)
    {
        var payload = Serialize(geometry);
        return new ProcessingAuxiliaryInput(
            KeogramGeometryV1.AuxiliaryInputName,
            ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: KeogramGeometryV1.CurrentSchemaVersion,
            IdentitySha256: ProcessingIdentity.ComputePayloadSha256(payload),
            Payload: payload);
    }

    public static KeogramGeometryV1? Parse(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return JsonSerializer.Deserialize<KeogramGeometryV1>(utf8Json, ParserOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Bounded options for the nightly star trail recipe.</summary>
public sealed record StarTrailRecipeOptions(int MaximumFrameCount = 512);

/// <summary>Composes a time-axis keogram product from ordered, compatible packed preview frames.</summary>
internal sealed class KeogramRecipe : IProcessingRecipe
{
    public ProcessingRecipeDefinition Definition { get; } = new(
        BuiltInProcessingRecipes.Keogram, "1.0.0", "keogram-recipe-v1",
        ProcessingOperationKind.Window);

    public JsonElement NormalizeOptions(JsonElement options)
    {
        var parsed = ProcessingRecipeSupport.ParseOptions<KeogramRecipeOptions>(options);
        if (parsed.MaximumGapSeconds <= 0 || !double.IsFinite(parsed.MaximumGapSeconds) ||
            parsed.MaximumGapSeconds > 86400 ||
            parsed.MaximumColumnCount is < 1 or > KeogramComposer.MaximumColumnLimit ||
            parsed.MaximumGapColumnCount < 1 || parsed.MaximumGapColumnCount > parsed.MaximumColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        return ProcessingRecipeSupport.Normalize(parsed);
    }

    public ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken)
    {
        var sources = NightlyProductRecipeSupport.ResolveOrderedFrames(request, out var failure);
        if (sources.Count == 0)
        {
            return ValueTask.FromResult(failure!);
        }
        if (!NightlyProductRecipeSupport.TryValidatePreviewSources(sources, out failure))
        {
            return ValueTask.FromResult(failure!);
        }

        var geometry = NightlyProductRecipeSupport.ResolveKeogramGeometry(request, sources, out failure);
        if (geometry is null)
        {
            return ValueTask.FromResult(failure!);
        }

        var composition = NightlyProductRecipeSupport.CreateKeogramComposition(identity, geometry);
        KeogramResult result;
        try
        {
            result = KeogramComposer.Compose(
                NightlyProductRecipeSupport.ToKeogramFrames(sources), composition, cancellationToken);
        }
        catch (ArgumentException)
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidLineage,
                nameof(request.Inputs)));
        }

        var layout = ProcessingRecipeSupport.CreatePackedLayout(result.Width, result.Height, result.PixelFormat);
        var totalIntegration = TimeSpan.FromTicks(sources.Sum(static source => source.Integration.Ticks));
        var product = ProcessingRecipeSupport.CreateProduct(
            FrameArtifactRole.Preview,
            request.OutputVariant,
            "application/x-hvo-packed-image",
            layout,
            result.PixelData,
            identity,
            NightlyProductRecipeSupport.KeogramAlgorithms,
            sources,
            totalIntegration,
            sources[0].Compatibility);
        return ValueTask.FromResult(ProcessingOutcome.Produced(product));
    }
}

/// <summary>Composes a lighten star trail product from ordered, compatible packed preview frames.</summary>
internal sealed class StarTrailRecipe : IProcessingRecipe
{
    public ProcessingRecipeDefinition Definition { get; } = new(
        BuiltInProcessingRecipes.StarTrail, "1.0.0", "star-trail-recipe-v1",
        ProcessingOperationKind.Window);

    public JsonElement NormalizeOptions(JsonElement options)
    {
        var parsed = ProcessingRecipeSupport.ParseOptions<StarTrailRecipeOptions>(options);
        if (parsed.MaximumFrameCount is < 1 or > NightlyProductRecipeSupport.MaximumSourceFrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }
        return ProcessingRecipeSupport.Normalize(parsed);
    }

    public ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken)
    {
        var sources = NightlyProductRecipeSupport.ResolveOrderedFrames(request, out var failure);
        if (sources.Count == 0)
        {
            return ValueTask.FromResult(failure!);
        }
        if (!NightlyProductRecipeSupport.TryValidatePreviewSources(sources, out failure))
        {
            return ValueTask.FromResult(failure!);
        }

        var options = ProcessingRecipeSupport.ParseOptions<StarTrailRecipeOptions>(
            identity.Descriptor.Options.GetProperty("parameters"));
        if (sources.Count > options.MaximumFrameCount)
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidLineage,
                nameof(request.Inputs)));
        }

        StarTrailResult result;
        try
        {
            result = StarTrailComposer.Compose(
                NightlyProductRecipeSupport.ToStarTrailFrames(sources), cancellationToken);
        }
        catch (ArgumentException)
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidLineage,
                nameof(request.Inputs)));
        }

        var layout = ProcessingRecipeSupport.CreatePackedLayout(result.Width, result.Height, result.PixelFormat);
        var totalIntegration = TimeSpan.FromTicks(sources.Sum(static source => source.Integration.Ticks));
        var product = ProcessingRecipeSupport.CreateProduct(
            FrameArtifactRole.Preview,
            request.OutputVariant,
            "application/x-hvo-packed-image",
            layout,
            result.PixelData,
            identity,
            NightlyProductRecipeSupport.StarTrailAlgorithms,
            sources,
            totalIntegration,
            sources[0].Compatibility);
        return ValueTask.FromResult(ProcessingOutcome.Produced(product));
    }
}

/// <summary>Shared deterministic source ordering and validation for the nightly product recipes and contracts.</summary>
internal static class NightlyProductRecipeSupport
{
    internal const int MaximumSourceFrameCount = 512;

    internal static readonly IReadOnlyList<ProcessingAlgorithmIdentity> KeogramAlgorithms =
    [
        new("meridian-path", MeridianSamplePath.AlgorithmVersion),
        new("keogram-path", KeogramComposer.AlgorithmVersion),
        new("row-packing", "packed-copy-v1")
    ];

    internal static readonly IReadOnlyList<ProcessingAlgorithmIdentity> StarTrailAlgorithms =
    [
        new("star-trail-lighten", StarTrailComposer.AlgorithmVersion),
        new("row-packing", "packed-copy-v1")
    ];

    internal static List<ProcessingArtifact> ResolveOrderedFrames(
        ProcessingExecutionRequest request,
        out ProcessingOutcome? failure)
    {
        var candidates = ProcessingRecipeSupport.ResolveMany(request, out failure);
        if (candidates.Count == 0)
        {
            return [];
        }
        if (candidates.Count > MaximumSourceFrameCount)
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidLineage,
                nameof(request.Inputs));
            return [];
        }

        var ordered = candidates
            .OrderBy(static source => source.ObservationStartedUtc ?? source.CreatedUtc)
            .ThenBy(static source => source.ArtifactId)
            .ToList();
        if (ordered.Select(static source => source.ArtifactId).Distinct().Count() != ordered.Count)
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidLineage,
                nameof(request.Inputs));
            return [];
        }
        return ordered;
    }

    internal static bool TryValidatePreviewSources(
        IReadOnlyList<ProcessingArtifact> sources,
        out ProcessingOutcome? failure)
    {
        var firstLayout = sources[0].Layout;
        if (firstLayout is null)
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidSelector,
                nameof(sources));
            return false;
        }

        foreach (var source in sources)
        {
            if (source.Role != FrameArtifactRole.Preview ||
                !ProcessingRecipeSupport.TryValidateFrame(source, out var layout, out _) ||
                layout.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24) ||
                layout.StrideBytes != checked(layout.Width * ImageLayout.BytesPerPixel(layout.PixelFormat)))
            {
                failure = ProcessingOutcome.TerminalFailure(
                    ProcessingReasonCodes.InvalidSelector,
                    nameof(sources));
                return false;
            }
            if (layout.Width != firstLayout.Width || layout.Height != firstLayout.Height ||
                layout.PixelFormat != firstLayout.PixelFormat)
            {
                failure = ProcessingOutcome.TerminalFailure(
                    ProcessingReasonCodes.IncompatibleInput,
                    nameof(sources));
                return false;
            }
        }

        failure = null;
        return true;
    }

    internal static KeogramGeometryV1? ResolveKeogramGeometry(
        ProcessingExecutionRequest request,
        IReadOnlyList<ProcessingArtifact> sources,
        out ProcessingOutcome? failure)
    {
        var matches = (request.AuxiliaryInputs ?? []).Where(static input =>
            string.Equals(input.Name, KeogramGeometryV1.AuxiliaryInputName, StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0)
        {
            failure = ProcessingOutcome.Skipped(
                ProcessingReasonCodes.MissingKeogramGeometry, KeogramGeometryV1.AuxiliaryInputName);
            return null;
        }

        var auxiliary = matches[0];
        var geometry = matches.Length == 1 && auxiliary.Kind == ProcessingAuxiliaryInputKind.CanonicalJson &&
            string.Equals(auxiliary.SchemaVersion, KeogramGeometryV1.CurrentSchemaVersion, StringComparison.Ordinal)
                ? KeogramGeometryJson.Parse(auxiliary.Payload.Span)
                : null;
        if (geometry is null || !IsValid(geometry) ||
            !string.Equals(
                KeogramGeometryJson.ComputeIdentitySha256(geometry),
                auxiliary.IdentitySha256,
                StringComparison.OrdinalIgnoreCase))
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidKeogramGeometry, KeogramGeometryV1.AuxiliaryInputName);
            return null;
        }

        var layout = sources[0].Layout!;
        if (geometry.Projection.WidthPixels != layout.Width || geometry.Projection.HeightPixels != layout.Height ||
            sources.Any(source => !string.Equals(
                source.Compatibility.Rig, geometry.RigProfileSha256, StringComparison.OrdinalIgnoreCase)))
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.KeogramGeometryMismatch, KeogramGeometryV1.AuxiliaryInputName);
            return null;
        }

        failure = null;
        return geometry;
    }

    internal static KeogramCompositionOptions CreateKeogramComposition(
        ProcessingRecipeIdentity identity,
        KeogramGeometryV1 geometry)
    {
        var options = ProcessingRecipeSupport.ParseOptions<KeogramRecipeOptions>(
            identity.Descriptor.Options.GetProperty("parameters"));
        var width = geometry.Projection.WidthPixels;
        var height = geometry.Projection.HeightPixels;

        // A projection that does not enforce sensor bounds can place a meridian direction off the readout; the sensor
        // did not image it, so it is unmapped rather than an invalid geometry.
        var path = MeridianSamplePath.Create(geometry.Projection, geometry.SampleCount)
            .Select(sample => sample.Pixel is { } pixel &&
                pixel.X >= 0 && pixel.X <= width && pixel.Y >= 0 && pixel.Y <= height
                    ? sample.Pixel
                    : null)
            .ToArray();
        return new KeogramCompositionOptions(
            path, options.MaximumGapSeconds, options.MaximumGapColumnCount, options.MaximumColumnCount);
    }

    private static bool IsValid(KeogramGeometryV1 geometry)
    {
        if (!string.Equals(geometry.SchemaVersion, KeogramGeometryV1.CurrentSchemaVersion, StringComparison.Ordinal) ||
            geometry.RigProfileSha256 is not { Length: 64 } rig || !rig.All(Uri.IsHexDigit) ||
            geometry.SampleCount is < MeridianSamplePath.MinimumSampleCount or > MeridianSamplePath.MaximumSampleCount)
        {
            return false;
        }
        try
        {
            geometry.Projection.Validate();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static KeogramFrame[] ToKeogramFrames(IReadOnlyList<ProcessingArtifact> sources) =>
        sources.Select(static source => new KeogramFrame(
            source.Layout!.Width,
            source.Layout.Height,
            source.Layout.StrideBytes,
            source.Layout.PixelFormat,
            source.Payload,
            source.ObservationStartedUtc ?? source.CreatedUtc)).ToArray();

    internal static StarTrailFrame[] ToStarTrailFrames(IReadOnlyList<ProcessingArtifact> sources) =>
        sources.Select(static source => new StarTrailFrame(
            source.Layout!.Width,
            source.Layout.Height,
            source.Layout.StrideBytes,
            source.Layout.PixelFormat,
            source.Payload,
            source.ObservationStartedUtc ?? source.CreatedUtc)).ToArray();
}
