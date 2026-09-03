using System.Text;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class CsvCelestialCatalogTests
{
    private static readonly string[] ExpectedIds = ["alpha", "beta"];

    [TestMethod]
    public void Constructor_LoadsOnceAndQueriesDeterministically()
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("id,proper,ra,dec,mag,ci\nbeta,Beta,1,2,2,0.2\nalpha,Alpha,2,3,1,0.1\n"));
        var catalog = new CsvCelestialCatalog(source);

        var result = catalog.Query(new CatalogQuery(5, 2));

        CollectionAssert.AreEqual(ExpectedIds, result.Select(item => item.Id).ToArray());
        Assert.AreEqual(0.1, result[0].ColorIndex!.Value, 1e-10);
    }

    [TestMethod]
    public void Constructor_MissingRequiredHeader_Throws()
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("id,ra,dec,mag\nalpha,1,2,3\n"));
        Assert.Throws<InvalidDataException>(() => new CsvCelestialCatalog(source));
    }
}
