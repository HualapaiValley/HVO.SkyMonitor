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
    int MaximumColumnCount = KeogramComposer.DefaultMaximumColumnCount,
    PlannedKeogramAxis? PlannedAxis = null);

/// <summary>Bounds shared by every nightly product recipe.</summary>
public static class NightlyProductRecipeLimits
{
    /// <summary>
    /// The most sources one keogram, star-trail, or keogram-assembly execution accepts. A longer night is composed in
    /// ordered segments and assembled, never truncated.
    /// </summary>
    public const int MaximumSourceCount = 512;

    /// <summary>The aggregate packed input byte bound of one recipe execution.</summary>
    public const long MaximumSourceBytes = 256L * 1024 * 1024;
}

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
    public static byte[] Serialize(KeogramGeometryV1 geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return NightlyProductJson.Serialize(geometry);
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

    public static KeogramGeometryV1? Parse(ReadOnlySpan<byte> utf8Json) =>
        NightlyProductJson.Parse<KeogramGeometryV1>(utf8Json);
}

/// <summary>The output column and captured exposure start of one source frame inside a keogram segment.</summary>
public sealed record KeogramSegmentFrameV1(
    [property: JsonRequired] int Column,
    [property: JsonRequired] DateTimeOffset ObservationStartedUtc);

/// <summary>The frame columns of one composed keogram segment, bound to the segment's artifact identity.</summary>
public sealed record KeogramSegmentAxisV1(
    [property: JsonRequired] Guid ArtifactId,
    [property: JsonRequired] IReadOnlyList<KeogramSegmentFrameV1> Frames);

/// <summary>
/// The per-segment frame columns a keogram assembly re-lays onto one time axis. Without them a segment's patterned gap
/// columns are indistinguishable from frame columns, so assembly could not reproduce direct composition.
/// </summary>
public sealed record KeogramSegmentAxesV1(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] IReadOnlyList<KeogramSegmentAxisV1> Segments)
{
    public const string CurrentSchemaVersion = "keogram-segment-axes-v1";
    public const string AuxiliaryInputName = "keogram-segment-axes";
}

/// <summary>Canonical serialization and auxiliary-input binding for <see cref="KeogramSegmentAxesV1"/>.</summary>
public static class KeogramSegmentAxesJson
{
    public static byte[] Serialize(KeogramSegmentAxesV1 axes)
    {
        ArgumentNullException.ThrowIfNull(axes);
        return NightlyProductJson.Serialize(axes);
    }

    public static ProcessingAuxiliaryInput CreateAuxiliaryInput(KeogramSegmentAxesV1 axes)
    {
        var payload = Serialize(axes);
        return new ProcessingAuxiliaryInput(
            KeogramSegmentAxesV1.AuxiliaryInputName,
            ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: KeogramSegmentAxesV1.CurrentSchemaVersion,
            IdentitySha256: ProcessingIdentity.ComputePayloadSha256(payload),
            Payload: payload);
    }

    public static KeogramSegmentAxesV1? Parse(ReadOnlySpan<byte> utf8Json) =>
        NightlyProductJson.Parse<KeogramSegmentAxesV1>(utf8Json);

    /// <summary>
    /// Creates the axis entry of a keogram segment the keogram recipe composed with <paramref name="options"/> from frames
    /// captured at <paramref name="frameTimes"/>, in the recipe's source order.
    /// </summary>
    public static KeogramSegmentAxisV1 CreateSegment(
        Guid artifactId,
        IReadOnlyList<DateTimeOffset> frameTimes,
        KeogramRecipeOptions options)
    {
        ArgumentNullException.ThrowIfNull(frameTimes);
        ArgumentNullException.ThrowIfNull(options);
        var columns = KeogramComposer.ComputeFrameColumns(KeogramComposer.ComputeTimeAxis(
            frameTimes, options.MaximumGapSeconds, options.MaximumGapColumnCount, options.MaximumColumnCount));
        return new KeogramSegmentAxisV1(
            artifactId,
            [.. columns.Select((column, index) => new KeogramSegmentFrameV1(column, frameTimes[index]))]);
    }
}

