using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.LocalFiles;
using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

internal static class CameraAgentFileSystem
{
    internal static void FlushFile(FileStream stream) => DurableSync.File(stream);

    internal static void FlushFile(SafeFileHandle handle, string pathForDiagnostics)
        => DurableSync.File(handle, pathForDiagnostics);

    internal static void SyncDirectory(SafeFileHandle handle, string pathForDiagnostics)
        => DurableSync.Directory(handle, pathForDiagnostics);

    internal static void PublishPrepared(string root, string temporaryPath, string finalPath, bool overwrite)
    {
        using var mutation = RetainedIntegrityLedger.BeginPublication(temporaryPath, finalPath);
        AtomicPublisher.PublishPrepared(PhysicalRoot.Open(Path.GetFullPath(root)), temporaryPath, finalPath,
            overwrite ? PublishMode.Replace : PublishMode.CreateNew);
    }

    /// <summary>Make the state root and the directory entries leading to it durable even after a failed prior attempt.</summary>
    internal static void EnsureStorageRootDurable(string root, Action? directorySyncRecorder = null)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        var physicalRoot = PhysicalRoot.Open(root);
        if (!DurableSync.SupportsDirectorySync) return;

        DirectoryInfo? current = new(physicalRoot.Path);
        var atRoot = true;
        while (current is not null)
        {
            if (!atRoot && current.LinkTarget is not null)
            {
                current = current.ResolveLinkTarget(returnFinalTarget: true) as DirectoryInfo
                    ?? throw new IOException("Storage root ancestor does not resolve to a directory.");
            }
            DurableSync.Directory(current.FullName);
            directorySyncRecorder?.Invoke();
            current = current.Parent;
            atRoot = false;
        }
    }
}
