using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class LogicHostIngestPerformanceTests
{
    private const string Issues437432Baseline = "5febc8efaa4b4b6e5e45eb45e9df0c55525bfdde";
    private const string Issues437432Product = "b6c357101d314c20b87b54c92da97d7482eb1b48";
    private const string Issues437432ResultSchema = "issues-437-432-layered-performance-v2";
    private const int Issues437432Width = 3096;
    private const int Issues437432Height = 2080;
    private const int Issues437432LayerCount = 5;
    private const int Issues437432FlattenedCombinationCount = 1 << Issues437432LayerCount;
    private static readonly JsonSerializerOptions Issues437432JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [TestMethod]
    public async Task LayeredIngestPresentationAndMaterialization_RecordsProvenanceLockedEvidence()
    {
        var evidenceMode = Environment.GetEnvironmentVariable("HVO_ISSUES437432_EVIDENCE") == "1";
        var developmentMode = Environment.GetEnvironmentVariable("HVO_ISSUES437432_DEVELOPMENT") == "1";
        if (!evidenceMode && !developmentMode)
        {
            Assert.Inconclusive(
                "Set HVO_ISSUES437432_DEVELOPMENT=1 for a reduced run or use scripts/evidence:issues-437-432.");
        }

        var warmups = evidenceMode ? 5 : 1;
        var measurements = evidenceMode ? 30 : 2;
        var root = GetRepositoryRoot();
        var provenance = ValidateIssues437432Provenance(root, evidenceMode);
        if (evidenceMode)
        {
            Assert.AreEqual("Release", Issues437432BuildConfiguration);
            Assert.IsTrue(GCSettings.IsServerGC, "Claimable evidence requires DOTNET_gcServer=1.");
        }

        var fixture = AssemblyHooks.Fixture;
        var workload = CreateWorkload("W2", Issues437432Width, Issues437432Height, CameraPixelFormat.BayerRggb16);
        await SeedWorkloadAsync(fixture, workload).ConfigureAwait(false);
        var rawUpload = CreateUpload(workload, 437432);
        await GrantIssues437432OperatorAccessAsync(fixture, workload.DeviceId).ConfigureAwait(false);

        using var ingestClient = fixture.Factory.CreateClient();
        var systemToken = await HttpHelpers.GetClientCredentialsTokenAsync(
            ingestClient,
            "/connect/token",
            TestClients.SystemCameraAgent.ClientId,
            TestClients.SystemCameraAgent.ClientSecret,
            string.Join(' ', TestClients.SystemCameraAgent.Scopes)).ConfigureAwait(false);
        SetAuthorization(ingestClient, systemToken.AccessToken);
        var rawResponse = await SendUploadAsync(
            ingestClient, rawUpload, workload.Payload, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Accepted, rawResponse.StatusCode, rawResponse.Body);
        AssertAcknowledgement(rawUpload, rawResponse.Body);

        var fixturePresentation = await CreateIssues437432PresentationFixtureAsync(
            fixture, ingestClient, rawUpload).ConfigureAwait(false);
        using var ownerClient = await ArtifactRetrievalTests.CreateUserClientAsync(
            TestUsers.Operator.Username, TestUsers.Operator.Password).ConfigureAwait(false);
        using var protocol = new ProtocolCounter(fixture.MinioEndpoint);

        var structuredIngest = await MeasureIssues437432UploadsAsync(
            ingestClient,
            protocol,
            warmups,
            measurements,
            index => CreateIssues437432StructuredMeasurement(rawUpload.Manifest, index)).ConfigureAwait(false);
        var flattenedIngest = await MeasureIssues437432UploadsAsync(
            ingestClient,
            protocol,
            warmups,
            measurements,
            index => CreateIssues437432FlattenedMeasurement(rawUpload.Manifest, index)).ConfigureAwait(false);

        var structuredRetrieval = await MeasureIssues437432RetrievalAsync(
            ownerClient, protocol, workload.DevicePublicId, structuredIngest.Artifacts, warmups).ConfigureAwait(false);
        var flattenedRetrieval = await MeasureIssues437432RetrievalAsync(
            ownerClient, protocol, workload.DevicePublicId, flattenedIngest.Artifacts, warmups).ConfigureAwait(false);

        var coldPresentation = await MeasureIssues437432PresentationAsync(
            fixture,
            ownerClient,
            protocol,
            fixturePresentation,
            warmups,
            measurements,
            clearBeforeEach: true).ConfigureAwait(false);
        var cachedPresentation = await MeasureIssues437432PresentationAsync(
            fixture,
            ownerClient,
            protocol,
            fixturePresentation,
            warmups,
            measurements,
            clearBeforeEach: false).ConfigureAwait(false);
        var conditionalPresentation = await ValidateIssues437432ConditionalAsync(
            fixture, ownerClient, fixturePresentation, coldPresentation.ETag).ConfigureAwait(false);
        var singleFlight = await MeasureIssues437432SingleFlightAsync(
            fixture, ownerClient, protocol, fixturePresentation).ConfigureAwait(false);

        var principal = await CreateIssues437432OperatorPrincipalAsync(fixture).ConfigureAwait(false);
        var firstMaterialization = await MeasureIssues437432MaterializationAsync(
            fixture, protocol, fixturePresentation, principal).ConfigureAwait(false);
        Assert.AreEqual(CentralPresentationMaterializationStatus.Saved, firstMaterialization.Status);
        Assert.IsFalse(firstMaterialization.Receipt!.Replayed);
        var replayMaterialization = await MeasureIssues437432MaterializationAsync(
            fixture, protocol, fixturePresentation, principal).ConfigureAwait(false);
        Assert.AreEqual(CentralPresentationMaterializationStatus.Saved, replayMaterialization.Status);
        Assert.IsTrue(replayMaterialization.Receipt!.Replayed);
        Assert.AreEqual(firstMaterialization.Receipt.ArtifactId, replayMaterialization.Receipt.ArtifactId);
        Assert.AreEqual(firstMaterialization.Receipt.ChecksumSha256, replayMaterialization.Receipt.ChecksumSha256);

        var materializedBytes = await ReadIssues437432ArtifactAsync(
            ownerClient,
            firstMaterialization.Receipt.ContentPath,
            firstMaterialization.Receipt.ChecksumSha256,
            firstMaterialization.Receipt.ByteLength).ConfigureAwait(false);
        var expectedLineage = structuredIngest.Artifacts.Concat(flattenedIngest.Artifacts)
            .GroupBy(static artifact => artifact.ArtifactId)
            .ToDictionary(static group => group.Key, static group => group.First().SourceArtifactIds);
        expectedLineage[fixturePresentation.BaseArtifactId] = [rawUpload.Manifest.Descriptor.Artifact.ArtifactId];
        foreach (var layerArtifactId in fixturePresentation.LayerArtifactIds)
        {
            expectedLineage[layerArtifactId] = [rawUpload.Manifest.Descriptor.Artifact.ArtifactId];
        }
        expectedLineage[fixturePresentation.ManifestArtifactId] = fixturePresentation.LayerArtifactIds
            .Prepend(fixturePresentation.BaseArtifactId).ToArray();
        expectedLineage[firstMaterialization.Receipt.ArtifactId] = fixturePresentation.LayerArtifactIds
            .Prepend(fixturePresentation.ManifestArtifactId)
            .Prepend(fixturePresentation.BaseArtifactId)
            .ToArray();
        var correctness = await ValidateIssues437432CorrectnessAsync(
            fixture,
            expectedLineage,
            firstMaterialization.Receipt,
            fixturePresentation).ConfigureAwait(false);
        var retained = await ReadIssues437432RetainedBytesAsync(
            fixture, fixturePresentation, flattenedIngest.Artifacts[0].ByteLength,
            flattenedIngest.Artifacts.Count).ConfigureAwait(false);

        var outputDirectory = Issues437432OutputDirectory(root, provenance.EvidenceHead, evidenceMode);
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "issues-437-432-layered-performance.json");
        Assert.IsFalse(File.Exists(outputPath), "Evidence output is create-only.");
        var flattenedMaskCoverage = evidenceMode
            ? "all 32 synthetic combination masks"
            : $"{flattenedIngest.Artifacts.Count} synthetic combination masks";
        var result = new
        {
            schemaVersion = Issues437432ResultSchema,
            issues = new[] { 437, 432 },
            provenance = new
            {
                baselineCommit = Issues437432Baseline,
                provenance.BaselineTree,
                productCommit = Issues437432Product,
                provenance.ProductTree,
                provenance.EvidenceHead,
                provenance.EvidenceTree,
                provenance.Branch,
                provenance.Dirty,
                provenance.Trial,
                provenance.BuildReceiptPath,
                provenance.BuildReceiptSha256,
                provenance.BaselineToProductDiffSha256,
                provenance.ProductToEvidenceDiffSha256,
                provenance.SourceInventorySha256,
                claimable = evidenceMode
            },
            environment = new
            {
                operatingSystem = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                cpu = ReadCpuModel(),
                processorCount = Environment.ProcessorCount,
                totalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                runtime = RuntimeInformation.FrameworkDescription,
                sdk = ReadPinnedSdkVersion(root),
                configuration = Issues437432BuildConfiguration,
                serverGc = GCSettings.IsServerGC,
                host = "ASP.NET Core TestServer",
                sqlServer = IntegrationTestFixture.SqlServerImage,
                minio = IntegrationTestFixture.MinioImage,
                redis = IntegrationTestFixture.RedisImage,
                storage = new DriveInfo(Path.GetPathRoot(root)!).DriveFormat,
                containerMode = "SQL Server, MinIO, and Redis Testcontainers; LogicHost in-process"
            },
            workload = new
            {
                id = "W2-shaped-layered-central-supplemental",
                canonicalW2PayloadThroughput = false,
                qualification = "W2 BayerRggb16 raw ingress is canonical geometry and byte length. Presentation uses a compact deterministic W2 Mono8 JPEG base, sparse structured layers, and distinct packed Mono8 flattened comparator artifacts; it is representative/supplemental rather than canonical 19,319,040-byte RGB24 preview throughput.",
                width = Issues437432Width,
                height = Issues437432Height,
                rawPixelFormat = CameraPixelFormat.BayerRggb16.ToString(),
                rawBytes = workload.Payload.LongLength,
                rawSha256 = workload.ChecksumSha256,
                presentationBaseMediaType = JpegImageCodec.MediaType,
                presentationBaseBytes = fixturePresentation.BaseBytes,
                presentationBaseSha256 = fixturePresentation.BaseChecksumSha256,
                selectableLayers = Issues437432LayerCount,
                flattenedStorageCounterfactualCombinations = Issues437432FlattenedCombinationCount,
                actualFlattenedArtifacts = flattenedIngest.Artifacts.Count,
                warmups,
                measuredOperations = measurements,
                concurrency = new[] { 1, 8 },
                fixtureSeed = 2025,
                recipes = new[]
                {
                    "presentation-layer/1.0.0/issues-437-432-v1",
                    "overlay-manifest/1.0.0/issues-437-432-v1",
                    "flattened-comparator/1.0.0/issues-437-432-v1",
                    "presentation-materialization/1.0.0/central-packed-presentation-v1"
                }
            },
            method = new
            {
                command = "DOTNET_gcServer=1 scripts/evidence:issues-437-432",
                latency = evidenceMode
                    ? "Nearest-rank median/p95/maximum over 30 independent operations after five warmups."
                    : "Reduced development run; p95 is intentionally N/A because fewer than 30 operations were measured.",
                resources = "Process TotalProcessorTime, 100 ms System.Runtime allocation-rate samples, and 10 ms process RSS sampling inherited from LogicHostIngestPerformanceTests.",
                protocol = "One aggregate EF Core SQL command/transaction and MinIO method/Content-Length snapshot is retained per measured phase as an N+1/regression diagnostic. Counts are not per-call data or performance acceptance budgets.",
                boundaries = "Ingest spans authenticated idempotent multipart request through durable acknowledgement; retrieval spans authenticated GET through checksum validation; presentation spans authenticated GET through complete SVG body; materialization spans production service admission through durable ingest acknowledgement.",
                flattenedComparator = $"Each warmup and measured operation creates a distinct complete-lineage W2 Mono8 packed output, retaining {flattenedIngest.Artifacts.Count} benchmark artifacts across {flattenedMaskCoverage}. The deployment storage counterfactual separately multiplies one representative exact output byte length by the 32 selectable-layer combinations.",
                presentation = "Only cold and cached 5+30 distributions are measured. One conditional request and one eight-way cold request are functional correctness checks; the latter reports elapsed total only.",
                materialization = "Only first save and one replay are measured. Production semaphore bounds and focused integration coverage replace synthetic concurrent-admission benchmarking.",
                redis = "Redis is disposable cache infrastructure. This harness neither listens to cache metrics nor measures Redis wire behavior; existing cache/fallback integration tests provide functional validation."
            },
            measurements = new
            {
                ingest = new { structured = structuredIngest, flattened = flattenedIngest },
                retrieval = new { structured = structuredRetrieval, flattened = flattenedRetrieval },
                presentation = new { cold = coldPresentation, cached = cachedPresentation },
                materialization = new { first = firstMaterialization, replay = replayMaterialization }
            },
            storageCounterfactual = retained,
            functional = new { conditionalPresentation, singleFlight },
            backlog = new
            {
                finalMeasuredArtifactBacklog = correctness.FinalBacklog,
                materializationDurableBacklog = "N/A: #427 durable materialization jobs are unavailable in this product commit.",
                synchronousAdmission = "N/A: no durable queue exists before #427. CentralPresentationMaterializationGate is a production singleton semaphore with capacity one; focused functional tests cover admission behavior."
            },
            correctness = new
            {
                correctness.AllMeasuredArtifactsAvailableAndComplete,
                correctness.CompleteResolvedLineage,
                exactIngestAndRetrievalChecksums = true,
                deterministicSvg = coldPresentation.DeterministicBytes && cachedPresentation.DeterministicBytes && singleFlight.DeterministicBytes,
                exactSvgSha256 = fixturePresentation.SvgChecksumSha256,
                exactEtag = coldPresentation.ETag,
                etag304 = conditionalPresentation.StatusCode == HttpStatusCode.NotModified && conditionalPresentation.BodyBytes == 0,
                singleFlight = singleFlight.SharedResult && singleFlight.SingleGenerationObserved,
                materializationChecksum = firstMaterialization.Receipt.ChecksumSha256,
                materializationOutputIdentity = firstMaterialization.Receipt.OutputIdentitySha256,
                materializationOutputBytes = materializedBytes.LongLength,
                materializationOutputSha256 = Convert.ToHexString(SHA256.HashData(materializedBytes)),
                materializationLineage = correctness.MaterializationLineage,
                replayConverged = replayMaterialization.Receipt.Replayed,
                zeroFinalPendingOrQuarantine = correctness.FinalBacklog.Count == 0
            },
            result = new
            {
                comparison = new
                {
                    sameBinary = true,
                    structuredVersusFlattenedIngestMedianPercent = Issues437432PercentChange(
                        flattenedIngest.Distribution.MedianMilliseconds,
                        structuredIngest.Distribution.MedianMilliseconds),
                    structuredVersusFlattenedRetrievalMedianPercent = Issues437432PercentChange(
                        flattenedRetrieval.Distribution.MedianMilliseconds,
                        structuredRetrieval.Distribution.MedianMilliseconds),
                    retainedBytesPercent = Issues437432PercentChange(
                        retained.CounterfactualFlattenedBytes,
                        retained.ActualLayeredBytes),
                    baselineTiming = "N/A: issues #437/#432 introduce the structured central path; the nearest flattened path is measured in the same product binary. The baseline commit is provenance/ancestry, not substituted timing evidence."
                },
                correctnessGatePassed = correctness.FinalBacklog.Count == 0 &&
                    correctness.CompleteResolvedLineage && conditionalPresentation.StatusCode == HttpStatusCode.NotModified &&
                    singleFlight.SharedResult && singleFlight.SingleGenerationObserved &&
                    replayMaterialization.Receipt.Replayed,
                interpretation = "Checksums, ETag/304, deterministic SVG, lineage, durable convergence, and functional single-flight are pass/fail. Aggregate protocol counts are N+1/regression diagnostics, not acceptance budgets. Timing and resources are machine-specific comparison evidence.",
                residualRisk = "TestServer excludes kernel TCP/TLS; SQL and MinIO containers are outside process CPU/RSS; Redis wire bytes and container filesystem I/O are unavailable; the compact Mono8 presentation does not establish canonical W2 RGB24 payload throughput; #427 durable materialization backlog cannot be measured."
            },
            recordedAtUtc = DateTimeOffset.UtcNow
        };
        var json = JsonSerializer.Serialize(result, Issues437432JsonOptions);
        Assert.IsFalse(json.Contains(fixture.SqlServerConnectionString, StringComparison.Ordinal));
        Assert.IsFalse(json.Contains(IntegrationTestFixture.MinioSecretKey, StringComparison.Ordinal));
        await using (var stream = new FileStream(
                         outputPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, result, Issues437432JsonOptions).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
        Console.WriteLine($"Issues #437/#432 evidence: {outputPath}");
    }

    private static async Task GrantIssues437432OperatorAccessAsync(
        IntegrationTestFixture fixture,
        string deviceId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var observatoryId = await db.DeviceRegistrations.Where(item => item.DeviceId == deviceId)
            .Select(static item => item.ObservatoryId).SingleAsync().ConfigureAwait(false);
        var userId = await db.Users.Where(item => item.UserName == TestUsers.Operator.Username)
            .Select(static item => item.Id).SingleAsync().ConfigureAwait(false);
        if (!await db.ObservatoryMemberships.AnyAsync(item =>
                item.ObservatoryId == observatoryId && item.UserId == userId).ConfigureAwait(false))
        {
            db.ObservatoryMemberships.Add(new ObservatoryMembership
            {
                ObservatoryId = observatoryId,
                UserId = userId,
                Role = ObservatoryMembershipRole.Owner,
                AddedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    private static async Task<Issues437432PresentationFixture> CreateIssues437432PresentationFixtureAsync(
        IntegrationTestFixture fixture,
        HttpClient ingestClient,
        ExpectedUpload rawUpload)
    {
        var rawManifest = rawUpload.Manifest;
        var rawArtifactId = rawManifest.Descriptor.Artifact.ArtifactId;
        var sourceIdentity = rawManifest.Descriptor.Artifact.ChecksumSha256;
        var basePixels = new byte[checked(Issues437432Width * Issues437432Height)];
        for (var index = 0; index < basePixels.Length; index += 4093)
        {
            basePixels[index] = (byte)(index % 251);
        }
        var baseBytes = JpegImageCodec.EncodeMono8ToJpeg(
            Issues437432Width, Issues437432Height, basePixels, quality: 80);
        var baseChecksum = Convert.ToHexString(SHA256.HashData(baseBytes));
        var recipe = RecipeIdentityDescriptor.Create(
            BuiltInProcessingRecipes.EncodedPreview,
            "1.0.0",
            JpegImageCodec.AlgorithmVersion,
            JsonSerializer.SerializeToElement(new { quality = 80 }));
        var baseOutputIdentity = ProcessingIdentity.CreateOutputIdentity(
            FrameArtifactRole.Preview,
            "presentation-base",
            ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256,
            [rawArtifactId]);
        var baseArtifactId = ProcessingIdentity.CreateArtifactId(baseOutputIdentity);
        var baseLayout = new FrameLayoutDescriptor(
            Issues437432Width,
            Issues437432Height,
            Issues437432Width,
            CameraPixelFormat.Mono8,
            FrameByteOrder.NotApplicable,
            8,
            8,
            FrameSamplePacking.ByteAligned,
            ColorFilterArrayPattern.None,
            0,
            255,
            basePixels.LongLength);
        var compatibility = new PresentationCompatibilityDescriptor(
            Issues437432Width,
            Issues437432Height,
            PresentationProcessingProducts.ComputeLayoutIdentity(baseLayout),
            sourceIdentity);
        Guid centralCaptureId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var raw = await db.CentralArtifacts.Include(static item => item.Frame)
                .SingleAsync(item => item.ArtifactId == rawArtifactId).ConfigureAwait(false);
            centralCaptureId = raw.CentralFrameId;
            var baseRow = new CentralArtifact
            {
                CentralFrameId = raw.CentralFrameId,
                DevicePublicId = raw.DevicePublicId,
                ArtifactId = baseArtifactId,
                Role = FrameArtifactRole.Preview,
                RecipeVersion = "encoded-preview-v1",
                ManifestSchemaVersion = ArtifactManifestV2.CurrentSchemaVersion,
                MediaType = JpegImageCodec.MediaType,
                ByteLength = baseBytes.LongLength,
                ChecksumSha256 = baseChecksum,
                StorageReference = $"minio://{ArtifactBucket}/issues-437-432/{baseArtifactId:D}.jpg",
                ReceivedAtUtc = DateTimeOffset.UtcNow,
                IdempotencyKey = Convert.ToHexString(SHA256.HashData(baseArtifactId.ToByteArray())),
                SourceId = "issues-437-432",
                Variant = "presentation-base",
                CreatedUtc = rawManifest.Descriptor.Timing.ReadoutCompletedUtc.AddSeconds(1),
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete,
                ReconciledAtUtc = DateTimeOffset.UtcNow,
                Layout = new CentralArtifactLayout
                {
                    Width = baseLayout.Width,
                    Height = baseLayout.Height,
                    StrideBytes = baseLayout.StrideBytes,
                    PixelFormat = baseLayout.PixelFormat.ToString(),
                    ByteOrder = baseLayout.ByteOrder.ToString(),
                    SampleDepthBits = baseLayout.SampleDepthBits,
                    ContainerDepthBits = baseLayout.ContainerDepthBits,
                    Packing = baseLayout.Packing.ToString(),
                    CfaPattern = baseLayout.CfaPattern.ToString(),
                    BlackLevel = baseLayout.BlackLevel,
                    WhiteLevel = baseLayout.WhiteLevel,
                    ByteLength = baseLayout.ByteLength
                },
                Recipe = new CentralArtifactRecipe
                {
                    Name = recipe.Name,
                    SemanticVersion = recipe.SemanticVersion,
                    ImplementationVersion = recipe.ImplementationVersion,
                    OptionsJson = recipe.Options.GetRawText(),
                    OptionsSha256 = recipe.OptionsSha256
                }
            };
            baseRow.Sources.Add(new CentralArtifactSource
            {
                Ordinal = 0,
                SourceArtifactId = rawArtifactId,
                ResolvedCentralArtifactId = raw.Id
            });
            db.CentralArtifacts.Add(baseRow);
            await db.SaveChangesAsync().ConfigureAwait(false);
            await using var baseStream = new MemoryStream(baseBytes, writable: false);
            await scope.ServiceProvider.GetRequiredService<IMinioClient>().PutObjectAsync(new PutObjectArgs()
                .WithBucket(ArtifactBucket)
                .WithObject($"issues-437-432/{baseArtifactId:D}.jpg")
                .WithStreamData(baseStream)
                .WithObjectSize(baseBytes.LongLength)
                .WithContentType(JpegImageCodec.MediaType)).ConfigureAwait(false);
        }

        var layerContracts = new List<PresentationLayerV1>(Issues437432LayerCount);
        var layerArtifactIds = new List<Guid>(Issues437432LayerCount);
        for (var index = 0; index < Issues437432LayerCount; index++)
        {
            var layer = PresentationLayerPayloadJson.Create(
                sourceIdentity,
                Issues437432Width,
                Issues437432Height,
                markers:
                [
                    new PresentationMarkerV1(
                        new PixelPoint(256 + index * 487, 300 + index * 271),
                        4 + index,
                        new PresentationColor((byte)(80 + index * 30), (byte)(220 - index * 20), (byte)(100 + index * 20)))
                ]);
            var payload = PresentationLayerPayloadJson.Serialize(layer);
            var upload = CreateIssues437432StructuredManifest(
                rawManifest,
                payload,
                layer.ContentIdentitySha256,
                PresentationLayerPayloadJson.MediaType,
                PresentationLayerPayloadV1.CurrentSchemaVersion,
                $"fixture-layer-{index}",
                [rawArtifactId]);
            await SendIssues437432UploadAsync(ingestClient, new(
                upload.Descriptor.Artifact.ArtifactId,
                StructuredProcessingProductManifestJson.Serialize(upload),
                payload,
                upload.Descriptor.Artifact.MediaType,
                upload.Descriptor.Artifact.ChecksumSha256,
                upload.Descriptor.Artifact.SourceArtifactIds.ToArray(),
                upload.IdempotencyKey)).ConfigureAwait(false);
            var contract = LayeredPresentationJson.CreateLayer(
                $"selectable-layer-{index}",
                new(upload.Descriptor.Artifact.ArtifactId, layer.ContentIdentitySha256,
                    PresentationLayerPayloadJson.MediaType, compatibility),
                sourceIdentity,
                PresentationCoordinateSpace.ScenePixels,
                GroupedSvgPresentationRenderer.RendererVersion,
                "issues-437-432-style-v1",
                index * 10,
                PresentationBlendMode.Normal,
                1_000_000,
                true,
                JsonSerializer.SerializeToElement(new { index }));
            layerContracts.Add(contract);
            layerArtifactIds.Add(upload.Descriptor.Artifact.ArtifactId);
        }

        var manifest = LayeredPresentationJson.CreateManifest(
            new(baseArtifactId, baseOutputIdentity, JpegImageCodec.MediaType, compatibility),
            sourceIdentity,
            layerContracts);
        var manifestBytes = LayeredPresentationJson.Serialize(manifest);
        var manifestSources = layerArtifactIds.Prepend(baseArtifactId).ToArray();
        var manifestUpload = CreateIssues437432StructuredManifest(
            rawManifest,
            manifestBytes,
            manifest.ManifestIdentitySha256,
            PresentationProcessingProducts.ManifestMediaType,
            OverlayManifestV1.CurrentSchemaVersion,
            "fixture-overlay-manifest",
            manifestSources);
        await SendIssues437432UploadAsync(ingestClient, new(
            manifestUpload.Descriptor.Artifact.ArtifactId,
            StructuredProcessingProductManifestJson.Serialize(manifestUpload),
            manifestBytes,
            manifestUpload.Descriptor.Artifact.MediaType,
            manifestUpload.Descriptor.Artifact.ChecksumSha256,
            manifestUpload.Descriptor.Artifact.SourceArtifactIds.ToArray(),
            manifestUpload.IdempotencyKey)).ConfigureAwait(false);
        var rendered = GroupedSvgPresentationRenderer.Render(
            manifest,
            layerContracts.Select((_, index) => PresentationLayerPayloadJson.Parse(
                CreateIssues437432FixtureLayerBytes(sourceIdentity, index)).Payload!).ToArray(),
            baseChecksum);
        return new(
            centralCaptureId,
            baseArtifactId,
            baseBytes.LongLength,
            baseChecksum,
            manifestUpload.Descriptor.Artifact.ArtifactId,
            manifestUpload.Descriptor.Artifact.ChecksumSha256,
            manifest.ManifestIdentitySha256,
            layerArtifactIds.ToArray(),
            layerContracts.Select(static layer => layer.LayerIdentitySha256).ToArray(),
            rendered.SvgChecksumSha256);
    }

    private static byte[] CreateIssues437432FixtureLayerBytes(string sourceIdentity, int index)
    {
        var layer = PresentationLayerPayloadJson.Create(
            sourceIdentity,
            Issues437432Width,
            Issues437432Height,
            markers:
            [
                new PresentationMarkerV1(
                    new PixelPoint(256 + index * 487, 300 + index * 271),
                    4 + index,
                    new PresentationColor((byte)(80 + index * 30), (byte)(220 - index * 20), (byte)(100 + index * 20)))
            ]);
        return PresentationLayerPayloadJson.Serialize(layer);
    }

    private static Issues437432Upload CreateIssues437432StructuredMeasurement(
        ArtifactManifestV2 rawManifest,
        int index)
    {
        var layer = PresentationLayerPayloadJson.Create(
            rawManifest.Descriptor.Artifact.ChecksumSha256,
            Issues437432Width,
            Issues437432Height,
            markers:
            [
                new PresentationMarkerV1(
                    new PixelPoint(100 + index * 37 % 2800, 100 + index * 53 % 1800),
                    3,
                    new PresentationColor(180, 210, 240))
            ]);
        var payload = PresentationLayerPayloadJson.Serialize(layer);
        var manifest = CreateIssues437432StructuredManifest(
            rawManifest,
            payload,
            layer.ContentIdentitySha256,
            PresentationLayerPayloadJson.MediaType,
            PresentationLayerPayloadV1.CurrentSchemaVersion,
            $"measurement-layer-{index}",
            [rawManifest.Descriptor.Artifact.ArtifactId]);
        return new(
            manifest.Descriptor.Artifact.ArtifactId,
            StructuredProcessingProductManifestJson.Serialize(manifest),
            payload,
            manifest.Descriptor.Artifact.MediaType,
            manifest.Descriptor.Artifact.ChecksumSha256,
            manifest.Descriptor.Artifact.SourceArtifactIds.ToArray(),
            manifest.IdempotencyKey);
    }

    private static Issues437432Upload CreateIssues437432FlattenedMeasurement(
        ArtifactManifestV2 rawManifest,
        int index)
    {
        var mask = index % Issues437432FlattenedCombinationCount;
        var payload = new byte[checked(Issues437432Width * Issues437432Height)];
        for (var layer = 0; layer < Issues437432LayerCount; layer++)
        {
            if ((mask & (1 << layer)) != 0)
            {
                var offset = (300 + layer * 271) * Issues437432Width + 256 + layer * 487;
                payload[offset] = (byte)(100 + layer * 25);
            }
        }
        var layout = new FrameLayoutDescriptor(
            Issues437432Width,
            Issues437432Height,
            Issues437432Width,
            CameraPixelFormat.Mono8,
            FrameByteOrder.NotApplicable,
            8,
            8,
            FrameSamplePacking.ByteAligned,
            ColorFilterArrayPattern.None,
            0,
            255,
            payload.LongLength);
        var recipe = RecipeIdentityDescriptor.Create(
            "flattened-comparator",
            "1.0.0",
            "issues-437-432-v1",
            JsonSerializer.SerializeToElement(new { mask, operation = index }));
        var sources = new[] { rawManifest.Descriptor.Artifact.ArtifactId };
        var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
            FrameArtifactRole.AnnotatedPreview,
            $"flattened-{index}",
            ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256,
            sources);
        var artifact = new ArtifactDescriptor(
            ProcessingIdentity.CreateArtifactId(outputIdentity),
            FrameArtifactRole.AnnotatedPreview,
            "flattened-comparator",
            $"flattened-{index}",
            rawManifest.Descriptor.Timing.ReadoutCompletedUtc.AddSeconds(10 + index),
            sources,
            recipe,
            "application/x-hvo-packed-image",
            Convert.ToHexString(SHA256.HashData(payload)));
        var descriptor = rawManifest.Descriptor with { Layout = layout, Artifact = artifact };
        var manifest = new ArtifactManifestV2(
            ArtifactManifestV2.CurrentSchemaVersion,
            descriptor,
            $"issues-437-432/flattened-{index:D2}.bin",
            ProducerStepId: "flattened-comparator");
        Assert.IsTrue(manifest.Validate().IsValid);
        return new(
            artifact.ArtifactId,
            CaptureContractJson.Serialize(manifest),
            payload,
            artifact.MediaType,
            artifact.ChecksumSha256,
            sources,
            manifest.IdempotencyKey);
    }

    private static StructuredProcessingProductManifestV1 CreateIssues437432StructuredManifest(
        ArtifactManifestV2 rawManifest,
        byte[] payload,
        string contentIdentity,
        string mediaType,
        string productSchemaVersion,
        string variant,
        IReadOnlyList<Guid> sourceIds)
    {
        var recipe = RecipeIdentityDescriptor.Create(
            mediaType == PresentationProcessingProducts.ManifestMediaType
                ? PresentationProcessingProducts.ManifestRecipeName
                : "presentation-layer",
            "1.0.0",
            "issues-437-432-v1",
            JsonSerializer.SerializeToElement(new { variant }));
        var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
            FrameArtifactRole.Metadata,
            variant,
            ProcessingIdentity.CreateRecipeIdentity(recipe).IdentitySha256,
            sourceIds);
        var artifact = new ArtifactDescriptor(
            ProcessingIdentity.CreateArtifactId(outputIdentity),
            FrameArtifactRole.Metadata,
            "issues-437-432",
            variant,
            rawManifest.Descriptor.Timing.ReadoutCompletedUtc.AddSeconds(2),
            sourceIds,
            recipe,
            mediaType,
            Convert.ToHexString(SHA256.HashData(payload)));
        var manifest = new StructuredProcessingProductManifestV1(
            StructuredProcessingProductManifestV1.CurrentSchemaVersion,
            new StructuredProcessingProductDescriptorV1(
                rawManifest.Descriptor,
                artifact,
                outputIdentity,
                [new("issues-437-432", "v1")],
                new("rig", "orientation", "calibration", "mask", "sensor", "night", "processing"),
                TimeSpan.FromSeconds(1).Ticks,
                payload.LongLength,
                ProcessingProductKind.Metadata,
                productSchemaVersion,
                contentIdentity),
            $"issues-437-432/{variant}.json",
            ProducerStepId: "issues-437-432");
        Assert.IsTrue(manifest.Validate().IsValid);
        return manifest;
    }

    private static async Task<Issues437432PathMeasurement> MeasureIssues437432UploadsAsync(
        HttpClient client,
        ProtocolCounter protocol,
        int warmups,
        int measurements,
        Func<int, Issues437432Upload> create)
    {
        var artifacts = new List<Issues437432Artifact>(warmups + measurements);
        for (var index = 0; index < warmups; index++)
        {
            artifacts.Add(await SendIssues437432UploadAsync(client, create(index)).ConfigureAwait(false));
        }
        StabilizeGc();
        using var resources = new ResourceSampler();
        protocol.Start();
        var samples = new List<double>(measurements);
        long requestBytes = 0;
        long responseBytes = 0;
        var started = Stopwatch.GetTimestamp();
        for (var index = warmups; index < warmups + measurements; index++)
        {
            var operationStarted = Stopwatch.GetTimestamp();
            var artifact = await SendIssues437432UploadAsync(client, create(index)).ConfigureAwait(false);
            samples.Add(Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds);
            requestBytes += artifact.RequestBytes;
            responseBytes += artifact.ResponseBytes;
            artifacts.Add(artifact);
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        var observed = protocol.Stop();
        await SettleIssues437432DevelopmentSamplerAsync().ConfigureAwait(false);
        var resource = await resources.StopAsync().ConfigureAwait(false);
        Assert.AreEqual(
            artifacts.Count,
            artifacts.Select(static artifact => artifact.ArtifactId).Distinct().Count(),
            "Every ingest sample must create a distinct artifact.");
        return new(
            CreateIssues437432Distribution(samples, elapsed),
            artifacts.ToArray(),
            artifacts.Skip(warmups).Sum(static item => item.ByteLength),
            requestBytes,
            responseBytes,
            observed,
            resource);
    }

    private static async Task<Issues437432Artifact> SendIssues437432UploadAsync(
        HttpClient client,
        Issues437432Upload upload)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(upload.ManifestBytes)
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
        }, "manifest");
        content.Add(new ByteArrayContent(upload.Payload)
        {
            Headers = { ContentType = new MediaTypeHeaderValue(upload.MediaType) }
        }, "payload", "artifact.bin");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1.0/artifacts") { Content = content };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", upload.IdempotencyKey);
        var requestBytes = content.Headers.ContentLength
            ?? throw new InvalidOperationException("Multipart Content-Length was unavailable.");
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var responseBytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, Encoding.UTF8.GetString(responseBytes));
        var acknowledgement = JsonSerializer.Deserialize<ArtifactUploadAcknowledgement>(
            responseBytes, HttpHelpers.DefaultJsonOptions);
        Assert.IsNotNull(acknowledgement);
        acknowledgement.Validate();
        Assert.AreEqual(upload.ArtifactId, acknowledgement.ArtifactId);
        Assert.AreEqual(upload.ChecksumSha256, acknowledgement.ChecksumSha256, ignoreCase: true);
        Assert.AreEqual(upload.Payload.LongLength, acknowledgement.ByteLength);
        return new(
            upload.ArtifactId,
            upload.ChecksumSha256,
            upload.Payload.LongLength,
            upload.SourceArtifactIds,
            requestBytes,
            responseBytes.LongLength);
    }

    private static async Task<Issues437432PathMeasurement> MeasureIssues437432RetrievalAsync(
        HttpClient client,
        ProtocolCounter protocol,
        Guid devicePublicId,
        IReadOnlyList<Issues437432Artifact> artifacts,
        int warmups)
    {
        foreach (var artifact in artifacts.Take(warmups))
        {
            _ = await ReadIssues437432ArtifactAsync(client, Issues437432ContentPath(devicePublicId, artifact.ArtifactId),
                artifact.ChecksumSha256, artifact.ByteLength).ConfigureAwait(false);
        }
        StabilizeGc();
        using var resources = new ResourceSampler();
        protocol.Start();
        var measured = artifacts.Skip(warmups).ToArray();
        var samples = new List<double>(measured.Length);
        long responseBytes = 0;
        var started = Stopwatch.GetTimestamp();
        foreach (var artifact in measured)
        {
            var operationStarted = Stopwatch.GetTimestamp();
            var bytes = await ReadIssues437432ArtifactAsync(
                client,
                Issues437432ContentPath(devicePublicId, artifact.ArtifactId),
                artifact.ChecksumSha256,
                artifact.ByteLength).ConfigureAwait(false);
            samples.Add(Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds);
            responseBytes += bytes.LongLength;
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        var observed = protocol.Stop();
        await SettleIssues437432DevelopmentSamplerAsync().ConfigureAwait(false);
        var resource = await resources.StopAsync().ConfigureAwait(false);
        return new(
            CreateIssues437432Distribution(samples, elapsed),
            artifacts,
            responseBytes,
            0,
            responseBytes,
            observed,
            resource);
    }

    private static async Task<byte[]> ReadIssues437432ArtifactAsync(
        HttpClient client,
        string contentPath,
        string checksumSha256,
        long byteLength)
    {
        using var response = await client.GetAsync(new Uri(contentPath, UriKind.Relative)).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(byteLength, bytes.LongLength);
        Assert.AreEqual(checksumSha256, Convert.ToHexString(SHA256.HashData(bytes)), ignoreCase: true);
        Assert.AreEqual(checksumSha256, response.Headers.GetValues("X-Artifact-SHA256").Single(), ignoreCase: true);
        return bytes;
    }

    private static string Issues437432ContentPath(Guid devicePublicId, Guid artifactId) =>
        $"/api/v1.0/devices/{devicePublicId:D}/artifacts/{artifactId:D}/content";

    private static async Task<Issues437432PresentationMeasurement> MeasureIssues437432PresentationAsync(
        IntegrationTestFixture fixture,
        HttpClient client,
        ProtocolCounter protocol,
        Issues437432PresentationFixture presentation,
        int warmups,
        int measurements,
        bool clearBeforeEach)
    {
        for (var index = 0; index < warmups; index++)
        {
            _ = await SendIssues437432PresentationAsync(
                fixture, client, presentation, clearBeforeEach, null).ConfigureAwait(false);
        }
        StabilizeGc();
        using var resources = new ResourceSampler();
        protocol.Start();
        var samples = new List<double>(measurements);
        var bodies = new List<byte[]>(measurements);
        var etags = new List<string>(measurements);
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < measurements; index++)
        {
            var response = await SendIssues437432PresentationAsync(
                fixture, client, presentation, clearBeforeEach, null).ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            samples.Add(response.ElapsedMilliseconds);
            bodies.Add(response.Body);
            etags.Add(response.ETag);
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        var observed = protocol.Stop();
        await SettleIssues437432DevelopmentSamplerAsync().ConfigureAwait(false);
        var resource = await resources.StopAsync().ConfigureAwait(false);
        var deterministic = bodies.Skip(1).All(body => body.SequenceEqual(bodies[0]));
        Assert.IsTrue(deterministic);
        Assert.AreEqual(presentation.SvgChecksumSha256,
            Convert.ToHexString(SHA256.HashData(bodies[0])), ignoreCase: true);
        Assert.AreEqual(1, etags.Distinct(StringComparer.Ordinal).Count());
        return new(
            CreateIssues437432Distribution(samples, elapsed),
            bodies.Sum(static body => (long)body.Length),
            etags[0],
            deterministic,
            observed,
            resource);
    }

    private static async Task<Issues437432ConditionalResult> ValidateIssues437432ConditionalAsync(
        IntegrationTestFixture fixture,
        HttpClient client,
        Issues437432PresentationFixture presentation,
        string etag)
    {
        var response = await SendIssues437432PresentationAsync(
            fixture, client, presentation, false, etag).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.NotModified, response.StatusCode);
        Assert.AreEqual(etag, response.ETag);
        Assert.AreEqual(0, response.Body.Length);
        return new(response.StatusCode, response.ETag, response.Body.LongLength);
    }

    private static async Task<Issues437432PresentationResponse> SendIssues437432PresentationAsync(
        IntegrationTestFixture fixture,
        HttpClient client,
        Issues437432PresentationFixture presentation,
        bool clear,
        string? conditionalEtag)
    {
        if (clear)
        {
            await ClearIssues437432PresentationCacheAsync(fixture, presentation).ConfigureAwait(false);
        }
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1.0/captures/{presentation.CaptureId:D}/presentation.svg");
        if (conditionalEtag is not null)
        {
            request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(conditionalEtag));
        }
        var started = Stopwatch.GetTimestamp();
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        return new(
            response.StatusCode,
            body,
            response.Headers.ETag?.ToString() ?? throw new AssertFailedException("Presentation ETag is missing."),
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static async Task ClearIssues437432PresentationCacheAsync(
        IntegrationTestFixture fixture,
        Issues437432PresentationFixture presentation)
    {
        var key = Issues437432PresentationCacheKey(presentation);
        fixture.Factory.Services.GetRequiredService<CentralLayeredPresentationCache>().Remove(key);
        await fixture.Factory.Services.GetRequiredService<IDistributedCache>().RemoveAsync(key).ConfigureAwait(false);
    }

    private static string Issues437432PresentationCacheKey(Issues437432PresentationFixture presentation) =>
        CentralLayeredPresentationService.CreateCacheKey(
            presentation.CaptureId, presentation.ManifestArtifactId, presentation.ManifestChecksumSha256);

    private static async Task<Issues437432SingleFlight> MeasureIssues437432SingleFlightAsync(
        IntegrationTestFixture fixture,
        HttpClient client,
        ProtocolCounter protocol,
        Issues437432PresentationFixture presentation)
    {
        await ClearIssues437432PresentationCacheAsync(fixture, presentation).ConfigureAwait(false);
        protocol.Start();
        var singleResponse = await SendIssues437432PresentationAsync(
            fixture, client, presentation, false, null).ConfigureAwait(false);
        var singleProtocol = protocol.Stop();
        Assert.AreEqual(HttpStatusCode.OK, singleResponse.StatusCode);

        await ClearIssues437432PresentationCacheAsync(fixture, presentation).ConfigureAwait(false);
        protocol.Start();
        var started = Stopwatch.GetTimestamp();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            SendIssues437432PresentationAsync(fixture, client, presentation, false, null))).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var concurrentProtocol = protocol.Stop();
        Assert.IsTrue(responses.All(static response => response.StatusCode == HttpStatusCode.OK));
        var sharedResult = responses.Skip(1).All(response =>
            response.Body.SequenceEqual(responses[0].Body) && response.ETag == responses[0].ETag);
        Assert.IsTrue(sharedResult);
        var singleGenerationObserved = singleProtocol.MinioGetObserved > 0 &&
            concurrentProtocol.MinioGetObserved == singleProtocol.MinioGetObserved &&
            concurrentProtocol.MinioResponseContentLengthBytesObserved ==
            singleProtocol.MinioResponseContentLengthBytesObserved;
        Assert.IsTrue(singleGenerationObserved, "Eight cold callers must perform one generation's MinIO reads.");
        var checksum = Convert.ToHexString(SHA256.HashData(responses[0].Body));
        Assert.AreEqual(presentation.SvgChecksumSha256, checksum, ignoreCase: true);
        return new(
            8,
            elapsed.TotalMilliseconds,
            responses.Skip(1).All(response => response.Body.SequenceEqual(responses[0].Body)),
            sharedResult,
            singleGenerationObserved,
            singleProtocol.MinioGetObserved,
            concurrentProtocol.MinioGetObserved,
            responses[0].ETag,
            checksum);
    }

    private static async Task<ClaimsPrincipal> CreateIssues437432OperatorPrincipalAsync(IntegrationTestFixture fixture)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var id = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users
            .Where(user => user.UserName == TestUsers.Operator.Username)
            .Select(static user => user.Id)
            .SingleAsync().ConfigureAwait(false);
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id)], IdentityConstants.ApplicationScheme));
    }

    private static async Task<Issues437432MaterializationMeasurement> MeasureIssues437432MaterializationAsync(
        IntegrationTestFixture fixture,
        ProtocolCounter protocol,
        Issues437432PresentationFixture presentation,
        ClaimsPrincipal principal)
    {
        StabilizeGc();
        using var resources = new ResourceSampler();
        protocol.Start();
        var started = Stopwatch.GetTimestamp();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<ICentralPresentationMaterializer>()
            .SaveAsync(presentation.CaptureId, presentation.ManifestIdentitySha256,
                presentation.LayerIdentitySha256, principal).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var observed = protocol.Stop();
        await SettleIssues437432DevelopmentSamplerAsync().ConfigureAwait(false);
        var resource = await resources.StopAsync().ConfigureAwait(false);
        return new(result.Status, result.Receipt, elapsed, observed, resource);
    }

    private static async Task<Issues437432Correctness> ValidateIssues437432CorrectnessAsync(
        IntegrationTestFixture fixture,
        IReadOnlyDictionary<Guid, Guid[]> expectedLineage,
        CentralPresentationMaterializationReceipt materialization,
        Issues437432PresentationFixture presentation)
    {
        var artifactIds = expectedLineage.Keys.ToArray();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifacts = await db.CentralArtifacts.AsNoTracking().Include(static item => item.Sources)
            .Where(item => artifactIds.Contains(item.ArtifactId)).ToArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(artifactIds.Length, artifacts.Length);
        var available = artifacts.All(static artifact =>
            artifact.ObjectState == CentralArtifactObjectState.Available &&
            artifact.ReconstructionState == CentralReconstructionState.Complete);
        Assert.IsTrue(available);
        var completeLineage = artifacts.All(artifact =>
            artifact.Sources.OrderBy(static source => source.Ordinal)
                .Select(static source => source.SourceArtifactId)
                .SequenceEqual(expectedLineage[artifact.ArtifactId]) &&
            artifact.Sources.All(static source => source.ResolvedCentralArtifactId.HasValue));
        Assert.IsTrue(completeLineage);
        var materialized = artifacts.Single(artifact => artifact.ArtifactId == materialization.ArtifactId);
        var expectedMaterializationLineage = presentation.LayerArtifactIds
            .Prepend(presentation.ManifestArtifactId)
            .Prepend(presentation.BaseArtifactId)
            .Distinct()
            .ToArray();
        var actualLineage = materialized.Sources.OrderBy(static source => source.Ordinal)
            .Select(static source => source.SourceArtifactId).ToArray();
        CollectionAssert.AreEqual(expectedMaterializationLineage, actualLineage);
        var backlogRows = artifacts.Where(static artifact =>
            artifact.ObjectState is CentralArtifactObjectState.Pending or CentralArtifactObjectState.Quarantined ||
            artifact.ReconstructionState is CentralReconstructionState.PendingReference or CentralReconstructionState.Quarantined)
            .ToArray();
        var backlog = new BacklogSnapshot(
            backlogRows.LongLength,
            backlogRows.Sum(static artifact => artifact.ByteLength),
            0,
            backlogRows.LongCount(static artifact => artifact.ObjectState == CentralArtifactObjectState.Pending),
            backlogRows.LongCount(static artifact => artifact.ObjectState == CentralArtifactObjectState.Quarantined),
            backlogRows.LongCount(static artifact => artifact.ReconstructionState == CentralReconstructionState.PendingReference),
            backlogRows.LongCount(static artifact => artifact.ReconstructionState == CentralReconstructionState.Quarantined));
        Assert.AreEqual(0L, backlog.Count);
        return new(available, completeLineage, actualLineage, backlog);
    }

    private static async Task<Issues437432RetainedBytes> ReadIssues437432RetainedBytesAsync(
        IntegrationTestFixture fixture,
        Issues437432PresentationFixture presentation,
        long measuredFlattenedOutputBytes,
        int retainedBenchmarkArtifacts)
    {
        var layeredIds = presentation.LayerArtifactIds
            .Append(presentation.BaseArtifactId)
            .Append(presentation.ManifestArtifactId)
            .ToArray();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var layeredBytes = await db.CentralArtifacts.Where(item => layeredIds.Contains(item.ArtifactId))
            .SumAsync(static item => item.ByteLength).ConfigureAwait(false);
        var counterfactualFlattenedBytes = checked(
            measuredFlattenedOutputBytes * Issues437432FlattenedCombinationCount);
        return new(
            layeredIds.Length,
            layeredBytes,
            measuredFlattenedOutputBytes,
            Issues437432FlattenedCombinationCount,
            counterfactualFlattenedBytes,
            counterfactualFlattenedBytes - layeredBytes,
            $"Deployment counterfactual retained object payload bytes: one measured packed W2 flattened output byte length multiplied by 32 selectable-layer combinations. The benchmark retains {retainedBenchmarkArtifacts} distinct measurement artifacts separately; SQL row/index and object-store metadata overhead are excluded.");
    }

    private static Issues437432Distribution CreateIssues437432Distribution(
        IEnumerable<double> samples,
        TimeSpan elapsed)
    {
        var ordered = samples.Order().ToArray();
        Assert.IsNotEmpty(ordered);
        return new(
            ordered.Length,
            Percentile(ordered, 0.50),
            ordered.Length >= 30 ? Percentile(ordered, 0.95) : null,
            ordered[^1],
            ordered.Length / elapsed.TotalSeconds,
            elapsed.TotalMilliseconds);
    }

    private static double? Issues437432PercentChange(double baseline, double candidate) =>
        baseline == 0 || !double.IsFinite(baseline) || !double.IsFinite(candidate)
            ? null
            : (candidate - baseline) / baseline * 100;

    private static Task SettleIssues437432DevelopmentSamplerAsync() =>
        Environment.GetEnvironmentVariable("HVO_ISSUES437432_DEVELOPMENT") == "1"
            ? Task.Delay(TimeSpan.FromMilliseconds(150))
            : Task.CompletedTask;

    private static Issues437432Provenance ValidateIssues437432Provenance(string root, bool evidenceMode)
    {
        var head = RunGit(root, "rev-parse", "HEAD");
        var tree = RunGit(root, "rev-parse", "HEAD^{tree}");
        var baselineTree = RunGit(root, "rev-parse", $"{Issues437432Baseline}^{{tree}}");
        var productTree = RunGit(root, "rev-parse", $"{Issues437432Product}^{{tree}}");
        var branch = RunGit(root, "branch", "--show-current");
        var dirty = !string.IsNullOrWhiteSpace(RunGit(root, "status", "--porcelain", "--untracked-files=all"));
        if (!evidenceMode)
        {
            return new(
                head, tree, baselineTree, productTree, branch, dirty, "development", null, null,
                "development-unavailable", "development-unavailable", "development-unavailable");
        }
        Assert.IsFalse(dirty, "Claimable evidence requires a clean worktree.");
        Assert.AreEqual(Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"), head, ignoreCase: true);
        _ = RunGit(root, "merge-base", "--is-ancestor", Issues437432Baseline, Issues437432Product);
        _ = RunGit(root, "merge-base", "--is-ancestor", Issues437432Product, head);
        var receiptPath = Environment.GetEnvironmentVariable("HVO_ISSUES437432_BUILD_RECEIPT");
        Assert.IsFalse(string.IsNullOrWhiteSpace(receiptPath));
        var receiptBytes = File.ReadAllBytes(receiptPath!);
        var receipt = JsonSerializer.Deserialize<Issues437432BuildReceipt>(
            receiptBytes, Issues437432JsonOptions);
        Assert.IsNotNull(receipt);
        Assert.AreEqual("issues-437-432-build-receipt-v1", receipt.SchemaVersion);
        Assert.AreEqual(head, receipt.EvidenceHead, ignoreCase: true);
        Assert.AreEqual(tree, receipt.EvidenceTree, ignoreCase: true);
        Assert.AreEqual(Issues437432Product, receipt.ProductCommit, ignoreCase: true);
        Assert.AreEqual(Issues437432Baseline, receipt.BaselineCommit, ignoreCase: true);
        Assert.AreEqual("Release", receipt.Configuration);
        var trial = Environment.GetEnvironmentVariable("HVO_EVIDENCE_TRIAL");
        Assert.AreEqual(trial, receipt.Trial);
        return new(
            head,
            tree,
            baselineTree,
            productTree,
            branch,
            false,
            trial!,
            Path.GetFullPath(receiptPath!),
            Convert.ToHexString(SHA256.HashData(receiptBytes)),
            receipt.BaselineToProductDiffSha256,
            receipt.ProductToEvidenceDiffSha256,
            receipt.SourceInventorySha256);
    }

    private static string Issues437432OutputDirectory(string root, string head, bool evidenceMode)
    {
        if (!evidenceMode)
        {
            return Path.Combine(root, "TestResults", "issues-437-432", "development", Guid.NewGuid().ToString("N"));
        }
        var evidenceRoot = Environment.GetEnvironmentVariable("HVO_ISSUES437432_EVIDENCE_ROOT");
        var trial = Environment.GetEnvironmentVariable("HVO_EVIDENCE_TRIAL");
        Assert.IsFalse(string.IsNullOrWhiteSpace(evidenceRoot));
        Assert.IsFalse(string.IsNullOrWhiteSpace(trial));
        return Path.GetFullPath(Path.Combine(evidenceRoot!, head, "trials", trial!));
    }

    private static string Issues437432BuildConfiguration =>
        typeof(LogicHostIngestPerformanceTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown";

    private sealed record Issues437432Upload(
        Guid ArtifactId,
        byte[] ManifestBytes,
        byte[] Payload,
        string MediaType,
        string ChecksumSha256,
        Guid[] SourceArtifactIds,
        string IdempotencyKey);

    private sealed record Issues437432Artifact(
        Guid ArtifactId,
        string ChecksumSha256,
        long ByteLength,
        Guid[] SourceArtifactIds,
        long RequestBytes,
        long ResponseBytes);

    private sealed record Issues437432PathMeasurement(
        Issues437432Distribution Distribution,
        [property: JsonIgnore] IReadOnlyList<Issues437432Artifact> Artifacts,
        long LogicalPayloadBytes,
        long RequestBodyBytes,
        long ResponseBodyBytes,
        ProtocolSnapshot Protocol,
        ResourceEvidence Resources);

    private sealed record Issues437432Distribution(
        int SampleCount,
        double MedianMilliseconds,
        double? P95Milliseconds,
        double MaximumMilliseconds,
        double OperationsPerSecond,
        double ElapsedMilliseconds);

    private sealed record Issues437432PresentationFixture(
        Guid CaptureId,
        Guid BaseArtifactId,
        long BaseBytes,
        string BaseChecksumSha256,
        Guid ManifestArtifactId,
        string ManifestChecksumSha256,
        string ManifestIdentitySha256,
        Guid[] LayerArtifactIds,
        string[] LayerIdentitySha256,
        string SvgChecksumSha256);

    private sealed record Issues437432PresentationResponse(
        HttpStatusCode StatusCode,
        byte[] Body,
        string ETag,
        double ElapsedMilliseconds);

    private sealed record Issues437432PresentationMeasurement(
        Issues437432Distribution Distribution,
        long ResponseBodyBytes,
        string ETag,
        bool DeterministicBytes,
        ProtocolSnapshot Protocol,
        ResourceEvidence Resources);

    private sealed record Issues437432ConditionalResult(
        HttpStatusCode StatusCode,
        string ETag,
        long BodyBytes);

    private sealed record Issues437432SingleFlight(
        int Concurrency,
        double ElapsedMilliseconds,
        bool DeterministicBytes,
        bool SharedResult,
        bool SingleGenerationObserved,
        long SingleRequestMinioGets,
        long ConcurrentMinioGets,
        string ETag,
        string SvgChecksumSha256);

    private sealed record Issues437432MaterializationMeasurement(
        CentralPresentationMaterializationStatus Status,
        CentralPresentationMaterializationReceipt? Receipt,
        double ElapsedMilliseconds,
        ProtocolSnapshot Protocol,
        ResourceEvidence Resources);

    private sealed record Issues437432Correctness(
        bool AllMeasuredArtifactsAvailableAndComplete,
        bool CompleteResolvedLineage,
        Guid[] MaterializationLineage,
        BacklogSnapshot FinalBacklog);

    private sealed record Issues437432RetainedBytes(
        int ActualLayeredArtifactCount,
        long ActualLayeredBytes,
        long MeasuredFlattenedOutputBytes,
        int CounterfactualCombinationCount,
        long CounterfactualFlattenedBytes,
        long CounterfactualFlattenedMinusActualLayeredBytes,
        string Scope);

    private sealed record Issues437432Provenance(
        string EvidenceHead,
        string EvidenceTree,
        string BaselineTree,
        string ProductTree,
        string Branch,
        bool Dirty,
        string Trial,
        string? BuildReceiptPath,
        string? BuildReceiptSha256,
        string BaselineToProductDiffSha256,
        string ProductToEvidenceDiffSha256,
        string SourceInventorySha256);

    private sealed record Issues437432BuildReceipt(
        string SchemaVersion,
        string EvidenceHead,
        string EvidenceTree,
        string ProductCommit,
        string ProductTree,
        string BaselineCommit,
        string BaselineTree,
        string BaselineToProductDiffSha256,
        string ProductToEvidenceDiffSha256,
        string SourceInventorySha256,
        int SourceInventoryFileCount,
        string RuntimeOutputInventorySha256,
        int RuntimeOutputFileCount,
        string Configuration,
        string Project,
        string SdkVersion,
        string Trial,
        string BuildStartedUtc,
        string BuildCompletedUtc);
}
