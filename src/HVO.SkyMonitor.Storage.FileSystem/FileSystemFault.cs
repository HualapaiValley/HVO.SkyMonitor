namespace HVO.SkyMonitor.Storage.FileSystem;

/// <summary>
/// The low-level fact about why a filesystem operation could not complete. This is deliberately
/// not a policy: it says what the filesystem reported, and the host that owns the storage
/// contract decides whether that is retryable, terminal, degraded, or fatal for its workflow.
/// </summary>
public enum FileSystemFaultKind
{
    /// <summary>A path resolved outside the supplied physical root, or through a symbolic link or reparse point.</summary>
    Containment,

    /// <summary>The target already exists and the operation required that it not (<c>EEXIST</c>).</summary>
    AlreadyExists,

    /// <summary>The target does not exist (<c>ENOENT</c>).</summary>
    NotFound,

    /// <summary>The caller lacks permission (<c>EACCES</c>, <c>EPERM</c>).</summary>
    PermissionDenied,

    /// <summary>The medium is full or over quota (<c>ENOSPC</c>, <c>EDQUOT</c>).</summary>
    NoSpace,

    /// <summary>Source and destination are on different filesystems, so an atomic rename is impossible (<c>EXDEV</c>).</summary>
    CrossDevice,

    /// <summary>The operation was cancelled by the caller before the durability boundary.</summary>
    Cancelled,

    /// <summary>An I/O error the filesystem reported that none of the above names (<c>EIO</c> and others).</summary>
    Io
}

/// <summary>
/// Raised by every primitive in this project. Carries the fact and the operation; never a
/// host's interpretation of it, and never the content of any file.
/// </summary>
public sealed class FileSystemFaultException : IOException
{
    // The three conventional constructors exist for the exception contract; a fault raised
    // without a kind is an Io fact for an unknown operation, which is the honest default.
    public FileSystemFaultException()
        : this(FileSystemFaultKind.Io, "unknown", string.Empty)
    {
    }

    public FileSystemFaultException(string message)
        : base(message)
    {
        Kind = FileSystemFaultKind.Io;
        Operation = "unknown";
        Path = string.Empty;
    }

    public FileSystemFaultException(string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = FileSystemFaultKind.Io;
        Operation = "unknown";
        Path = string.Empty;
    }

    public FileSystemFaultException(FileSystemFaultKind kind, string operation, string path, Exception? innerException = null)
        : base($"Filesystem operation '{operation}' failed with '{kind}'.", innerException)
    {
        Kind = kind;
        Operation = operation;
        Path = path;
    }

    public FileSystemFaultKind Kind { get; }

    public string Operation { get; }

    /// <summary>The path the operation was attempting; callers decide whether it may be logged.</summary>
    public string Path { get; }

    /// <summary>Translate a framework exception into the fact it represents.</summary>
    public static FileSystemFaultException From(string operation, string path, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var kind = exception switch
        {
            FileSystemFaultException fault => fault.Kind,
            OperationCanceledException => FileSystemFaultKind.Cancelled,
            UnauthorizedAccessException => FileSystemFaultKind.PermissionDenied,
            FileNotFoundException or DirectoryNotFoundException => FileSystemFaultKind.NotFound,
            IOException io => ClassifyIo(io),
            _ => FileSystemFaultKind.Io
        };
        return new FileSystemFaultException(kind, operation, path, exception);
    }

    private static FileSystemFaultKind ClassifyIo(IOException exception)
    {
        // HResult carries errno on Unix for IOExceptions the runtime raises from a syscall.
        var errno = exception.HResult & 0xFFFF;
        return errno switch
        {
            17 => FileSystemFaultKind.AlreadyExists,   // EEXIST
            2 => FileSystemFaultKind.NotFound,         // ENOENT
            13 or 1 => FileSystemFaultKind.PermissionDenied, // EACCES, EPERM
            28 or 122 => FileSystemFaultKind.NoSpace,  // ENOSPC, EDQUOT
            18 => FileSystemFaultKind.CrossDevice,     // EXDEV
            _ => FileSystemFaultKind.Io
        };
    }
}
