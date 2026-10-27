using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class MeasuredStellarAssociationAcceptanceConfigurationTests
{
    [TestMethod]
    public void FullCatalogTruthRequiresExplicitExhaustiveCaptureWithoutChangingTheProfile()
    {
        var profile = VirtualAstrometryFixture.Profiles(110201)[0];
        var original = profile.Config.Module.Options!.Value.GetRawText();

        var configured = MeasuredStellarAssociationAcceptanceTests.CreateConfiguration(profile);

        var options = configured.Module.Options!.Value.Deserialize<VirtualSkyCameraModuleOptions>(VirtualAstrometryFixture.JsonOptions)!;
        Assert.AreEqual(VirtualSkyStellarCandidateSelection.Exhaustive, options.StellarCandidateSelection,
            "Full-catalog truth cannot score an operator-limited capture containing only200stars.");
        Assert.AreEqual(2000, options.MaximumResults);
        Assert.AreEqual(5d, options.MaximumMagnitude);
        Assert.AreEqual(110201, options.Seed);
        Assert.IsTrue(options.ShotNoiseEnabled);
        Assert.AreEqual(original, profile.Config.Module.Options.Value.GetRawText());
        Assert.AreEqual(profile.Config.Rig, configured.Rig);
    }
}
