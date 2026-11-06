using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>Pure, whole-draft opt-in. The source definition remains the before image until explicitly accepted.</summary>
internal static class FrameFactsDraftConversion
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static readonly string[] Prefixes = ["topLeft", "topRight", "bottomLeft", "bottomRight"];

    internal static bool IsLegacy(string alias) => alias.Equals("EnvironmentPresentationLayer", StringComparison.OrdinalIgnoreCase);

    internal static CapturePipelineConfig Convert(CapturePipelineConfig source)
    {
        if (!source.Steps.Any(step => IsLegacy(step.Type)))
            throw new ArgumentException("This draft has no legacy Frame Facts preset node.", nameof(source));
        // Build every replacement before returning anything. Keep ids, ordering, dependencies and publication policies.
        var steps = source.Steps.Select(step =>
        {
            if (IsLegacy(step.Type))
            {
                var legacy = (step.Options ?? JsonSerializer.SerializeToElement(new { })).Deserialize<LegacyOptions>(JsonOptions)
                    ?? throw new ArgumentException("Legacy options are required.", nameof(source));
                var presets = FrameFactsCornerPresets.FromConfiguration(legacy.EnvironmentalKinds);
                if (!presets.IsValid) throw new ArgumentException($"{presets.FailedPresetId}: {presets.Error!.Code}", nameof(source));
                var form = new ProcessingNodeOptionsForm(FrameFactsPresentationOptionDescriptors.Descriptor, step.Options);
                for (var index = 0; index < Prefixes.Length; index++)
                {
                    form.Set(Prefixes[index] + "Template", presets.Presets[index].Source);
                    var colour = legacy.Value ?? 255;
                    form.Set(Prefixes[index] + "Color", $"#{colour:x2}{colour:x2}{colour:x2}");
                }
                // Legacy inset/spacing and dimensions are carried, including their omitted defaults.
                form.Set("inset", legacy.Inset.ToString(System.Globalization.CultureInfo.InvariantCulture));
                form.Set("lineSpacing", legacy.LineSpacing.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (!form.TryBuild(out var options, out var errors))
                    throw new ArgumentException(string.Join(' ', errors.Select(error => error.Message)), nameof(source));
                // Keep the old variants so all existing dependency/publication references stay valid.
                options = Patch(options, Text("factsOutputVariant", legacy.FactsOutputVariant), Text("outputVariant", legacy.OutputVariant));
                if (!options.Value.EnumerateObject().Any(property => property.Name.Equals("environmentalKinds", StringComparison.OrdinalIgnoreCase)))
                {
                    // Only the missing legacy default is materialized. Existing arrays and unknown JSON stay byte-for-byte.
                    var json = options.Value.GetRawText();
                    options = JsonDocument.Parse(json.Insert(json.LastIndexOf('}'),
                        (options.Value.EnumerateObject().Any() ? "," : string.Empty) + "\"environmentalKinds\":" + JsonSerializer.Serialize(legacy.EnvironmentalKinds, JsonOptions))).RootElement.Clone();
                }
                return step with { Type = FrameFactsPresentation.StepAlias, Options = options };
            }
            if (step.Type.Equals("OverlayManifest", StringComparison.OrdinalIgnoreCase) ||
                step.Type.Equals("PresentationMaterializer", StringComparison.OrdinalIgnoreCase))
            {
                return step with
                {
                    Options = Patch(step.Options, (
                    ProcessingOptionDescriptor.Toggle("useRichFrameFacts", "useRichFrameFacts", "Rich Frame Facts", "Explicit consumer opt-in.", false),
                    ProcessingOptionValue.FromToggle(true)))
                };
            }
            return step;
        }).ToArray();
        return source with { Steps = steps };
    }

    private static (ProcessingOptionDescriptor Descriptor, ProcessingOptionValue Value) Text(string key, string value)
        => (ProcessingOptionDescriptor.Text(key, key, key, "Preserved legacy variant.", string.Empty, 128), ProcessingOptionValue.FromText(value));

    private static JsonElement Patch(JsonElement? source, params (ProcessingOptionDescriptor Descriptor, ProcessingOptionValue Value)[] edits)
    {
        var descriptor = new ProcessingNodeOptionsDescriptor("frame-facts-draft-conversion-v1", "DraftConversion", "1", "Draft conversion", "Explicit whole-draft conversion.",
            edits.Select(edit => edit.Descriptor));
        var encoded = ProcessingNodeOptionsCodec.Encode(descriptor, source ?? JsonSerializer.SerializeToElement(new { }),
            edits.ToDictionary(edit => edit.Descriptor.Key, edit => edit.Value, StringComparer.Ordinal));
        if (!encoded.IsValid) throw new ArgumentException(string.Join(' ', encoded.Diagnostics.Select(error => error.Message)), nameof(source));
        return encoded.Options!.Value;
    }

    private sealed class LegacyOptions
    {
        public string FactsOutputVariant { get; init; } = "presentation-metadata-facts-v2";
        public string OutputVariant { get; init; } = "environment-layer-v1";
        public byte? Value { get; init; }
        public int Inset { get; init; } = 4;
        public int LineSpacing { get; init; } = 2;
        public IReadOnlyList<EnvironmentalObservationKind> EnvironmentalKinds { get; init; } =
        [EnvironmentalObservationKind.AirTemperature, EnvironmentalObservationKind.RelativeHumidity, EnvironmentalObservationKind.AtmosphericPressure,
            EnvironmentalObservationKind.WindSpeed, EnvironmentalObservationKind.RainState, EnvironmentalObservationKind.CloudCover];
    }
}
