using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
public sealed class InMemoryCelestialCatalogTests
{
    private static readonly string[] ExpectedBrightestIds = ["alpha", "beta", "zeta"];
    private static readonly string[] ExpectedCandidateIds = ["a", "b"];

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

    private static CelestialCatalogObject Create(string id, double magnitude)
        => new(id, id, 1, 1, magnitude);
}
