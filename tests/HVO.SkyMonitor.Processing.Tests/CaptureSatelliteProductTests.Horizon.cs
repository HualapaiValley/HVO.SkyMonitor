using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Processing.Tests;

public sealed partial class CaptureSatelliteProductTests
{
    [TestMethod]
    [DataRow("clear")]
    [DataRow("blocked")]
    [DataRow("unknown")]
    public async Task CaptureBoundHorizonPreservesTracksAndAuthenticatesEligibility(string horizon)
    {
        var f = new CaptureSatelliteFixture(horizon);
        var product = await CaptureSatelliteProducts.PredictAsync(f.Evidence, f.Raw, "satellites", CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(product);
        var output = CaptureOrbitalPredictionJson.Parse(product.Payload.Span, f.Input);
        Assert.IsNotEmpty(output.Tracks);
        Assert.IsNotNull(output.Obstruction);
        Assert.HasCount(output.Tracks.Count, output.Obstruction);
        var profileHash = f.Configuration.Rig.HorizonObstruction!.ComputeIdentitySha256();
        for (var i = 0; i < output.Tracks.Count; i++)
        {
            var facts = output.Obstruction[i];
            Assert.AreEqual(profileHash, facts.Binding.ProfileIdentitySha256);
            Assert.AreEqual(output.Tracks[i].ProductSha256, facts.Binding.SourceIdentitySha256);
            Assert.HasCount(output.Tracks[i].Samples.Count, facts.Eligibility);
            if (horizon == "clear") Assert.IsTrue(facts.Eligibility.Any(static value => value == HorizonObstructionEligibility.Clear));
            else Assert.IsFalse(facts.Eligibility.Any(static value => value == HorizonObstructionEligibility.Clear));
        }
        Assert.ThrowsExactly<ArgumentException>(() => CaptureOrbitalPredictionJson.Serialize(output with { Obstruction = null }, f.Input));
        var restored = CaptureSatelliteProducts.Restore(CaptureSatelliteProducts.ParseInput(CaptureSatelliteProducts.SerializeInput(f.Evidence)));
        Assert.AreEqual(f.Input.IdentitySha256, restored.IdentitySha256);
        var products = CaptureSatelliteProducts.Create(f.Evidence, f.Raw, "satellites", product.Payload);
        Assert.IsNotEmpty(products);
        Assert.AreEqual("Unknown", output.PhysicalVisibility);
    }
}
