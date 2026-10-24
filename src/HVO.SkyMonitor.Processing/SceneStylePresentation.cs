using System.Collections.Immutable;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Explicit scene typography and point glyph. Scientific captions retain their own fixed appearance.</summary>
public sealed record ScenePresentationStyleV1(PresentationAnnotationStyleV1 Annotation,
    string LabelFont = "MonoBold", string CardinalFont = "MonoBold", string PointGlyph = "Circle")
{
    public PresentationFontFaceV3 LabelFace => Face(LabelFont);
    public PresentationFontFaceV3 CardinalFace => Face(CardinalFont);
    public string IdentitySha256 => CaptureContractJson.ComputeCanonicalJsonSha256(CaptureContractJson.SerializeToElement(new
    {
        version = SceneStylePresentation.ProducerVersion,
        fonts = PresentationRichText.FontSetIdentity,
        Annotation,
        LabelFont,
        CardinalFont,
        PointGlyph
    }));

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Annotation);
        _ = LabelFace;
        _ = CardinalFace;
        if (PointGlyph is not ("Circle" or "Crosshair"))
            throw new ArgumentException("Only Circle and Crosshair point glyphs are supported.", nameof(PointGlyph));
    }

    private static PresentationFontFaceV3 Face(string value) => value switch
    {
        "MonoRegular" => PresentationFontFaceV3.MonoRegular,
        "MonoBold" => PresentationFontFaceV3.MonoBold,
        "PlexRegular" => PresentationFontFaceV3.PlexRegular,
        _ => throw new ArgumentException("Only pinned Mono regular/bold and Plex regular are supported; no synthetic bold or fallback.", nameof(value))
    };
}

/// <summary>Opt-in scene style producer and exact descriptor; legacy aliases and defaults remain unchanged.</summary>
public static class SceneStylePresentation
{
    public const string StepAlias = "SceneStylePresentationLayerV1";
    public const string ProducerVersion = "projected-scene-presentation-v14-explicit-style";
    public static ProcessingNodeOptionsDescriptor Descriptor { get; } = new(
        "scene-style-presentation-options-v1", StepAlias, ProducerVersion, "Scene overlay style",
        "Choose packaged fonts, RGB colours and a point glyph. Mono8 uses the red channel; display composition promotes monochrome bases to RGB. Qualification captions and measured/expected semantics remain fixed.", Options());

    public static PresentationSceneLayerPayloadsV2 Create(ProjectedSceneV1 scene, ScenePresentationStyleV1 style,
        bool includeMarkers = true, bool includeLabels = true, bool includeConstellations = true,
        bool includeImageCircle = true, bool includeCardinalDirections = true,
        MeasuredStellarAssociationsV1? associations = null, bool expectedPositionDiagnostics = true)
    {
        ArgumentNullException.ThrowIfNull(style);
        style.Validate();
        return PresentationLayerProducers.FromProjectedSceneGroupsCore(scene, style.Annotation, includeMarkers, includeLabels,
            includeConstellations, includeImageCircle, includeCardinalDirections, associations, expectedPositionDiagnostics, style);
    }

    private static ImmutableArray<ProcessingOptionDescriptor> Options()
    {
        var options = ProcessingNodeOptionsDescriptors.ScenePresentationLayer.Options
            .Where(option => option.Key is not ("markerValue" or "labelValue" or "cardinalValue" or "imageCircleValue")).ToImmutableArray();
        foreach (var (prefix, label, color) in new (string, string, PresentationColor)[]
        {
            ("marker", "Marker colour", new(144, 144, 144)), ("label", "Label colour", new(255, 255, 255)),
            ("cardinal", "Cardinal colour", new(255, 255, 255)), ("imageCircle", "Image circle colour", new(96, 96, 96))
        })
            options = options.Add(ProcessingOptionDescriptor.Color(prefix + "Color", prefix + "Red", prefix + "Green", prefix + "Blue",
                label, "RGB channels 0–255. Monochrome exports use red. Required scientific captions keep their fixed palette.", color));
        foreach (var prefix in new[] { "label", "cardinal" })
            options = options.Add(ProcessingOptionDescriptor.Font(prefix + "Font", prefix + "Font", prefix + " font",
                "Checksum-pinned Mono regular/bold or Plex regular. Face and emphasis are selected together; Plex bold is unavailable.",
                "MonoBold", ["MonoRegular", "MonoBold", "PlexRegular"]));
        return options.Add(ProcessingOptionDescriptor.Glyph("pointGlyph", "pointGlyph", "Point marker glyph",
            "Circle or crosshair at the existing projected position. This never makes a position measured.", "Circle", ["Circle", "Crosshair"]));
    }

    internal static bool MatchesRetainedLayer(PresentationLayerPayloadV1 payload, PresentationLayerV1 layer) =>
        payload.SchemaVersion != PresentationLayerPayloadV1.SceneStyleSchemaVersion ||
        layer.RendererVersion == PresentationLayerCompositor.SceneStyleAlgorithmVersion && layer.StyleVersion == ProducerVersion;

