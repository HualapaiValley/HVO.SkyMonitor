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

internal enum CenteredAuthoritativeAssociationResolutionState
{
    Unresolved,
    None,
    Proven,
    UnidentifiableExpired
}

internal readonly record struct CenteredAuthoritativeAssociationResolution(
    CenteredAuthoritativeAssociationResolutionState State,
    CenteredAuthoritativeAssociationWriter? Writer)
{
    internal bool IsResolved => State != CenteredAuthoritativeAssociationResolutionState.Unresolved;

    internal bool SuppressesSceneExpectedNotDetected =>
        State is CenteredAuthoritativeAssociationResolutionState.Proven or
            CenteredAuthoritativeAssociationResolutionState.UnidentifiableExpired;

    internal static CenteredAuthoritativeAssociationResolution Unresolved => default;

    internal static CenteredAuthoritativeAssociationResolution None =>
        new(CenteredAuthoritativeAssociationResolutionState.None, null);

    internal static CenteredAuthoritativeAssociationResolution Resolved(CenteredAuthoritativeAssociationWriter writer) =>
        new(CenteredAuthoritativeAssociationResolutionState.Proven, writer);

    internal static CenteredAuthoritativeAssociationResolution UnidentifiableExpired(CenteredAuthoritativeAssociationWriter writer) =>
        new(CenteredAuthoritativeAssociationResolutionState.UnidentifiableExpired, writer);
}

