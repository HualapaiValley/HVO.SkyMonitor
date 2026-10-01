using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralTransientMaskFactoryTests
{
    [TestMethod]
    public async Task SweptPolicyUsesKnownCaptureClockAndIgnoresPoisonedObjectTruth()
    {
        await using var fixture = await Fixture.CreateAsync();
        var options = new CentralTransientOptions { ExposureIntegratedStarMask = true }.CreateExecutionOptions();
        var first = (await fixture.Factory.CreateAsync([fixture.Source], options, CancellationToken.None))!;
        var star = first.Single(mask => mask.Kind == TransientDetectorMaskKind.Star);
        Assert.AreEqual(StellarExposureMask.AlgorithmVersion, star.Algorithm.Name);
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(star.Mask, 15, 15));
        Assert.IsFalse(Linear16MaskOperations.IsExcluded(star.Mask, 3, 3));
        fixture.Frame.SceneProvenanceJson = JsonSerializer.Serialize(fixture.Scene with
        {
            Objects = [new("different-truth", "different-truth", 25, 25, -20)]
        });
        await fixture.Db.SaveChangesAsync();
        var second = (await fixture.Factory.CreateAsync([fixture.Source], options, CancellationToken.None))!;
        Assert.AreEqual(star.MaskIdentitySha256, second.Single(mask => mask.Kind == TransientDetectorMaskKind.Star).MaskIdentitySha256);
    }

    [TestMethod]
    public async Task LegacyDurablePolicyReproducesBytesEvenWhenLiveHostOptedIntoSweptPolicy()
    {
        await using var fixture = await Fixture.CreateAsync();
        var frozen = CentralTransientExecutionOptionsJson.Serialize(new CentralTransientOptions().CreateExecutionOptions());
        var options = CentralTransientExecutionOptionsJson.Deserialize(frozen.Json);
        var first = (await fixture.Factory.CreateAsync([fixture.Source], options, CancellationToken.None))!;
        Assert.AreEqual("catalog-projected-star-mask", first.Single(mask => mask.Kind == TransientDetectorMaskKind.Star).Algorithm.Name);
        _ = new CentralTransientOptions { ExposureIntegratedStarMask = true }.CreateExecutionOptions();
        fixture.Frame.SceneProvenanceJson = "invalid-irrelevant-renderer-truth";
        await fixture.Db.SaveChangesAsync();
        var second = (await fixture.Factory.CreateAsync([fixture.Source],
            CentralTransientExecutionOptionsJson.Deserialize(frozen.Json), CancellationToken.None))!;
        CollectionAssert.AreEqual(first.Select(mask => mask.MaskIdentitySha256).ToArray(),
            second.Select(mask => mask.MaskIdentitySha256).ToArray());
        CollectionAssert.AreEqual(first.SelectMany(mask => mask.Mask.Bits.ToArray()).ToArray(),
            second.SelectMany(mask => mask.Mask.Bits.ToArray()).ToArray());
    }

    [TestMethod]
    public async Task IncompleteCatalogOrConflictingClockRefusesRatherThanPublishingPartialMasks()
    {
        await using var fixture = await Fixture.CreateAsync(candidateCount: 2);
        var bounded = new CentralTransientOptions { ExposureIntegratedStarMask = true, StarMaximumResults = 1 }
            .CreateExecutionOptions();
        Assert.IsNull(await fixture.Factory.CreateAsync([fixture.Source], bounded, CancellationToken.None));
        var sufficient = new CentralTransientOptions { ExposureIntegratedStarMask = true, StarMaximumResults = 2 }
            .CreateExecutionOptions();
        Assert.IsNotNull(await fixture.Factory.CreateAsync([fixture.Source], sufficient, CancellationToken.None));
        fixture.Frame.SceneProvenanceJson = JsonSerializer.Serialize(fixture.Scene with
        {
            VirtualExposure = fixture.Scene.VirtualExposure! with
            {
                CelestialEndUtc = fixture.Scene.VirtualExposure.CelestialEndUtc.AddTicks(1)
            }
        });
        await fixture.Db.SaveChangesAsync();
        Assert.IsNull(await fixture.Factory.CreateAsync([fixture.Source], sufficient, CancellationToken.None));
        Assert.IsNull(await fixture.Factory.CreateAsync([fixture.Source],
            sufficient with { MaskPolicy = (CentralTransientMaskPolicyV1)99 }, CancellationToken.None));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required ApplicationDbContext Db { get; init; }
        public required CentralFrame Frame { get; init; }
        public required SceneProvenance Scene { get; init; }
        public required CentralTransientMaskFactory Factory { get; init; }
        public required CentralTransientMaskSource Source { get; init; }

        public static async Task<Fixture> CreateAsync(int candidateCount = 1)
        {
            var rig = ProcessingConformanceFixture.CameraConfig.Rig with
            {
                Sensor = ProcessingConformanceFixture.CameraConfig.Rig.Sensor with { WidthPixels = 32, HeightPixels = 32 },
                Optics = new("EquidistantFisheye", 0, 180, 0, LensKind.Fisheye, 16, 16, 15,
                    CalibrationVersion: "fixture-optics-v1")
            };
            var rigSha = RigProjectionContextFactory.CreateProfileHashSha256(rig);
            var payload = new byte[2048];
            var manifest = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono16, 32, 32, 64, payload);
            var maskSha = CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(new { mode = "none" }));
            var descriptor = manifest.Descriptor with
            {
                Profiles = manifest.Descriptor.Profiles with
                {
                    Rig = new("rig", rig.ProfileVersion, rigSha),
                    Mask = new("mask", "none-v1", maskSha)
                }
            };
            var clock = VirtualExposureProvenance.Create(descriptor.Timing.RequestedStartUtc, descriptor.Timing.RequestedStartUtc,
                descriptor.Timing.RequestedStartUtc.AddHours(-6), descriptor.Controls.EffectiveExposure,
                VirtualExposureTimeMapping.FixedCelestialUtc);
            var metadata = new CatalogMetadata("HYG", "fixture", new("https://astronexus.com/projects/hyg"), new string('B', 64), "CC BY-SA 4.0", "2");
            var horizontal = ProjectorFactory.Create(RigProjectionContextFactory.Create(rig)).Unproject(new(16, 16))!.Value;
            var j2000 = EquatorialPrecession.PrecessToJ2000(CoordinateTransforms.HorizontalToEquatorial(horizontal,
                clock.CelestialMidpointUtc, 0, 0), clock.CelestialMidpointUtc);
            var catalog = new InMemoryCelestialCatalog(Enumerable.Range(0, candidateCount).Select(index =>
                new CelestialCatalogObject($"star-{index}", "known", j2000.RightAscensionHours, j2000.DeclinationDegrees, 1)));
            var scene = new SceneProvenance("scene", rig.ProfileVersion, metadata.Name, metadata.Version, metadata.Checksum,
                "EquidistantFisheye", "projection-v1", "astronomy-v1", "sensor-v1",
                Objects: [new("poison", "poison", 3, 3, -20)], SceneUtc: clock.CelestialMidpointUtc, VirtualExposure: clock);
            var registration = new DeviceRegistration();
            var profile = new DeviceRigProfile
            {
                Registration = registration,
                RegistrationId = registration.Id,
                ConfigJson = JsonSerializer.Serialize(rig),
                ProfileSha256 = rigSha
            };
            var frame = new CentralFrame
            {
                RegistrationId = registration.Id,
                DeviceRigProfile = profile,
                DeviceRigProfileId = profile.Id,
                SceneProvenanceJson = JsonSerializer.Serialize(scene)
            };
            frame.Profiles.Add(new CentralCaptureProfile { Kind = CentralProfileKind.Rig, Sha256 = rigSha });
            var centralArtifact = new CentralArtifact { Frame = frame, CentralFrameId = frame.Id };
            var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
            db.AddRange(registration, centralArtifact);
            await db.SaveChangesAsync();
            var artifact = new ProcessingArtifact(descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, "fixture",
                new string('C', 64), "application/x-hvo-frame", descriptor.Layout, payload,
                descriptor.Timing.ExposureStartedUtc, descriptor.Controls.EffectiveExposure,
                ProcessingConformanceFixture.Compatibility with { Rig = rigSha }, CaptureSequence: 1,
                ObservationStartedUtc: descriptor.Timing.ExposureStartedUtc, ObservationEndedUtc: descriptor.Timing.ExposureEndedUtc);
            var evidence = new TransientSourceEvidenceReferenceV1(TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
                Guid.NewGuid(), new TransientWholeArtifactLocatorV1(TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                    TransientSourceLocatorKind.WholeArtifact, new(artifact.ArtifactId, artifact.Role, artifact.Variant,
                        RecipeIdentitySha256: artifact.RecipeIdentitySha256,
                        ChecksumSha256: Convert.ToHexString(SHA256.HashData(payload)))),
                descriptor.Timing.ExposureStartedUtc, descriptor.Timing.ExposureEndedUtc, TransientTimingQuality.Reported,
                new("fixture", "v1"));
            var created = TransientDetectorInputFactory.Create(artifact, evidence, new(0, ushort.MaxValue, ushort.MaxValue));
            Assert.IsTrue(created.Validation.IsValid, $"{created.Validation.ReasonCode}: {created.Validation.FieldPath}");
            return new Fixture
            {
                Db = db,
                Frame = frame,
                Scene = scene,
                Factory = new CentralTransientMaskFactory(db, catalog, new MetadataSource(metadata)),
                Source = new(TransientTemporalPosition.N, centralArtifact.Id, created.Input!, descriptor)
            };
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class MetadataSource(CatalogMetadata metadata) : ICelestialCatalogMetadataSource
    {
        public CatalogMetadata Metadata { get; } = metadata;
    }
}
