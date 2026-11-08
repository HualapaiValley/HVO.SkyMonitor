using System.Collections.Immutable;
using System.Text.Json;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Existing rolling-window controls; recipe selection and normalization plans are preserved.</summary>
public static class RollingCombinationOptionDescriptors
{
    public static ProcessingNodeOptionsDescriptor Descriptor { get; } = new(
        "rolling-combination-options-v1", "RollingCombination", Linear16ArithmeticMean.AlgorithmVersion,
        "Same-gain rolling combination",
        "Combine compatible linear frames at the same gain. Trailing windows warm up immediately; centered windows wait for all required positions. Age and integration limits cut the selected frames from newest backwards. Output routes, recipes and normalization plans are preserved.",
        [
            ProcessingOptionDescriptor.Toggle("enabled", "enabled", "Enable rolling combination",
                "Run the existing rolling mean when this graph node is enabled.", true),
            ProcessingOptionDescriptor.WholeNumber("windowSize", "windowSize", "Window size",
                $"Maximum compatible frames: 100 trailing or {ProcessingGraphCompiler.MaximumContractsPerNode} centered. Centered windows require the full size, including future frames.", 5, 1, 100, "frames"),
            ProcessingOptionDescriptor.Choice("windowKind", "windowKind", "Window kind",
                "Trailing accepts a partial window. Centered requires positions from -(size / 2) through size - (size / 2) - 1.",
                "Trailing", ["Trailing", "Centered"]),
            ProcessingOptionDescriptor.NullableRealNumber("maximumIntegrationMilliseconds", "maximumIntegrationMilliseconds",
                "Maximum integration", "Positive total exposure limit. The newest frame is always retained even when its exposure exceeds the limit.",
                null, double.Epsilon, double.MaxValue, "ms", "Unlimited", "Remove limit"),
            ProcessingOptionDescriptor.NullableRealNumber("maximumAgeMilliseconds", "maximumAgeMilliseconds",
                "Maximum age", "Positive age limit measured from the newest selected frame. Frames exactly at the limit are included.",
                null, double.Epsilon, double.MaxValue, "ms", "Unlimited", "Remove limit")
        ]);

    public static ProcessingNodeOptionsDescriptor MixedGain { get; } = new(
        "mixed-gain-rolling-combination-options-v1", "RollingCombination",
        new MixedGainNormalizedMeanRecipe().Definition.ImplementationVersion,
        "Mixed-gain rolling combination",
        "Configure the existing mixed-gain window. Each execution still requires its frozen normalization plan and compatible calibration masters. Recipe, output routes and normalization bindings are preserved; saving does not acquire masters, change algorithms or activate the graph.",
        Descriptor.Options);

    public static bool IsSameGain(JsonElement options) => options.ValueKind == JsonValueKind.Object &&
        options.EnumerateObject().Where(static property => property.Name.Equals("recipe", StringComparison.OrdinalIgnoreCase))
            .All(static property => property.Value.ValueKind == JsonValueKind.Null);

    public static bool IsMixedGain(JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Object) return false;
        var recipes = options.EnumerateObject().Where(static property => property.Name.Equals("recipe", StringComparison.OrdinalIgnoreCase)).ToArray();
        return recipes.Length == 1 && recipes[0].Value.ValueKind == JsonValueKind.String &&
            recipes[0].Value.GetString() == BuiltInProcessingRecipes.MixedGainNormalizedMean;
    }

    internal static bool MatchesRecipe(ProcessingNodeOptionsDescriptor descriptor, JsonElement options) =>
        descriptor.StepVersion == MixedGain.StepVersion ? IsMixedGain(options) : IsSameGain(options);

    internal static bool IsDescriptor(ProcessingNodeOptionsDescriptor descriptor) =>
        descriptor.StepAlias.Equals(Descriptor.StepAlias, StringComparison.OrdinalIgnoreCase) &&
        (descriptor.StepVersion == Descriptor.StepVersion || descriptor.StepVersion == MixedGain.StepVersion);

    internal static ImmutableArray<ProcessingGraphDiagnostic> ValidateWindow(ProcessingGraphNodeDefinition node, string path)
    {
        var descriptor = node.StepVersion == MixedGain.StepVersion ? MixedGain : Descriptor;
        if (!node.StepAlias.Equals(descriptor.StepAlias, StringComparison.OrdinalIgnoreCase) ||
            node.StepVersion != descriptor.StepVersion)
            return [];
        var decoded = ProcessingNodeOptionsCodec.Decode(descriptor, node.EffectiveOptions, path + ".effectiveOptions");
        if (!decoded.IsValid) return decoded.Diagnostics;
        var size = (int)decoded.Get("windowSize").Value.AsWholeNumber();
        var kind = Enum.Parse<ProcessingGraphWindowKind>(decoded.Get("windowKind").Value.AsText());
        var positions = kind == ProcessingGraphWindowKind.Centered
            ? Enumerable.Range(-(size / 2), size).ToArray() : [];
        return node.Window is { } window && window.Kind == kind && window.MaximumInputCount == size &&
            window.MinimumInputCount == (kind == ProcessingGraphWindowKind.Centered ? size : 1) &&
            !window.RequiredPositions.IsDefault && window.RequiredPositions.SequenceEqual(positions)
            ? [] : [new(ProcessingGraphReasonCodes.InvalidWindow, path + ".window",
                "RollingCombination window kind, counts and required positions must agree with its effective options.")];
    }
}
