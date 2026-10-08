using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

/// <summary>The fail-closed catalog label rule shared by the raster and presentation-layer annotations.</summary>
[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class StellarLabelPolicyTests
{
    private static readonly ProjectedAnnotationObject[] Objects =
    [
        new("solar-system:Moon", "Moon", new PixelPoint(10, 10)),
        new("star:eligible", "ELIGIBLE", new PixelPoint(20, 20)),
        new("star:ineligible", "INELIGIBLE", new PixelPoint(30, 30)),
        new("star:unassociated", "UNASSOCIATED", new PixelPoint(40, 40)),
        new("star:unlabelled", "UNLABELLED", new PixelPoint(50, 50), DrawLabel: false)
    ];

    [TestMethod]
    public void MissingProductSuppressesEveryStarLabelAndLeavesSolarSystemBodies()
    {
        var gated = StellarLabelPolicy.Apply(Objects, associations: null);

        bool[] drawn = [true, false, false, false, false];
        CollectionAssert.AreEqual(drawn, gated.Select(static item => item.DrawLabel).ToArray());
        CollectionAssert.AreEqual(Objects.Select(static item => (item.Id, item.Pixel)).ToArray(),
            gated.Select(static item => (item.Id, item.Pixel)).ToArray(), "marks stay at their expected pixels");
    }

    [TestMethod]
    public async Task OnlyPolicyEligibleAssociationsKeepTheirLabel()
    {
        var gated = StellarLabelPolicy.Apply(Objects, await ProductAsync().ConfigureAwait(false));

        string[] labelled = ["solar-system:Moon", "star:eligible"];
        CollectionAssert.AreEqual(labelled,
            gated.Where(static item => item.DrawLabel).Select(static item => item.Id).ToArray());
        Assert.IsTrue(gated.All(static item => !item.DisplayName.EndsWith(StellarLabelPolicy.ExpectedSuffix, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ExpectedPositionDiagnosticsAreOptInAndVisiblyDistinct()
    {
        var gated = StellarLabelPolicy.Apply(Objects, await ProductAsync().ConfigureAwait(false),
            expectedPositionDiagnostics: true);

        string[] names = ["Moon", "ELIGIBLE", "INELIGIBLE (expected)", "UNASSOCIATED (expected)", "UNLABELLED"];
        CollectionAssert.AreEqual(names, gated.Select(static item => item.DisplayName).ToArray());
        bool[] drawn = [true, true, true, true, false];
        CollectionAssert.AreEqual(drawn, gated.Select(static item => item.DrawLabel).ToArray());
    }

    [TestMethod]
    public void RejectionReasonsFollowTheDeclaredPrecedence()
    {
        var association = new MeasuredStellarAssociationV1("star:a", 0, 2, 10, 10, 10, 10, 0, 0, 1000, 50,
            null, null, null, false, 0, false, null, null, true, null);
        var strict = new StellarLabelPolicySettingsV1(MinimumSignalToNoise: 10, AllowSaturated: false, AllowTrailed: false);

        Assert.IsNull(StellarLabelPolicy.RejectionReason(association, strict));
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.LabelSignalToNoiseUnavailable,
            StellarLabelPolicy.RejectionReason(association with { SignalToNoise = null, Saturated = true }, strict));
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.LabelLowSignalToNoise,
            StellarLabelPolicy.RejectionReason(association with { SignalToNoise = 9.9, Saturated = true }, strict));
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.LabelSaturated,
            StellarLabelPolicy.RejectionReason(association with { Saturated = true, Trailed = true }, strict));
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.LabelTrailed,
            StellarLabelPolicy.RejectionReason(association with { Trailed = true }, strict));
        Assert.IsTrue(StellarLabelPolicy.IsEligible(association with { Saturated = true, Trailed = true },
            new StellarLabelPolicySettingsV1()));
    }

    private static async Task<MeasuredStellarAssociationsV1> ProductAsync()
    {
        var scene = await MeasuredStellarAssociationFixtures.StarFieldAsync(
            ("star:eligible", 0, 0, 2), ("star:ineligible", 5, 3, 3)).ConfigureAwait(false);
        var product = MeasuredStellarAssociationFixtures.AllEligible(scene);
        return product with
        {
            Associations = [.. product.Associations.Select(static item => item.CatalogId == "star:ineligible"
                ? item with { LabelEligible = false, LabelRejectionReason = MeasuredStellarAssociationReasonCodes.LabelLowSignalToNoise }
                : item)]
        };
    }
}
