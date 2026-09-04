using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentCurrentSkyFactsProjectorTests
{
    private static readonly ObservingDayCalendar Phoenix = ObservingDayCalendar.Create("America/Phoenix");
    private static readonly DateTimeOffset LateNight = new(2026, 9, 4, 6, 30, 0, TimeSpan.Zero);

    [TestMethod]
    public void Project_ReadsDurableControlsLayoutCloudAndCombinedLineage()
    {
        var sources = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        var combinedId = Guid.NewGuid();
        var capture = Capture(LateNight) with
        {
            Detail = Detail(),
            Artifacts =
            [
                Artifact(Guid.NewGuid(), FrameArtifactRole.Raw, []),
                Artifact(combinedId, FrameArtifactRole.Combined, sources, "rolling-mean", "ABC123")
            ]
        };

        var facts = CameraAgentCurrentSkyFactsProjector.Project(capture, Phoenix);

        Assert.AreEqual(capture.CaptureId, facts.CaptureId);
        Assert.AreEqual("rig-1", facts.RigId);
        Assert.AreEqual(new DateOnly(2026, 9, 3), facts.ObservingDay.Date);
        Assert.AreEqual(2500d, facts.ExposureMilliseconds);
        Assert.AreEqual(120d, facts.Gain);
        Assert.AreEqual(-9.5d, facts.SensorTemperatureC);
        Assert.AreEqual(4056, facts.Width);
        Assert.AreEqual(3040, facts.Height);
        Assert.AreEqual("Available", facts.EvidenceAvailability);
        Assert.AreEqual("Assessed", facts.Cloud.Availability);
        Assert.AreEqual("Clear", facts.Cloud.Status);
        Assert.AreEqual(120000, facts.Cloud.CoverageMillionths);
        Assert.IsNotNull(facts.CombinedLineage);
        Assert.AreEqual(combinedId, facts.CombinedLineage.ArtifactId);
        Assert.AreEqual(3, facts.CombinedLineage.SourceCount);
        CollectionAssert.AreEqual(sources, facts.CombinedLineage.SourceArtifactIds.ToArray());
        Assert.AreEqual("rolling-mean", facts.CombinedLineage.RecipeName);
        Assert.AreEqual("ABC123", facts.CombinedLineage.RecipeIdentitySha256);
        Assert.IsFalse(facts.CombinedLineage.SourcesTruncated);
        Assert.AreEqual("profile", facts.ProcessingProfile?.Name);
    }

    [TestMethod]
    public void Project_ReportsUnavailableFactsWithoutInventingValues()
    {
        var capture = Capture(LateNight);

        var facts = CameraAgentCurrentSkyFactsProjector.Project(capture, ObservingDayCalendar.Create(null));

        Assert.IsNull(facts.ExposureMilliseconds);
        Assert.IsNull(facts.Gain);
        Assert.IsNull(facts.Width);
        Assert.AreEqual("Unavailable", facts.EvidenceAvailability);
        Assert.AreEqual("Unavailable", facts.Cloud.Availability);
        Assert.IsNull(facts.Cloud.Status);
        Assert.IsNull(facts.CombinedLineage);
        Assert.IsNull(facts.ProcessingProfile);
        Assert.IsTrue(facts.ObservingDay.TimeZoneFallback);
        Assert.AreEqual(new DateOnly(2026, 9, 3), facts.ObservingDay.Date);
    }

    [TestMethod]
    public void Project_PrefersTheNewestCombinedArtifactAndMarksTruncatedSources()
    {
        var older = Artifact(Guid.NewGuid(), FrameArtifactRole.Combined, [Guid.NewGuid()], created: LateNight.AddMinutes(-5));
        var newer = Artifact(Guid.NewGuid(), FrameArtifactRole.Combined, [Guid.NewGuid(), Guid.NewGuid()], created: LateNight);
        var capture = Capture(LateNight) with { Artifacts = [older, newer], ArtifactsTruncated = true };

        var facts = CameraAgentCurrentSkyFactsProjector.Project(capture, Phoenix);

        Assert.AreEqual(newer.ArtifactId, facts.CombinedLineage?.ArtifactId);
        Assert.AreEqual(2, facts.CombinedLineage?.SourceCount);
        Assert.IsTrue(facts.CombinedLineage?.SourcesTruncated);
    }

    private static CameraAgentGalleryCapture Capture(DateTimeOffset exposureStartedUtc) => new(
        Guid.NewGuid(),
        "agent-1",
        "rig-1",
        42,
        exposureStartedUtc,
        exposureStartedUtc.AddSeconds(3),
        "Durable",
        GalleryEvidenceOrigin.Simulated,
        [],
        []);

    private static CameraAgentGalleryCaptureDetail Detail() => new(
        "Available",
        "v1",
        new CameraAgentGalleryLayout(4056, 3040, 8112, "Mono16", "LittleEndian", 12, 16, "Unpacked", "None", 64, 4095, 24660480),
        new CameraAgentGalleryTiming(LateNight, LateNight, LateNight.AddSeconds(2.5), LateNight.AddSeconds(3), LateNight.AddSeconds(3), null),
        new CameraAgentGalleryControls(2500, 2500, 120, 120, 8, 8, -10, -9.5),
        false,
        [],
        [],
        new CameraAgentGalleryCloudAssessment("Assessed", "Clear", "Good", 120000, 900000, [], true, 64, 48, "MASK"),
        new ProfileIdentityDescriptor("profile", "1", "SHA"));

    private static CameraAgentGalleryArtifact Artifact(
        Guid artifactId,
        FrameArtifactRole role,
        IReadOnlyList<Guid> sources,
        string? recipeName = null,
        string? recipeIdentity = null,
        DateTimeOffset? created = null) => new(
        artifactId,
        role,
        null,
        null,
        created ?? LateNight,
        "image/png",
        "CHECKSUM",
        1024,
        recipeName is null ? null : new CameraAgentGalleryRecipe(recipeName, "1.0.0", "1.0.0", "OPT", recipeIdentity ?? "ID"),
        sources,
        null);
}
