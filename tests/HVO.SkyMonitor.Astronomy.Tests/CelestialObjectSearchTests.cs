using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class CelestialObjectSearchTests
{
    private static readonly DeepSkySemantics Semantics = new(
        "OpenNGC", "v20260501", "36cb178a0f69dba8bfc03a99c10512831edf1c6b", new Uri("https://github.com/mattiaverga/OpenNGC"),
        "CC BY-SA 4.0", "equatorial-j2000-icrs-aligned", "J2000.0", "arcminute",
        "degrees-north-through-east-0-inclusive-to-180-exclusive", "1-widest-2-standard-3-narrowest",
        "b-mag-per-square-arcsecond-within-25-mag-isophote");

    [TestMethod]
    public void FindReturnsStarMatchesBeforeDeepSkyMatches()
    {
        var search = new CelestialObjectSearch(
            new Stars(new CelestialCatalogAlias("HIP 26311", "26241", "hipparcos")), new DeepSkySource(CreateDeepSky()));

        var matches = search.Find("hip 26311");

        Assert.AreEqual(2, matches.Count);
        Assert.AreEqual(new CelestialSearchMatch(CelestialSearchCollection.Star, "26241", "HIP 26311", "hipparcos", false), matches[0]);
        Assert.AreEqual(
            new CelestialSearchMatch(CelestialSearchCollection.DeepSky, "NGC1990", "HIP 26311", DeepSkyAliasKinds.Hipparcos, false),
            matches[1]);
    }

    [TestMethod]
    public void FindFlagsTheDisputedMessierAliasAndResolvesMessierIdentity()
    {
        var search = new CelestialObjectSearch(new Stars(), new DeepSkySource(CreateDeepSky()));

        var disputed = search.Find("M102");
        var messier = search.Find("m101");

        Assert.AreEqual(
            new CelestialSearchMatch(CelestialSearchCollection.DeepSky, "NGC5457", "M102", DeepSkyAliasKinds.Disputed, true),
            disputed.Single());
        Assert.AreEqual(
            new CelestialSearchMatch(CelestialSearchCollection.DeepSky, "NGC5457", "M101", DeepSkyAliasKinds.Messier, false),
            messier.Single());
    }

    [TestMethod]
    public void FindSearchesOnlyStarsWithoutADeepSkyCollection()
    {
        var stars = new Stars(new CelestialCatalogAlias("M102", "1", "identifier"));

        Assert.AreEqual(1, new CelestialObjectSearch(stars, null).Find("M102").Count);
        Assert.AreEqual(1, new CelestialObjectSearch(stars, new DeepSkySource(null)).Find("M102").Count);
        Assert.AreEqual(0, new CelestialObjectSearch(stars, null).Find("M101").Count);
    }

    [TestMethod]
    public void ConstructorAndFindRejectNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new CelestialObjectSearch(null!, null));
        Assert.Throws<ArgumentNullException>(() => new CelestialObjectSearch(new Stars(), null).Find(null!));
    }

    private static DeepSkyCatalog CreateDeepSky() => new(
        Semantics,
        [
            new DeepSkyObject(
                "NGC5457", "NGC 5457", "M101", "G", 14.053483, 54.348944, "UMa", 23.99, 23.07, 28, 8.36, 7.9, 23.97, "SABc",
                101, null, null, null),
            new DeepSkyObject(
                "NGC1990", "NGC 1990", "NGC 1990", DeepSkyObjectTypes.Star, 5.603561, -1.201917, "Ori", null, null, null, 1.48,
                1.69, null, null, null, null, "26311", null),
        ],
        [
            new DeepSkyAlias("NGC 5457", "NGC5457", DeepSkyAliasKinds.Designation),
            new DeepSkyAlias("M101", "NGC5457", DeepSkyAliasKinds.Messier),
            new DeepSkyAlias("M102", "NGC5457", DeepSkyAliasKinds.Disputed),
            new DeepSkyAlias("NGC 1990", "NGC1990", DeepSkyAliasKinds.Designation),
            new DeepSkyAlias("HIP 26311", "NGC1990", DeepSkyAliasKinds.Hipparcos),
        ],
        [],
        []);

    private sealed class Stars(params CelestialCatalogAlias[] aliases) : ICelestialCatalogDetailsSource
    {
        public CatalogSemantics Semantics => throw new NotSupportedException();

        public bool TryGetDetails(string objectId, out CelestialCatalogObjectDetails details) => throw new NotSupportedException();

        public IReadOnlyList<CelestialCatalogAlias> FindByAlias(string designation)
            => aliases.Where(item => string.Equals(item.Alias, designation.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    private sealed class DeepSkySource(IDeepSkyCatalog? catalog) : IDeepSkyCatalogSource
    {
        public IDeepSkyCatalog? DeepSky => catalog;
    }
}
