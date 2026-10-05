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
}
