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

    internal static bool SupportsSchema(string? schema) => schema is ExposureMeasurementProducts.SchemaVersion or
        RegionalStellarVisibilityProducts.SchemaVersion or
        MeasuredAstrometrySolutionV1.CurrentSchemaVersion ||
        MeasuredStellarAssociationsV1.IsSupportedSchemaVersion(schema) ||
        PresentationLayerPayloadV1.SupportsSchema(schema);

    internal static string SourceSchema(string schema) => schema.StartsWith(DispositionPrefix, StringComparison.Ordinal)
        ? schema[DispositionPrefix.Length..] : schema;

    private const string DefaultMeasuredAssociationVariant = "measured-stellar-associations-v1";

    internal static bool IsAuthoritativeAssociationSlot(
        ExposureProductSlot slot, CenteredAuthoritativeAssociationWriter? authoritativeWriter = null)
    {
        if (!MeasuredStellarAssociationsV1.IsSupportedSchemaVersion(SourceSchema(slot.SchemaVersion)))
            return false;
        if (authoritativeWriter is { } writer)
            return writer.Matches(slot);
        return slot.Variant is ExposureAssociationProducts.Variant or DefaultMeasuredAssociationVariant ||
            slot.Producer == ExposureAssociationProducts.StepAlias;
    }

    internal static bool IsSceneExpectedNotDetectedSlot(ExposureProductSlot slot) =>
        PresentationLayerPayloadV1.SupportsSchema(SourceSchema(slot.SchemaVersion)) &&
        slot.Variant == "expected-not-detected-layer-v1";

    internal static IReadOnlyList<CenteredViewLayerBinding> ReadLegacySlot(
        ExposureProductSlot slot, CenteredAuthoritativeAssociationWriter? authoritativeWriter = null) => SourceSchema(slot.SchemaVersion) switch
        {
            ExposureMeasurementProducts.SchemaVersion when slot.Variant == ExposureMeasurementProducts.Variant => [Markers],
            _ when IsAuthoritativeAssociationSlot(slot, authoritativeWriter) => [MeasuredAssociations, ExpectedNotDetected],
            RegionalStellarVisibilityProducts.SchemaVersion => [RegionalVisibility],
            MeasuredAstrometrySolutionV1.CurrentSchemaVersion => [Residuals],
            var schema when PresentationLayerPayloadV1.SupportsSchema(schema) => slot.Variant switch
            {
                "scene-annotation-layer-v1" => [Annotation],
                "scene-cardinal-layer-v1" => [Cardinals],
                "scene-image-circle-layer-v1" => [ImageCircle],
                "scene-constellation-layer-v1" => [Constellations],
                "scene-deep-sky-v1" => [DeepSky],
                "expected-not-detected-layer-v1" => authoritativeWriter is not null ? [] : [ExpectedNotDetected],
                "cloud-mask-layer-v1" => [CloudMask],
                "cloud-label-layer-v1" => [CloudLabel],
                "environment-layer-v1" => [Environment],
                "capture-satellites-v1-layer" => [Satellites],
                _ => []
            },
            _ => []
        };

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
