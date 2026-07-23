using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed class DataProtectionDeploymentLocationProtector : IDeploymentLocationProtector
{
    private readonly Lazy<IDataProtector> _protector;

    public DataProtectionDeploymentLocationProtector(IOptions<CameraAgentHostOptions> options)
    {
        _protector = new Lazy<IDataProtector>(() =>
        {
            var root = Path.GetFullPath(options.Value.RawIngressRoot);
            var keyPath = Path.Combine(root, ".location", "keys");
            EnsurePhysicalPath(root, keyPath);
            DeviceStateFilePermissions.RestrictDirectory(keyPath);
            EnsurePhysicalPath(root, keyPath);
            foreach (var keyFile in Directory.EnumerateFiles(keyPath, "*.xml", SearchOption.TopDirectoryOnly))
            {
                EnsurePhysicalPath(root, keyFile);
            }
            var provider = DataProtectionProvider.Create(
                new DirectoryInfo(keyPath),
                builder => builder.SetApplicationName("HVO.SkyMonitor.CameraAgent.DeploymentLocation"));
            return provider.CreateProtector("CameraAgent", "DeploymentLocation", "v1");
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal DataProtectionDeploymentLocationProtector(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = new Lazy<IDataProtector>(() => dataProtectionProvider.CreateProtector(
            "CameraAgent", "DeploymentLocation", "v1"));
    }

    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return _protector.Value.Protect(plaintext);
    }

    public byte[] Unprotect(byte[] protectedPayload)
    {
        ArgumentNullException.ThrowIfNull(protectedPayload);
        return _protector.Value.Unprotect(protectedPayload);
    }

    private static void EnsurePhysicalPath(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        if (new FileInfo(fullPath).LinkTarget is not null || new DirectoryInfo(fullPath).LinkTarget is not null)
        {
            throw new IOException("Deployment-location key state must not be a symbolic link.");
        }
        var current = new DirectoryInfo(Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath)!);
        while (current is not null)
        {
            if (current.LinkTarget is not null)
            {
                throw new IOException("Deployment-location key paths must not traverse symbolic links.");
            }
            if (string.Equals(
                    Path.TrimEndingDirectorySeparator(current.FullName),
                    root,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return;
            }
            current = current.Parent;
        }
        throw new IOException("Deployment-location key path is outside the CameraAgent data root.");
    }
}
