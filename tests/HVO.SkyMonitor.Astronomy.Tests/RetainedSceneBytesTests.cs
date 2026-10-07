using System.Reflection;
using System.Security.Cryptography;

namespace HVO.SkyMonitor.Astronomy.Tests;

/// <summary>
/// Pins the canonical bytes of representative projected scenes that contain no deep-sky object. Each row was recorded
/// by <see cref="RetainedSceneFixtures"/> compiled against the revision before deep-sky placement existed, so a scene
/// that does not ask for the deep-sky layer must keep serializing to exactly these bytes.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class RetainedSceneBytesTests
{
    [TestMethod]
    [DataRow("v1-stars-identity", "542698BDCD51A1F33554CEA62B907236457B48408E4D74C9517592178E85C432", 7966)]
    [DataRow("v1-stars-transformed", "C5F2E04B7242ECC1FDE35EC83D3AA2D4EE9F9FD58B37A8062113B502B8219556", 7219)]
    [DataRow("v1-moon-cropped-away", "E2F1385C6ECAA644D3FD2FB98E894F132D2A82FA6156A3C37FFF6D8163AB27AB", 1968)]
    [DataRow("v2-moon-identity", "291583239ECBD27F75257D64F3EACE69F2E89A9CACF9E9B4857DB84629D9D1EC", 12905)]
    [DataRow("v2-moon-transformed", "25C46ED6830123EBA3CBBC449D90B47BDDDA11693C3996E3A8A6BB954BF158DA", 6747)]
    [DataRow("v2-moon-crop-clipped", "18267275F173D047E20DD931425979DBAE3593C034A5A43B280564728A7A73A5", 5438)]
    [DataRow("v2-moon-binned-readout", "60F1C9B578C3BC8894A78D9DA7BBF439F39C37899AFE180B9FE36C6A4026691E", 12896)]
    [DataRow("v2-moon-circle-clipped", "C74D64E8503CD9734D3238FBE1B78F3806A7EFF06C38036C8B2D282AE71C00C3", 8996)]
    [DataRow("v2-moon-circle-and-crop-clipped", "F8A37649562544A3C7D1B40148D9FB6DE7F0A2ACC98F8D78809D387AC7E2EE93", 4858)]
    public async Task SceneBytesEqualTheValuesRecordedBeforeDeepSkyPlacement(string name, string sha256, int length)
    {
        var bytes = await RetainedSceneFixtures.BuildAsync(name).ConfigureAwait(false);

        Assert.AreEqual(length, bytes.Length, name);
        Assert.AreEqual(sha256, Convert.ToHexString(SHA256.HashData(bytes)), name);
    }

    [TestMethod]
    public void EveryFixtureHasExactlyOnePinnedValue()
    {
        var pinned = typeof(RetainedSceneBytesTests)
            .GetMethod(nameof(SceneBytesEqualTheValuesRecordedBeforeDeepSkyPlacement))!
            .GetCustomAttributes<DataRowAttribute>()
            .Select(static row => (string)row.Data[0]!)
            .ToArray();

        // Reflection does not promise declaration order, so compare as multisets: one row per fixture, no strays.
        CollectionAssert.AreEquivalent(RetainedSceneFixtures.Names.ToArray(), pinned);
    }
}
