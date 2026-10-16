using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>Release versions in SemVer 2.0 precedence, the order an upgrade compares its target with.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ProductVersionTests
{
    // SemVer 2.0 section 11's own example, then the build kinds of the install contract.
    private static readonly string[] Ascending =
    [
        "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11",
        "1.0.0-rc.1", "1.0.0", "1.0.1", "1.2.0-ci.7", "1.2.0-dev", "1.2.0-dev.2", "1.2.0-dev.10", "1.2.0-rc.1",
        "1.2.0-rc.2", "1.2.0", "1.10.0", "2.0.0"
    ];

    [TestMethod]
    public void Compare_OrdersEveryVersionAfterTheOnesBeforeIt()
    {
        for (var left = 0; left < Ascending.Length; left++)
        {
            for (var right = 0; right < Ascending.Length; right++)
            {
                Assert.AreEqual(
                    left.CompareTo(right),
                    ProductVersion.Compare(Ascending[left], Ascending[right]),
                    $"{Ascending[left]} against {Ascending[right]}");
            }
        }
        Assert.IsTrue(ProductVersion.IsOlder("1.2.0-rc.1", "1.2.0"));
        Assert.IsFalse(ProductVersion.IsOlder("1.2.0", "1.2.0"));
    }

    [TestMethod]
    public void Compare_IgnoresBuildMetadata()
    {
        Assert.AreEqual(0, ProductVersion.Compare("1.2.0+0123456", "1.2.0"));
        Assert.AreEqual(0, ProductVersion.Compare("1.2.0-rc.1+a", "1.2.0-rc.1+b"));
    }

    [TestMethod]
    [DataRow("0.0.0")]
    [DataRow("1.2.3")]
    [DataRow("1.2.3-dev.4")]
    [DataRow("1.2.3-rc.1+0123456789abcdef0123456789abcdef01234567")]
    [DataRow("10.20.30-0a.x-y")]
    public void IsValid_AcceptsReleaseVersions(string version) => Assert.IsTrue(ProductVersion.IsValid(version), version);

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("v1.2.3")]
    [DataRow("1.2")]
    [DataRow("01.2.3")]
    [DataRow("1.2.3-01")]
    [DataRow("1.2.3-")]
    [DataRow("1.2.3+")]
    [DataRow("1.2.3-rc..1")]
    [DataRow(" 1.2.3")]
    public void IsValid_RefusesAnythingElse(string? version)
    {
        Assert.IsFalse(ProductVersion.IsValid(version), version);
        if (version is not null)
        {
            Assert.ThrowsExactly<FormatException>(() => ProductVersion.Compare(version, "1.0.0"));
        }
    }

    [TestMethod]
    [DataRow("1.2.0", "1.2.0")]
    [DataRow("1.2.0-rc.1", "1.2.0")]
    [DataRow("1.2.0-dev.3+0123456", "1.2.0")]
    public void Release_IsTheVersionWithoutPreReleaseOrBuild(string version, string release)
        => Assert.AreEqual(release, ProductVersion.Release(version));
}
