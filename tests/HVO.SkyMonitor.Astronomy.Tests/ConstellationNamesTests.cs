using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ConstellationNamesTests
{
    [TestMethod]
    public void EveryFigureInTheStandardTopology_HasAName()
    {
        var topology = (InMemoryConstellationTopology)StandardConstellationTopology.CreateD3Celestial();
        var ids = topology.AllSegments.Select(static segment => segment.ConstellationId).Distinct(StringComparer.Ordinal).ToArray();

        Assert.HasCount(ConstellationNames.Count, ids);
        Assert.IsEmpty(ids.Where(static id => ConstellationNames.Find(id) is null));
    }

    [TestMethod]
    [DataRow("UMa", "Ursa Major")]
    [DataRow("UMA", "Ursa Major")]
    [DataRow("cvn", "Canes Venatici")]
    [DataRow("SER", "Serpens")]
    [DataRow("BOO", "Boötes")]
    public void Find_IgnoresTheCaseOfTheAbbreviation(string abbreviation, string name)
        => Assert.AreEqual(name, ConstellationNames.Find(abbreviation));

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("SE1")]
    [DataRow("Ursa Major")]
    public void Find_ReturnsNullForAnUnrecognizedAbbreviation(string? abbreviation)
        => Assert.IsNull(ConstellationNames.Find(abbreviation));
}
