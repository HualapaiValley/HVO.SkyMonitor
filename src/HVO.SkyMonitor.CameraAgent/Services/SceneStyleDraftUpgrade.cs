using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>Explicit all-or-nothing local draft upgrade. Preview and activation use the ordinary authorized services.</summary>
internal static class SceneStyleDraftUpgrade
{
    internal static bool IsLegacy(string alias) => alias.Equals(ProcessingNodeOptionsDescriptors.ScenePresentationLayerStepAlias,
        StringComparison.OrdinalIgnoreCase);

    internal static CapturePipelineConfig Upgrade(CapturePipelineConfig source)
    {
        if (!source.Steps.Any(step => IsLegacy(step.Type)))
            throw new ArgumentException("This draft has no legacy scene producer.", nameof(source));
        var upgrade = new SceneStylePresentationUpgrade(PresentationLayerProducers.SceneProducerVersion);
        var steps = source.Steps.Select(step => IsLegacy(step.Type)
            ? step with
            {
                Type = SceneStylePresentation.StepAlias,
                Options = upgrade.UpgradeOptions(step.Options ?? JsonSerializer.SerializeToElement(new { }))
            }
            : step.Type.Equals("OverlayManifest", StringComparison.OrdinalIgnoreCase) ||
                step.Type.Equals("PresentationMaterializer", StringComparison.OrdinalIgnoreCase)
                ? step with { Options = SceneStylePresentation.OptInConsumer(step.Options) } : step).ToArray();
        return source with { Steps = steps };
    }
}
