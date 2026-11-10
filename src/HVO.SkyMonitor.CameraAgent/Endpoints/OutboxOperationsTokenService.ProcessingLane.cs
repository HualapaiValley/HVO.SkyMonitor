using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal sealed partial class OutboxOperationsTokenService
{
    public string ProtectProcessingLaneAction(ProcessingLaneQuarantineTarget target)
        => Protect("processing-lane.acknowledge", target);

    public bool TryReadProcessingLaneAction(string token, out ProcessingLaneQuarantineTarget? target)
        => TryUnprotect("processing-lane.acknowledge", token, out target);

    public string ProtectProcessingLaneCursor(long workId)
        => Protect("processing-lane.cursor", new CursorPayload("processing-lane", null, workId, workId));

    public bool TryReadProcessingLaneCursor(string token, out long? workId)
    {
        workId = null;
        if (!TryUnprotect("processing-lane.cursor", token, out CursorPayload? payload) || payload?.Alias != "processing-lane")
        {
            return false;
        }
        workId = payload.RecordId;
        return true;
    }
}