internal static class NightlyProductJson
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

    internal static byte[] Serialize<T>(T value)
    {
        var element = CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(value, SerializerOptions));
        return Encoding.UTF8.GetBytes(element.GetRawText());
    }

    internal static T? Parse<T>(ReadOnlySpan<byte> utf8Json)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(utf8Json, ParserOptions);
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
        BuiltInProcessingRecipes.Keogram, "2.0.0", "keogram-recipe-v2",
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
        if (parsed.PlannedAxis is not null) _ = parsed.PlannedAxis.Width(parsed.MaximumColumnCount);
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
            var frames = NightlyProductRecipeSupport.ToKeogramFrames(sources);
            var planned = NightlyProductRecipeSupport.PlannedAxis(identity);
            var natural = planned is null ? composition : composition with
            { MaximumColumnCount = KeogramComposer.MaximumColumnLimit, MaximumGapColumnCount = 1 };
            result = KeogramComposer.Compose(frames, natural, cancellationToken);
            if (planned is not null)
            {
                var columns = KeogramComposer.ComputeFrameColumns(KeogramComposer.ComputeTimeAxis(frames, natural));
                var segment = new KeogramSegment(result.Width, result.Height, result.StrideBytes, result.PixelFormat,
                    result.PixelData, [.. columns.Select((column, index) => new KeogramSegmentColumn(column, frames[index].TimestampUtc))]);
                result = PlannedKeogramComposer.Assemble([segment], composition, planned, cancellationToken);
            }
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
            NightlyProductRecipeSupport.FrameAlgorithms(identity),
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

/// <summary>
/// Assembles composed keogram segments into one longer keogram by re-laying their frame columns on a time axis computed
/// over every segment frame. The result is byte-identical to composing all segment sources at once, so a night longer
/// than the per-execution source bound keeps its exact meridian samples and its proportional gaps.
/// </summary>
internal sealed class KeogramAssemblyRecipe : IProcessingRecipe
{
    public ProcessingRecipeDefinition Definition { get; } = new(
        BuiltInProcessingRecipes.KeogramAssembly, "3.0.0", "keogram-assembly-recipe-v3",
        ProcessingOperationKind.Window);

    public JsonElement NormalizeOptions(JsonElement options) => new KeogramRecipe().NormalizeOptions(options);

    public ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken)
    {
        var plan = NightlyProductRecipeSupport.ResolveKeogramAssembly(request, identity, out var failure);
        if (plan is null)
        {
            return ValueTask.FromResult(failure!);
        }

        KeogramResult result;
        try
        {
            result = NightlyProductRecipeSupport.PlannedAxis(identity) is { } axis
                ? PlannedKeogramComposer.Assemble(plan.Segments, plan.Composition, axis, cancellationToken)
                : KeogramComposer.Assemble(plan.Segments, plan.Composition, cancellationToken);
        }
        catch (ArgumentException)
        {
            return ValueTask.FromResult(ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidLineage,
                nameof(request.Inputs)));
        }

        var layout = ProcessingRecipeSupport.CreatePackedLayout(result.Width, result.Height, result.PixelFormat);
        var product = ProcessingRecipeSupport.CreateProduct(
            FrameArtifactRole.Preview,
            request.OutputVariant,
            "application/x-hvo-packed-image",
            layout,
            result.PixelData,
            identity,
            NightlyProductRecipeSupport.AssemblyAlgorithms(identity),
            plan.Sources,
            plan.TotalIntegration,
            plan.Sources[0].Compatibility);
        return ValueTask.FromResult(ProcessingOutcome.Produced(product));
    }
}

/// <summary>The validated inputs of one keogram assembly.</summary>
internal sealed record KeogramAssemblyPlan(
    IReadOnlyList<ProcessingArtifact> Sources,
    IReadOnlyList<KeogramSegment> Segments,
    KeogramCompositionOptions Composition,
    KeogramGeometryV1 Geometry,
    TimeSpan TotalIntegration);

/// <summary>Shared deterministic source ordering and validation for the nightly product recipes and contracts.</summary>
internal static class NightlyProductRecipeSupport
{
    internal const int MaximumSourceFrameCount = NightlyProductRecipeLimits.MaximumSourceCount;

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

    internal static readonly IReadOnlyList<ProcessingAlgorithmIdentity> KeogramAssemblyAlgorithms =
    [
        .. KeogramAlgorithms,
        new("keogram-segment-assembly", "keogram-segment-assembly-source-order-v2")
    ];