internal sealed record RecoveredNodeDeclaredOutput(
    string Producer,
    string SchemaVersion,
    string Variant,
    int PortIndex,
    CenteredProductSemanticProvenanceV1? Semantics);

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

    internal static CenteredProductSemanticProvenanceV1? TryParseFromNodeOutputsJson(
        string? outputsJson, string variant, out int? portIndex)
    {
        portIndex = null;
        if (string.IsNullOrWhiteSpace(outputsJson))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(outputsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;
            var index = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var itemVariant = GetStringIgnoreCase(item, "variant");
                    if (string.Equals(itemVariant, variant, StringComparison.Ordinal))
                    {
                        portIndex = index;
                        if (TryGetObjectIgnoreCase(item, "recipe", out var recipe))
                        {
                            var name = GetStringIgnoreCase(recipe, "name");
                            var impl = GetStringIgnoreCase(recipe, "implementationVersion");
                            if (name is not null || impl is not null)
                                return new(CurrentSchemaVersion, null, name, impl, null);
                        }
                        return null;
                    }
                }
                index++;
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<RecoveredNodeDeclaredOutput> ParseDeclaredNodeOutputs(string nodeId, string? outputsJson)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(outputsJson))
            return [];
        try
        {
            using var doc = JsonDocument.Parse(outputsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return [];
            var outputs = new List<RecoveredNodeDeclaredOutput>();
            var index = 0;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object)
                {
                    var variant = GetStringIgnoreCase(item, "variant");
                    var schema = GetStringIgnoreCase(item, "schemaVersion");
                    if (!string.IsNullOrWhiteSpace(variant) && !string.IsNullOrWhiteSpace(schema))
                    {
                        var sourceSchema = CenteredViewLayerBindings.SourceSchema(schema);
                        if (CenteredViewLayerBindings.SupportsSchema(sourceSchema))
                        {
                            string? recipeName = null;
                            string? implVersion = null;
                            if (TryGetObjectIgnoreCase(item, "recipe", out var recipe))
                            {
                                recipeName = GetStringIgnoreCase(recipe, "name");
                                implVersion = GetStringIgnoreCase(recipe, "implementationVersion");
                            }
                            CenteredProductSemanticProvenanceV1? semantics = null;
                            if (recipeName is not null || implVersion is not null)
                            {
                                var layerKinds = PresentationLayerPayloadV1.SupportsSchema(sourceSchema)
                                    ? CenteredViewLayerBindings.InferPresentationLayerKinds(recipeName, implVersion, index)
                                    : null;
                                semantics = new(CurrentSchemaVersion, null, recipeName, implVersion, layerKinds);
                            }
                            outputs.Add(new(nodeId, sourceSchema, variant, index, semantics));
                        }
                    }
                }
                index++;
            }
            return outputs;
        }
        catch (JsonException)
        {
            return [];
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
        string? implementationVersion,
        IReadOnlyDictionary<(string Producer, string Schema, string Variant), IReadOnlyList<CenteredViewLayerBinding>>? configuredBindings = null)
    {
        var sourceSchema = SourceSchema(schemaVersion);
        if (!SupportsSchema(sourceSchema))
            return null;

        IReadOnlyList<string> layerKinds;
        if (configuredBindings is not null)
        {
            layerKinds = configuredBindings.TryGetValue((node.Id, sourceSchema, variant), out var bound)
                ? bound.Select(static binding => binding.Style.Kind).ToArray()
                : [];
        }
        else if (MeasuredStellarAssociationsV1.IsSupportedSchemaVersion(sourceSchema))
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

    internal static bool IsScenePresentationBaseVersion(string? implementationVersion) =>
        implementationVersion is not null &&
        implementationVersion.StartsWith("projected-scene-presentation-", StringComparison.Ordinal);

    internal static bool IsCloudPresentationVersion(string? implementationVersion) =>
        implementationVersion is not null &&
        implementationVersion.StartsWith("cloud-presentation-", StringComparison.Ordinal);

    internal static IReadOnlyList<string>? InferPresentationLayerKinds(
        string? recipeName, string? implementationVersion, int? portIndex)
    {
        if (recipeName is not null &&
            !string.Equals(recipeName, PresentationProcessingProducts.LayerRecipeName, StringComparison.Ordinal))
            return null;
        if (string.Equals(implementationVersion, MeasuredAssociationPresentation.ExpectedNotDetectedVersion, StringComparison.Ordinal))
            return [ExpectedNotDetected.Style.Kind];
        if (string.Equals(implementationVersion, PresentationDeepSkyLayerProducer.ProducerVersion, StringComparison.Ordinal))
            return [DeepSky.Style.Kind];
        if (string.Equals(implementationVersion, PresentationLayerProducers.MetadataProducerVersion, StringComparison.Ordinal) ||
            string.Equals(implementationVersion, FrameFactsPresentation.ProducerVersion, StringComparison.Ordinal))
            return [Environment.Style.Kind];
        if (string.Equals(implementationVersion, OrbitalTrackPresentation.ProducerVersion, StringComparison.Ordinal))
            return [Satellites.Style.Kind];
        if (IsScenePresentationBaseVersion(implementationVersion) && portIndex is >= 0 and <= 3)
            return portIndex.Value switch
            {
                0 => [Annotation.Style.Kind],
                1 => [Cardinals.Style.Kind],
                2 => [ImageCircle.Style.Kind],
                3 => [Constellations.Style.Kind],
                _ => null
            };
        if (IsCloudPresentationVersion(implementationVersion) && portIndex is >= 0 and <= 1)
            return portIndex.Value switch
            {
                0 => [CloudMask.Style.Kind],
                1 => [CloudLabel.Style.Kind],
                _ => null
            };
        return null;
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
        CenteredProductSemanticProvenanceV1? semantics = null,
        ExposureProductAvailability? availability = null)
    {
        if (!MeasuredStellarAssociationsV1.IsSupportedSchemaVersion(SourceSchema(slot.SchemaVersion)))
            return false;
        if (semantics is not null)
        {
            if (semantics.LayerKinds is { Count: 0 })
                return false;
            var isExposureAssociation =
                string.Equals(semantics.RecipeName, ExposureAssociationProducts.RecipeName, StringComparison.Ordinal) ||
                string.Equals(semantics.StepAlias, ExposureAssociationProducts.StepAlias, StringComparison.Ordinal);
            if (!isExposureAssociation)
                return false;
            return authoritativeWriter.State switch
            {
                CenteredAuthoritativeAssociationResolutionState.Unresolved => true,
                CenteredAuthoritativeAssociationResolutionState.Proven =>
                    authoritativeWriter.Writer is { } resolvedWriter && resolvedWriter.Matches(slot),
                CenteredAuthoritativeAssociationResolutionState.UnidentifiableExpired =>
                    availability != ExposureProductAvailability.Available &&
                    authoritativeWriter.Writer is { } expiredWriter && expiredWriter.Matches(slot),
                _ => false
            };
        }
        return authoritativeWriter.State switch
        {
            CenteredAuthoritativeAssociationResolutionState.Proven =>
                authoritativeWriter.Writer is { } writer && writer.Matches(slot),
            CenteredAuthoritativeAssociationResolutionState.UnidentifiableExpired =>
                availability != ExposureProductAvailability.Available &&
                authoritativeWriter.Writer is { } expiredWriter && expiredWriter.Matches(slot),
            CenteredAuthoritativeAssociationResolutionState.None => false,
            _ => slot.Variant == ExposureAssociationProducts.Variant ||
                slot.Producer == ExposureAssociationProducts.StepAlias
        };
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
        CenteredProductSemanticProvenanceV1? semantics = null,
        ExposureProductAvailability? availability = null)
    {
        var sourceSchema = SourceSchema(slot.SchemaVersion);
        if (MeasuredStellarAssociationsV1.IsSupportedSchemaVersion(sourceSchema))
            return IsAuthoritativeAssociationSlot(slot, authoritativeWriter, semantics, availability)
                ? [MeasuredAssociations, ExpectedNotDetected]
                : [];

        if (PresentationLayerPayloadV1.SupportsSchema(sourceSchema))
        {
            if (semantics is not null)
            {
                if (semantics.LayerKinds is not null)
                {
                    var resolved = new List<CenteredViewLayerBinding>(semantics.LayerKinds.Count);
                    foreach (var kind in semantics.LayerKinds)
                    {
                        if (kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind &&
                            authoritativeWriter.SuppressesSceneExpectedNotDetected)
                            continue;
                        if (KnownByKind.TryGetValue(kind, out var binding))
                            resolved.Add(binding);
                    }
                    return resolved;
                }
                if (string.Equals(semantics.RecipeImplementationVersion, MeasuredAssociationPresentation.ExpectedNotDetectedVersion, StringComparison.Ordinal))
                    return authoritativeWriter.SuppressesSceneExpectedNotDetected ? [] : [ExpectedNotDetected];
                if (string.Equals(semantics.RecipeImplementationVersion, PresentationDeepSkyLayerProducer.ProducerVersion, StringComparison.Ordinal))
                    return [DeepSky];
            }
            return slot.Variant switch
            {
                "scene-annotation-layer-v1" => [Annotation],
                "scene-cardinal-layer-v1" => [Cardinals],
                "scene-image-circle-layer-v1" => [ImageCircle],
                "scene-constellation-layer-v1" => [Constellations],
                "scene-deep-sky-v1" => [DeepSky],
                "expected-not-detected-layer-v1" => authoritativeWriter.SuppressesSceneExpectedNotDetected ? [] : [ExpectedNotDetected],
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

    /// <summary>
    /// Reconstructs per-slot centered-view bindings when the frozen graph revision is unavailable, preserving the
    /// exact deterministic first-node-by-ordinal-id single-writer selection of <see cref="ReadConfigured(ProcessingGraphDefinition)"/>
    /// across multi-node, custom-variant, renamed-alias, missing-winning-row, and legacy expired-writer executions.
    /// </summary>
    internal static IReadOnlyDictionary<(string Producer, string Schema, string Variant), IReadOnlyList<CenteredViewLayerBinding>>
        BuildRecoveredBindings(
            IReadOnlyList<(ExposureProductLookup Lookup, CenteredProductSemanticProvenanceV1? Semantics)> entries,
            CenteredAuthoritativeAssociationResolution authoritativeWriter,
            IReadOnlyList<RecoveredNodeDeclaredOutput>? declaredOutputs = null)
    {
        var result = new Dictionary<(string Producer, string Schema, string Variant), IReadOnlyList<CenteredViewLayerBinding>>();
        var boundLayers = new HashSet<(CenteredViewLayerScope Scope, string Kind)>();
        var scenePortCounters = new Dictionary<string, int>(StringComparer.Ordinal);
        var cloudPortCounters = new Dictionary<string, int>(StringComparer.Ordinal);
        var presentKeys = new HashSet<(string Producer, string Schema, string Variant)>();
        var items = new List<(ExposureProductSlot Slot, long OrderKey, ExposureProductAvailability? Availability,
            CenteredProductSemanticProvenanceV1? Semantics)>(entries.Count + (declaredOutputs?.Count ?? 0));
        foreach (var (lookup, semantics) in entries)
        {
            var slot = lookup.Slot;
            var key = (slot.Producer, SourceSchema(slot.SchemaVersion), slot.Variant);
            presentKeys.Add(key);
            items.Add((slot, lookup.Revision ?? long.MaxValue, lookup.Availability, semantics));
        }
        if (declaredOutputs is { Count: > 0 })
        {
            // Include declared node output contracts whose product row is absent so a missing winning writer
            // still reserves layer ownership and never falls back to a lower-priority writer's row.
            foreach (var declared in declaredOutputs)
            {
                var key = (declared.Producer, declared.SchemaVersion, declared.Variant);
                if (!presentKeys.Add(key))
                    continue;
                var syntheticSlot = new ExposureProductSlot(Guid.Empty, declared.Producer, declared.SchemaVersion, declared.Variant);
                items.Add((syntheticSlot, declared.PortIndex, null, declared.Semantics));
            }
        }

        var ordered = items
            .OrderBy(static item => item.Slot.Producer, StringComparer.Ordinal)
            .ThenBy(static item => item.OrderKey)
            .ThenBy(static item => item.Slot.Variant, StringComparer.Ordinal);
        foreach (var (slot, _, availability, semantics) in ordered)
        {
            var key = (slot.Producer, SourceSchema(slot.SchemaVersion), slot.Variant);
            if (result.ContainsKey(key))
                continue;
            var effectiveSemantics = semantics;
            if (effectiveSemantics is { LayerKinds: null } &&
                PresentationLayerPayloadV1.SupportsSchema(key.Item2))
            {
                if (IsScenePresentationBaseVersion(effectiveSemantics.RecipeImplementationVersion))
                {
                    var rank = scenePortCounters.TryGetValue(slot.Producer, out var existing) ? existing : 0;
                    scenePortCounters[slot.Producer] = rank + 1;
                    if (InferPresentationLayerKinds(
                        effectiveSemantics.RecipeName, effectiveSemantics.RecipeImplementationVersion, rank) is { } inferred)
                        effectiveSemantics = effectiveSemantics with { LayerKinds = inferred };
                }
                else if (IsCloudPresentationVersion(effectiveSemantics.RecipeImplementationVersion))
                {
                    var rank = cloudPortCounters.TryGetValue(slot.Producer, out var existing) ? existing : 0;
                    cloudPortCounters[slot.Producer] = rank + 1;
                    if (InferPresentationLayerKinds(
                        effectiveSemantics.RecipeName, effectiveSemantics.RecipeImplementationVersion, rank) is { } inferred)
                        effectiveSemantics = effectiveSemantics with { LayerKinds = inferred };
                }
            }
            var candidates = ReadLegacySlot(slot, authoritativeWriter, effectiveSemantics, availability);
            if (candidates.Count == 0)
                continue;
            var claimed = candidates.Where(binding => boundLayers.Add((binding.Scope, binding.Style.Kind))).ToArray();
            if (claimed.Length > 0)
                result.Add(key, claimed);
        }
        return result;
    }

    internal static IReadOnlyDictionary<(string Producer, string Schema, string Variant), IReadOnlyList<CenteredViewLayerBinding>> ReadConfigured(
        ProcessingGraphDefinition graph) => ReadConfiguredNodes(graph.Nodes);

    internal static IReadOnlyDictionary<(string Producer, string Schema, string Variant), IReadOnlyList<CenteredViewLayerBinding>> ReadConfigured(
        CaptureProcessingGraph graph)
    {
        if (graph.SharedPlan is { } sharedPlan)
        {
            return ReadConfiguredNodes(sharedPlan.Nodes
                .Select(static planNode => planNode.Definition)
                .OrderBy(static node => node.Id, StringComparer.Ordinal));
        }

        var nodes = graph.Nodes.OrderBy(static node => node.Id, StringComparer.Ordinal).ToArray();
        var result = new Dictionary<(string, string, string), IReadOnlyList<CenteredViewLayerBinding>>();
        var hasAuthoritativeAssociations = nodes.Any(static node =>
            node.Alias == ExposureAssociationProducts.StepAlias);
        var boundLayers = new HashSet<(CenteredViewLayerScope Scope, string Kind)>();
        foreach (var node in nodes)
        {
            if (node.Step is not ICaptureProcessingGraphStep step)
                continue;
            var ports = PortBindings(node.Alias);
            var declared = step is IMultiOutputCaptureProcessingGraphStep multi
                ? multi.Outputs
                : [new CaptureProcessingOutputDescriptor(step.OutputRole, step.OutputVariant, step.RecipeName,
                    step.OutputSchemaVersion, step.SharedOutputRecipe)];
            for (var index = 0; index < declared.Count; index++)
            {
                var output = declared[index];
                if (output.SchemaVersion is not { } schema || !SupportsSchema(schema))
                    continue;
                IReadOnlyList<CenteredViewLayerBinding> candidates;
                if (node.Alias is "ScenePresentationLayer" or SceneStylePresentation.StepAlias &&
                    output.SharedRecipe?.ImplementationVersion == MeasuredAssociationPresentation.ExpectedNotDetectedVersion)
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

    private static Dictionary<(string Producer, string Schema, string Variant), IReadOnlyList<CenteredViewLayerBinding>> ReadConfiguredNodes(
        IEnumerable<ProcessingGraphNodeDefinition> nodes)
    {
        var enabledNodes = nodes.Where(static node => node.Enabled).ToArray();
        var result = new Dictionary<(string, string, string), IReadOnlyList<CenteredViewLayerBinding>>();
        // ExposureStellarAssociations is the single authoritative centered producer of expected-not-detected
        // when configured; prevent a second scene-presentation writer from racing or overwriting it.
        var hasAuthoritativeAssociations = enabledNodes.Any(static node =>
            node.StepAlias == ExposureAssociationProducts.StepAlias);
        var boundLayers = new HashSet<(CenteredViewLayerScope Scope, string Kind)>();
        foreach (var node in enabledNodes)
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
