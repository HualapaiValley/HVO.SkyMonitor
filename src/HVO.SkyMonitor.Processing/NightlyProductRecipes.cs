using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Bounded options for the nightly keogram recipe.</summary>
public sealed record KeogramRecipeOptions(
    int? SliceColumn = null,
    double MaximumGapSeconds = KeogramComposer.DefaultMaximumGapSeconds,
    int GapColumnCount = 1,
    int MaximumColumnCount = KeogramComposer.DefaultMaximumColumnCount);

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
        if (parsed.SliceColumn is < 0 ||
            parsed.MaximumGapSeconds <= 0 || !double.IsFinite(parsed.MaximumGapSeconds) ||
            parsed.MaximumGapSeconds > 86400 ||
            parsed.GapColumnCount is < 1 or > KeogramComposer.MaximumGapColumnCount ||
            parsed.MaximumColumnCount is < 1 or > 65536)
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

        var options = ProcessingRecipeSupport.ParseOptions<KeogramRecipeOptions>(
            identity.Descriptor.Options.GetProperty("parameters"));
        var composition = new KeogramCompositionOptions(
            options.SliceColumn, options.MaximumGapSeconds, options.GapColumnCount, options.MaximumColumnCount);
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
        new("keogram-slice", KeogramComposer.AlgorithmVersion),
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