    /// <summary>Losslessly opts a consumer into the versioned scene payload; no other setting changes.</summary>
    public static JsonElement OptInConsumer(JsonElement? source)
    {
        var descriptor = new ProcessingNodeOptionsDescriptor("scene-style-consumer-opt-in-v1", "SceneStyleConsumer", "1",
            "Scene style consumer", "Explicit whole-draft upgrade.",
            [ProcessingOptionDescriptor.Toggle("useSceneStyle", "useSceneStyle", "Use scene style", "Accept the versioned scene payload.", false)]);
        var result = ProcessingNodeOptionsCodec.Encode(descriptor, source ?? JsonSerializer.SerializeToElement(new { }),
            new Dictionary<string, ProcessingOptionValue> { ["useSceneStyle"] = ProcessingOptionValue.FromToggle(true) });
        if (!result.IsValid) throw new ArgumentException(string.Join(' ', result.Diagnostics.Select(item => item.Message)), nameof(source));
        return result.Options!.Value;
    }
}

/// <summary>Whole-draft-only conversion from an exact legacy version. Unknown and unedited options stay untouched.</summary>
public sealed class SceneStylePresentationUpgrade(string fromStepVersion) : IProcessingNodeOptionsUpgrade
{
    public string StepAlias => ProcessingNodeOptionsDescriptors.ScenePresentationLayerStepAlias;
    public string ToStepAlias => SceneStylePresentation.StepAlias;
    public string FromStepVersion { get; } = fromStepVersion;
    public string ToStepVersion => SceneStylePresentation.ProducerVersion;

    public ProcessingGraphNodeDefinition Upgrade(ProcessingGraphNodeDefinition node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var options = UpgradeOptions(node.EffectiveOptions);
        var outputs = node.Outputs.Select(output => output with
        {
            SchemaVersion = PresentationLayerPayloadV1.SceneStyleSchemaVersion,
            Recipe = output.Recipe is null ? null : output.Recipe with { ImplementationVersion = ToStepVersion }
        }).ToImmutableArray();
        return CopyNode(node, ToStepAlias, ToStepVersion, options, outputs: outputs);
    }

    public JsonElement UpgradeOptions(JsonElement source)
    {
        var old = ProcessingNodeOptionsCodec.Decode(ProcessingNodeOptionsCatalog.BuiltIn.Resolve(StepAlias, FromStepVersion).Descriptor!, source);
        if (!old.IsValid) throw new ArgumentException(string.Join(' ', old.Diagnostics.Select(item => item.Message)), nameof(source));
        var edits = new Dictionary<string, ProcessingOptionValue>();
        foreach (var prefix in new[] { "marker", "label", "cardinal", "imageCircle" })
            if (old.Get(prefix + "Value").Value.AsNullableWholeNumber() is { } channel)
                edits[prefix + "Color"] = ProcessingOptionValue.FromColor(new((byte)channel, (byte)channel, (byte)channel));
        // v12 never opted into expected-position diagnostics; preserve its strict labels when explicitly upgrading.
        if (FromStepVersion == ProcessingNodeOptionsDescriptors.ScenePresentationLayerV12.StepVersion &&
            !source.EnumerateObject().Any(property => property.Name.Equals("expectedPositionDiagnostics", StringComparison.OrdinalIgnoreCase)))
            edits["expectedPositionDiagnostics"] = ProcessingOptionValue.FromToggle(false);
        var encoded = ProcessingNodeOptionsCodec.Encode(SceneStylePresentation.Descriptor, source, edits);
        if (!encoded.IsValid) throw new ArgumentException(string.Join(' ', encoded.Diagnostics.Select(item => item.Message)), nameof(source));
        return encoded.Options!.Value;
    }

    internal static ProcessingGraphNodeDefinition CopyNode(ProcessingGraphNodeDefinition node, string alias, string version,
        JsonElement options, ImmutableArray<ProcessingGraphProductContract>? outputs = null,
        ImmutableArray<ProcessingGraphInputContract>? inputs = null) => new(node.Id, alias, version, node.OperationKind,
            node.Enabled, node.FailurePolicy, node.Order, options, node.Dependencies, inputs ?? node.Inputs,
            outputs ?? node.Outputs, node.Window, node.CapabilityLabels, node.HostApplicability);

    internal static ProcessingGraphNodeDefinition UpgradeConsumer(ProcessingGraphNodeDefinition node, IReadOnlySet<string> sceneVariants)
    {
        var inputs = node.Inputs.Select(input =>
            (input.RecipeNames.Contains(PresentationProcessingProducts.LayerRecipeName, StringComparer.Ordinal) ||
                input.RecipeNames.Contains("scene-presentation-layer", StringComparer.Ordinal)) &&
            (input.Variants.IsEmpty || input.Variants.Any(sceneVariants.Contains)) && !input.SchemaVersions.IsEmpty &&
            !input.SchemaVersions.Contains(PresentationLayerPayloadV1.SceneStyleSchemaVersion, StringComparer.Ordinal)
            ? input with { SchemaVersions = input.SchemaVersions.Add(PresentationLayerPayloadV1.SceneStyleSchemaVersion) }
            : input).ToImmutableArray();
        return CopyNode(node, node.StepAlias, node.StepVersion, SceneStylePresentation.OptInConsumer(node.EffectiveOptions), inputs: inputs);
    }
}
