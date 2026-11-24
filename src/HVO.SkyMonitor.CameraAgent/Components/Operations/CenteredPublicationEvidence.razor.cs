using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

public sealed partial class CenteredPublicationEvidence : SiteTimeComponent
{
    [Parameter, EditorRequired] public CenteredView View { get; set; } = default!;
    [Parameter] public bool ShowArchiveLink { get; set; } = true;
    [Parameter] public bool Live { get; set; }
    [Parameter] public IReadOnlySet<CenteredViewLayerKey>? PendingLayers { get; set; }

    private IReadOnlyList<LayerGroup> _layers = [];

    protected override void OnParametersSet() => _layers = Layers(View, Live, PendingLayers);

    internal static string ArchiveUrl(CenteredView view) => CenteredViewLinks.Revision(
        view.Base.Definition.Reference.Capture.CaptureId, view.Base.BaseId, view.Revision.RevisionId);

    internal static string CaptureUrl(Guid id) => $"/gallery/{id:D}";

    internal static string RunUrl(Guid id) => $"/operations/pipeline/executions/{id:D}";

    internal static string Label<T>(T value) where T : struct, Enum => OperationsPage.SplitWords(value.ToString());

    private static string LayerLabel(string kind) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(kind.Replace('-', ' '));

    internal static string? Reason(string? value)
    {
        // Only known producer codes bypass free-text redaction. Any detail after the code stays private.
        var code = value?.Split('/', 2)[0].Trim();
        return code switch
        {
            "centered.awaiting-product" => "Awaiting source product at publication",
            "centered.layer-not-configured" => "Layer absent from the selected configuration",
            "centered.registration-map-unavailable" => "Source registration map unavailable",
            "centered.segments-outside-registration" => "Segments fall outside the registered image",
            "centered.rich-text-preview-too-small" => "Preview is too small for this layer",
            "centered.product-expired" => "Source product expired",
            "centered.product-missing-evidence" => "Source product evidence missing",
            "centered.product-failed" => "Source production failed",
            "centered.product-unavailable" => "Source product unavailable",
            "centered.product-incompatible" => "Source product is incompatible",
            "centered.trail-geometry-unavailable" => "Measured trail geometry unavailable",
            "runtime.live-buffer-unobserved" => "Live buffer was not observed",
            "runtime.no-centered-run" => "No centered acquisition run recorded",
            "runtime.journal-not-ready" => "Acquisition journal is initializing",
            "calendar-predecessor-unavailable" or "exposure-start-outside-tolerance" or "processing-deadline-expired" or
            "invalid-window-checkpoint" or "window-capacity-exhausted" or "registration-deadline-expired" or
            "registration.invalid-retained-evidence" or "registration.legacy-window-pins-unavailable" or
            "registration.recipe-identity-mismatch" or "registration.reference-input-unavailable" or
            "schedule-admission-interrupted" or "schedule.start-window-closed" or "window.input-custody-unavailable" or
            "window.registration-pixel-limit" or "linear.calibration-master-evidence-missing" or "linear.exact-product-unavailable" or
            "linear.explicit-no-correction" or "linear.no-configured-step" or "linear.original-provenance-mismatch" or
            "linear.unqualified-output" or "linear.unsupported-or-unverified-layout" or "product.configured-output-not-retained" or
            "product.retained-output-unavailable" or "product.settlement-identity-missing" => code,
            _ => CameraAgentReplayUiService.Sanitize(value)
        };
    }

    internal static string FormatDuration(TimeSpan? value) => value is { } duration
        ? string.Create(CultureInfo.InvariantCulture, $"{duration.TotalSeconds:0.###} s") : "Unavailable";

    internal static string FormatLayout(FrameLayoutDescriptor layout)
    {
        var encoding = layout.PixelFormat switch
        {
            CameraPixelFormat.Mono8 or CameraPixelFormat.Mono16 => "mono",
            CameraPixelFormat.BayerRggb16 or CameraPixelFormat.Rgb24 => "color",
            _ => "unavailable"
        };
        return string.Create(CultureInfo.InvariantCulture,
            $"{layout.PixelFormat} ({encoding}) · {layout.Width} × {layout.Height} px · {layout.SampleDepthBits}/{layout.ContainerDepthBits}-bit · {layout.ByteOrder}");
    }

