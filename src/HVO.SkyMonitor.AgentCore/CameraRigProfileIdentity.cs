namespace HVO.SkyMonitor.AgentCore;

/// <summary>Computes the canonical content identity used by capture-time rig references.</summary>
public static class CameraRigProfileIdentity
{
    public static string ComputeSha256(CameraRigConfig rig)
    {
        ArgumentNullException.ThrowIfNull(rig);
        return CaptureContractJson.ComputeCanonicalJsonSha256(rig);
    }
}
