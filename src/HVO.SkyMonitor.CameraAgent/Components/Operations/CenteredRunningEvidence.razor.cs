using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

public sealed partial class CenteredRunningEvidence : SiteTimeComponent
{
    [Parameter, EditorRequired] public CenteredPipelineRunningSnapshot Snapshot { get; set; } = default!;

    private IReadOnlyList<WindowSlot> _slots = [];
    private string? _windowState;
    private DateTimeOffset? _deadline;
    private DateTimeOffset? _cutoff;

    private bool LiveAcquisition => Snapshot.Acquisition?.Basis == CenteredRuntimeEvidenceBasis.ActiveAcquisition;

    private string AcquisitionStatus => (Snapshot.Paused, Snapshot.Acquisition?.Basis) switch
    {
        (true, CenteredRuntimeEvidenceBasis.ActiveAcquisition) => "Acquisition paused · buffer observed",
        (_, CenteredRuntimeEvidenceBasis.RetainedJournal) => Snapshot.Paused ? "Acquisition paused · retained journal" : "Retained journal",
        (_, CenteredRuntimeEvidenceBasis.ActiveAcquisition) => "Active acquisition",
        (true, _) => "Acquisition paused · telemetry unavailable",
        _ => "Unavailable"
    };

    protected override void OnParametersSet()
    {
        var display = Snapshot.Display?.View.Base.Definition;
        if (display?.Window is { } published)
        {
            _windowState = "Published window · frozen registration";
            _deadline = published.DeadlineUtc;
            _cutoff = published.EvidenceCutoffUtc;
            _slots = published.Slots.Select(slot => FromPublishedSlot(display, slot)).ToArray();
        }
        else if (Snapshot.Window is { } journal)
        {
            var subject = display is null ? "Acquisition head window" : "Display reference window";
            _windowState = $"{subject} · {WindowStatus(journal)}";
            _deadline = journal.DeadlineUtc;
            _cutoff = journal.EvidenceCutoffUtc;
            _slots = journal.Slots.Select(FromJournalSlot).ToArray();
        }
        else
        {
            _windowState = null;
            _deadline = null;
            _cutoff = null;
            _slots = [];
        }
    }

    private WindowSlot FromJournalSlot(CenteredWindowSlotSnapshot slot)
    {
        var capture = slot.Exposure?.CaptureId;
        var head = Snapshot.Head?.Raw;
        var reference = Snapshot.Display?.View.Base.Definition.Reference;
        var input = reference?.Capture.CaptureId == capture ? reference?.Input.Raw : head?.Capture.CaptureId == capture ? head : null;
        return new(slot.Offset, slot.ExpectedStartUtc, capture, slot.CaptureSequence ?? input?.Capture.CaptureSequence,
            slot.StartedUtc ?? input?.Timing.ExposureStartedUtc, slot.EndedUtc ?? input?.Timing.ExposureEndedUtc,
            slot.State is { } state ? CenteredPublicationEvidence.Label(state) : capture is not null ? "Admitted" : "Unsettled",
            slot.Reason, slot.RegistrationOutcome is { } outcome ? CenteredPublicationEvidence.Label(outcome) : "Unavailable");
    }

    private WindowSlot FromPublishedSlot(CenteredViewBaseDefinition display, CenteredViewWindowSlot slot)
    {
        var source = display.Contributors.FirstOrDefault(member => member.Exposure.Capture.CaptureId == slot.CaptureId);
        return new(slot.Offset, slot.ExpectedStartUtc, slot.CaptureId, source?.Exposure.Capture.CaptureSequence ??
            (Snapshot.Head?.Key.CaptureId == slot.CaptureId ? Snapshot.Head?.Raw.Capture.CaptureSequence : null), slot.StartedUtc,
            slot.EndedUtc, OperationsPage.SplitWords(slot.ClosureState), slot.ClosureReason,
            source?.RegistrationMethod ?? OperationsPage.SplitWords(slot.RegistrationOutcome));
    }

    private static string WindowStatus(CenteredWindowSnapshot window)
    {
        if (window.FailureReason is not null)
            return window.EvidenceCutoffUtc is null ? "Window refused" : "Registration failed";
        if (window.StackDisposition is { } disposition)
            return CenteredPublicationEvidence.Label(disposition);
        if (window.IsTerminal)
            return "Closed without a registered stack";
        return window.EvidenceCutoffUtc is null ? "Open · expected slot times" : "Closed · registration unsettled";
    }

    private TimeSpan? ReferenceAge(CenteredViewCurrent current)
    {
        var end = current.View.Base.Definition.Reference.ExposureEndedUtc;
        return Snapshot.Acquisition?.ObservedUtc is { } observed && end != default && observed >= end ? observed - end : null;
    }

    private string ObservedTime(DateTimeOffset? time) => time is { } instant && instant != default
        ? SiteTime.Format(instant) : "Unavailable";

    private static string GraphUrl(string revision) => $"/operations/pipeline/graphs/{Uri.EscapeDataString(revision)}";

    private static string CaptureLabel(Guid id, long? sequence) => sequence is not null ? $"Capture #{sequence}" : $"Capture {id:D}";

    private sealed record WindowSlot(int Offset, DateTimeOffset ExpectedUtc, Guid? CaptureId, long? Sequence,
        DateTimeOffset? StartedUtc, DateTimeOffset? EndedUtc, string Outcome, string? Reason, string Registration);
}
