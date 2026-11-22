using FluentAssertions;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Silo;

namespace HVO.SkyMonitor.CameraAgent.Tests.Silo;

/// <summary>
/// One normalized identity per artifact revision and per view revision (#1965 4.2, 4.4, 6.1). Changed producer or
/// sidecar facts are a new revision, and a view's required reopen set follows from its own layers and preview.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SiloIdentityTests
{
    [TestMethod]
    public async Task ChangedProducerFactsUnderTheSameArtifactRevisionAreAConflict()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        var first = SiloDrafts.Capture(silo.Payloads, 1);
        await silo.PublishAsync(first).ConfigureAwait(false);
        var second = SiloDrafts.Capture(silo.Payloads, 2);
        var reattributed = second with { Members = [first.Members[0] with { Producer = "another-producer/2" }, second.Members[1]] };

        var stage = () => silo.Publisher.StageAsync(reattributed, CancellationToken.None).AsTask();

        (await stage.Should().ThrowAsync<SiloConflictException>().ConfigureAwait(false)).Which.Code
            .Should().Be("silo.artifact-revision-conflict", "a changed producer is a new revision, never the same one");
    }

    [TestMethod]
    public void AViewManifestMustRequireTheEvidenceItsReadyLayersNeed()
    {
        var manifest = SiloFormatTests.GoldenManifest();
        var weakened = manifest with
        {
            Dependencies = [.. manifest.Dependencies.Select(dependency =>
                dependency.ArtifactId == SiloTypedViews.EvidenceId("0001") ? dependency with { Required = false } : dependency)]
        };

        var validate = () => SiloJson.ValidateManifest(weakened, "manifest");

        validate.Should().Throw<ArgumentException>("a Ready layer's evidence is part of the exact reopen set");
    }

    [TestMethod]
    public void EvidenceSharedByAWaitingAndAReadyLayerIsRequired()
    {
        var shared = new CenteredViewArtifact(new Guid(7, 0, 0, new byte[8]), Convert.ToHexString(SiloDrafts.Bytes("identity", 32)),
            Convert.ToHexString(SiloDrafts.Bytes("payload", 32)));
        CenteredViewLayer[] layers =
        [
            new(new("satellites", CenteredViewLayerScope.Stack, null), 1, shared, SiloHarness.Start, CenteredViewReadiness.Waiting, null, null),
            new(new("stars", CenteredViewLayerScope.Stack, null), 2, shared, SiloHarness.Start, CenteredViewReadiness.Ready, null, null)
        ];
        var preview = new CenteredViewArtifact(new Guid(8, 0, 0, new byte[8]), Convert.ToHexString(SiloDrafts.Bytes("p-id", 32)),
            Convert.ToHexString(SiloDrafts.Bytes("p", 32)));

        var dependencies = SiloCenteredViews.ReopenDependencies(preview, preview, scienceDisplayed: false, null, layers);

        dependencies.Single(dependency => dependency.ArtifactId == shared.ArtifactId.ToString("D")).Required.Should().BeTrue();
    }

    [TestMethod]
    public async Task AnExactViewLinkMustNameTheRevisionsStreamAndReferenceCapture()
    {
        using var silo = await SiloHarness.CreateAsync().ConfigureAwait(false);
        await silo.PublishAsync(SiloDrafts.View(silo.Payloads, "base-l", "rev-1", null, 1, [SiloDrafts.Layer("clouds", "Waiting", null, 0)],
            [], publishPreview: true)).ConfigureAwait(false);
        var stream = SiloTypedViews.StreamId("base-l");

        (await silo.Catalog.ReopenViewAsync(stream, SiloDrafts.ReferenceCapture("base-l"), "base-l", "rev-1", CancellationToken.None)
            .ConfigureAwait(false)).Reopenable.Should().BeTrue();
        foreach (var (otherStream, reference) in new[]
                 {
                     (SiloTypedViews.StreamId("base-other"), SiloDrafts.ReferenceCapture("base-l")), (stream, SiloDrafts.ReferenceCapture("base-other"))
                 })
        {
            var link = await silo.Catalog.ReopenViewAsync(otherStream, reference, "base-l", "rev-1", CancellationToken.None).ConfigureAwait(false);
            link.Reopenable.Should().BeFalse();
            link.ClosedOutcome.Should().Be(SiloCatalogIndex.LinkMismatch);
        }
    }
}
