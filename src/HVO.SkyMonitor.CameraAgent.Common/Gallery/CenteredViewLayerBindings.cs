using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal sealed record CenteredViewLayerBinding(CenteredViewLayerScope Scope, CenteredViewLayerStyle Style)
{
    internal CenteredViewLayerKey Key(Guid captureId) => new(Style.Kind, Scope, captureId);
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
        MeasuredStellarAssociationsV1.CurrentSchemaVersion or
        RegionalStellarVisibilityProducts.SchemaVersion or
        MeasuredAstrometrySolutionV1.CurrentSchemaVersion || PresentationLayerPayloadV1.SupportsSchema(schema);

    internal static string SourceSchema(string schema) => schema.StartsWith(DispositionPrefix, StringComparison.Ordinal)
        ? schema[DispositionPrefix.Length..] : schema;

    internal static IReadOnlyList<CenteredViewLayerBinding> ReadLegacySlot(ExposureProductSlot slot) => SourceSchema(slot.SchemaVersion) switch
    {
        ExposureMeasurementProducts.SchemaVersion when slot.Variant == ExposureMeasurementProducts.Variant => [Markers],
        MeasuredStellarAssociationsV1.CurrentSchemaVersion => [MeasuredAssociations, ExpectedNotDetected],
        RegionalStellarVisibilityProducts.SchemaVersion => [RegionalVisibility],
        MeasuredAstrometrySolutionV1.CurrentSchemaVersion => [Residuals],
        var schema when PresentationLayerPayloadV1.SupportsSchema(schema) => slot.Variant switch
        {
            "scene-annotation-layer-v1" => [Annotation],
            "scene-cardinal-layer-v1" => [Cardinals],
            "scene-image-circle-layer-v1" => [ImageCircle],
            "scene-constellation-layer-v1" => [Constellations],
            "scene-deep-sky-v1" => [DeepSky],
            "expected-not-detected-layer-v1" => [ExpectedNotDetected],
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
        foreach (var node in graph.Nodes.Where(node => node.Enabled))
        {
            var ports = PortBindings(node.StepAlias);
            for (var index = 0; index < node.Outputs.Length; index++)
            {
                var output = node.Outputs[index];
                if (output.SchemaVersion is not { } schema || !SupportsSchema(schema))
                    continue;
                IReadOnlyList<CenteredViewLayerBinding> bindings;
                if (node.StepAlias is "ScenePresentationLayer" or SceneStylePresentation.StepAlias &&
                    output.Recipe?.ImplementationVersion == MeasuredAssociationPresentation.ExpectedNotDetectedVersion)
                {
                    bindings = [ExpectedNotDetected];
                }
                else if (index < ports.Count)
                {
                    bindings = ports[index];
                }
                else
                {
                    continue;
                }
                if (bindings.Count > 0)
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
