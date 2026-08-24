namespace HVO.SkyMonitor.Deployment;

internal sealed class OperationLock : IDisposable
{
    private readonly FileStream stream;

    private OperationLock(FileStream stream)
    {
        this.stream = stream;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned operation lock owns the authenticated handle through its FileStream.")]
    public static OperationLock Acquire(
        string path,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var handle = NativeLinux.OpenLockFile(path);
        FileStream? stream = null;
        try
        {
            SafeFileSystem.ValidateOwnerFile(handle, path);
            stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: false);
            var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    stream.Lock(0, 1);
                    break;
                }
                catch (IOException) when (DateTimeOffset.UtcNow < deadline)
                {
                    cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(100));
                }
                catch (IOException exception)
                {
                    throw new InstallerException($"Operation lock '{path}' is busy.", exception);
                }
            }
            return new OperationLock(stream);
        }
        catch
        {
            if (stream is null)
            {
                handle.Dispose();
            }
            else
            {
                stream.Dispose();
            }
            throw;
        }
    }

    public void Dispose()
    {
        stream.Unlock(0, 1);
        stream.Dispose();
    }
}