    internal static PlannedKeogramAxis? PlannedAxis(ProcessingRecipeIdentity identity) =>
        ProcessingRecipeSupport.ParseOptions<KeogramRecipeOptions>(identity.Descriptor.Options.GetProperty("parameters")).PlannedAxis;

    internal static IReadOnlyList<ProcessingAlgorithmIdentity> FrameAlgorithms(ProcessingRecipeIdentity identity) =>
        PlannedAxis(identity) is null ? KeogramAlgorithms
            : [.. KeogramAlgorithms, new("planned-time-axis", PlannedKeogramComposer.AlgorithmVersion)];

    internal static IReadOnlyList<ProcessingAlgorithmIdentity> AssemblyAlgorithms(ProcessingRecipeIdentity identity) =>
        PlannedAxis(identity) is null ? KeogramAssemblyAlgorithms
            : [.. KeogramAssemblyAlgorithms, new("planned-time-axis", PlannedKeogramComposer.AlgorithmVersion)];

    internal static List<ProcessingArtifact> ResolveOrderedFrames(
        ProcessingExecutionRequest request,
        out ProcessingOutcome? failure)
    {
        var candidates = ProcessingRecipeSupport.ResolveMany(request, out failure);
        if (candidates.Count == 0)
        {
            return [];
        }
        if (candidates.Count > MaximumSourceFrameCount || candidates.Sum(static source => (long)source.Payload.Length) > NightlyProductRecipeLimits.MaximumSourceBytes)
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
        var geometry = ParseKeogramGeometry(request, out failure);
        if (geometry is null)
        {
            return null;
        }

        var layout = sources[0].Layout!;
        if (geometry.Projection.WidthPixels != layout.Width || geometry.Projection.HeightPixels != layout.Height ||
            !SharesRig(sources, geometry))
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.KeogramGeometryMismatch, KeogramGeometryV1.AuxiliaryInputName);
            return null;
        }
        return geometry;
    }

    /// <summary>
    /// Validates keogram segments, their geometry, and their frame axes. Segments are ordered like every nightly source,
    /// share the geometry's rig and row count, and each declares exactly one axis entry.
    /// </summary>
    internal static KeogramAssemblyPlan? ResolveKeogramAssembly(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        out ProcessingOutcome? failure)
    {
        var sources = ResolveOrderedFrames(request, out failure);
        if (sources.Count == 0)
        {
            return null;
        }
        if (!TryValidateSegmentSources(sources, out failure))
        {
            return null;
        }

        var geometry = ParseKeogramGeometry(request, out failure);
        if (geometry is null)
        {
            return null;
        }
        if (sources.Any(source => source.Layout!.Height != geometry.SampleCount) || !SharesRig(sources, geometry))
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.KeogramGeometryMismatch, KeogramGeometryV1.AuxiliaryInputName);
            return null;
        }

        var axes = (request.AuxiliaryInputs ?? []).Where(static input =>
            string.Equals(input.Name, KeogramSegmentAxesV1.AuxiliaryInputName, StringComparison.Ordinal)).ToArray();
        if (axes.Length == 0)
        {
            failure = ProcessingOutcome.Skipped(
                ProcessingReasonCodes.MissingKeogramSegmentAxes, KeogramSegmentAxesV1.AuxiliaryInputName);
            return null;
        }
        var parsed = axes.Length == 1 && axes[0].Kind == ProcessingAuxiliaryInputKind.CanonicalJson &&
            string.Equals(axes[0].SchemaVersion, KeogramSegmentAxesV1.CurrentSchemaVersion, StringComparison.Ordinal)
                ? KeogramSegmentAxesJson.Parse(axes[0].Payload.Span)
                : null;
        var bySegment = parsed is { Segments: { } entries } &&
            string.Equals(parsed.SchemaVersion, KeogramSegmentAxesV1.CurrentSchemaVersion, StringComparison.Ordinal) &&
            string.Equals(
                ProcessingIdentity.ComputePayloadSha256(KeogramSegmentAxesJson.Serialize(parsed)),
                axes[0].IdentitySha256,
                StringComparison.OrdinalIgnoreCase) &&
            entries.All(static entry => entry is { Frames.Count: > 0 } &&
                entry.Frames.All(static frame => frame is not null && frame.ObservationStartedUtc.Offset == TimeSpan.Zero)) &&
            entries.Select(static entry => entry.ArtifactId).Distinct().Count() == entries.Count &&
            entries.Count == sources.Count
                ? entries.ToDictionary(static entry => entry.ArtifactId)
                : null;
        if (bySegment is null || sources.Any(source => !bySegment.ContainsKey(source.ArtifactId)))
        {
            failure = ProcessingOutcome.TerminalFailure(
                ProcessingReasonCodes.InvalidKeogramSegmentAxes, KeogramSegmentAxesV1.AuxiliaryInputName);
            return null;
        }

        // Immediate segment identity is not the original equal-time source tie-breaker. Keep the committed
        // ordered source lineage and pair it with each sampled column before globally ordering actual frames.
        if (sources.Any(source => source.SourceArtifactIds is null ||
                source.SourceArtifactIds.Count != bySegment[source.ArtifactId].Frames.Count ||
                source.SourceArtifactIds.Any(static id => id == Guid.Empty)) ||
            sources.Sum(static source => (long)source.SourceArtifactIds!.Count) > KeogramComposer.MaximumColumnLimit ||
            sources.SelectMany(static source => source.SourceArtifactIds!).Distinct().Count() !=
                sources.Sum(static source => source.SourceArtifactIds!.Count))
        {
            failure = ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidKeogramSegmentAxes,
                KeogramSegmentAxesV1.AuxiliaryInputName);
            return null;
        }
        if (sources.Any(source => bySegment[source.ArtifactId].Frames.Select((frame, index) =>
                frame.Column < 0 || frame.Column >= source.Layout!.Width || (index > 0 &&
                    (frame.Column <= bySegment[source.ArtifactId].Frames[index - 1].Column ||
                     frame.ObservationStartedUtc < bySegment[source.ArtifactId].Frames[index - 1].ObservationStartedUtc)))
                .Any(static invalid => invalid)))
        {
            failure = ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidLineage, nameof(request.Inputs));
            return null;
        }
        var segments = sources.SelectMany(source => bySegment[source.ArtifactId].Frames.Select((frame, index) =>
                (Source: source, Frame: frame, OriginalId: source.SourceArtifactIds![index])))
            .OrderBy(static item => item.Frame.ObservationStartedUtc).ThenBy(static item => item.OriginalId)
            .Select(static item => new KeogramSegment(item.Source.Layout!.Width, item.Source.Layout.Height,
                item.Source.Layout.StrideBytes, item.Source.Layout.PixelFormat, item.Source.Payload,
                [new KeogramSegmentColumn(item.Frame.Column, item.Frame.ObservationStartedUtc)])).ToArray();
        failure = null;
        return new KeogramAssemblyPlan(
            sources,
            segments,
            CreateKeogramComposition(identity, geometry),
            geometry,
            TimeSpan.FromTicks(sources.Sum(static source => source.Integration.Ticks)));
    }

    private static bool TryValidateSegmentSources(
        List<ProcessingArtifact> sources,
        out ProcessingOutcome? failure)
    {
        var first = sources[0].Layout;
        foreach (var source in sources)
        {
            if (first is null || source.Role != FrameArtifactRole.Preview ||
                !ProcessingRecipeSupport.TryValidateFrame(source, out var layout, out _) ||
                layout.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24) ||
                layout.StrideBytes != checked(layout.Width * ImageLayout.BytesPerPixel(layout.PixelFormat)))
            {
                failure = ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidSelector, nameof(sources));
                return false;
            }
            if (layout.Height != first.Height || layout.PixelFormat != first.PixelFormat)
            {
                failure = ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.IncompatibleInput, nameof(sources));
                return false;
            }
        }
        failure = null;
        return true;
    }

    private static bool SharesRig(IReadOnlyList<ProcessingArtifact> sources, KeogramGeometryV1 geometry) =>
        sources.All(source => string.Equals(
            source.Compatibility.Rig, geometry.RigProfileSha256, StringComparison.OrdinalIgnoreCase));

    private static KeogramGeometryV1? ParseKeogramGeometry(
        ProcessingExecutionRequest request,
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
