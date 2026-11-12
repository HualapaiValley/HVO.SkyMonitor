using System.Security.Cryptography;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using static HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation.RetainedIntegrityFormat;

namespace HVO.SkyMonitor.CameraAgent.Tests.DeploymentLocation;

[TestClass]
[TestCategory("Unit")]
public sealed class RetainedIntegrityFormatTests
{
    [TestMethod]
    [DataRow("nonce")]
    [DataRow("key")]
    [DataRow("digest")]
    [DataRow("kind")]
    [DataRow("generation")]
    [DataRow("secret")]
    [DataRow("mac")]
    public void RowMacBindsEveryAuthorityFieldAndTheProtectedSecret(string fault)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var proof = new EntryProof(new string('a', 32), "F|retained.json", new string('b', 64), 2, 7);
        static byte[] NoProtection(byte[] _) => throw new AssertFailedException("New rows must not use per-entry Data Protection.");
        var auth = ProtectEntry(proof, key, NoProtection);
        Assert.IsTrue(AuthenticateEntry(proof, auth, key, NoProtection));
        var changed = fault switch
        {
            "nonce" => proof with { Nonce = new string('c', 32) },
            "key" => proof with { Key = "F|other.json" },
            "digest" => proof with { Digest = new string('d', 64) },
            "kind" => proof with { Kind = 1 },
            "generation" => proof with { Generation = 8 },
            _ => proof
        };
        if (fault == "secret") key[0] ^= 1;
        if (fault == "mac") auth[0] ^= 1;
        Assert.IsFalse(AuthenticateEntry(changed, auth, key, NoProtection));
    }
}
