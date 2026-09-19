using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using System.Text.Json;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralDerivativeOutputWriterTests
{
    [TestMethod]
    public void ValidateProductRejectsEveryLeaseIdentityMismatch()
    {
        var (lease, product, _, _, _) = CreateContractState();
        CentralDerivativeOutputWriter.ValidateProduct(lease, product);

        var legacy = lease with { GraphExecutionId = null };
        var mismatches = new[]
        {
            product with { Role = FrameArtifactRole.AnnotatedPreview },
            product with { Variant = "different" },
            product with { SourceArtifactIds = [Guid.NewGuid()] },
            product with { ChecksumSha256 = new string('0', 64) },
            product with
            {
                Layout = new FrameLayoutDescriptor(
                    1, 1, 4, CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian,
                    16, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.None,
                    0, 65535, 4)
            }
        };
        foreach (var mismatch in mismatches)
        {
            Assert.ThrowsExactly<CentralDerivativeJobStateException>(() =>
                CentralDerivativeOutputWriter.ValidateProduct(legacy, mismatch));
        }

        var boundLease = lease with
        {
            RequestedRecipeIdentitySha256 = new string('D', 64),
            ExpectedRecipeIdentitySha256 = product.Recipe.IdentitySha256
        };
        CentralDerivativeOutputWriter.ValidateProduct(boundLease, product);
        Assert.ThrowsExactly<CentralDerivativeJobStateException>(() =>
            CentralDerivativeOutputWriter.ValidateProduct(
                boundLease,
                product with { Recipe = product.Recipe with { IdentitySha256 = new string('E', 64) } }));
    }

    [TestMethod]
    public void ValidateExistingRejectsEveryConflictingEvidenceAndArtifactField()
    {
        var (lease, product, evidence, artifactId, objectKey) = CreateContractState();
        const string ContractIdentity = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
        const string Bucket = "skymonitor-artifacts";
        CentralDerivativeOutputWriter.ValidateExisting(
            evidence, lease, product, artifactId, objectKey, Bucket, ContractIdentity);

        void Reject(Action mutate, Action restore, string? contractIdentity = ContractIdentity)
        {
            mutate();
            Assert.ThrowsExactly<CentralDerivativeJobStateException>(() =>
                CentralDerivativeOutputWriter.ValidateExisting(
                    evidence, lease, product, artifactId, objectKey, Bucket, contractIdentity));
            restore();
        }

        Reject(
            () => evidence.GraphProductContractIdentitySha256 = new string('0', 64),
            () => evidence.GraphProductContractIdentitySha256 = ContractIdentity);
        Reject(
            () => evidence.Job!.InputSetIdentitySha256 = new string('0', 64),
            () => evidence.Job!.InputSetIdentitySha256 = lease.InputSetIdentitySha256);
        Reject(
            () => evidence.RequestedRecipeIdentitySha256 = new string('0', 64),
            () => evidence.RequestedRecipeIdentitySha256 = lease.RequestedRecipeIdentitySha256);
        Reject(
            () => evidence.RecipeIdentitySha256 = new string('0', 64),
            () => evidence.RecipeIdentitySha256 = product.Recipe.IdentitySha256);
        Reject(
            () => evidence.RecipeOperationKind = ProcessingOperationKind.Analyzer,
            () => evidence.RecipeOperationKind = product.Recipe.OperationKind);
        Reject(
            () => evidence.ProductKind = ProcessingProductKind.Metadata,
            () => evidence.ProductKind = product.Kind);
        Reject(
            () => evidence.ProductSchemaVersion = "different",
            () => evidence.ProductSchemaVersion = product.SchemaVersion);
        Reject(
            () => evidence.ProductMediaType = "application/json",
            () => evidence.ProductMediaType = product.MediaType);

        var artifact = evidence.Artifact!;
        Reject(() => artifact.ArtifactId = Guid.NewGuid(), () => artifact.ArtifactId = artifactId);
        Reject(() => artifact.Role = FrameArtifactRole.Metadata, () => artifact.Role = product.Role);
        Reject(() => artifact.Variant = "different", () => artifact.Variant = product.Variant);
        Reject(
            () => artifact.RecipeVersion = "different",
            () => artifact.RecipeVersion = lease.TargetRecipeVersion);
        Reject(() => artifact.MediaType = "application/json", () => artifact.MediaType = product.MediaType);
        Reject(() => artifact.ByteLength++, () => artifact.ByteLength--);
        Reject(
            () => artifact.ChecksumSha256 = new string('0', 64),
            () => artifact.ChecksumSha256 = product.ChecksumSha256);
        Reject(
            () => artifact.StorageReference = "object://different/key",
            () => artifact.StorageReference = $"object://{Bucket}/{objectKey}");
        var source = artifact.Sources.Single();
        Reject(() => source.SourceArtifactId = Guid.NewGuid(), () => source.SourceArtifactId = lease.SourceArtifactId);
        Reject(
            () => source.ResolvedCentralArtifactId = Guid.NewGuid(),
            () => source.ResolvedCentralArtifactId = lease.Inputs!.Single().CentralArtifactId);

        var legacy = lease with { GraphExecutionId = null, Inputs = null, InputSetIdentitySha256 = null };
        CentralDerivativeOutputWriter.ValidateExisting(
            evidence, legacy, product, artifactId, objectKey, Bucket, graphContractIdentity: null);
        var originalJobId = evidence.CentralDerivativeJobId;
        evidence.CentralDerivativeJobId = Guid.NewGuid();
        Assert.ThrowsExactly<CentralDerivativeJobStateException>(() =>
            CentralDerivativeOutputWriter.ValidateExisting(
                evidence, legacy, product, artifactId, objectKey, Bucket, graphContractIdentity: null));
        evidence.CentralDerivativeJobId = originalJobId;

        evidence.ProductKind = null;
        evidence.ProductSchemaVersion = null;
        evidence.ProductMediaType = null;
        CentralDerivativeOutputWriter.ValidateExisting(
            evidence, legacy, product, artifactId, objectKey, Bucket, graphContractIdentity: null);
        evidence.ProductKind = ProcessingProductKind.Metadata;
        Assert.ThrowsExactly<CentralDerivativeJobStateException>(() =>
            CentralDerivativeOutputWriter.ValidateExisting(
                evidence, legacy, product, artifactId, objectKey, Bucket, graphContractIdentity: null));
        evidence.ProductKind = null;
        evidence.ProductSchemaVersion = "different";
        Assert.ThrowsExactly<CentralDerivativeJobStateException>(() =>
            CentralDerivativeOutputWriter.ValidateExisting(
                evidence, legacy, product, artifactId, objectKey, Bucket, graphContractIdentity: null));
        evidence.ProductSchemaVersion = null;
        evidence.ProductMediaType = "application/json";
        Assert.ThrowsExactly<CentralDerivativeJobStateException>(() =>
            CentralDerivativeOutputWriter.ValidateExisting(
                evidence, legacy, product, artifactId, objectKey, Bucket, graphContractIdentity: null));
    }

    [TestMethod]
    public void CreateLayoutCopiesOptionalReadoutAndCodeSpace()
    {
        var descriptor = new FrameLayoutDescriptor(
            4, 3, 8, CameraPixelFormat.Mono16, FrameByteOrder.LittleEndian,
            12, 16, FrameSamplePacking.ByteAligned, ColorFilterArrayPattern.Rggb,
            64, 4095, 24)
        {
            StoredCodeTransform = FrameStoredCodeTransform.LeftShiftedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample,
            Readout = new FrameReadoutDescriptor(
                8, 6, 2, 1, 4, 3, 2, 2, FrameBinningAlgorithm.DigitalAverageV1, 1, 0)
        };

        var layout = CentralDerivativeOutputWriter.CreateLayout(descriptor);
        Assert.AreEqual(descriptor.Width, layout.Width);
        Assert.AreEqual(descriptor.Height, layout.Height);
        Assert.AreEqual(descriptor.StrideBytes, layout.StrideBytes);
        Assert.AreEqual(descriptor.Readout.NativeWidth, layout.NativeWidth);
        Assert.AreEqual(descriptor.Readout.BinningAlgorithm.ToString(), layout.BinningAlgorithm);
        Assert.AreEqual(descriptor.StoredCodeTransform.ToString(), layout.StoredCodeTransform);
        Assert.AreEqual(descriptor.LevelCodeSpace.ToString(), layout.LevelCodeSpace);
        Assert.AreEqual(descriptor.ByteLength, layout.ByteLength);

        var noReadout = CentralDerivativeOutputWriter.CreateLayout(descriptor with { Readout = null });
        Assert.IsNull(noReadout.NativeWidth);
        Assert.IsNull(noReadout.BinningAlgorithm);
    }

    private static (
        CentralDerivativeJobLease Lease,
        ProcessingProduct Product,
        CentralArtifactProcessingEvidence Evidence,
        Guid ArtifactId,
        string ObjectKey) CreateContractState()
    {
        var deviceId = Guid.NewGuid();
        var sourceArtifactId = Guid.NewGuid();
        var sourceCentralArtifactId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var recipeDescriptor = RecipeIdentityDescriptor.Create(
            "preview", "1.0.0", "implementation-v1", JsonSerializer.SerializeToElement(new { }));
        var recipe = new ProcessingRecipeIdentity(recipeDescriptor, new string('A', 64))
        {
            OperationKind = ProcessingOperationKind.Transform
        };
        byte[] payload = [1, 2, 3];
        var product = new ProcessingProduct(
            FrameArtifactRole.Preview,
            "graph",
            new string('B', 64),
            "application/octet-stream",
            null,
            payload,
            ProcessingIdentity.ComputePayloadSha256(payload),
            recipe,
            [],
            [sourceArtifactId],
            TimeSpan.Zero,
            new ProcessingCompatibilityIdentity(
                "rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile"))
        {
            SchemaVersion = "product-v1"
        };
        var input = new CentralDerivativeJobLeaseInput(
            0,
            sourceCentralArtifactId,
            deviceId,
            sourceArtifactId,
            FrameArtifactRole.Raw,
            "raw-v1",
            new string('D', 64),
            "application/octet-stream",
            1,
            Guid.NewGuid(),
            "agent",
            1,
            DateTimeOffset.UtcNow,
            new string('E', 64));
        var lease = new CentralDerivativeJobLease(
            jobId,
            Guid.NewGuid(),
            "worker",
            DateTimeOffset.UtcNow.AddMinutes(1),
            deviceId,
            sourceArtifactId,
            FrameArtifactRole.Raw,
            "raw-v1",
            "/content",
            input.ChecksumSha256,
            input.MediaType,
            input.FrameId,
            input.AgentId,
            input.CapturedAtUtc,
            null,
            null,
            product.Role,
            "preview-v1",
            product.Variant,
            recipeDescriptor.Name,
            "{}",
            "{}",
            recipe.IdentitySha256,
            new string('F', 64),
            null,
            null,
            1,
            3,
            Inputs: [input],
            ExpectedRecipeIdentitySha256: recipe.IdentitySha256,
            InputSetIdentitySha256: new string('1', 64),
            GraphExecutionId: Guid.NewGuid());
        var artifactId = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256);
        var objectKey = $"derivatives/{deviceId:N}/{product.OutputIdentitySha256}.bin";
        var artifact = new CentralArtifact
        {
            ArtifactId = artifactId,
            DevicePublicId = deviceId,
            Role = product.Role,
            Variant = product.Variant,
            RecipeVersion = lease.TargetRecipeVersion,
            MediaType = product.MediaType,
            ByteLength = product.Payload.Length,
            ChecksumSha256 = product.ChecksumSha256,
            StorageReference = $"object://skymonitor-artifacts/{objectKey}"
        };
        artifact.Sources.Add(new CentralArtifactSource
        {
            Ordinal = 0,
            SourceArtifactId = sourceArtifactId,
            ResolvedCentralArtifactId = sourceCentralArtifactId
        });
        const string ContractIdentity = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
        var evidence = new CentralArtifactProcessingEvidence
        {
            Artifact = artifact,
            CentralArtifactId = artifact.Id,
            CentralDerivativeJobId = jobId,
            Job = new CentralDerivativeJob
            {
                Id = jobId,
                InputSetIdentitySha256 = lease.InputSetIdentitySha256
            },
            RequestedRecipeIdentitySha256 = lease.RequestedRecipeIdentitySha256,
            RecipeIdentitySha256 = recipe.IdentitySha256,
            RecipeOperationKind = recipe.OperationKind,
            GraphProductContractIdentitySha256 = ContractIdentity,
            ProductKind = product.Kind,
            ProductSchemaVersion = product.SchemaVersion,
            ProductMediaType = product.MediaType
        };
        return (lease, product, evidence, artifactId, objectKey);
    }
}