    internal static string FormatReadout(FrameLayoutDescriptor layout) => layout.Readout is { } readout
        ? string.Create(CultureInfo.InvariantCulture,
            $"Native {readout.NativeWidth} × {readout.NativeHeight} px · ROI ({readout.RoiX}, {readout.RoiY}), {readout.RoiWidth} × {readout.RoiHeight} px · bin {readout.BinX} × {readout.BinY} · {FormatCfa(layout, readout)}")
        : "Native readout unavailable";

    private static string FormatCfa(FrameLayoutDescriptor layout, FrameReadoutDescriptor readout) => layout.CfaPattern == ColorFilterArrayPattern.None
        ? "No CFA" : $"CFA {layout.CfaPattern} · origin ({Number(readout.CfaOriginX)}, {Number(readout.CfaOriginY)})";

    internal static string FormatGeometry(ExposureGeometryV1 geometry) => string.Create(CultureInfo.InvariantCulture,
        $"Boresight {geometry.Orientation.BoresightAzimuthDegrees:0.###}° az / {geometry.Orientation.BoresightAltitudeDegrees:0.###}° alt · roll {geometry.Orientation.RollAdjustmentDegrees:0.###}° · pixel scale {FormatScale(geometry.PixelScaleArcseconds)}");

    private static string FormatScale(double? scale) => scale is { } measured
        ? string.Create(CultureInfo.InvariantCulture, $"{measured:0.###} arcsec/px") : "unavailable";

    internal static string Number<T>(T? value) where T : struct, IFormattable =>
        value?.ToString(null, CultureInfo.InvariantCulture) ?? "Unavailable";

    internal sealed record LayerSite(CenteredViewExposure? Source, CenteredViewLayer? Evidence, bool Pending, bool Live)
    {
        public CenteredViewReadiness? Readiness => Pending ? CenteredViewReadiness.Waiting :
            Live && Evidence?.Readiness == CenteredViewReadiness.Waiting ? null : Evidence?.Readiness;

        public string? Description => Pending ? "Exact source producer is queued, running, or awaiting retry" :
            Live && Evidence?.Readiness == CenteredViewReadiness.Waiting ? "Waiting at publication. Current producer state unavailable." :
            Reason(Evidence?.Reason);
    }

    internal sealed record LayerGroup(string Kind, CenteredViewLayerScope Scope, IReadOnlyList<LayerSite> Sites)
    {
        private int Assessed => Sites.Count(static site => site.Readiness is
            CenteredViewReadiness.Ready or CenteredViewReadiness.Partial or CenteredViewReadiness.AssessedEmpty);
        private int Unknown => Sites.Count(static site => site.Readiness is null);

        public string? Assessment => Sites.All(static site => site.Readiness == CenteredViewReadiness.NotConfigured) ? null :
            Unknown == 0 ? $"{Assessed}/{Sites.Count} assessed" : Assessed > 0
                ? $"At least {Assessed}/{Sites.Count} assessed · {Unknown} unavailable"
                : $"Assessment unavailable · {Unknown}/{Sites.Count} source states unavailable";

        public string Readiness
        {
            get
            {
                var states = Sites.Select(static site => site.Readiness).Distinct().ToArray();
                return states.Length == 1 ? states[0] is { } state ? Label(state) : "Unavailable" : "Mixed outcomes";
            }
        }
    }

    internal static IReadOnlyList<LayerGroup> Layers(CenteredView view, bool live = false, IReadOnlySet<CenteredViewLayerKey>? pending = null)
    {
        var definition = view.Base.Definition;
        var evidence = view.Revision.Layers.ToDictionary(static layer => layer.Key);
        return evidence.Keys.Concat(pending ?? Enumerable.Empty<CenteredViewLayerKey>())
            .Select(static key => (key.Kind, key.Scope)).Distinct()
            .OrderBy(static key => key.Kind, StringComparer.Ordinal).ThenBy(static key => key.Scope)
            .Select(group =>
            {
                IEnumerable<CenteredViewExposure?> sources = group.Scope switch
                {
                    CenteredViewLayerScope.Reference => [definition.Reference],
                    CenteredViewLayerScope.Contributor => definition.Contributors.Select(static member => (CenteredViewExposure?)member.Exposure),
                    _ => [null]
                };
                return new LayerGroup(group.Kind, group.Scope, sources.Select(source =>
                {
                    var key = new CenteredViewLayerKey(group.Kind, group.Scope, source?.Capture.CaptureId);
                    return new LayerSite(source, evidence.GetValueOrDefault(key), live && pending?.Contains(key) == true, live);
                }).ToArray());
            }).ToArray();
    }
}
