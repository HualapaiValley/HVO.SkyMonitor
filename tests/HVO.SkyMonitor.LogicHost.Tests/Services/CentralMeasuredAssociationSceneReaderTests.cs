using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralMeasuredAssociationSceneReaderTests
{
    [TestMethod]
    public async Task VerifiedSceneCarriesTheFrozenIdentityAndThePayloadChecksum()
    {
        var (scene, payload, reference) = await CreateAsync(resolvedSun: false).ConfigureAwait(false);

        var verified = CentralMeasuredAssociationSceneReader.Verify(reference, payload);

        Assert.IsNull(verified.FailureReasonCode);
        var input = verified.Input!;
        Assert.AreEqual(BuiltInProcessingRecipes.MeasuredStellarAssociationsSceneInputName, input.Name);
        Assert.AreEqual(ProcessingAuxiliaryInputKind.CanonicalJson, input.Kind);
        Assert.AreEqual(ProjectedSceneV1.CurrentSchemaVersion, input.SchemaVersion);
        Assert.AreEqual(scene.SceneIdentitySha256, input.IdentitySha256);
        Assert.AreEqual(ProcessingIdentity.ComputePayloadSha256(payload), input.ChecksumSha256);
        CollectionAssert.AreEqual(payload, input.Payload.ToArray());
        Assert.AreEqual(CentralMeasuredAssociationSceneReader.CreateIdentityInput(reference),
            input with { Payload = default, ChecksumSha256 = null },
            "the payload and checksum never enter the identity-bearing auxiliary");
    }

    [TestMethod]
    public async Task PayloadThatDoesNotMatchTheFrozenChecksumFailsWithTheChecksumReason()
    {
        var (_, payload, reference) = await CreateAsync(resolvedSun: false).ConfigureAwait(false);

        var verified = CentralMeasuredAssociationSceneReader.Verify(
            reference with { ChecksumSha256 = new string('0', 64) }, payload);

        Assert.IsNull(verified.Input);
        Assert.AreEqual(CentralMeasuredAssociationSceneReader.ChecksumMismatchReasonCode, verified.FailureReasonCode);
    }

    [TestMethod]
    public async Task SceneWhoseRecomputedIdentityDiffersFromTheFrozenIdentityFailsWithTheIdentityReason()
    {
        var (scene, payload, reference) = await CreateAsync(resolvedSun: false).ConfigureAwait(false);
        var (_, otherPayload, _) = await CreateAsync(resolvedSun: false, calibrationVersion: "calibration-v2")
            .ConfigureAwait(false);
        var otherChecksum = ProcessingIdentity.ComputePayloadSha256(otherPayload);

        // The checksum check passes in every case below, so only the independent identity check can reject them.
        foreach (var (name, candidateReference, candidatePayload) in new[]
                 {
                     ("frozen-identity", reference with { ContentIdentitySha256 = new string('A', 64) }, payload),
                     ("other-scene", reference with { ChecksumSha256 = otherChecksum }, otherPayload),
                     ("source", reference with
                     {
                         Source = scene.Source with { ArtifactId = Guid.NewGuid() }
                     }, payload),
                     ("unparseable", reference with
                     {
                         ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256("{}"u8.ToArray())
                     }, "{}"u8.ToArray())
                 })
        {
            var verified = CentralMeasuredAssociationSceneReader.Verify(candidateReference, candidatePayload);

            Assert.IsNull(verified.Input, name);
            Assert.AreEqual(CentralMeasuredAssociationSceneReader.IdentityMismatchReasonCode,
                verified.FailureReasonCode, name);
        }
    }

    [TestMethod]
    public async Task ResolvedFootprintSceneIsNotTheCurrentSchemaTheAssociationNodeBinds()
    {
        var (_, payload, reference) = await CreateAsync(resolvedSun: true).ConfigureAwait(false);

        var verified = CentralMeasuredAssociationSceneReader.Verify(reference, payload);

        Assert.IsNull(verified.Input);
        Assert.AreEqual(CentralMeasuredAssociationSceneReader.IdentityMismatchReasonCode, verified.FailureReasonCode);
    }

    private static async Task<(ProjectedSceneV1 Scene, byte[] Payload, CentralProjectedSceneReference Reference)>
        CreateAsync(bool resolvedSun, string calibrationVersion = "calibration-v1")
    {
        var raw = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono8, 2, 2, 2, [1, 2, 3, 4]);
        var utc = raw.Descriptor.Timing.ExposureStartedUtc;
        var catalog = new InMemoryCelestialCatalog([
            new CelestialCatalogObject("zenith", "Zenith", AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15, 0, 1)
        ]);
        var visible = await (resolvedSun
            ? new VisibleSceneBuilder(catalog, null, new AstronomyEnginePlanetEphemeris())
            : new VisibleSceneBuilder(catalog)).BuildAsync(new VisibleSceneRequest(utc, new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.Perspective, 1, 1, 1, 1, 2, 2,
                ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 90),
            new CatalogQuery(6, 10), new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"),
                new string('C', 64), "test", "v1"), projectionVersion: "perspective-v1",
            solarSystemBodies: resolvedSun ? [SolarSystemBody.Sun] : null)).ConfigureAwait(false);
        if (resolvedSun)
        {
            visible = visible.WithResolvedBodies([new SolarDiskAppearance(SolarSystemBody.Sun, utc,
                new AltAzPoint(90, 0), 10, 0, 1, 0, 149600000)]);
        }
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(2, 2),
            new ProjectedSceneSource(raw.Descriptor.Capture.CaptureId, raw.Descriptor.Artifact.ArtifactId,
                raw.IdempotencyKey), calibrationVersion, visible.Request.ProjectionVersion);
        var payload = ProjectedSceneJson.Serialize(scene);
        var reference = new CentralProjectedSceneReference(
            Guid.NewGuid(), Guid.NewGuid(), raw.Descriptor.Capture.CaptureId,
            ProcessingIdentity.ComputePayloadSha256(payload), scene.SceneIdentitySha256, scene.Source, "scene",
            new string('D', 64));
        return (scene, payload, reference);
    }
}
