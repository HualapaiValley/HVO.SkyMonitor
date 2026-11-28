using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Extensions.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal sealed class SiloInstalledSettings
{
    private SiloInstalledSettings(IConfigurationSection section)
    {
        foreach (var setting in section.GetChildren())
        {
            if (setting.Key.ToUpperInvariant() is not ("STATECONTRACT" or "STOREID" or "BUCKET" or "STORAGEBINDINGID" or
                "WRITERGENERATION" or "ENDPOINT" or "ACCESSKEY" or "SECRETKEY" or "DATAROOT" or
                "MAXIMUMSTAGEDBYTES" or "MAXIMUMSTAGEDFILES" or "RECLAIMCAPTUREHOURBYTES"))
                throw new InvalidOperationException("CameraAgent:Silo contains an unsupported storage setting. Use the settings from a fresh Silo installation.");
        }

        string Required(string key) => section[key] is { Length: > 0 } value ? value :
            throw new InvalidOperationException($"The installed Silo layout requires CameraAgent:Silo:{key}.");

        if (Required("StateContract") != CameraAgentSiloBinding.StateContract)
            throw new InvalidOperationException("The configured Silo State contract is unsupported.");

        var storeId = Required("StoreId");
        if (!CameraAgentSiloBinding.TryReadInstanceId(storeId, out var instanceId))
            throw new InvalidOperationException("The configured Silo store identity is invalid.");

        var bucket = Required("Bucket");
        if (bucket != CameraAgentSiloBinding.BucketFor(instanceId))
            throw new InvalidOperationException("The configured Silo bucket belongs to a different instance.");

        Binding = new(instanceId.ToString("D"), storeId, bucket);
        if (Required("StorageBindingId") != Binding.BindingId)
            throw new InvalidOperationException("The configured Silo storage binding belongs to a different instance.");

        if (Required("WriterGeneration") != "1")
            throw new InvalidOperationException("This CameraAgent opens only the installer-issued first Silo writer generation.");
        if (!Uri.TryCreate(Required("Endpoint"), UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https") ||
            endpoint.AbsolutePath != "/" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidOperationException("The installed Silo endpoint must be an HTTP(S) service origin.");

        Endpoint = endpoint;
        AccessKey = Required("AccessKey");
        SecretKey = Required("SecretKey");
        if (AccessKey.Length is < 16 or > 128 || !AccessKey.All(char.IsAsciiLetterOrDigit) ||
            SecretKey.Length is < 16 or > 128 || !SecretKey.All(static c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '='))
            throw new InvalidOperationException("The installed Silo writer credential is invalid.");

        DataRoot = section["DataRoot"] is { Length: > 0 } path ? Path.GetFullPath(path) : null;
        MaximumStagedBytes = section.GetValue("MaximumStagedBytes", 2L * 1024 * 1024 * 1024);
        MaximumStagedFiles = section.GetValue("MaximumStagedFiles", DefaultMaximumStagedFiles);
        ReclaimCaptureHourBytes = section.GetValue("ReclaimCaptureHourBytes", MaximumStagedBytes / 2);
        if (ReclaimCaptureHourBytes <= 0 || MaximumStagedBytes <= 0 || MaximumStagedFiles is < 2 or int.MaxValue)
            throw new InvalidOperationException("The installed Silo spool requires a positive byte limit and room for an admission pair.");

    }

    public SiloStorageBinding Binding { get; }

    public Uri Endpoint { get; }

    public string AccessKey { get; }

    public string SecretKey { get; }

    public string? DataRoot { get; }

    public long ReclaimCaptureHourBytes { get; }

    public long MaximumStagedBytes { get; }

    public int MaximumStagedFiles { get; }

    internal const int DefaultMaximumStagedFiles = 4096;

    internal static SiloInstalledSettings ReadHostConfiguration(IConfiguration configuration)
    {
        var agent = configuration.GetSection("CameraAgent");
        string[] retired = ["Storage", "StorageRoot", "DerivedProductLifecycle", "RetentionSweepIntervalMinutes",
            "DiskPressureThresholdPercent", "DiskPressureRecoveryPercent", "DiskPressureRetentionDays"];
        foreach (var key in retired)
        {
            if (agent.GetChildren().Any(setting => setting.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"CameraAgent:{key} is a retired filesystem storage setting. Configure CameraAgent:Silo for a fresh installation.");
        }

        var section = agent.GetSection("Silo");
        if (!section.Exists())
            throw new InvalidOperationException("CameraAgent requires CameraAgent:Silo settings for a fresh installation.");

        return Read(section);
    }

    public static SiloInstalledSettings Read(IConfigurationSection configuration) => new(configuration);
}
