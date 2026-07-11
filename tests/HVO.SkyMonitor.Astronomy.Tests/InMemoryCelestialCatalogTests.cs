using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
public sealed class InMemoryCelestialCatalogTests
{
    private static readonly string[] ExpectedBrightestIds = ["alpha", "beta", "zeta"];

    [TestMethod]
    public void Query_UsesBrightnessThenIdentifierAndHonorsLimit()
    {
        var catalog = new InMemoryCelestialCatalog([
            Create("zeta", 2), Create("beta", 1), Create("alpha", 1), Create("dim", 8)
        ]);

        var result = catalog.Query(new CatalogQuery(5, 3));

        CollectionAssert.AreEqual(ExpectedBrightestIds, result.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public void Query_InvalidResultLimit_Throws()
    {
        var catalog = new InMemoryCelestialCatalog([Create("alpha", 1)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.Query(new CatalogQuery(2, 0)));
    }

    [TestMethod]
    public void Constructor_InvalidRightAscension_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new InMemoryCelestialCatalog([
            new CelestialCatalogObject("alpha", "Alpha", 24, 0, 1)
        ]));
    }

    private static CelestialCatalogObject Create(string id, double magnitude)
        => new(id, id, 1, 1, magnitude);
}
