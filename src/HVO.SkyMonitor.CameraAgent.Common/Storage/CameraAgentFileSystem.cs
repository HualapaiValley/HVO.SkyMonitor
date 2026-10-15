using HVO.SkyMonitor.Storage.FileSystem;
using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

/// <summary>Adapts shared mechanisms to CameraAgent's existing exception contract.</summary>
internal static class CameraAgentFileSystem
{
    internal static void FlushFile(FileStream stream)
    {
        try
        {
            DurableSync.File(stream);
        }
        catch (FileSystemFaultException exception)
        {
            FileSystemFaultCompatibility.Rethrow(exception);
        }
    }

    internal static void FlushFile(SafeFileHandle handle, string pathForDiagnostics)
    {
        try
        {
            DurableSync.File(handle, pathForDiagnostics);
        }
        catch (FileSystemFaultException exception)
        {
            FileSystemFaultCompatibility.Rethrow(exception);
        }
    }

    internal static void SyncDirectory(SafeFileHandle handle, string pathForDiagnostics)
    {
        try
        {
            DurableSync.Directory(handle, pathForDiagnostics);
        }
        catch (FileSystemFaultException exception)
        {
            FileSystemFaultCompatibility.Rethrow(exception);
        }
    }

    internal static void PublishPrepared(string root, string temporaryPath, string finalPath, bool overwrite)
    {
        try
        {
            AtomicPublisher.PublishPrepared(
                PhysicalRoot.Open(Path.GetFullPath(root)), temporaryPath, finalPath,
                overwrite ? PublishMode.Replace : PublishMode.CreateNew);
        }
        catch (FileSystemFaultException exception)
        {
            FileSystemFaultCompatibility.Rethrow(exception);
        }
    }
    /// <summary>
    /// Establish a configured storage root and its parent entries before publication. Existence
    /// cannot distinguish a durable root from directories left by a failed earlier attempt.
    /// Outside-root ancestor links retain PhysicalRoot's trusted-ancestor contract.
    /// </summary>
    internal static void EnsureStorageRootDurable(string root, Action? directorySyncRecorder = null)
    {
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        try
        {
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
        catch (FileSystemFaultException exception)
        {
            FileSystemFaultCompatibility.Rethrow(exception);
        }
    }
}
