using System.Text.Json;

namespace HVO.SkyMonitor.AgentCore;

/// <summary>
/// Computes the canonical content identity that raw capture descriptors, calibration acquisition plans and gain
/// responses use for a configured sensor. Durable raw captures already pin this convention (the sensor's default
/// serializer shape, canonicalized), so every producer that is compared with a capture's sensor profile uses it.
/// </summary>
public static class SensorProfileIdentity
{
    public static string ComputeSha256(SensorProfile sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        return CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(sensor));
    }
}
