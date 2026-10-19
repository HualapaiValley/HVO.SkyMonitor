using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

/// <summary>Retains CameraAgent's framework exception contract at the shared filesystem boundary.</summary>
internal static class FileSystemFaultCompatibility
{
    internal static bool IsContainmentFailure(IOException exception)
        => exception.InnerException is FileSystemFaultException { Kind: FileSystemFaultKind.Containment };

    [DoesNotReturn]
    internal static void Rethrow(FileSystemFaultException fault)
    {
        Exception original = fault;
        while (original is FileSystemFaultException { InnerException: { } inner })
        {
            original = inner;
        }
        if (original is not FileSystemFaultException &&
            original is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            ExceptionDispatchInfo.Capture(original).Throw();
        }
        throw new IOException(fault.Message, fault);
    }
}
