using FluentAssertions;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralTransientReviewTests
{
    [TestMethod]
    public void EventEtag_IsStrongOpaqueAndRoundTripsOnlyEightByteRowVersion()
    {
        byte[] rowVersion = [0, 1, 2, 3, 4, 5, 6, 255];

        var etag = CentralTransientEventEtag.Create(rowVersion);

        etag.Should().StartWith("\"").And.EndWith("\"");
        CentralTransientEventEtag.TryParse(etag, out var parsed).Should().BeTrue();
        parsed.Should().Equal(rowVersion);
        CentralTransientEventEtag.TryParse($"W/{etag}", out _).Should().BeFalse();
        CentralTransientEventEtag.TryParse("\"AA\"", out _).Should().BeFalse();
        CentralTransientEventEtag.TryParse("*", out _).Should().BeFalse();
    }

    [TestMethod]
    public void NonMeteorOverride_DoesNotInheritAssessmentMeteorSeverity()
    {
        var effective = CentralTransientReviewProjection.Resolve(
            TransientClassification.Meteor,
            TransientMeteorSeverity.Fireball,
            800_000,
            new TransientReviewOverrideV1(TransientClassification.Aircraft, null, 900_000));

        effective.Classification.Should().Be(TransientClassification.Aircraft);
        effective.MeteorSeverity.Should().BeNull();
        effective.ConfidenceMillionths.Should().Be(900_000);
    }
}
