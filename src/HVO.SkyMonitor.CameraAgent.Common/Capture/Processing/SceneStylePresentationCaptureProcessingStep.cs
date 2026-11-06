using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class SceneStylePresentationCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata, SceneStylePresentationStepOptions options)
    : ScenePresentationLayerCaptureProcessingStep(metadata, options)
{
    protected override string ProducerVersion => SceneStylePresentation.ProducerVersion;
    public override string? OutputSchemaVersion => PresentationLayerPayloadV1.SceneStyleSchemaVersion;

    protected override PresentationSceneLayerPayloadsV2 Produce(ProjectedSceneV1 scene, PresentationAnnotationStyleV1 style,
        bool markers, bool labels, bool constellations, bool circle, bool cardinals,
        MeasuredStellarAssociationsV1? measured, bool expected) => SceneStylePresentation.Create(scene,
            new(style with
            {
                MarkerColor = new(options.MarkerRed, options.MarkerGreen, options.MarkerBlue),
                LabelColor = new(options.LabelRed, options.LabelGreen, options.LabelBlue),
                CardinalColor = new(options.CardinalRed, options.CardinalGreen, options.CardinalBlue),
                ImageCircleColor = new(options.ImageCircleRed, options.ImageCircleGreen, options.ImageCircleBlue)
            }, options.LabelFont, options.CardinalFont, options.PointGlyph),
            markers, labels, constellations, circle, cardinals, measured, expected);
}

internal sealed class SceneStylePresentationStepOptions : ScenePresentationLayerProcessingStepOptions
{
    public byte MarkerRed { get; init; } = 144;
    public byte MarkerGreen { get; init; } = 144;
    public byte MarkerBlue { get; init; } = 144;
    public byte LabelRed { get; init; } = 255;
    public byte LabelGreen { get; init; } = 255;
    public byte LabelBlue { get; init; } = 255;
    public byte CardinalRed { get; init; } = 255;
    public byte CardinalGreen { get; init; } = 255;
    public byte CardinalBlue { get; init; } = 255;
    public byte ImageCircleRed { get; init; } = 96;
    public byte ImageCircleGreen { get; init; } = 96;
    public byte ImageCircleBlue { get; init; } = 96;
    public string LabelFont { get; init; } = "MonoBold";
    public string CardinalFont { get; init; } = "MonoBold";
    public string PointGlyph { get; init; } = "Circle";
}
