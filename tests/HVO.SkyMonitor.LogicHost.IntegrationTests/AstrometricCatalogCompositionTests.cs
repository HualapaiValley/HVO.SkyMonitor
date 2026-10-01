using HVO.SkyMonitor.Astronomy;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
public sealed class AstrometricCatalogCompositionTests
{
    [TestMethod]
    public async Task HostSharesInstalledCatalogWithoutPromotingFixtureToCompleteSky()
    {
        var services = AssemblyHooks.Fixture.Factory.Services;
        var source = services.GetRequiredService<IAstrometricCatalogSource>();
        Assert.AreSame<object>(services.GetRequiredService<ICelestialCatalog>(), source);
        Assert.AreSame<object>(services.GetRequiredService<IHipparcosCatalog>(), source);

        var selected = await source.ReadAsync(7, AstrometricCatalogData.MaximumEntries).ConfigureAwait(false);
        var bounded = await source.ReadAsync(7, 2).ConfigureAwait(false);

        Assert.HasCount(9, selected.Stars);
        Assert.HasCount(2, bounded.Stars);
        Assert.IsFalse(selected.IsCompleteForRequestedMagnitude,
            "The host's explicit nine-star fixture cannot certify a complete production selection.");
        Assert.IsFalse(bounded.IsCompleteForRequestedMagnitude);
        Assert.AreEqual("hyg-v42-fixture", selected.Provenance!.CatalogId);
        Assert.AreEqual("fixture", selected.Provenance.PackageKind);
        Assert.AreEqual(services.GetRequiredService<ICelestialCatalogMetadataSource>().Metadata, selected.Metadata);
        Assert.AreEqual(AstrometricConventions.CoordinateModel, selected.CoordinateModel);
        Assert.AreEqual(selected.IdentitySha256, bounded.IdentitySha256);
        Assert.AreNotEqual(selected.SelectionIdentitySha256, bounded.SelectionIdentitySha256);
        CollectionAssert.AreEqual(selected.Stars.Take(2).ToArray(), bounded.Stars.ToArray());
    }
}
