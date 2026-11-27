using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.OpticalCalibration;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class OpticalAlignmentPanel : IDisposable
{
    [Inject] internal ICameraAgentNamedRigUiService RigService { get; set; } = default!;
    [Parameter, EditorRequired] public string RigRevisionId { get; set; } = string.Empty;
    [Parameter] public long SelectionVersion { get; set; }
    [Parameter] public bool RigChangePending { get; set; }
    [Parameter] public OpticalAlignmentProvenance? ActiveAlignment { get; set; }
    [Parameter] public EventCallback<string> DraftAccepted { get; set; }

    private string _training = string.Empty;
    private string _validation = string.Empty;
    private bool _fitDistortion;
    private string? _message;
    private OpticalAlignmentProposal? _proposal;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "RunAsync owns disposal after the command returns; teardown only cancels it.")]
    private CancellationTokenSource? _command;
    private bool _disposed;

    private bool Busy => _command is not null;

    protected override void OnParametersSet() => ClearStaleProposal();

    private void ClearStaleProposal()
    {
        if (_proposal is { } proposal &&
            (proposal.Review.RigRevisionId != RigRevisionId || proposal.Review.SelectionVersion != SelectionVersion))
        {
            _proposal = null;
            _message = "The active rig changed. Fit the captures again to obtain a new review.";
        }
    }

    private Task FitAsync() => RunAsync(async token =>
    {
        _proposal = null;
        var training = ParseCaptureIds(_training, OpticalAlignmentWorkflow.MaximumTrainingCaptures);
        var validation = ParseCaptureIds(_validation, OpticalAlignmentWorkflow.MaximumValidationCaptures);
        if (training is null || validation is null || training.Length < OpticalAlignmentWorkflow.MinimumTrainingCaptures ||
            validation.Length == 0 || training.Concat(validation).Distinct().Count() != training.Length + validation.Length)
        {
            _message = "Enter valid, distinct archive capture IDs for both groups.";
            return;
        }
        _message = "Fitting selected captures…";
        var result = await RigService.FitOpticalAlignmentAsync(training, validation, _fitDistortion, token);
        token.ThrowIfCancellationRequested();
        _proposal = result.Value;
        _message = _proposal is not null ? null : result.Message ?? "The optical alignment fit is unavailable.";
        ClearStaleProposal();
    });

    private Task DecideAsync(bool accept) => RunAsync(async token =>
    {
        if (_proposal is not { } proposal || accept && (RigChangePending || !proposal.Review.CanAccept)) return;
        var result = await RigService.DecideOpticalAlignmentAsync(proposal, accept, token);
        if (result.Value is null)
        {
            _message = result.Message ?? "The review decision is unavailable.";
            return;
        }
        _proposal = null;
        _message = accept
            ? "Draft saved. Review and stage it using the named-rig controls. Activate by restarting, and roll back by staging the previous revision."
            : "Proposal rejected.";
        if (result.Value.CandidateRigRevisionId is { } revision && !_disposed)
            await DraftAccepted.InvokeAsync(revision);
    });

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (Busy || _disposed) return;
        using var command = new CancellationTokenSource();
        _command = command;
        try { await operation(command.Token); }
        catch (OperationCanceledException) when (command.IsCancellationRequested) { _message = "Optical alignment operation cancelled."; }
        finally { _command = null; }
    }

    private Task CancelAsync() => _command?.CancelAsync() ?? Task.CompletedTask;

    private static Guid[]? ParseCaptureIds(string value, int maximum)
    {
        var entries = value.Split([' ', '\t', '\r', '\n', ','], maximum + 1, StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length > maximum) return null;
        var ids = new Guid[entries.Length];
        for (var index = 0; index < entries.Length; index++)
        {
            if (!Guid.TryParse(entries[index], out ids[index]) || ids[index] == Guid.Empty) return null;
        }
        return ids;
    }

    private static string Number(double? value) => value?.ToString("0.######", CultureInfo.InvariantCulture) ?? "Unavailable";

    public void Dispose()
    {
        _disposed = true;
        _command?.Cancel();
    }
}
