using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

public sealed partial class HorizonObstructionEditor : ComponentBase
{
    [Inject] internal ICameraAgentNamedRigUiService RigService { get; set; } = default!;
    [Parameter] public NamedRigRevision? ActiveRevision { get; set; }
    [Parameter] public long SelectionVersion { get; set; }
    [Parameter] public EventCallback<string> Saved { get; set; }
    private string? _basis;
    private string _installation = string.Empty;
    private string _samples = string.Empty;
    private double _gap = 30;
    private HorizonObstructionPreview? _preview;
    private bool _busy;
    private string? _message;

    protected override void OnParametersSet()
    {
        var basis = $"{ActiveRevision?.RevisionId}:{SelectionVersion}";
        if (_basis == basis) return;
        _basis = basis;
        var profile = ActiveRevision?.Rig.HorizonObstruction;
        _installation = profile?.InstallationId ?? string.Empty;
        _gap = profile?.MaximumInterpolationGapDegrees ?? 30;
        _samples = string.Join('\n', (profile?.Samples ?? []).Select(sample =>
            $"{Number(sample.AzimuthDegrees)},{(sample.ElevationDegrees is { } elevation ? Number(elevation) : "?")}"));
        Invalidate();
    }

    private void Invalidate() { _preview = null; _message = null; }
    private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);

    private async Task PreviewAsync()
    {
        if (_busy || ActiveRevision is null) return;
        _busy = true;
        _preview = null;
        try
        {
            var lines = _samples.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length > HorizonObstructionProfile.MaximumSamples || _samples.Length > 32768)
                throw new FormatException();
            var samples = lines.Select(line =>
            {
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length != 2) throw new FormatException();
                return new HorizonObstructionSample(double.Parse(parts[0], CultureInfo.InvariantCulture),
                    parts[1] == "?" ? null : double.Parse(parts[1], CultureInfo.InvariantCulture));
            }).ToArray();
            var result = await RigService.PreviewHorizonAsync(ActiveRevision.RevisionId, SelectionVersion,
                _installation, _gap, samples, CancellationToken.None);
            _preview = result.IsSuccess ? result.Value : null;
            _message = result.IsSuccess ? "Preview ready. Review before saving." : result.Message;
        }
        catch (FormatException) { _message = "Enter sorted azimuth,elevation pairs, using ? for unknown elevation."; }
        finally { _busy = false; }
    }

    private async Task SaveAsync()
    {
        if (_busy || _preview is null) return;
        _busy = true;
        try
        {
            var result = await RigService.SaveHorizonAsync(_preview, CancellationToken.None);
            if (result.IsSuccess)
            {
                _preview = null;
                await Saved.InvokeAsync(result.Value!.RevisionId);
                _message = "Candidate saved. Review and explicitly stage it in Rig preview to activate after restart.";
            }
            else _message = result.Message;
        }
        finally { _busy = false; }
    }

}
