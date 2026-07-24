using Microsoft.Extensions.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Tests.Configuration;

[TestClass]
[TestCategory("Unit")]
public sealed class LocalIdentityPasswordFileTests
{
    [TestMethod]
    public void ApplyLocalIdentityPasswordFile_ReadsSecretWithoutRetainingNewline()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "AcceptancePassword!197\n");
            var configuration = new ConfigurationManager();
            configuration["LocalIdentity:AdminPasswordFile"] = path;

            Program.ApplyLocalIdentityPasswordFile(configuration);

            Assert.AreEqual("AcceptancePassword!197", configuration["LocalIdentity:AdminPassword"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public void ApplyLocalIdentityPasswordFile_RejectsRelativePath()
    {
        var configuration = new ConfigurationManager();
        configuration["LocalIdentity:AdminPasswordFile"] = "relative-password.txt";

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => Program.ApplyLocalIdentityPasswordFile(configuration));

        StringAssert.Contains(exception.Message, "absolute path", StringComparison.Ordinal);
    }

    [TestMethod]
    public void ApplyLocalIdentityPasswordFile_RejectsEmptyFile()
    {
        var path = Path.GetTempFileName();
        try
        {
            var configuration = new ConfigurationManager();
            configuration["LocalIdentity:AdminPasswordFile"] = path;

            var exception = Assert.ThrowsExactly<InvalidOperationException>(
                () => Program.ApplyLocalIdentityPasswordFile(configuration));

            StringAssert.Contains(exception.Message, "non-empty regular file", StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
