using System.Security.Cryptography;
using System.Text;

namespace HVO.SkyMonitor.Deployment.Contracts;

/// <summary>The installed local store identity shared by CameraAgent and its installer.</summary>
public static class CameraAgentSiloBinding
{
    public const string StateContract = "cameraagent-silo-state-v1";
    public const string MarkerKey = "meta/instance.v1.json";
    public const string FenceKey = "meta/fence.json";
    public const string MarkerFormat = "hvo.cameraagent.silo.instance/1";
    public const string FenceFormat = "hvo.cameraagent.silo.fence/1";
    public const string LocalStorePrefix = "local-silo:";

    public static string BucketFor(Guid instanceId) => $"hvo-{instanceId:N}";

    public static string StoreIdFor(Guid instanceId) => $"{LocalStorePrefix}{instanceId:D}";

    public static string BindingIdFor(Guid instanceId, string storeId, string bucket) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"hvo.cameraagent.silo.binding/1\n{instanceId:D}\n{storeId}\n{bucket}")));

    public static bool TryReadInstanceId(string? storeId, out Guid instanceId)
    {
        instanceId = Guid.Empty;
        return storeId is not null && storeId.StartsWith(LocalStorePrefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(storeId.AsSpan(LocalStorePrefix.Length), "D", out instanceId) &&
            instanceId != Guid.Empty && storeId == StoreIdFor(instanceId);
    }
}
