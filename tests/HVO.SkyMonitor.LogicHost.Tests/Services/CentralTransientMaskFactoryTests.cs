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
        fixture.Frame.Location = null;
        fixture.Frame.LocationEvidenceState = CentralCaptureLocationEvidenceState.Mismatch;
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

    [TestMethod]
    public async Task SweptPolicyUsesCaptureBoundDeploymentRatherThanRegistrationSnapshot()
    {
        await using var fixture = await Fixture.CreateAsync(latitude: 35.347, longitude: -113.878);
        var options = new CentralTransientOptions { ExposureIntegratedStarMask = true }.CreateExecutionOptions();
        var first = (await fixture.Factory.CreateAsync([fixture.Source], options, CancellationToken.None))!;
        var star = first.Single(mask => mask.Kind == TransientDetectorMaskKind.Star);
        Assert.IsTrue(Linear16MaskOperations.IsExcluded(star.Mask, 15, 15));
        var registration = await fixture.Db.DeviceRegistrations.SingleAsync();
        registration.ObservatoryLatitudeDegrees = -60;
        registration.ObservatoryLongitudeDegrees = 150;
        await fixture.Db.SaveChangesAsync();
        var second = (await fixture.Factory.CreateAsync([fixture.Source], options, CancellationToken.None))!;
        Assert.AreEqual(star.MaskIdentitySha256, second.Single(mask => mask.Kind == TransientDetectorMaskKind.Star).MaskIdentitySha256);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("unresolved")]
    [DataRow("mismatch")]
    [DataRow("pending")]
    [DataRow("descriptor")]
    [DataRow("authority")]
    [DataRow("interval")]
    [DataRow("hash")]
    [DataRow("deployment-missing")]
    [DataRow("source")]
    [DataRow("accuracy")]
    [DataRow("coordinates")]
    [DataRow("observatory-interval")]
    public async Task SweptPolicyRefusesMissingOrConflictingCaptureLocation(string defect)
    {
        await using var fixture = await Fixture.CreateAsync();
        var source = fixture.Source;
        var location = fixture.Frame.Location!;
        switch (defect)
        {
            case "missing": fixture.Frame.Location = null; break;
            case "unresolved": fixture.Frame.LocationEvidenceState = CentralCaptureLocationEvidenceState.ReportedUnresolved; break;
            case "mismatch": fixture.Frame.LocationEvidenceState = CentralCaptureLocationEvidenceState.Mismatch; break;
            case "pending": location.DeploymentLocation!.Status = DeploymentLocationResolutionStatus.Pending; break;
            case "descriptor": source = source with { Descriptor = source.Descriptor with { Location = source.Descriptor.Location! with { Version = 2 } } }; break;
            case "authority": location.DeploymentLocation!.RegistrationId = Guid.NewGuid(); break;
            case "interval": fixture.Frame.CapturedAtUtc = location.EffectiveFromUtc.AddTicks(-1); break;
            case "hash": location.DeploymentLocation!.CanonicalSha256 = new string('F', 64); break;
            case "deployment-missing": location.DeploymentLocation = null; location.DeviceDeploymentLocationVersionId = null; break;
            case "source": location.Source = "conflict"; break;
            case "accuracy": location.HorizontalAccuracyMeters = 10; break;
            case "coordinates": location.DeploymentLocation!.LatitudeDegrees = double.NaN; break;
            case "observatory-interval": location.DeploymentLocation!.ObservatoryLocationVersion!.SupersededAtUtc = fixture.Frame.CapturedAtUtc; break;
        }
        await fixture.Db.SaveChangesAsync();
        Assert.IsNull(await fixture.Factory.CreateAsync([source],
            new CentralTransientOptions { ExposureIntegratedStarMask = true }.CreateExecutionOptions(), CancellationToken.None));
    }

    [TestMethod]
    public async Task SweptPolicyValidatesLocationOfEverySourceInWindow()
    {
        await using var fixture = await Fixture.CreateAsync();
        var unboundFrame = new CentralFrame
        {
            RegistrationId = fixture.Frame.RegistrationId,
            CapturedAtUtc = fixture.Frame.CapturedAtUtc,
            DeviceRigProfile = fixture.Frame.DeviceRigProfile,
            SceneProvenanceJson = fixture.Frame.SceneProvenanceJson
        };
        var artifact = new CentralArtifact { Frame = unboundFrame, CentralFrameId = unboundFrame.Id };
        fixture.Db.Add(artifact);
        await fixture.Db.SaveChangesAsync();
        var second = fixture.Source with { CentralArtifactId = artifact.Id, Position = TransientTemporalPosition.NPlus1 };
        var options = new CentralTransientOptions { ExposureIntegratedStarMask = true }.CreateExecutionOptions();
        Assert.IsNotNull(await fixture.Factory.CreateAsync([fixture.Source], options, CancellationToken.None));
        Assert.IsNull(await fixture.Factory.CreateAsync([fixture.Source, second], options, CancellationToken.None));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required ApplicationDbContext Db { get; init; }
        public required CentralFrame Frame { get; init; }
        public required SceneProvenance Scene { get; init; }
        public required CentralTransientMaskFactory Factory { get; init; }
        public required CentralTransientMaskSource Source { get; init; }

        public static async Task<Fixture> CreateAsync(int candidateCount = 1, double latitude = 0, double longitude = 0)
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
            var locationSnapshot = DeploymentLocationSnapshot.Create("capture-location", 1, "fixture", null,
                manifest.Descriptor.Timing.ExposureStartedUtc.AddMinutes(-1), null, latitude, longitude, 1000, "UTC");
            var descriptor = manifest.Descriptor with
            {
                Location = locationSnapshot.ToProvenance(),
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
                clock.CelestialMidpointUtc, latitude, longitude), clock.CelestialMidpointUtc);
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
                CapturedAtUtc = descriptor.Timing.ExposureStartedUtc,
                LocationEvidenceState = CentralCaptureLocationEvidenceState.ReportedResolved,
                RegistrationId = registration.Id,
                DeviceRigProfile = profile,
                DeviceRigProfileId = profile.Id,
                SceneProvenanceJson = JsonSerializer.Serialize(scene)
            };
            var deployment = new DeviceDeploymentLocationVersion
            {
                ObservatoryLocationVersion = new()
                {
                    EffectiveFromUtc = locationSnapshot.EffectiveFromUtc,
                    LatitudeDegrees = latitude,
                    LongitudeDegrees = longitude
                },
                RegistrationId = registration.Id,
                Registration = registration,
                LocationId = locationSnapshot.LocationId,
                Version = locationSnapshot.Version,
                CanonicalSha256 = locationSnapshot.CanonicalSha256,
                Source = locationSnapshot.Source,
                EffectiveFromUtc = locationSnapshot.EffectiveFromUtc,
                LatitudeDegrees = latitude,
                LongitudeDegrees = longitude,
                ElevationMeters = locationSnapshot.ElevationMeters,
                TimeZoneId = locationSnapshot.TimeZoneId,
                Status = DeploymentLocationResolutionStatus.Acknowledged
            };
            frame.Location = new CentralCaptureLocation
            {
                CentralFrame = frame,
                CentralFrameId = frame.Id,
                DeviceDeploymentLocationVersionId = deployment.Id,
                DeploymentLocation = deployment,
                LocationId = locationSnapshot.LocationId,
                Version = locationSnapshot.Version,
                Source = locationSnapshot.Source,
                EffectiveFromUtc = locationSnapshot.EffectiveFromUtc
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
