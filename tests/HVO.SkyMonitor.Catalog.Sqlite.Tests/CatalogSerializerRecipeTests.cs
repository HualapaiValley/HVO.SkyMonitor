using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.Catalog.Sqlite.Tests;

internal sealed partial class ApprovedCatalogSpecificationsTests
{
    [TestMethod]
    [DataRow("scripts/catalog/build-hyg-v42.sh", 1)]
    [DataRow("scripts/catalog/hyg-v44-common.sh", 1)]
    [DataRow("scripts/catalog/hyg-v44-openngc-common.sh", 2)]
    [DataRow("tests/fixtures/catalog/build-fixture.sql", 1)]
    [DataRow("tests/fixtures/catalog/build-fixture-v44.sql", 1)]
    [DataRow("tests/fixtures/catalog/build-fixture-v44-openngc.sql", 1)]
    public void EverySerializerConnectionPinsSecureDeletionBeforeWrites(string relativePath, int connectionCount)
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));
        var recipes = SerializerSql().Matches(text);
        Assert.HasCount(connectionCount, recipes, $"Unexpected serializer connection count in {relativePath}.");

        foreach (Match recipe in recipes)
        {
            var sql = recipe.Groups["sql"].Value;
            var pin = SecureDeletionPin().Match(sql);
            Assert.IsTrue(pin.Success, $"Missing secure_delete pin in {relativePath} at offset {recipe.Index}.");
            var write = SerializerWrite().Match(sql);
            Assert.IsTrue(write.Success, $"No serializer write found in {relativePath}.");
            Assert.IsLessThan(write.Index, pin.Index, $"secure_delete must be pinned before writes in {relativePath}.");
        }
    }

    [GeneratedRegex(@"(?ms)^\.bail on\r?\n(?<sql>.*?)(?=^SQL\r?$|\z)")]
    private static partial Regex SerializerSql();

    [GeneratedRegex(@"(?mi)^\s*PRAGMA\s+secure_delete\s*=\s*ON\s*;")]
    private static partial Regex SecureDeletionPin();

    [GeneratedRegex(@"(?mi)^\s*(?:CREATE|DROP|INSERT|UPDATE|DELETE|VACUUM|REPLACE|ALTER|REINDEX)\b|^\s*PRAGMA\s+(?:user_version|journal_mode)\s*=")]
    private static partial Regex SerializerWrite();
}
