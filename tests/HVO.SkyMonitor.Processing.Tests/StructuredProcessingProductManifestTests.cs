using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class StructuredProcessingProductManifestTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void CanonicalRoundTripPreservesImmutableProductFacts()
    {
        var manifest = CreateManifest();

        var bytes = StructuredProcessingProductManifestJson.Serialize(manifest);
        var parsed = StructuredProcessingProductManifestJson.Parse(bytes);

        Assert.IsTrue(parsed.IsValid);
        Assert.AreEqual(manifest.Descriptor.Artifact.ArtifactId, parsed.Manifest!.Descriptor.Artifact.ArtifactId);
        Assert.AreEqual(manifest.Descriptor.OutputIdentitySha256, parsed.Manifest.Descriptor.OutputIdentitySha256);
        Assert.AreEqual(manifest.Descriptor.ContentIdentitySha256, parsed.Manifest.Descriptor.ContentIdentitySha256);
        Assert.AreEqual(manifest.IdempotencyKey, parsed.Manifest.IdempotencyKey);
        CollectionAssert.AreEqual(bytes, StructuredProcessingProductManifestJson.Serialize(parsed.Manifest));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void IdempotencyExcludesStoragePathAndIncludesProductIdentity()
    {
        var manifest = CreateManifest();
        var moved = manifest with { RelativeArtifactPath = "moved/product.json", ProducerStepId = "storage-2" };
        var changed = manifest with
        {
            Descriptor = manifest.Descriptor with { ContentIdentitySha256 = new string('B', 64) }
        };

        Assert.AreEqual(manifest.IdempotencyKey, moved.IdempotencyKey);
        Assert.AreNotEqual(manifest.IdempotencyKey, changed.IdempotencyKey);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ValidationRejectsIdentityPathAndPayloadBoundViolations()
    {
        var manifest = CreateManifest();

        Assert.IsFalse((manifest with { RelativeArtifactPath = "../product.json" }).Validate().IsValid);
        Assert.IsFalse((manifest with
        {
            Descriptor = manifest.Descriptor with { OutputIdentitySha256 = new string('B', 64) }
        }).Validate().IsValid);
        Assert.IsFalse((manifest with
        {
            Descriptor = manifest.Descriptor with
            {
                ByteLength = StructuredProcessingProductDescriptorV1.MaximumPayloadBytes + 1L
            }
        }).Validate().IsValid);
        Assert.IsFalse((manifest with
        {
            Descriptor = manifest.Descriptor with
            {
                Artifact = manifest.Descriptor.Artifact with { MediaType = "application/vnd.example.unknown+json" }
            }
        }).Validate().IsValid);
        Assert.IsFalse((manifest with
        {
            Descriptor = manifest.Descriptor with
            {
                Algorithms = [new(new string('A', 128), new string('B', 128)), .. Enumerable.Repeat(
                    new ProcessingAlgorithmIdentity(new string('C', 128), new string('D', 128)), 63)]
            }
        }).Validate().IsValid);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ParserRejectsUnknownAndDuplicateProperties()
    {
        var json = Encoding.UTF8.GetString(StructuredProcessingProductManifestJson.Serialize(CreateManifest()));
        var duplicate = json.Replace(
            "\"schemaVersion\":",
            "\"schemaVersion\":\"hvo-structured-processing-product-v1\",\"schemaVersion\":",
            StringComparison.Ordinal);
        var unknown = json.Replace("\"relativeArtifactPath\":", "\"unknown\":1,\"relativeArtifactPath\":", StringComparison.Ordinal);

        Assert.IsFalse(StructuredProcessingProductManifestJson.Parse(Encoding.UTF8.GetBytes(duplicate)).IsValid);
        Assert.IsFalse(StructuredProcessingProductManifestJson.Parse(Encoding.UTF8.GetBytes(unknown)).IsValid);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void MetadataFactsParserRecomputesContentIdentity()
    {
        var facts = new PresentationMetadataFactsProductV1(
            PresentationMetadataFactsProductV1.CurrentSchemaVersion,
            string.Empty,
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            1,
            JsonSerializer.SerializeToElement(new { exposure = "original" }),
            [],
            JsonSerializer.SerializeToElement(new { }),
            JsonSerializer.SerializeToElement(new { }),
            JsonSerializer.SerializeToElement(new { }),
            JsonSerializer.SerializeToElement(new { }),
            new(string.Empty, ["capture"], [], [], []),
            [
                Guid.Parse("30000000-0000-0000-0000-000000000002"),
                Guid.Parse("30000000-0000-0000-0000-000000000003")
            ]);
        var identity = PresentationProcessingProducts.ComputeMetadataFactsIdentity(facts);
        facts = facts with
        {
            FactsIdentitySha256 = identity,
            Corners = facts.Corners with { SourceIdentitySha256 = identity }
        };
        var canonical = Encoding.UTF8.GetBytes(CaptureContractJson.Canonicalize(
            CaptureContractJson.SerializeToElement(facts)).GetRawText());

        Assert.AreEqual(facts.FactsIdentitySha256,
            PresentationProcessingProducts.ParseMetadataFacts(canonical).FactsIdentitySha256);

        var legacy = facts with
        {
            SchemaVersion = PresentationMetadataFactsProductV1.LegacySchemaVersion,
            FactsIdentitySha256 = string.Empty,
            SourceArtifactIds = null,
            Corners = facts.Corners with { SourceIdentitySha256 = string.Empty }
        };
        var legacyIdentity = PresentationProcessingProducts.ComputeMetadataFactsIdentity(legacy);
        legacy = legacy with
        {
            FactsIdentitySha256 = legacyIdentity,
            Corners = legacy.Corners with { SourceIdentitySha256 = legacyIdentity }
        };
        var legacyJson = JsonNode.Parse(CaptureContractJson.SerializeToElement(legacy).GetRawText())!.AsObject();
        legacyJson.Remove("sourceArtifactIds");
        var legacyBytes = Encoding.UTF8.GetBytes(CaptureContractJson.Canonicalize(
            JsonSerializer.SerializeToElement(legacyJson)).GetRawText());
        Assert.AreEqual(legacyIdentity,
            PresentationProcessingProducts.ParseMetadataFacts(legacyBytes).FactsIdentitySha256);

        var tampered = facts with { Capture = JsonSerializer.SerializeToElement(new { exposure = "tampered" }) };
        var tamperedBytes = Encoding.UTF8.GetBytes(CaptureContractJson.Canonicalize(
            CaptureContractJson.SerializeToElement(tampered)).GetRawText());
        Assert.ThrowsExactly<ArgumentException>(() => PresentationProcessingProducts.ParseMetadataFacts(tamperedBytes));
        var mismatchedSource = facts with
        {
            Corners = facts.Corners with { SourceIdentitySha256 = new string('B', 64) }
        };
        var mismatchedSourceBytes = Encoding.UTF8.GetBytes(CaptureContractJson.Canonicalize(
            CaptureContractJson.SerializeToElement(mismatchedSource)).GetRawText());
        Assert.ThrowsExactly<ArgumentException>(() =>
            PresentationProcessingProducts.ParseMetadataFacts(mismatchedSourceBytes));
    }

    private static StructuredProcessingProductManifestV1 CreateManifest()
    {
        var source = CreateSourceDescriptor();
        using var options = JsonDocument.Parse("{}");
        var sourceIds = new[] { source.Artifact.ArtifactId };
        var recipe = RecipeIdentityDescriptor.Create("projected-scene", "1.0.0", "test-1", options.RootElement);
        var recipeIdentity = ProcessingIdentity.CreateRecipeIdentity(recipe);
        var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
            FrameArtifactRole.Metadata,
            "projected-scene",
            recipeIdentity.IdentitySha256,
            sourceIds);
        var artifact = new ArtifactDescriptor(
            ProcessingIdentity.CreateArtifactId(outputIdentity),
            FrameArtifactRole.Metadata,
            "projected-scene-step",
            "projected-scene",
            DateTimeOffset.UnixEpoch,
            sourceIds,
            recipe,
            "application/vnd.hvo.projected-scene+json",
            new string('C', 64));
        return new StructuredProcessingProductManifestV1(
            StructuredProcessingProductManifestV1.CurrentSchemaVersion,
            new StructuredProcessingProductDescriptorV1(
                source,
                artifact,
                outputIdentity,
                [new("projected-scene", "test-1")],
                new(
                    new string('D', 64),
                    new string('E', 64),
                    new string('F', 64),
                    new string('A', 64),
                    new string('B', 64),
                    "night",
                    new string('C', 64)),
                TimeSpan.FromSeconds(1).Ticks,
                128,
                ProcessingProductKind.Metadata,
                ProjectedSceneV1.CurrentSchemaVersion,
                new string('A', 64)),
            "derived/projected-scene.json",
            ProducerStepId: "projected-scene-step");
    }

    private static ReconstructionDescriptor CreateSourceDescriptor()
    {
        using var options = JsonDocument.Parse("{}");
        var sha256 = new string('A', 64);
        var instant = DateTimeOffset.UnixEpoch;
        return new ReconstructionDescriptor(
            new("central-agent", "central-rig", 1, Guid.Parse("10000000-0000-0000-0000-000000000001")),
            new(instant, instant, instant, instant, instant),
            new(TimeSpan.Zero, TimeSpan.Zero, 0, 0, null, null, null, null),
            new(
                new("rig", "1", sha256),
                new("calibration", "1", sha256),
                new("mask", "1", sha256),
                new("sensor", "1", sha256),
                new("processing", "1", sha256)),
            new(
                1, 1, 1, CameraPixelFormat.Mono8, FrameByteOrder.NotApplicable, 8, 8,
                FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None, 0, 255, 1),
            new(
                Guid.Parse("20000000-0000-0000-0000-000000000001"),
                FrameArtifactRole.Raw,
                "central-source",
                "native",
                instant,
                [],
                RecipeIdentityDescriptor.Create("raw", "1.0.0", "central-1", options.RootElement),
                "application/octet-stream",
                sha256));
    }
}
