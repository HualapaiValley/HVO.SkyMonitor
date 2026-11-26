using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal sealed record CenteredViewLayerBinding(CenteredViewLayerScope Scope, CenteredViewLayerStyle Style)
{
    internal CenteredViewLayerKey Key(Guid captureId) => new(Style.Kind, Scope, captureId);
}

internal readonly record struct CenteredAuthoritativeAssociationWriter(string Producer, string Variant)
{
    internal bool Matches(ExposureProductSlot slot) =>
        MeasuredStellarAssociationsV1.IsSupportedSchemaVersion(CenteredViewLayerBindings.SourceSchema(slot.SchemaVersion)) &&
        string.Equals(slot.Producer, Producer, StringComparison.Ordinal) &&
        string.Equals(slot.Variant, Variant, StringComparison.Ordinal);
}

internal readonly record struct CenteredAuthoritativeAssociationResolution(
    bool IsResolved,
    CenteredAuthoritativeAssociationWriter? Writer)
{
    internal static CenteredAuthoritativeAssociationResolution Unresolved => default;
    internal static CenteredAuthoritativeAssociationResolution None => new(true, null);
    internal static CenteredAuthoritativeAssociationResolution Resolved(CenteredAuthoritativeAssociationWriter writer) => new(true, writer);
    internal static CenteredAuthoritativeAssociationResolution FromLookup(CenteredAuthoritativeAssociationWriter? writer) => new(true, writer);
}

