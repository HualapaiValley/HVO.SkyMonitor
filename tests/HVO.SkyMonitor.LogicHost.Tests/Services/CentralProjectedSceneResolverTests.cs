using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralProjectedSceneResolverTests
{
    [TestMethod]
    [DataRow(1, DisplayName = "projected-scene-v1")]
    [DataRow(2, DisplayName = "projected-scene-v2")]
    [DataRow(3, DisplayName = "projected-scene-v3 curated")]
    public async Task SelectionWaitsForRawThenFrozenSceneSurvivesRawExpiryAndRejectsChangedIdentity(int schema)
    {
        var resolvedSun = schema == 2;
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
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
        if (schema == 3)
        {
            var item = new DeepSkyObject("NGC0224", "NGC 224", "M31", "G",
                AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15, 0, "And",
                null, null, null, null, null, null, null, 31, null, null, null);
            var semantics = new DeepSkySemantics("OpenNGC", "v20260501", "36cb178a0f69dba8bfc03a99c10512831edf1c6b",
                new Uri("https://github.com/mattiaverga/OpenNGC"), "CC BY-SA 4.0", "equatorial-j2000-icrs-aligned",
                "J2000.0", "arcminute", "degrees-north-through-east-0-inclusive-to-180-exclusive",
                "1-widest-2-standard-3-narrowest", "b-mag-per-square-arcsecond-within-25-mag-isophote");
            visible = visible.WithCuratedDeepSky(new DeepSkyCatalog(semantics, [item],
                [new("NGC 224", item.Id, DeepSkyAliasKinds.Designation), new("M31", item.Id, DeepSkyAliasKinds.Messier)], [], []), ProjectedSceneDeepSkySelection.Default);
        }
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(2, 2),
            new ProjectedSceneSource(raw.Descriptor.Capture.CaptureId, raw.Descriptor.Artifact.ArtifactId,
                raw.IdempotencyKey), "calibration-v1", visible.Request.ProjectionVersion);
        Assert.AreEqual(schema switch
        {
            2 => ProjectedSceneV1.ResolvedFootprintSchemaVersion,
            3 => ProjectedSceneV1.DeepSkySchemaVersion,
            _ => ProjectedSceneV1.CurrentSchemaVersion
        }, scene.SchemaVersion);
        var provenance = new SceneProvenance(new string('D', 64), "rig", scene.Catalog.Name,
            scene.Catalog.Version, scene.Catalog.ChecksumSha256, "Perspective", scene.Projection.AlgorithmVersion,
            scene.AstronomyAlgorithmVersion, "sensor", SceneUtc: utc,
            EphemerisModelVersion: scene.EphemerisModelVersion).WithoutProjectedGeometry() with
        {
            ProjectedSceneSchemaVersion = scene.SchemaVersion
        };
        var payload = ProjectedSceneJson.Serialize(scene);
        var recipe = RecipeIdentityDescriptor.Create(BuiltInProcessingRecipes.ProjectedScene, "1.0.0", "fixture",
            JsonSerializer.SerializeToElement(new { }));
        var identity = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, "projected-scene",
            ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256, [raw.Descriptor.Artifact.ArtifactId]);
        var artifactDescriptor = new ArtifactDescriptor(ProcessingIdentity.CreateArtifactId(identity),
            FrameArtifactRole.Metadata, "scene", "projected-scene", utc, [raw.Descriptor.Artifact.ArtifactId],
            recipe, StructuredProcessingProductContracts.ProjectedSceneMediaType, PayloadChecksum.ComputeSha256(payload));
        var descriptor = new StructuredProcessingProductDescriptorV1(raw.Descriptor, artifactDescriptor, identity,
            [new("projected-scene", "fixture")], new("rig", "orientation", "calibration", "mask", "sensor", "night", "processing"),
            raw.Descriptor.Controls.EffectiveExposure.Ticks, payload.Length, ProcessingProductKind.Metadata,
            scene.SchemaVersion, scene.SceneIdentitySha256);
        var frame = new CentralFrame
        {
            FrameId = raw.Descriptor.Capture.CaptureId,
            SceneProvenanceJson = JsonSerializer.Serialize(provenance, JsonSerializerOptions.Web)
        };
        var artifact = new CentralArtifact
        {
            Frame = frame,
            CentralFrameId = frame.Id,
            ArtifactId = artifactDescriptor.ArtifactId,
            Role = FrameArtifactRole.Metadata,
            MediaType = artifactDescriptor.MediaType,
            ByteLength = payload.Length,
            ChecksumSha256 = artifactDescriptor.ChecksumSha256,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        artifact.StructuredProduct = new CentralStructuredProcessingProduct
        {
            Artifact = artifact,
            CentralArtifactId = artifact.Id,
            ContentIdentitySha256 = scene.SceneIdentitySha256,
            DescriptorJson = Encoding.UTF8.GetString(StructuredProcessingProductManifestJson.SerializeDescriptor(descriptor))
        };
        db.Add(artifact);
        await db.SaveChangesAsync().ConfigureAwait(false);
        var reader = new SceneReader(payload);
        var resolver = new CentralProjectedSceneResolver(db, reader);
        Assert.IsNull(await resolver.SelectAsync(frame, CancellationToken.None).ConfigureAwait(false));
        Assert.AreEqual(0, reader.ReadCount, "an early arrival waits without trying to freeze unauthenticated geometry");
        var rawArtifact = new CentralArtifact
        {
            Frame = frame,
            CentralFrameId = frame.Id,
            ArtifactId = raw.Descriptor.Artifact.ArtifactId,
            Role = FrameArtifactRole.Raw,
            IdempotencyKey = raw.IdempotencyKey,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        rawArtifact.ObjectState = CentralArtifactObjectState.Pending;
        db.Add(rawArtifact);
        await db.SaveChangesAsync().ConfigureAwait(false);
        Assert.IsNull(await resolver.SelectAsync(frame, CancellationToken.None).ConfigureAwait(false));
        rawArtifact.ObjectState = CentralArtifactObjectState.Available;
        await db.SaveChangesAsync().ConfigureAwait(false);
        var selected = await resolver.SelectAsync(frame, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(selected);
        if (schema == 3)
        {
            Assert.AreEqual(ProjectedSceneV1.DeepSkySchemaVersion, selected.Reference.PayloadSchemaVersion);
            Assert.IsNotNull(selected.PayloadInput);
            Assert.AreEqual(artifactDescriptor.ArtifactId, selected.PayloadInput.ArtifactId);
            await Assert.ThrowsExactlyAsync<CentralDerivativeJobStateException>(() => resolver.ResolveAsync(
                selected.Reference with { PayloadSchemaVersion = null }, CancellationToken.None)).ConfigureAwait(false);
        }
        else
        {
            Assert.IsNull(selected.PayloadInput);
            Assert.IsFalse(JsonSerializer.Serialize(selected.Reference, JsonSerializerOptions.Web)
                .Contains("payloadSchemaVersion", StringComparison.Ordinal), "Released legacy selectors keep the old JSON shape.");
        }
        var retention = new CentralArtifactRetentionReferences(db);
        Assert.IsTrue(await retention.IsHeldAsync(artifact.Id, CancellationToken.None).ConfigureAwait(false),
            "compact image consumers hold geometry independently of job lifetime");
        Assert.IsTrue(await retention.IsHeldOutsideTransientEventAsync(artifact.Id, Guid.NewGuid(), CancellationToken.None)
            .ConfigureAwait(false));
        rawArtifact.ObjectState = CentralArtifactObjectState.Expired;
        await db.SaveChangesAsync().ConfigureAwait(false);
        Assert.IsNull(await resolver.SelectAsync(frame, CancellationToken.None).ConfigureAwait(false),
            "an expired rejected intent is not authenticated source evidence");
        rawArtifact.RetentionDeletionToken = Guid.NewGuid();
        rawArtifact.StateReasonCode = "retention.expired";
        await db.SaveChangesAsync().ConfigureAwait(false);
        Assert.IsNull(await resolver.SelectAsync(frame, CancellationToken.None).ConfigureAwait(false),
            "retiring a never-authenticated pending intent cannot create source authority");
        rawArtifact.ObjectVerifiedAtUtc = DateTimeOffset.UnixEpoch;
        await db.SaveChangesAsync().ConfigureAwait(false);
        Assert.IsFalse(CentralProjectedSceneResolver.SourceEligibility(projectedScene: false).Compile()(rawArtifact),
            "ordinary pixel lineage still requires an available source object");
        var selectedAfterExpiry = await resolver.SelectAsync(frame, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(selectedAfterExpiry, "new work can freeze the authenticated source identity after pixel expiry");
        Assert.AreEqual(selected.Reference, selectedAfterExpiry.Reference);
        Assert.AreEqual(selected.Annotation.ProvenanceSha256, selectedAfterExpiry.Annotation.ProvenanceSha256);
        Assert.IsFalse(await retention.IsHeldAsync(artifact.Id, CancellationToken.None).ConfigureAwait(false),
            "expired consumers do not retain scene geometry forever");
        var pendingPreview = new CentralArtifact
        {
            Frame = frame,
            CentralFrameId = frame.Id,
            ArtifactId = Guid.NewGuid(),
            Role = FrameArtifactRole.Preview,
            ObjectState = CentralArtifactObjectState.Pending
        };
        db.Add(pendingPreview);
        await db.SaveChangesAsync().ConfigureAwait(false);
        Assert.IsTrue(await retention.IsHeldAsync(artifact.Id, CancellationToken.None).ConfigureAwait(false),
            "an accepted derivative waiting for its source is still a geometry consumer");
        frame.SceneProvenanceJson = JsonSerializer.Serialize(provenance with { ProjectedSceneSchemaVersion = null }, JsonSerializerOptions.Web);
        await db.SaveChangesAsync().ConfigureAwait(false);
        Assert.IsFalse(await retention.IsHeldAsync(artifact.Id, CancellationToken.None).ConfigureAwait(false),
            "legacy inline captures keep their existing independent retention behavior");
        frame.SceneProvenanceJson = JsonSerializer.Serialize(provenance, JsonSerializerOptions.Web);
        pendingPreview.ObjectState = CentralArtifactObjectState.Expired;
        await db.SaveChangesAsync().ConfigureAwait(false);
        var resolved = await resolver.ResolveAsync(selected.Reference, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(selected.Annotation.ProvenanceSha256, resolved.ProvenanceSha256);
        await Assert.ThrowsExactlyAsync<CentralArtifactIntegrityException>(() => resolver.ResolveAsync(
            selected.Reference with { ContentIdentitySha256 = new string('F', 64) }, CancellationToken.None)).ConfigureAwait(false);
        rawArtifact.ObjectState = CentralArtifactObjectState.Available;
        rawArtifact.IdempotencyKey = new string('E', 64);
        await db.SaveChangesAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CentralArtifactIntegrityException>(() => resolver.SelectAsync(
            frame, CancellationToken.None)).ConfigureAwait(false);
    }

    // Every edge stamps the rig calibration version as the scene's projection version while its provenance names the
    // rig projection algorithm, so a calibration other than the algorithm name must still select. A scene whose
    // projection version is neither the provenance algorithm nor its own calibration is a genuine mismatch, and the
    // calibration convention is honoured only for the rig projection algorithm that produces it.
    [TestMethod]
    [DataRow("virtual-fisheye-180-equidistant-v1", "virtual-fisheye-180-equidistant-v1", "virtual-fisheye-180-equidistant-v1",
        RigProjectionContextFactory.AlgorithmVersion, true, DisplayName = "edge convention with the sample calibration")]
    [DataRow("unspecified", "unspecified", "unspecified", RigProjectionContextFactory.AlgorithmVersion, true,
        DisplayName = "edge convention with the default calibration")]
    [DataRow("rig-projection-v2", "calibration-v1", "calibration-v1", RigProjectionContextFactory.AlgorithmVersion, true,
        DisplayName = "scene names the provenance algorithm")]
    [DataRow("rig-projection-v1", "calibration-v1", "calibration-v1", RigProjectionContextFactory.AlgorithmVersion, false,
        DisplayName = "different projection algorithm")]
    [DataRow("calibration-v1", "calibration-v1", "calibration-v2", RigProjectionContextFactory.AlgorithmVersion, false,
        DisplayName = "different calibration")]
    [DataRow("calibration-v1", "calibration-v1", null, RigProjectionContextFactory.AlgorithmVersion, false,
        DisplayName = "calibration convention without provenance calibration")]
    [DataRow("calibration-v1", "calibration-v1", "calibration-v1", "rig-projection-v1", false,
        DisplayName = "calibration convention from an unsupported provenance algorithm")]
    [DataRow("calibration-v1", "calibration-v1", "calibration-v1", "fixture-projection-v9", false,
        DisplayName = "calibration convention from an unknown provenance algorithm")]
    public async Task SelectionAcceptsTheEdgeProjectionVersionConventionAndRejectsAGenuineMismatch(
        string sceneProjectionVersion, string sceneCalibrationVersion, string? provenanceCalibrationVersion,
        string provenanceAlgorithmVersion, bool accepted)
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var raw = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono8, 2, 2, 2, [1, 2, 3, 4]);
        var utc = raw.Descriptor.Timing.ExposureStartedUtc;
        var scene = await CreateSceneAsync(raw, sceneProjectionVersion, sceneCalibrationVersion, resolvedSun: false)
            .ConfigureAwait(false);
        var provenance = new SceneProvenance(new string('D', 64), "rig", scene.Catalog.Name,
            scene.Catalog.Version, scene.Catalog.ChecksumSha256, "Perspective", provenanceAlgorithmVersion,
            scene.AstronomyAlgorithmVersion, "sensor", SceneUtc: utc, EphemerisModelVersion: scene.EphemerisModelVersion,
            ProjectionCalibrationVersion: provenanceCalibrationVersion).WithoutProjectedGeometry();
        var frame = await SeedAsync(db, raw, scene, provenance).ConfigureAwait(false);
        var resolver = new CentralProjectedSceneResolver(db, new SceneReader(ProjectedSceneJson.Serialize(scene)));

        if (!accepted)
        {
            var rejected = await Assert.ThrowsExactlyAsync<CentralArtifactIntegrityException>(() => resolver.SelectAsync(
                frame, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual("projected-scene.source-mismatch", rejected.ReasonCode);
            return;
        }
        var selected = await resolver.SelectAsync(frame, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(selected);
        var resolved = await resolver.ResolveAsync(selected.Reference, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(selected.Annotation.ProvenanceSha256, resolved.ProvenanceSha256);
    }

    // The capture provenance declares which retained scene schema the edge produced; a scene of the other schema
    // for the same capture is not the scene that capture described, in either direction.
    [TestMethod]
    [DataRow(false, SceneProvenance.RetainedProjectedSceneSchemaVersion, true, DisplayName = "v1 scene declared v1")]
    [DataRow(true, SceneProvenance.ResolvedFootprintProjectedSceneSchemaVersion, true, DisplayName = "v2 scene declared v2")]
    [DataRow(true, SceneProvenance.RetainedProjectedSceneSchemaVersion, false, DisplayName = "v2 scene declared v1")]
    [DataRow(false, SceneProvenance.ResolvedFootprintProjectedSceneSchemaVersion, false, DisplayName = "v1 scene declared v2")]
    [DataRow(false, "projected-scene-v9", false, DisplayName = "unknown declared schema")]
    public async Task SelectionRequiresTheDeclaredProjectedSceneSchema(bool resolvedSun, string declaredSchema, bool accepted)
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var raw = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono8, 2, 2, 2, [1, 2, 3, 4]);
        var scene = await CreateSceneAsync(raw, "perspective-v1", "calibration-v1", resolvedSun).ConfigureAwait(false);
        Assert.AreEqual(resolvedSun ? ProjectedSceneV1.ResolvedFootprintSchemaVersion : ProjectedSceneV1.CurrentSchemaVersion,
            scene.SchemaVersion);
        var provenance = new SceneProvenance(new string('D', 64), "rig", scene.Catalog.Name,
            scene.Catalog.Version, scene.Catalog.ChecksumSha256, "Perspective", scene.Projection.AlgorithmVersion,
            scene.AstronomyAlgorithmVersion, "sensor", SceneUtc: scene.EffectiveUtc,
            EphemerisModelVersion: scene.EphemerisModelVersion).WithoutProjectedGeometry() with
        {
            ProjectedSceneSchemaVersion = declaredSchema
        };
        var frame = await SeedAsync(db, raw, scene, provenance).ConfigureAwait(false);
        var resolver = new CentralProjectedSceneResolver(db, new SceneReader(ProjectedSceneJson.Serialize(scene)));

        if (!accepted)
        {
            var rejected = await Assert.ThrowsExactlyAsync<CentralArtifactIntegrityException>(() => resolver.SelectAsync(
                frame, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual("projected-scene.source-mismatch", rejected.ReasonCode);
            return;
        }
        Assert.IsNotNull(await resolver.SelectAsync(frame, CancellationToken.None).ConfigureAwait(false));
    }

    // Edges declare the constellation selection as configured: VirtualSky upper-cases and de-duplicates it in
    // configuration order, and the capture stager passes it raw. The scene records the selection normalized, so
    // either declaration of the scene's selection selects, and a different selection is still a mismatch.
    [TestMethod]
    [DataRow(new[] { "ORI", "UMA", "UMI", "CAS", "CYG", "LYR" }, true, DisplayName = "VirtualSky shape")]
    [DataRow(new[] { "ori", "Uma", "UMI", "cas", "ORI", "cyg", "LYR", "lyr" }, true, DisplayName = "stager shape")]
    [DataRow(new[] { "CAS", "CYG", "LYR", "ORI", "UMA", "UMI" }, true, DisplayName = "already normalized")]
    [DataRow(new[] { "ORI", "UMA", "UMI", "CAS", "CYG" }, false, DisplayName = "missing constellation")]
    [DataRow(new[] { "ORI", "UMA", "UMI", "CAS", "CYG", "LYR", "AND" }, false, DisplayName = "extra constellation")]
    [DataRow(new[] { "ORI", "UMA", "UMI", "CAS", "CYG", "LYR", " " }, false, DisplayName = "blank constellation")]
    [DataRow(new[] { "ORI", "UMA", "UMI", "CAS", "CYG", "LYR", null }, false, DisplayName = "null constellation")]
    public async Task SelectionAcceptsTheDeclaredConstellationsInConfigurationOrderAndCase(string?[] declared, bool accepted)
    {
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var raw = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono8, 2, 2, 2, [1, 2, 3, 4]);
        var scene = await CreateSceneAsync(raw, "perspective-v1", "calibration-v1", resolvedSun: false,
            constellationIds: ["ORI", "UMA", "UMI", "CAS", "CYG", "LYR"]).ConfigureAwait(false);
        CollectionAssert.AreEqual(new[] { "CAS", "CYG", "LYR", "ORI", "UMA", "UMI" }, scene.Selection.ConstellationIds.ToArray());
        var topology = scene.ConstellationTopology!;
        var provenance = new SceneProvenance(new string('D', 64), "rig", scene.Catalog.Name,
            scene.Catalog.Version, scene.Catalog.ChecksumSha256, "Perspective", scene.Projection.AlgorithmVersion,
            scene.AstronomyAlgorithmVersion, "sensor", SceneUtc: scene.EffectiveUtc,
            EphemerisModelVersion: scene.EphemerisModelVersion, ConstellationTopologyVersion: topology.Version,
            ConstellationTopologySourceUrl: topology.SourceUrl, ConstellationTopologySha256: topology.SourceSha256,
            ConstellationTopologyLicense: topology.License,
            ConstellationTopologyPreprocessingVersion: topology.PreprocessingVersion,
            ConstellationIds: declared.Select(static id => id!).ToArray()).WithoutProjectedGeometry();
        var frame = await SeedAsync(db, raw, scene, provenance).ConfigureAwait(false);
        var resolver = new CentralProjectedSceneResolver(db, new SceneReader(ProjectedSceneJson.Serialize(scene)));

        if (!accepted)
        {
            var rejected = await Assert.ThrowsExactlyAsync<CentralArtifactIntegrityException>(() => resolver.SelectAsync(
                frame, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual("projected-scene.source-mismatch", rejected.ReasonCode);
            return;
        }
        Assert.IsNotNull(await resolver.SelectAsync(frame, CancellationToken.None).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(3, false)]
    [DataRow(0, true)]
    [DataRow(1, true)]
    [DataRow(3, true)]
    public async Task SqliteSceneProbesRemainBoundedAndRejectAmbiguity(int count, bool validateInline)
    {
        await using var fixture = new SqliteRowLimitFixture();
        var db = fixture.Db;
        var raw = ArtifactManifestFixture.CreateManifest(CameraPixelFormat.Mono8, 2, 2, 2, [1, 2, 3, 4]);
        var scene = await CreateSceneAsync(raw, "perspective-v1", "calibration-v1", resolvedSun: false);
        var inline = new SceneProvenance(new string('D', 64), "rig", scene.Catalog.Name,
            scene.Catalog.Version, scene.Catalog.ChecksumSha256, "Perspective", scene.Projection.AlgorithmVersion,
            scene.AstronomyAlgorithmVersion, "sensor", SceneUtc: scene.EffectiveUtc,
            EphemerisModelVersion: scene.EphemerisModelVersion);
        var frame = await SeedAsync(db, raw, scene, inline.WithoutProjectedGeometry());
        var first = await db.CentralArtifacts.SingleAsync(item => item.Role == FrameArtifactRole.Metadata);
        if (count == 0)
        {
            db.Remove(first.StructuredProduct!);
            db.Remove(first);
            await db.SaveChangesAsync();
        }
        for (var index = 1; index < count; index++)
            fixture.Insert(new CentralArtifact
            {
                CentralFrameId = frame.Id,
                ArtifactId = Guid.NewGuid(),
                Role = FrameArtifactRole.Metadata,
                MediaType = first.MediaType,
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete
            });
        var reader = new SceneReader(ProjectedSceneJson.Serialize(scene));
        var resolver = new CentralProjectedSceneResolver(db, reader);
        fixture.Queries.Commands.Clear();
        if (validateInline)
        {
            if (count > 1)
                await Assert.ThrowsExactlyAsync<ArtifactIngestConflictException>(
                    () => resolver.ValidateInlineGeometryAsync(frame, inline, CancellationToken.None));
            else if (count == 0)
                await Assert.ThrowsExactlyAsync<ArtifactSceneReferencePendingException>(
                    () => resolver.ValidateInlineGeometryAsync(frame, inline, CancellationToken.None));
            else
                await resolver.ValidateInlineGeometryAsync(frame, inline with
                {
                    Objects = scene.Objects.Select(item => new ProjectedObjectProvenance(item.Id, item.DisplayName,
                        item.Pixel.X, item.Pixel.Y, item.Magnitude)).ToArray()
                }, CancellationToken.None);
        }
        else if (count > 1)
        {
            var error = await Assert.ThrowsExactlyAsync<CentralArtifactIntegrityException>(
                () => resolver.SelectAsync(frame, CancellationToken.None));
            Assert.AreEqual("projected-scene.ambiguous", error.ReasonCode);
        }
        else if (count == 0)
            Assert.IsNull(await resolver.SelectAsync(frame, CancellationToken.None));
        else
            Assert.IsNotNull(await resolver.SelectAsync(frame, CancellationToken.None));
        if (count != 1) Assert.AreEqual(0, reader.ReadCount);
        var probes = fixture.Queries.Commands.Where(sql => sql.Contains("LIMIT", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(1, probes);
        foreach (var sql in probes)
            StringAssert.Contains(sql, "ORDER BY");
    }

    private static async Task<ProjectedSceneV1> CreateSceneAsync(
        ArtifactManifestV2 raw, string projectionVersion, string calibrationVersion, bool resolvedSun,
        IReadOnlyList<string>? constellationIds = null)
    {
        var utc = raw.Descriptor.Timing.ExposureStartedUtc;
        var catalog = new InMemoryCelestialCatalog([
            new CelestialCatalogObject("zenith", "Zenith", AstronomyTime.LocalMeanSiderealDegrees(utc, 0) / 15, 0, 1)
        ]);
        // A selection needs topology provenance; the scene needs no segments to record the selection.
        var topology = constellationIds is null ? null : new InMemoryConstellationTopology([],
            new ConstellationTopologyMetadata("fixture", "1", new Uri("https://example.test/constellations"),
                new string('E', 64), "test", "v1"));
        var visible = await (resolvedSun
            ? new VisibleSceneBuilder(catalog, topology, new AstronomyEnginePlanetEphemeris())
            : new VisibleSceneBuilder(catalog, topology)).BuildAsync(new VisibleSceneRequest(utc, new ObserverLocation(0, 0, 0),
            new ProjectionContext(ProjectionModel.Perspective, 1, 1, 1, 1, 2, 2,
                ProjectionAperture.Rectangular, BoresightAltitudeDegrees: 90),
            new CatalogQuery(6, 10), new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"),
                new string('C', 64), "test", "v1"), projectionVersion: projectionVersion,
            constellationIds: constellationIds,
            solarSystemBodies: resolvedSun ? [SolarSystemBody.Sun] : null)).ConfigureAwait(false);
        if (resolvedSun)
        {
            visible = visible.WithResolvedBodies([new SolarDiskAppearance(SolarSystemBody.Sun, utc,
                new AltAzPoint(90, 0), 10, 0, 1, 0, 149600000)]);
        }
        return ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(2, 2),
            new ProjectedSceneSource(raw.Descriptor.Capture.CaptureId, raw.Descriptor.Artifact.ArtifactId,
                raw.IdempotencyKey), calibrationVersion, visible.Request.ProjectionVersion);
    }

    private static async Task<CentralFrame> SeedAsync(
        ApplicationDbContext db, ArtifactManifestV2 raw, ProjectedSceneV1 scene, SceneProvenance provenance)
    {
        var utc = raw.Descriptor.Timing.ExposureStartedUtc;
        var payload = ProjectedSceneJson.Serialize(scene);
        var recipe = RecipeIdentityDescriptor.Create(BuiltInProcessingRecipes.ProjectedScene, "1.0.0", "fixture",
            JsonSerializer.SerializeToElement(new { }));
        var identity = ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Metadata, "projected-scene",
            ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256, [raw.Descriptor.Artifact.ArtifactId]);
        var artifactDescriptor = new ArtifactDescriptor(ProcessingIdentity.CreateArtifactId(identity),
            FrameArtifactRole.Metadata, "scene", "projected-scene", utc, [raw.Descriptor.Artifact.ArtifactId],
            recipe, StructuredProcessingProductContracts.ProjectedSceneMediaType, PayloadChecksum.ComputeSha256(payload));
        var descriptor = new StructuredProcessingProductDescriptorV1(raw.Descriptor, artifactDescriptor, identity,
            [new("projected-scene", "fixture")], new("rig", "orientation", "calibration", "mask", "sensor", "night", "processing"),
            raw.Descriptor.Controls.EffectiveExposure.Ticks, payload.Length, ProcessingProductKind.Metadata,
            scene.SchemaVersion, scene.SceneIdentitySha256);
        var frame = new CentralFrame
        {
            FrameId = raw.Descriptor.Capture.CaptureId,
            SceneProvenanceJson = JsonSerializer.Serialize(provenance, JsonSerializerOptions.Web)
        };
        var artifact = new CentralArtifact
        {
            Frame = frame,
            CentralFrameId = frame.Id,
            ArtifactId = artifactDescriptor.ArtifactId,
            Role = FrameArtifactRole.Metadata,
            MediaType = artifactDescriptor.MediaType,
            ByteLength = payload.Length,
            ChecksumSha256 = artifactDescriptor.ChecksumSha256,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };
        artifact.StructuredProduct = new CentralStructuredProcessingProduct
        {
            Artifact = artifact,
            CentralArtifactId = artifact.Id,
            ContentIdentitySha256 = scene.SceneIdentitySha256,
            DescriptorJson = Encoding.UTF8.GetString(StructuredProcessingProductManifestJson.SerializeDescriptor(descriptor))
        };
        db.Add(artifact);
        db.Add(new CentralArtifact
        {
            Frame = frame,
            CentralFrameId = frame.Id,
            ArtifactId = raw.Descriptor.Artifact.ArtifactId,
            Role = FrameArtifactRole.Raw,
            IdempotencyKey = raw.IdempotencyKey,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
        return frame;
    }

    private sealed class SceneReader(byte[] payload) : ICentralArtifactObjectReader
    {
        internal int ReadCount { get; private set; }
        public Task<CentralArtifactObjectSnapshot> VerifyAsync(CentralArtifact artifact, CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(new CentralArtifactObjectSnapshot("scene", "generation", payload.Length));
        }
        public Task<bool> IsCurrentGenerationAsync(CentralArtifact artifact, string storageETag, CancellationToken cancellationToken)
            => Task.FromResult(true);
        public Task CopyToAsync(CentralArtifactObjectSnapshot snapshot, Stream destination,
            CentralArtifactByteRange? range, CancellationToken cancellationToken)
            => destination.WriteAsync(payload, cancellationToken).AsTask();
    }
}
