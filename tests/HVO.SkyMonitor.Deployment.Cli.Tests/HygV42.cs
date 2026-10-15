namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// The pinned HYG 4.2 production identity existing lifecycle fixtures install. Issue #521 keeps HYG 4.2 approved and
/// unchanged; <see cref="ProductionCatalogRegistryTests"/> proves these constants still equal the reviewed registry.
/// </summary>
internal static class HygV42
{
    public const string CatalogId = "hyg-v42-production";
    public const string PackageVersion = "hyg-v4.2-p3-s2-r1";
    public const string DatabaseSha256 = "b51d18b722199e89aa8fe4622ebe507346c75effb375e546881452a263f0b9e2";
    public const long DatabaseLength = 9_302_016;
    public const long RowCount = 119_625;
}
