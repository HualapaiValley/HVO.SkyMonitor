using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal sealed partial class OutboxOperationsTokenService
{
    public string ProtectRawEvidenceAction(RawEvidenceReleaseAction action, RawEvidenceTarget target)
        => Protect($"raw-evidence.action.{action}", target);

    public bool TryReadRawEvidenceAction(RawEvidenceReleaseAction action, string token, out RawEvidenceTarget? target)
        => TryUnprotect($"raw-evidence.action.{action}", token, out target);

    public string ProtectRawEvidenceCursor(long rowId)
        => Protect("raw-evidence.cursor", new CursorPayload("raw-evidence", null, rowId, rowId));

    public bool TryReadRawEvidenceCursor(string token, out long? rowId)
    {
        rowId = null;
        if (!TryUnprotect("raw-evidence.cursor", token, out CursorPayload? payload) || payload?.Alias != "raw-evidence")
            return false;
        rowId = payload.RecordId;
        return true;
    }
}
