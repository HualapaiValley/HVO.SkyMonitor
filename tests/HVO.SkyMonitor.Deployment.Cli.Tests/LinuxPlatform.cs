using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("linux")]

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Marks the Deployment.Cli contracts that exercise Linux-only installer semantics and therefore must be
/// selected only on Linux. On any other operating system these tests are still discovered but are reported
/// as skipped with <see cref="Reason"/> so that per-project totals stay exact and no Linux contract is hidden.
/// </summary>
internal static class LinuxOnly
{
    public const string Reason =
        "Linux-only Deployment.Cli contract: it exercises statx, openat, renameat2, ownership, hard-link, and symlink-safety semantics under an installer root that only Linux provides.";
}
