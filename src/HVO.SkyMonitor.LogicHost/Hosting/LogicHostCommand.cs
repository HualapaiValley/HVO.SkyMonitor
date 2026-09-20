namespace HVO.SkyMonitor.LogicHost.Hosting;

internal enum LogicHostHostMode
{
    Runtime,
    DatabaseInitialize,
    /// <summary>Offline: write a backup of the filesystem object store to a directory and exit.</summary>
    ObjectStoreBackup,
    /// <summary>Offline and destructive: replace the filesystem object store's buckets from a backup and exit.</summary>
    ObjectStoreRestore,
    /// <summary>Offline: re-hash the object store against a backup inventory and exit non-zero on any mismatch.</summary>
    ObjectStoreVerify
}

internal sealed record LogicHostCommand(
    LogicHostHostMode Mode,
    IReadOnlyList<string> ForwardedArguments,
    string? Path = null);

internal static class LogicHostCommandParser
{
    private const string Prefix = "--host-mode";
    private const string DatabaseInitializeArgument = "--host-mode=database-initialize";
    private const string ObjectStoreBackupArgument = "--host-mode=object-store-backup";
    private const string ObjectStoreRestoreArgument = "--host-mode=object-store-restore";
    private const string ObjectStoreVerifyArgument = "--host-mode=object-store-verify";
    private const string PathPrefix = "--path=";

    internal static LogicHostCommand Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var mode = LogicHostHostMode.Runtime;
        var modeSpecified = false;
        string? path = null;
        var forwarded = new List<string>(arguments.Count);
        foreach (var argument in arguments)
        {
            var selected = argument switch
            {
                DatabaseInitializeArgument => LogicHostHostMode.DatabaseInitialize,
                ObjectStoreBackupArgument => LogicHostHostMode.ObjectStoreBackup,
                ObjectStoreRestoreArgument => LogicHostHostMode.ObjectStoreRestore,
                ObjectStoreVerifyArgument => LogicHostHostMode.ObjectStoreVerify,
                _ => (LogicHostHostMode?)null
            };
            if (selected is { } chosen)
            {
                if (modeSpecified)
                {
                    throw new ArgumentException("The LogicHost host mode can be specified only once.", nameof(arguments));
                }
                mode = chosen;
                modeSpecified = true;
                continue;
            }
            if (argument.StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new ArgumentException("The LogicHost host mode is invalid.", nameof(arguments));
            }
            if (argument.StartsWith(PathPrefix, StringComparison.Ordinal))
            {
                if (path is not null)
                {
                    throw new ArgumentException("The --path argument can be specified only once.", nameof(arguments));
                }
                path = argument[PathPrefix.Length..];
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new ArgumentException("The --path argument requires a value.", nameof(arguments));
                }
                continue;
            }
            forwarded.Add(argument);
        }
        var needsPath = mode is LogicHostHostMode.ObjectStoreBackup or LogicHostHostMode.ObjectStoreRestore or LogicHostHostMode.ObjectStoreVerify;
        if (needsPath && path is null)
        {
            throw new ArgumentException("The object-store backup, restore and verify modes require --path=<backup directory>.", nameof(arguments));
        }
        if (!needsPath && path is not null)
        {
            throw new ArgumentException("The --path argument applies only to the object-store backup, restore and verify modes.", nameof(arguments));
        }
        return new(mode, forwarded, path);
    }
}
