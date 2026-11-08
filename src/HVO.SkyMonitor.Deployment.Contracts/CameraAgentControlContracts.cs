using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Deployment.Contracts;

/// <summary>The bounded installer-owned runtime controls. No executable, path or host-power verb is accepted.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CameraAgentControlKind>))]
public enum CameraAgentControlKind { Drain, Restart }

[JsonConverter(typeof(JsonStringEnumConverter<CameraAgentControlPhase>))]
public enum CameraAgentControlPhase { Queued, Draining, Restarting, Verifying, Succeeded, Failed, Cancelled, Skipped }

/// <summary>Immutable authenticated intent. AutomationRunKey binds delegated completion to one retained occurrence.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CameraAgentControlRequest(Guid OperationId, CameraAgentControlKind Kind, string Actor,
    string Reason, long ExpectedCaptureVersion, DateTimeOffset RequestedUtc, string? AutomationRunKey = null)
{
    public bool IsValid() => OperationId != Guid.Empty && Enum.IsDefined(Kind) &&
        IsText(Actor, 256) && IsText(Reason, 512) && ExpectedCaptureVersion >= 0 && RequestedUtc != default &&
        RequestedUtc.Offset == TimeSpan.Zero && (AutomationRunKey is null ||
            Kind == CameraAgentControlKind.Restart && IsText(AutomationRunKey, 512));

    private static bool IsText(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && value == value.Trim() && !value.Any(char.IsControl);
}

public sealed record CameraAgentControlEvent(CameraAgentControlPhase Phase, DateTimeOffset Utc, string Code);

/// <summary>The installer journal is authoritative; CameraAgent retains a read projection of these receipts.</summary>
public sealed record CameraAgentControlReceipt(CameraAgentControlRequest Request, CameraAgentControlPhase Phase,
    IReadOnlyList<CameraAgentControlEvent> Events, Guid? OriginalProcessId = null, Guid? VerifiedProcessId = null,
    long? PausedVersion = null, long? DrainedCaptureSequence = null, bool ResumeAfterVerification = false, DateTimeOffset? VerificationDeadlineUtc = null)
{
    public bool IsTerminal => Phase is CameraAgentControlPhase.Succeeded or CameraAgentControlPhase.Failed or
        CameraAgentControlPhase.Cancelled or CameraAgentControlPhase.Skipped;

    public bool IsValid()
    {
        if (Request is null || !Request.IsValid() || !Enum.IsDefined(Phase) || Events is not { Count: > 0 and <= 10 } ||
            Events[0].Phase != CameraAgentControlPhase.Queued || Events[^1].Phase != Phase ||
            Events.Any(item => item is null || !Enum.IsDefined(item.Phase) || item.Utc < Request.RequestedUtc ||
                item.Code is not { Length: > 0 and <= 96 } || !item.Code.All(character => char.IsAsciiLetterLower(character) || character is '.' or '-')))
            return false;
        for (var index = 1; index < Events.Count; index++)
        {
            var prior = Events[index - 1];
            var next = Events[index];
            if (next.Utc < prior.Utc || prior.Phase >= CameraAgentControlPhase.Succeeded ||
                next.Phase < CameraAgentControlPhase.Succeeded && (int)next.Phase != (int)prior.Phase + 1)
                return false;
        }
        return Phase != CameraAgentControlPhase.Succeeded || PausedVersion is >= 0 && DrainedCaptureSequence is >= 0 &&
            (Request.Kind == CameraAgentControlKind.Drain && Events.Any(item => item.Phase == CameraAgentControlPhase.Draining) ||
             Request.Kind == CameraAgentControlKind.Restart && OriginalProcessId is { } original && original != Guid.Empty &&
             VerifiedProcessId is { } verified && verified != Guid.Empty && original != verified &&
             Events.Any(item => item.Phase == CameraAgentControlPhase.Verifying));
    }
}

/// <summary>Only the holder of the installation credential may register a worker or publish a receipt.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CameraAgentControlExchange(Guid WorkerId, CameraAgentControlReceipt? Receipt = null);

public sealed record CameraAgentControlSnapshot(CameraAgentControlReceipt? Current, bool CancelRequested,
    Guid ProcessId, string CaptureState, long CaptureVersion, bool Initialized, bool Supervised,
    bool RestartWindowSafe);

public sealed record CameraAgentControlView(bool Available, IReadOnlyList<CameraAgentControlReceipt> Receipts);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CameraAgentControlRestart(Guid WorkerId, Guid OperationId, long ExpectedCaptureVersion);

/// <summary>Persisted before each effect, including the deadline that survives an installer-worker crash.</summary>
public sealed record CameraAgentControlJournal(int SchemaVersion, IReadOnlyList<CameraAgentControlReceipt> Receipts);
