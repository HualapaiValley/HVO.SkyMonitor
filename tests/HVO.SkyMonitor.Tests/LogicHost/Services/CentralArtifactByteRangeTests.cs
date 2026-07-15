using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Services;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralArtifactByteRangeTests
{
    [TestMethod]
    [DataRow("bytes=0-3", 0L, 4L)]
    [DataRow("bytes=4-", 4L, 6L)]
    [DataRow("bytes=-3", 7L, 3L)]
    [DataRow("bytes=8-99", 8L, 2L)]
    public void TryParse_WithSupportedSingleRange_ReturnsBoundedRange(string value, long start, long length)
    {
        CentralArtifactByteRange.TryParse(value, 10, out var range).Should().BeTrue();
        range.Should().Be(new CentralArtifactByteRange(start, length));
    }

    [TestMethod]
    [DataRow("items=0-1")]
    [DataRow("bytes=10-")]
    [DataRow("bytes=5-4")]
    [DataRow("bytes=0-1,4-5")]
    [DataRow("bytes=-0")]
    [DataRow("bytes=--")]
    public void TryParse_WithUnsupportedOrUnsatisfiableRange_ReturnsFalse(string value)
    {
        CentralArtifactByteRange.TryParse(value, 10, out var range).Should().BeFalse();
        range.Should().BeNull();
    }

    [TestMethod]
    public void TryParse_WithoutRange_RequestsWholeArtifact()
    {
        CentralArtifactByteRange.TryParse(null, 10, out var range).Should().BeTrue();
        range.Should().BeNull();
    }
}
