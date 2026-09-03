using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class InMemoryCelestialCatalogTests
{
    private static readonly string[] ExpectedBrightestIds = ["alpha", "beta", "zeta"];
    private static readonly string[] ExpectedCandidateIds = ["a", "b"];
    private static readonly string[] ExpectedWrappedIds = ["east", "west", "boundary"];
    private static readonly string[] ExpectedPolarIds = ["pole"];
    private static readonly string[] ExpectedFullSkyIds = ["alpha", "zeta"];

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

    [TestMethod]
    public async Task QueryCandidatesAsync_DoesNotApplyAVisibleResultLimitAndHonorsCancellation()
    {
        var catalog = new InMemoryCelestialCatalog([Create("b", 2), Create("a", 1)]);

        var result = await catalog.QueryCandidatesAsync(new CatalogCandidateQuery(5)).ConfigureAwait(false);

        CollectionAssert.AreEqual(ExpectedCandidateIds, result.Select(item => item.Id).ToArray());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await catalog.QueryCandidatesAsync(new CatalogCandidateQuery(5), cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task QueryCandidatesAsync_AppliesInclusiveJ2000CapAcrossRightAscensionWrapAndPoles()
    {
        var catalog = new InMemoryCelestialCatalog([
            new("west", "west", 23.9, 0, 2),
            new("east", "east", 0.1, 0, 1),
            new("boundary", "boundary", 1, 0, 3),
            new("pole", "pole", 12, 89, 4),
            new("outside", "outside", 12, 0, 0)
        ]);

        var wrapped = await catalog.QueryCandidatesAsync(
            new CatalogCandidateQuery(5, new J2000SphericalCap(0, 0, 15))).ConfigureAwait(false);
        var polar = await catalog.QueryCandidatesAsync(
            new CatalogCandidateQuery(5, new J2000SphericalCap(0, 90, 2))).ConfigureAwait(false);

        CollectionAssert.AreEqual(ExpectedWrappedIds, wrapped.Select(item => item.Id).ToArray());
        CollectionAssert.AreEqual(ExpectedPolarIds, polar.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public async Task QueryCandidatesAsync_FullSkyCapPreservesDeterministicOrder()
    {
        var catalog = new InMemoryCelestialCatalog([Create("zeta", 2), Create("alpha", 1)]);

        var result = await catalog.QueryCandidatesAsync(
            new CatalogCandidateQuery(5, new J2000SphericalCap(6, -30, 180))).ConfigureAwait(false);

        CollectionAssert.AreEqual(ExpectedFullSkyIds, result.Select(item => item.Id).ToArray());
    }

    [TestMethod]
    public void J2000SphericalCap_RejectsInvalidCapAndObjectCoordinates()
    {
        J2000SphericalCap[] invalid =
        [
            new(double.NaN, 0, 1), new(24, 0, 1), new(0, -91, 1),
            new(0, 0, -1), new(0, 0, 181)
        ];

        foreach (var cap in invalid)
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(cap.Validate);
        }

        var valid = new J2000SphericalCap(0, 0, 1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => valid.Contains(24, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => valid.Contains(0, double.PositiveInfinity));
    }

    [TestMethod]
    public async Task GetByHipparcosIdsAsync_IgnoresMagnitudeAndDeduplicatesRequestedIds()
    {
        var catalog = new InMemoryCelestialCatalog([
            new CelestialCatalogObject("bright", "Bright", 1, 1, 1, HipparcosId: "10"),
            new CelestialCatalogObject("faint", "Faint", 1, 1, 12, HipparcosId: "20")
        ]);

        var result = await catalog.GetByHipparcosIdsAsync(["20", "20", "missing"]).ConfigureAwait(false);

        Assert.HasCount(1, result);
        Assert.AreEqual("faint", result[0].Id);
        await Assert.ThrowsExactlyAsync<ArgumentException>(async () =>
            await catalog.GetByHipparcosIdsAsync([""]).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static CelestialCatalogObject Create(string id, double magnitude)
        => new(id, id, 1, 1, magnitude);
}
