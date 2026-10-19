using HVO.SkyMonitor.Deployment.Wizard;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// How the installer names itself in its usage and in the commands its messages tell the operator to run (#1384): by the
/// path of the program that is running, as the shell takes it. A release's installer is
/// <c>hvo-sky-install-linux-&lt;arch&gt;</c> in the release folder and nothing puts it on PATH, so the bare name would be
/// a command that is not there. A process that is not the installer itself, such as <c>dotnet</c> running it, says the
/// product's name.
/// </summary>
internal static class InstallerProgram
{
    /// <summary>The product's name for its installer, which every release's installer file name starts with.</summary>
    public const string ProductName = "hvo-sky-install";

    /// <summary>This process's installer, as the commands it prints name it.</summary>
    public static string Current { get; } = Of(Environment.ProcessPath);

    /// <summary>The installer running from <paramref name="processPath"/>, as the shell takes it.</summary>
    internal static string Of(string? processPath)
        => processPath is { Length: > 0 } && Path.GetFileName(processPath).StartsWith(ProductName, StringComparison.Ordinal)
            ? InstallWizardRunner.Quote([processPath])
            : ProductName;
}