/// <summary>
/// Durable producer, recipe, and semantic layer-role metadata persisted on <c>centered_exposure_products.provenance_json</c>
/// for non-linear, non-scene centered products so missing-graph recovery preserves exact semantic roles even after
/// <c>processing_outputs</c> retention expiry and without <c>processing_execution_nodes.outputs_json</c>.
/// </summary>
internal sealed record CenteredProductSemanticProvenanceV1(
    string SchemaVersion,
    string? StepAlias,
    string? RecipeName,
    string? RecipeImplementationVersion,
    IReadOnlyList<string>? LayerKinds)
{
    internal const string CurrentSchemaVersion = "centered-product-semantics-v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);

    internal static CenteredProductSemanticProvenanceV1? TryParse(byte[]? json)
    {
        if (json is null || json.Length == 0)
            return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<CenteredProductSemanticProvenanceV1>(json, JsonOptions);
            return parsed is not null && string.Equals(parsed.SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal)
                ? parsed
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static CenteredProductSemanticProvenanceV1? TryParseFromDescriptorJson(byte[]? descriptorJson)
    {
        if (descriptorJson is null || descriptorJson.Length == 0)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(descriptorJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (TryReadRecipe(root, out var name, out var impl))
                return new(CurrentSchemaVersion, null, name, impl, null);
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static CenteredProductSemanticProvenanceV1? TryParseFromNodeOutputsJson(string? outputsJson, string variant)
    {
        if (string.IsNullOrWhiteSpace(outputsJson))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(outputsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;
                var itemVariant = GetStringIgnoreCase(item, "variant");
                if (!string.Equals(itemVariant, variant, StringComparison.Ordinal))
                    continue;
                if (TryGetObjectIgnoreCase(item, "recipe", out var recipe))
                {
                    var name = GetStringIgnoreCase(recipe, "name");
                    var impl = GetStringIgnoreCase(recipe, "implementationVersion");
                    if (name is not null || impl is not null)
                        return new(CurrentSchemaVersion, null, name, impl, null);
                }
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadRecipe(JsonElement root, out string? name, out string? implementationVersion)
    {
        name = null;
        implementationVersion = null;
        JsonElement artifact;
        if (!TryGetObjectIgnoreCase(root, "artifact", out artifact) &&
            (!TryGetObjectIgnoreCase(root, "descriptor", out var descriptor) ||
             !TryGetObjectIgnoreCase(descriptor, "artifact", out artifact)))
            return false;
        if (!TryGetObjectIgnoreCase(artifact, "recipe", out var recipe))
            return false;
        name = GetStringIgnoreCase(recipe, "name");
        implementationVersion = GetStringIgnoreCase(recipe, "implementationVersion");
        return name is not null || implementationVersion is not null;
    }

    private static bool TryGetObjectIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.Object)
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string? GetStringIgnoreCase(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
            }
        }
        return null;
    }
}

internal static class CenteredViewLayerBindings
{
    // Outputless view settlements have a cursor without asserting that the science slot produced an artifact.
    internal const string DispositionPrefix = "centered-view-disposition-v1/";

    private static readonly CenteredViewLayerBinding Markers = Reference("measured-source-markers", 19, false);
    private static readonly CenteredViewLayerBinding Annotation = Reference("star-annotations", 20);
    private static readonly CenteredViewLayerBinding MeasuredAssociations = Reference(MeasuredAssociationPresentation.MeasuredLayerKind, 21, true);
    private static readonly CenteredViewLayerBinding ExpectedNotDetected = Reference(MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind, 23, false);
    private static readonly CenteredViewLayerBinding Cardinals = Reference("scene-cardinals", 25);
    private static readonly CenteredViewLayerBinding ImageCircle = Reference("scene-image-circle", 15);
    private static readonly CenteredViewLayerBinding Constellations = Reference("scene-constellations", 10);
    private static readonly CenteredViewLayerBinding DeepSky = Reference("scene-deep-sky", 35, false);
    private static readonly CenteredViewLayerBinding CloudMask = Reference("cloud-mask", 30, false);
    private static readonly CenteredViewLayerBinding CloudLabel = Reference("cloud-label", 31, false);
    private static readonly CenteredViewLayerBinding RegionalVisibility = Reference(RegionalStellarVisibilityProducts.LayerKind, 32, false);
    private static readonly CenteredViewLayerBinding Environment = Reference("environment", 50);
    private static readonly CenteredViewLayerBinding Residuals = Reference(AstrometricDiagnosticPresentation.ResidualsLayer, 412, false);
    private static readonly CenteredViewLayerBinding Satellites = new(CenteredViewLayerScope.Contributor, new("predicted-satellites", 400, false));

    internal static IReadOnlyList<CenteredViewLayerBinding> Known { get; } =
        [Markers, Annotation, MeasuredAssociations, ExpectedNotDetected, Cardinals, ImageCircle, Constellations, DeepSky, CloudMask, CloudLabel, RegionalVisibility, Environment, Residuals, Satellites];

    private static readonly Dictionary<string, CenteredViewLayerBinding> KnownByKind =
        Known.ToDictionary(static binding => binding.Style.Kind, StringComparer.Ordinal);

    internal static bool SupportsSchema(string? schema) => schema is ExposureMeasurementProducts.SchemaVersion or
        RegionalStellarVisibilityProducts.SchemaVersion or
        MeasuredAstrometrySolutionV1.CurrentSchemaVersion ||
        MeasuredStellarAssociationsV1.IsSupportedSchemaVersion(schema) ||
        PresentationLayerPayloadV1.SupportsSchema(schema);

    internal static string SourceSchema(string schema) => schema.StartsWith(DispositionPrefix, StringComparison.Ordinal)
        ? schema[DispositionPrefix.Length..] : schema;

    internal static CenteredProductSemanticProvenanceV1? CreateSemanticProvenance(
        CaptureProcessingGraphNode node,
        string schemaVersion,
        string variant,
        string? recipeName,
        string? implementationVersion)
    {
        var sourceSchema = SourceSchema(schemaVersion);
        if (!SupportsSchema(sourceSchema))
            return null;

        IReadOnlyList<string> layerKinds;
        if (MeasuredStellarAssociationsV1.IsSupportedSchemaVersion(sourceSchema))
        {
            var isExposureAssociation =
                string.Equals(node.Alias, ExposureAssociationProducts.StepAlias, StringComparison.Ordinal) ||
                string.Equals(recipeName, ExposureAssociationProducts.RecipeName, StringComparison.Ordinal);
            layerKinds = isExposureAssociation
                ? [MeasuredAssociations.Style.Kind, ExpectedNotDetected.Style.Kind]
                : [];
        }
        else if (node.Alias is "ScenePresentationLayer" or SceneStylePresentation.StepAlias &&
            string.Equals(implementationVersion, MeasuredAssociationPresentation.ExpectedNotDetectedVersion, StringComparison.Ordinal))
        {
            layerKinds = [ExpectedNotDetected.Style.Kind];
        }
        else if (node.Alias is "ScenePresentationLayer" or SceneStylePresentation.StepAlias &&
            string.Equals(implementationVersion, PresentationDeepSkyLayerProducer.ProducerVersion, StringComparison.Ordinal))
        {
            layerKinds = [DeepSky.Style.Kind];
        }
        else
        {
            var ports = PortBindings(node.Alias);
            if (ports.Count == 0)
            {
                layerKinds = [];
            }
            else
            {
                var portIndex = ResolveDeclaredOutputIndex(node, variant);
                layerKinds = portIndex >= 0 && portIndex < ports.Count
                    ? ports[portIndex].Select(static binding => binding.Style.Kind).ToArray()
                    : [];
            }
        }

        return new(CenteredProductSemanticProvenanceV1.CurrentSchemaVersion, node.Alias, recipeName,
            implementationVersion, layerKinds);
    }

    private static int ResolveDeclaredOutputIndex(CaptureProcessingGraphNode node, string variant)
    {
        if (node.Step is IMultiOutputCaptureProcessingGraphStep multi)
        {
            for (var index = 0; index < multi.Outputs.Count; index++)
            {
                if (string.Equals(multi.Outputs[index].Variant, variant, StringComparison.Ordinal))
                    return index;
            }
            return -1;
        }
        if (node.Step is ICaptureProcessingGraphStep single &&
            string.Equals(single.OutputVariant, variant, StringComparison.Ordinal))
            return 0;
        return -1;
    }

    internal static bool IsAuthoritativeAssociationSlot(
        ExposureProductSlot slot, CenteredAuthoritativeAssociationWriter? authoritativeWriter = null) =>
        IsAuthoritativeAssociationSlot(
            slot,
            authoritativeWriter is { } writer
                ? CenteredAuthoritativeAssociationResolution.Resolved(writer)
                : CenteredAuthoritativeAssociationResolution.Unresolved,
            semantics: null);

    internal static bool IsAuthoritativeAssociationSlot(
        ExposureProductSlot slot,
        CenteredAuthoritativeAssociationResolution authoritativeWriter,
        CenteredProductSemanticProvenanceV1? semantics = null)
    {
        if (!MeasuredStellarAssociationsV1.IsSupportedSchemaVersion(SourceSchema(slot.SchemaVersion)))
            return false;
        if (semantics is not null)
        {
            var isExposureAssociation =
                string.Equals(semantics.RecipeName, ExposureAssociationProducts.RecipeName, StringComparison.Ordinal) ||
                string.Equals(semantics.StepAlias, ExposureAssociationProducts.StepAlias, StringComparison.Ordinal);
            if (!isExposureAssociation)
                return false;
            return !authoritativeWriter.IsResolved ||
                authoritativeWriter.Writer is { } resolvedWriter && resolvedWriter.Matches(slot);
        }
        if (authoritativeWriter.IsResolved)
            return authoritativeWriter.Writer is { } writer && writer.Matches(slot);
        return slot.Variant == ExposureAssociationProducts.Variant ||
            slot.Producer == ExposureAssociationProducts.StepAlias;
    }

    internal static bool IsSceneExpectedNotDetectedSlot(
        ExposureProductSlot slot, CenteredProductSemanticProvenanceV1? semantics = null)
    {
        if (!PresentationLayerPayloadV1.SupportsSchema(SourceSchema(slot.SchemaVersion)))
            return false;
        if (semantics is not null)
        {
            if (string.Equals(semantics.RecipeImplementationVersion, MeasuredAssociationPresentation.ExpectedNotDetectedVersion, StringComparison.Ordinal))
                return true;
            if (semantics.LayerKinds is not null)
                return semantics.LayerKinds.Contains(MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind, StringComparer.Ordinal);
        }
        return slot.Variant == "expected-not-detected-layer-v1";
    }

    internal static IReadOnlyList<CenteredViewLayerBinding> ReadLegacySlot(
        ExposureProductSlot slot, CenteredAuthoritativeAssociationWriter? authoritativeWriter = null) =>
        ReadLegacySlot(
            slot,
            authoritativeWriter is { } writer
                ? CenteredAuthoritativeAssociationResolution.Resolved(writer)
                : CenteredAuthoritativeAssociationResolution.Unresolved,
            semantics: null);

    internal static IReadOnlyList<CenteredViewLayerBinding> ReadLegacySlot(
        ExposureProductSlot slot,
        CenteredAuthoritativeAssociationResolution authoritativeWriter,
        CenteredProductSemanticProvenanceV1? semantics = null)
    {
        var sourceSchema = SourceSchema(slot.SchemaVersion);
        if (MeasuredStellarAssociationsV1.IsSupportedSchemaVersion(sourceSchema))
            return IsAuthoritativeAssociationSlot(slot, authoritativeWriter, semantics)
                ? [MeasuredAssociations, ExpectedNotDetected]
                : [];

        if (PresentationLayerPayloadV1.SupportsSchema(sourceSchema))
        {
            if (semantics is not null)
            {
                if (string.Equals(semantics.RecipeImplementationVersion, MeasuredAssociationPresentation.ExpectedNotDetectedVersion, StringComparison.Ordinal))
                    return authoritativeWriter.Writer is not null ? [] : [ExpectedNotDetected];
                if (string.Equals(semantics.RecipeImplementationVersion, PresentationDeepSkyLayerProducer.ProducerVersion, StringComparison.Ordinal))
                    return [DeepSky];
                if (semantics.LayerKinds is not null)
                {
                    var resolved = new List<CenteredViewLayerBinding>(semantics.LayerKinds.Count);
                    foreach (var kind in semantics.LayerKinds)
                    {
                        if (kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind &&
                            authoritativeWriter.Writer is not null)
                            continue;
                        if (KnownByKind.TryGetValue(kind, out var binding))
                            resolved.Add(binding);
                    }
                    return resolved;
                }
            }
            return slot.Variant switch
            {
                "scene-annotation-layer-v1" => [Annotation],
                "scene-cardinal-layer-v1" => [Cardinals],
                "scene-image-circle-layer-v1" => [ImageCircle],
                "scene-constellation-layer-v1" => [Constellations],
                "scene-deep-sky-v1" => [DeepSky],
                "expected-not-detected-layer-v1" => authoritativeWriter.Writer is not null ? [] : [ExpectedNotDetected],
                "cloud-mask-layer-v1" => [CloudMask],
                "cloud-label-layer-v1" => [CloudLabel],
                "environment-layer-v1" => [Environment],
                "capture-satellites-v1-layer" => [Satellites],
                _ => []
            };
        }

        return sourceSchema switch
        {
            ExposureMeasurementProducts.SchemaVersion when slot.Variant == ExposureMeasurementProducts.Variant => [Markers],
            RegionalStellarVisibilityProducts.SchemaVersion => [RegionalVisibility],
            MeasuredAstrometrySolutionV1.CurrentSchemaVersion => [Residuals],
            _ => []
        };
    }

    internal static IReadOnlyDictionary<(string Producer, string Schema, string Variant), IReadOnlyList<CenteredViewLayerBinding>> ReadConfigured(
        ProcessingGraphDefinition graph)
    {
        var result = new Dictionary<(string, string, string), IReadOnlyList<CenteredViewLayerBinding>>();
        // ExposureStellarAssociations is the single authoritative centered producer of expected-not-detected
        // when configured; prevent a second scene-presentation writer from racing or overwriting it.
        var hasAuthoritativeAssociations = graph.Nodes.Any(static node =>
            node.Enabled && node.StepAlias == ExposureAssociationProducts.StepAlias);
        var boundLayers = new HashSet<(CenteredViewLayerScope Scope, string Kind)>();
        foreach (var node in graph.Nodes.Where(node => node.Enabled))
        {
            var ports = PortBindings(node.StepAlias);
            for (var index = 0; index < node.Outputs.Length; index++)
            {
                var output = node.Outputs[index];
                if (output.SchemaVersion is not { } schema || !SupportsSchema(schema))
                    continue;
                IReadOnlyList<CenteredViewLayerBinding> candidates;
                if (node.StepAlias is "ScenePresentationLayer" or SceneStylePresentation.StepAlias &&
                    output.Recipe?.ImplementationVersion == MeasuredAssociationPresentation.ExpectedNotDetectedVersion)
                {
                    candidates = hasAuthoritativeAssociations ? [] : [ExpectedNotDetected];
                }
                else if (index < ports.Count)
                {
                    candidates = ports[index];
                }
                else
                {
                    continue;
                }
                var bindings = candidates.Where(binding => boundLayers.Add((binding.Scope, binding.Style.Kind))).ToArray();
                if (bindings.Length > 0)
                    result.Add((node.Id, schema, output.Variant), bindings);
            }
        }
        return result;
    }

    // These are the output port orders declared by the corresponding step contracts.
    // The slot's schema and variant come from the frozen graph so custom variants still bind.
    internal static IReadOnlyList<IReadOnlyList<CenteredViewLayerBinding>> PortBindings(string? alias) => alias switch
    {
        ExposureMeasurementProducts.StepAlias => [[Markers]],
        ExposureAssociationProducts.StepAlias => [[MeasuredAssociations, ExpectedNotDetected]],
        MeasuredAstrometryCaptureProcessingStep.Alias or ExposureAstrometryCaptureProcessingStep.CenteredAlias => [[Residuals]],
        "ScenePresentationLayer" or SceneStylePresentation.StepAlias => [[Annotation], [Cardinals], [ImageCircle], [Constellations], [DeepSky]],
        "CloudPresentationLayer" => [[CloudMask], [CloudLabel]],
        RegionalStellarVisibilityProducts.StepAlias => [[RegionalVisibility]],
        "EnvironmentPresentationLayer" or FrameFactsPresentation.StepAlias => [[], [Environment]],
        CaptureSatelliteLayerProcessingStep.Alias => [[Satellites]],
        _ => []
    };

    private static CenteredViewLayerBinding Reference(string kind, int order, bool enabled = true) =>
        new(CenteredViewLayerScope.Reference, new(kind, order, enabled));
}
