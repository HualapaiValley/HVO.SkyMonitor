using HVO.SkyMonitor.Deployment.Wizard;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// How the installer names itself in usage and recovery commands: an executable durable copy, once installed, or the
/// running release file before one exists. This keeps bootstrap hints valid after its temporary download is removed.
/// </summary>
internal static class InstallerProgram
{
    /// <summary>The product's name for its installer, which every release's installer file name starts with.</summary>
    public const string ProductName = "hvo-sky-install";

    /// <summary>This process's installer, as the commands it prints name it.</summary>
    public static string Current => DurableOf(Environment.ProcessPath,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", ProductName),
        PathPrograms.SystemInstaller);

    internal static string DurableOf(string? processPath, params string[] candidates)
        => candidates.FirstOrDefault(path => File.Exists(path) && new FileInfo(path).LinkTarget is null &&
               (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0) is { } durable
            ? InstallWizardRunner.Quote([durable]) : Of(processPath);

    /// <summary>The installer running from <paramref name="processPath"/>, as the shell takes it.</summary>
    internal static string Of(string? processPath)
        => processPath is { Length: > 0 } && Path.GetFileName(processPath).StartsWith(ProductName, StringComparison.Ordinal)
            ? InstallWizardRunner.Quote([processPath])
            : ProductName;
}
