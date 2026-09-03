using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using System.Text.Json;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralProcessingGraphOutputBindingTests
{
    [TestMethod]
    public async Task BindAsyncRequiresOneExactFrozenContractMatch()
    {
        await using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var now = DateTimeOffset.UtcNow;
        var frameId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var anchor = CreateArtifact(frameId, deviceId, FrameArtifactRole.Raw, "raw", "raw-v1");
        var result = CreateArtifact(frameId, deviceId, FrameArtifactRole.Preview, "graph", "recipe-v1");
        var execution = CreateExecution(anchor, now);
        var job = CreateJob(execution, anchor, now);
        var recipe = new ProcessingRecipeDefinition(
            "preview", "1.0.0", "implementation-v1", ProcessingOperationKind.Transform);
        var algorithms = ImmutableArray.Create(new ProcessingAlgorithmIdentity("resize", "1"));
        var contract = new ProcessingGraphProductContract(
            result.Role, result.Variant!, ProcessingProductKind.PixelData, recipe, "product-v1", algorithms,
            result.MediaType);
        var contractElement = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(contract));
        var output = CentralDerivativeJobOutput.CreateFromFrozenPlan(job, 0, contract);
        Assert.AreEqual(contractElement.GetRawText(), output.ContractJson);
        Assert.AreNotEqual(CaptureContractJson.SerializeToElement(contract).GetRawText(), output.ContractJson);
        Assert.AreEqual(
            CentralDerivativeJobOutput.ComputeContractIdentitySha256(output.ContractJson),
            output.ContractIdentitySha256);
        job.Outputs.Add(output);
        execution.Jobs.Add(job);
        var producingExecution = CreateExecution(anchor, now);
        producingExecution.RequestIdentitySha256 = new string('2', 64);
        var producingJob = CreateJob(producingExecution, anchor, now);
        producingJob.RequestIdentitySha256 = new string('3', 64);
        producingJob.InputSetIdentitySha256 = job.InputSetIdentitySha256;
        producingJob.Outputs.Add(CentralDerivativeJobOutput.CreateFromFrozenPlan(producingJob, 0, contract));
        producingExecution.Jobs.Add(producingJob);
        result.Recipe = new CentralArtifactRecipe
        {
            Name = recipe.Name,
            SemanticVersion = recipe.SemanticVersion,
            ImplementationVersion = recipe.ImplementationVersion,
            OptionsJson = "{}",
            OptionsSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(new { }))
        };
        var outputIdentity = new string('8', 64);
        var evidence = new CentralArtifactProcessingEvidence
        {
            Artifact = result,
            CentralArtifactId = result.Id,
            DevicePublicId = result.DevicePublicId,
            OutputIdentitySha256 = outputIdentity,
            RequestedRecipeIdentitySha256 = job.RequestedRecipeIdentitySha256,
            RecipeIdentitySha256 = job.ExpectedRecipeIdentitySha256,
            RecipeOperationKind = recipe.OperationKind,
            GraphProductContractIdentitySha256 = output.ContractIdentitySha256,
            ProductKind = contract.ProductKind,
            ProductSchemaVersion = contract.SchemaVersion,
            ProductMediaType = contract.MediaType,
            AlgorithmsJson = JsonSerializer.Serialize(algorithms),
            CompatibilityJson = "{}",
            CentralDerivativeJobId = producingJob.Id,
            Job = producingJob,
            CreatedAtUtc = now
        };
        context.AddRange(anchor, result, execution, evidence);
        context.Add(producingExecution);
        await context.SaveChangesAsync().ConfigureAwait(false);
        execution.ExpandedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);
        var descriptor = RecipeIdentityDescriptor.Create(
            recipe.Name, recipe.SemanticVersion, recipe.ImplementationVersion,
            JsonSerializer.SerializeToElement(new { }));
        var product = new ProcessingProduct(
            result.Role,
            result.Variant!,
            outputIdentity,
            result.MediaType,
            null,
            new byte[] { 1 },
            result.ChecksumSha256,
            new ProcessingRecipeIdentity(descriptor, job.ExpectedRecipeIdentitySha256)
            {
                OperationKind = recipe.OperationKind
            },
            algorithms,
            [anchor.ArtifactId],
            TimeSpan.Zero,
            new ProcessingCompatibilityIdentity("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile"));
        product = product with { SchemaVersion = contract.SchemaVersion };

        await CentralProcessingGraphOutputBinding.BindAsync(
            context, job.Id, result.Id, null, now, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(result.Id, output.ResultCentralArtifactId);
        output.ResultCentralArtifactId = null;
        output.ResultOutputIdentitySha256 = null;
        output.BoundAtUtc = null;

        async Task AssertDurableMismatchAsync(Action mutate, Action restore)
        {
            mutate();
            await context.SaveChangesAsync().ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<CentralDerivativeJobStateException>(() =>
                CentralProcessingGraphOutputBinding.BindAsync(
                    context, job.Id, result.Id, null, now, CancellationToken.None)).ConfigureAwait(false);
            Assert.IsNull(output.ResultCentralArtifactId);
            restore();
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        await AssertDurableMismatchAsync(
            () => result.CentralFrameId = Guid.NewGuid(),
            () => result.CentralFrameId = frameId).ConfigureAwait(false);
        await AssertDurableMismatchAsync(
            () => result.DevicePublicId = Guid.NewGuid(),
            () => result.DevicePublicId = deviceId).ConfigureAwait(false);
        await AssertDurableMismatchAsync(
            () => result.RecipeVersion = "different",
            () => result.RecipeVersion = job.TargetRecipeVersion).ConfigureAwait(false);
        await AssertDurableMismatchAsync(
            () => evidence.DevicePublicId = Guid.NewGuid(),
            () => evidence.DevicePublicId = deviceId).ConfigureAwait(false);
        await AssertDurableMismatchAsync(
            () => evidence.RequestedRecipeIdentitySha256 = new string('4', 64),
            () => evidence.RequestedRecipeIdentitySha256 = job.RequestedRecipeIdentitySha256).ConfigureAwait(false);
        await AssertDurableMismatchAsync(
            () => evidence.RecipeIdentitySha256 = new string('5', 64),
            () => evidence.RecipeIdentitySha256 = job.ExpectedRecipeIdentitySha256).ConfigureAwait(false);
        await AssertDurableMismatchAsync(
            () => result.MediaType = "application/json",
            () => result.MediaType = contract.MediaType!).ConfigureAwait(false);
        await AssertDurableMismatchAsync(
            () => result.Recipe!.Name = "different",
            () => result.Recipe!.Name = recipe.Name).ConfigureAwait(false);
        await AssertDurableMismatchAsync(
            () => result.Recipe!.SemanticVersion = "2.0.0",
            () => result.Recipe!.SemanticVersion = recipe.SemanticVersion).ConfigureAwait(false);
        await AssertDurableMismatchAsync(
            () => result.Recipe!.ImplementationVersion = "different",
            () => result.Recipe!.ImplementationVersion = recipe.ImplementationVersion).ConfigureAwait(false);
        await AssertDurableMismatchAsync(
            () => evidence.AlgorithmsJson = "[]",
            () => evidence.AlgorithmsJson = JsonSerializer.Serialize(algorithms)).ConfigureAwait(false);

        evidence.AlgorithmsJson = "{";
        await context.SaveChangesAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CentralDerivativeJobStateException>(() =>
            CentralProcessingGraphOutputBinding.BindAsync(
                context, job.Id, result.Id, null, now, CancellationToken.None)).ConfigureAwait(false);
        evidence.AlgorithmsJson = JsonSerializer.Serialize(algorithms);
        await context.SaveChangesAsync().ConfigureAwait(false);

        var wrongOperation = product with
        {
            Recipe = product.Recipe with { OperationKind = ProcessingOperationKind.Analyzer }
        };
        await Assert.ThrowsExactlyAsync<CentralDerivativeJobStateException>(() =>
            CentralProcessingGraphOutputBinding.BindAsync(
                context, job.Id, result.Id, wrongOperation, now, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(output.ResultCentralArtifactId);

        var wrongOutputIdentity = product with { OutputIdentitySha256 = new string('7', 64) };
        await Assert.ThrowsExactlyAsync<CentralDerivativeJobStateException>(() =>
            CentralProcessingGraphOutputBinding.BindAsync(
                context, job.Id, result.Id, wrongOutputIdentity, now, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(output.ResultCentralArtifactId);

        var incompatible = product with
        {
            Algorithms = [new ProcessingAlgorithmIdentity("different", "1")]
        };
        await Assert.ThrowsExactlyAsync<CentralDerivativeJobStateException>(() =>
            CentralProcessingGraphOutputBinding.BindAsync(
                context, job.Id, result.Id, incompatible, now, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(output.ResultCentralArtifactId);

        var wrongMedia = product with { MediaType = "application/json" };
        await Assert.ThrowsExactlyAsync<CentralDerivativeJobStateException>(() =>
            CentralProcessingGraphOutputBinding.BindAsync(
                context, job.Id, result.Id, wrongMedia, now, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(output.ResultCentralArtifactId);

        await CentralProcessingGraphOutputBinding.BindAsync(
            context, job.Id, result.Id, product, now, CancellationToken.None).ConfigureAwait(false);
        await context.SaveChangesAsync().ConfigureAwait(false);

        Assert.AreEqual(result.Id, output.ResultCentralArtifactId);
        Assert.AreEqual(outputIdentity, output.ResultOutputIdentitySha256);
        Assert.AreEqual(now, output.BoundAtUtc);
    }

    [TestMethod]
    public void ResolveContractIdentityUsesExactStoredJsonBytes()
    {
        var recipe = new ProcessingRecipeDefinition(
            "preview", "1.0.0", "implementation-v1", ProcessingOperationKind.Transform);
        var algorithms = ImmutableArray.Create(new ProcessingAlgorithmIdentity("resize", "1"));
        var contract = new ProcessingGraphProductContract(
            FrameArtifactRole.Preview, "graph", ProcessingProductKind.PixelData, recipe, "product-v1",
            algorithms, "application/octet-stream");
        var serializedContractJson = CaptureContractJson.SerializeToElement(contract).GetRawText();
        using var serializedContract = JsonDocument.Parse(serializedContractJson);
        var canonicalContract = CaptureContractJson.Canonicalize(serializedContract.RootElement);
        Assert.AreNotEqual(canonicalContract.GetRawText(), serializedContractJson);

        var job = new CentralDerivativeJob { GraphExecutionId = Guid.NewGuid() };
        var output = new CentralDerivativeJobOutput
        {
            Job = job,
            CentralDerivativeJobId = job.Id,
            Role = contract.Role,
            Variant = contract.Variant,
            ProductKind = contract.ProductKind,
            ContractJson = serializedContractJson,
            ContractIdentitySha256 = CaptureContractJson.ComputeCanonicalJsonSha256(canonicalContract)
        };
        job.Outputs.Add(output);
        var descriptor = RecipeIdentityDescriptor.Create(
            recipe.Name, recipe.SemanticVersion, recipe.ImplementationVersion,
            JsonSerializer.SerializeToElement(new { }));
        var product = new ProcessingProduct(
            contract.Role,
            contract.Variant,
            new string('8', 64),
            contract.MediaType!,
            null,
            new byte[] { 1 },
            new string('9', 64),
            new ProcessingRecipeIdentity(descriptor, new string('E', 64))
            {
                OperationKind = recipe.OperationKind
            },
            algorithms,
            [],
            TimeSpan.Zero,
            new ProcessingCompatibilityIdentity("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile"))
        {
            SchemaVersion = contract.SchemaVersion
        };

        Assert.ThrowsExactly<CentralDerivativeJobStateException>(() =>
            CentralProcessingGraphOutputBinding.ResolveContractIdentity(job, product));

        output.ContractIdentitySha256 = CentralDerivativeJobOutput.ComputeContractIdentitySha256(
            serializedContractJson);

        Assert.AreEqual(
            output.ContractIdentitySha256,
            CentralProcessingGraphOutputBinding.ResolveContractIdentity(job, product));

        output.ContractJson = serializedContractJson.Insert(serializedContractJson.Length - 1, ",\"unknown\":true");
        output.ContractIdentitySha256 = CentralDerivativeJobOutput.ComputeContractIdentitySha256(
            output.ContractJson);
        Assert.ThrowsExactly<CentralDerivativeJobStateException>(() =>
            CentralProcessingGraphOutputBinding.ResolveContractIdentity(job, product));
    }

    [TestMethod]
    public void ResolveContractIdentityRejectsEveryFrozenProductMismatch()
    {
        var definition = new ProcessingRecipeDefinition(
            "preview", "1.0.0", "implementation-v1", ProcessingOperationKind.Transform);
        var algorithms = ImmutableArray.Create(new ProcessingAlgorithmIdentity("resize", "1"));
        var contract = new ProcessingGraphProductContract(
            FrameArtifactRole.Preview,
            "graph",
            ProcessingProductKind.PixelData,
            definition,
            "product-v1",
            algorithms,
            "application/octet-stream");
        var descriptor = RecipeIdentityDescriptor.Create(
            definition.Name,
            definition.SemanticVersion,
            definition.ImplementationVersion,
            JsonSerializer.SerializeToElement(new { }));
        var product = new ProcessingProduct(
            contract.Role,
            contract.Variant,
            new string('8', 64),
            contract.MediaType!,
            null,
            new byte[] { 1 },
            new string('9', 64),
            new ProcessingRecipeIdentity(descriptor, new string('E', 64))
            {
                OperationKind = definition.OperationKind
            },
            algorithms,
            [],
            TimeSpan.Zero,
            new ProcessingCompatibilityIdentity("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile"))
        {
            SchemaVersion = contract.SchemaVersion
        };
        var job = new CentralDerivativeJob { GraphExecutionId = Guid.NewGuid() };
        var output = CentralDerivativeJobOutput.CreateFromFrozenPlan(job, 0, contract);
        job.Outputs.Add(output);

        Assert.AreEqual(
            job.Outputs.Single().ContractIdentitySha256,
            CentralProcessingGraphOutputBinding.ResolveContractIdentity(job, product));

        var mismatches = new[]
        {
            product with { Role = FrameArtifactRole.AnnotatedPreview },
            product with { Variant = "different" },
            product with { Kind = ProcessingProductKind.Metadata },
            product with { SchemaVersion = "different" },
            product with { MediaType = "application/json" },
            product with { Recipe = null! },
            product with
            {
                Recipe = product.Recipe with
                {
                    Descriptor = product.Recipe.Descriptor with { Name = "different" }
                }
            },
            product with
            {
                Recipe = product.Recipe with
                {
                    Descriptor = product.Recipe.Descriptor with { SemanticVersion = "2.0.0" }
                }
            },
            product with
            {
                Recipe = product.Recipe with
                {
                    Descriptor = product.Recipe.Descriptor with { ImplementationVersion = "different" }
                }
            },
            product with { Recipe = product.Recipe with { OperationKind = ProcessingOperationKind.Analyzer } },
            product with { Algorithms = [] }
        };
        foreach (var mismatch in mismatches)
        {
            Assert.ThrowsExactly<CentralDerivativeJobStateException>(() =>
                CentralProcessingGraphOutputBinding.ResolveContractIdentity(job, mismatch));
        }

        var invalidContracts = new[]
        {
            contract with { Role = FrameArtifactRole.AnnotatedPreview },
            contract with { Variant = "different" },
            contract with { ProductKind = ProcessingProductKind.Metadata }
        };
        foreach (var invalidContract in invalidContracts)
        {
            output.ContractJson = CaptureContractJson.Canonicalize(
                CaptureContractJson.SerializeToElement(invalidContract)).GetRawText();
            output.ContractIdentitySha256 = CentralDerivativeJobOutput.ComputeContractIdentitySha256(
                output.ContractJson);
            Assert.ThrowsExactly<CentralDerivativeJobStateException>(() =>
                CentralProcessingGraphOutputBinding.ResolveContractIdentity(job, product));
        }
        foreach (var invalidJson in new[] { "null", "{" })
        {
            output.ContractJson = invalidJson;
            output.ContractIdentitySha256 = CentralDerivativeJobOutput.ComputeContractIdentitySha256(invalidJson);
            Assert.ThrowsExactly<CentralDerivativeJobStateException>(() =>
                CentralProcessingGraphOutputBinding.ResolveContractIdentity(job, product));
        }
        output.ContractJson = CaptureContractJson.Canonicalize(
            CaptureContractJson.SerializeToElement(contract)).GetRawText();
        output.ContractIdentitySha256 = CentralDerivativeJobOutput.ComputeContractIdentitySha256(output.ContractJson);

        Assert.IsNull(CentralProcessingGraphOutputBinding.ResolveContractIdentity(
            new CentralDerivativeJob(), product));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            CentralProcessingGraphOutputBinding.ResolveContractIdentity(null!, product));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            CentralProcessingGraphOutputBinding.ResolveContractIdentity(job, null!));

        var openContract = new ProcessingGraphProductContract(
            product.Role,
            product.Variant,
            product.Kind,
            Recipe: null,
            SchemaVersion: product.SchemaVersion,
            MediaType: null);
        var openJob = new CentralDerivativeJob { GraphExecutionId = Guid.NewGuid() };
        openJob.Outputs.Add(CentralDerivativeJobOutput.CreateFromFrozenPlan(openJob, 0, openContract));
        Assert.AreEqual(
            openJob.Outputs.Single().ContractIdentitySha256,
            CentralProcessingGraphOutputBinding.ResolveContractIdentity(openJob, product with { Algorithms = [] }));
    }

    [TestMethod]
    public void ContractMatchesRejectsEveryDurableEvidenceMismatch()
    {
        var definition = new ProcessingRecipeDefinition(
            "preview", "1.0.0", "implementation-v1", ProcessingOperationKind.Transform);
        var algorithms = ImmutableArray.Create(new ProcessingAlgorithmIdentity("resize", "1"));
        var contract = new ProcessingGraphProductContract(
            FrameArtifactRole.Preview,
            "graph",
            ProcessingProductKind.PixelData,
            definition,
            "product-v1",
            algorithms,
            "application/octet-stream");
        var job = new CentralDerivativeJob { GraphExecutionId = Guid.NewGuid() };
        var slot = CentralDerivativeJobOutput.CreateFromFrozenPlan(job, 0, contract);
        var artifact = CreateArtifact(
            Guid.NewGuid(), Guid.NewGuid(), contract.Role, contract.Variant, "recipe-v1");
        artifact.Recipe = new CentralArtifactRecipe
        {
            Name = definition.Name,
            SemanticVersion = definition.SemanticVersion,
            ImplementationVersion = definition.ImplementationVersion,
            OptionsJson = "{}",
            OptionsSha256 = new string('A', 64)
        };
        var evidence = new CentralArtifactProcessingEvidence
        {
            Artifact = artifact,
            GraphProductContractIdentitySha256 = slot.ContractIdentitySha256,
            ProductKind = contract.ProductKind,
            ProductSchemaVersion = contract.SchemaVersion,
            ProductMediaType = contract.MediaType,
            RecipeOperationKind = definition.OperationKind,
            AlgorithmsJson = JsonSerializer.Serialize(algorithms)
        };
        var descriptor = RecipeIdentityDescriptor.Create(
            definition.Name,
            definition.SemanticVersion,
            definition.ImplementationVersion,
            JsonSerializer.SerializeToElement(new { }));
        var product = new ProcessingProduct(
            contract.Role,
            contract.Variant,
            new string('B', 64),
            contract.MediaType!,
            null,
            new byte[] { 1 },
            new string('C', 64),
            new ProcessingRecipeIdentity(descriptor, new string('D', 64))
            {
                OperationKind = definition.OperationKind
            },
            algorithms,
            [],
            TimeSpan.Zero,
            new ProcessingCompatibilityIdentity(
                "rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile"))
        {
            SchemaVersion = contract.SchemaVersion
        };

        Assert.IsTrue(CentralProcessingGraphOutputBinding.ContractMatches(slot, artifact, evidence, null));
        Assert.IsTrue(CentralProcessingGraphOutputBinding.ContractMatches(slot, artifact, evidence, product));

        void Reject(Action mutate, Action restore)
        {
            mutate();
            Assert.IsFalse(CentralProcessingGraphOutputBinding.ContractMatches(slot, artifact, evidence, null));
            restore();
        }

        Reject(() => slot.Role = FrameArtifactRole.Metadata, () => slot.Role = contract.Role);
        Reject(() => slot.Variant = "different", () => slot.Variant = contract.Variant);
        Reject(() => slot.ProductKind = ProcessingProductKind.Metadata, () => slot.ProductKind = contract.ProductKind);
        Reject(
            () => evidence.GraphProductContractIdentitySha256 = new string('0', 64),
            () => evidence.GraphProductContractIdentitySha256 = slot.ContractIdentitySha256);
        Reject(() => artifact.MediaType = "application/json", () => artifact.MediaType = contract.MediaType!);
        Reject(() => evidence.ProductKind = ProcessingProductKind.Metadata, () => evidence.ProductKind = contract.ProductKind);
        Reject(() => evidence.ProductSchemaVersion = "different", () => evidence.ProductSchemaVersion = contract.SchemaVersion);
        Reject(() => evidence.ProductMediaType = "application/json", () => evidence.ProductMediaType = contract.MediaType);
        Reject(() => artifact.Recipe = null, () => artifact.Recipe = new CentralArtifactRecipe
        {
            Name = definition.Name,
            SemanticVersion = definition.SemanticVersion,
            ImplementationVersion = definition.ImplementationVersion,
            OptionsJson = "{}",
            OptionsSha256 = new string('A', 64)
        });
        Reject(() => artifact.Recipe!.Name = "different", () => artifact.Recipe!.Name = definition.Name);
        Reject(
            () => artifact.Recipe!.SemanticVersion = "2.0.0",
            () => artifact.Recipe!.SemanticVersion = definition.SemanticVersion);
        Reject(
            () => artifact.Recipe!.ImplementationVersion = "different",
            () => artifact.Recipe!.ImplementationVersion = definition.ImplementationVersion);
        Reject(
            () => evidence.RecipeOperationKind = ProcessingOperationKind.Analyzer,
            () => evidence.RecipeOperationKind = definition.OperationKind);
        Reject(() => evidence.AlgorithmsJson = "[]", () => evidence.AlgorithmsJson = JsonSerializer.Serialize(algorithms));
        Reject(() => evidence.AlgorithmsJson = "{", () => evidence.AlgorithmsJson = JsonSerializer.Serialize(algorithms));

        evidence.ProductKind = null;
        Assert.IsTrue(CentralProcessingGraphOutputBinding.ContractMatches(slot, artifact, evidence, null));
        artifact.StructuredProduct = new CentralStructuredProcessingProduct
        {
            ProductKind = ProcessingProductKind.PixelData.ToString()
        };
        Assert.IsTrue(CentralProcessingGraphOutputBinding.ContractMatches(slot, artifact, evidence, null));
        artifact.StructuredProduct.ProductKind = "invalid";
        Assert.IsFalse(CentralProcessingGraphOutputBinding.ContractMatches(slot, artifact, evidence, null));
        artifact.StructuredProduct = null;
        evidence.ProductKind = contract.ProductKind;

        var openContract = new ProcessingGraphProductContract(
            contract.Role,
            contract.Variant,
            contract.ProductKind,
            Recipe: null,
            SchemaVersion: contract.SchemaVersion,
            MediaType: null);
        var openSlot = CentralDerivativeJobOutput.CreateFromFrozenPlan(job, 1, openContract);
        evidence.GraphProductContractIdentitySha256 = openSlot.ContractIdentitySha256;
        evidence.AlgorithmsJson = "[]";
        Assert.IsTrue(CentralProcessingGraphOutputBinding.ContractMatches(openSlot, artifact, evidence, null));
    }

    [TestMethod]
    public void FrozenContractHelpersRejectEveryIdentityComponent()
    {
        var definition = new ProcessingRecipeDefinition(
            "preview", "1.0.0", "implementation-v1", ProcessingOperationKind.Transform);
        var algorithm = new ProcessingAlgorithmIdentity("resize", "1");
        var contract = new ProcessingGraphProductContract(
            FrameArtifactRole.Preview,
            "graph",
            ProcessingProductKind.PixelData,
            definition,
            "product-v1",
            [algorithm],
            "application/octet-stream");
        var job = new CentralDerivativeJob { GraphExecutionId = Guid.NewGuid() };
        var slot = CentralDerivativeJobOutput.CreateFromFrozenPlan(job, 0, contract);
        Assert.IsTrue(CentralProcessingGraphOutputBinding.TryReadContract(slot, out var parsed));
        Assert.AreEqual(contract.Role, parsed!.Role);
        Assert.AreEqual(contract.Variant, parsed.Variant);
        Assert.AreEqual(contract.ProductKind, parsed.ProductKind);

        var identity = slot.ContractIdentitySha256;
        slot.ContractIdentitySha256 = new string('0', 64);
        Assert.IsFalse(CentralProcessingGraphOutputBinding.TryReadContract(slot, out parsed));
        Assert.IsNull(parsed);
        slot.ContractJson = "null";
        slot.ContractIdentitySha256 = CentralDerivativeJobOutput.ComputeContractIdentitySha256(slot.ContractJson);
        Assert.IsFalse(CentralProcessingGraphOutputBinding.TryReadContract(slot, out parsed));
        slot.ContractJson = "{";
        slot.ContractIdentitySha256 = CentralDerivativeJobOutput.ComputeContractIdentitySha256(slot.ContractJson);
        Assert.IsFalse(CentralProcessingGraphOutputBinding.TryReadContract(slot, out parsed));
        slot.ContractJson = CaptureContractJson.Canonicalize(
            CaptureContractJson.SerializeToElement(contract)).GetRawText();
        slot.ContractIdentitySha256 = identity;

        var descriptor = RecipeIdentityDescriptor.Create(
            definition.Name,
            definition.SemanticVersion,
            definition.ImplementationVersion,
            JsonSerializer.SerializeToElement(new { }));
        var recipe = new ProcessingRecipeIdentity(descriptor, new string('D', 64))
        {
            OperationKind = definition.OperationKind
        };
        var product = new ProcessingProduct(
            contract.Role,
            contract.Variant,
            new string('B', 64),
            contract.MediaType!,
            null,
            new byte[] { 1 },
            new string('C', 64),
            recipe,
            [algorithm],
            [],
            TimeSpan.Zero,
            new ProcessingCompatibilityIdentity(
                "rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile"))
        {
            SchemaVersion = contract.SchemaVersion
        };
        Assert.IsTrue(CentralProcessingGraphOutputBinding.ProductMatches(contract, product));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.ProductMatches(
            contract, product with { Role = FrameArtifactRole.Metadata }));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.ProductMatches(
            contract, product with { Variant = "different" }));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.ProductMatches(
            contract, product with { Kind = ProcessingProductKind.Metadata }));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.ProductMatches(
            contract, product with { SchemaVersion = "different" }));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.ProductMatches(
            contract, product with { MediaType = "application/json" }));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.ProductMatches(
            contract, product with { Recipe = null! }));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.ProductMatches(
            contract, product with { Algorithms = [] }));
        Assert.IsTrue(CentralProcessingGraphOutputBinding.ProductMatches(
            contract with { MediaType = null, Recipe = null, Algorithms = default },
            product with { Algorithms = [] }));

        Assert.IsTrue(CentralProcessingGraphOutputBinding.RecipeMatches(null, (ProcessingRecipeIdentity?)null));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.RecipeMatches(definition, (ProcessingRecipeIdentity?)null));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.RecipeMatches(
            definition, recipe with { Descriptor = descriptor with { Name = "different" } }));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.RecipeMatches(
            definition, recipe with { Descriptor = descriptor with { SemanticVersion = "2.0.0" } }));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.RecipeMatches(
            definition, recipe with { Descriptor = descriptor with { ImplementationVersion = "different" } }));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.RecipeMatches(
            definition, recipe with { OperationKind = ProcessingOperationKind.Analyzer }));

        var storedRecipe = new CentralArtifactRecipe
        {
            Name = definition.Name,
            SemanticVersion = definition.SemanticVersion,
            ImplementationVersion = definition.ImplementationVersion
        };
        Assert.IsTrue(CentralProcessingGraphOutputBinding.RecipeMatches(
            null, (CentralArtifactRecipe?)null, null));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.RecipeMatches(
            definition, (CentralArtifactRecipe?)null, definition.OperationKind));
        storedRecipe.Name = "different";
        Assert.IsFalse(CentralProcessingGraphOutputBinding.RecipeMatches(
            definition, storedRecipe, definition.OperationKind));
        storedRecipe.Name = definition.Name;
        storedRecipe.SemanticVersion = "2.0.0";
        Assert.IsFalse(CentralProcessingGraphOutputBinding.RecipeMatches(
            definition, storedRecipe, definition.OperationKind));
        storedRecipe.SemanticVersion = definition.SemanticVersion;
        storedRecipe.ImplementationVersion = "different";
        Assert.IsFalse(CentralProcessingGraphOutputBinding.RecipeMatches(
            definition, storedRecipe, definition.OperationKind));
        storedRecipe.ImplementationVersion = definition.ImplementationVersion;
        Assert.IsFalse(CentralProcessingGraphOutputBinding.RecipeMatches(
            definition, storedRecipe, ProcessingOperationKind.Analyzer));
        Assert.IsTrue(CentralProcessingGraphOutputBinding.RecipeMatches(
            definition, storedRecipe, definition.OperationKind));
        Assert.IsTrue(CentralProcessingGraphOutputBinding.SequenceEqual(default, []));
        Assert.IsTrue(CentralProcessingGraphOutputBinding.SequenceEqual([algorithm], [algorithm]));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.SequenceEqual([algorithm], []));
    }

    /// <summary>
    /// A contract that omits (or pins an empty) <c>algorithms</c> set leaves the product's algorithms unconstrained
    /// for identity resolution, product matching, and durable evidence matching; a non-empty pinned set still
    /// requires exact equality.
    /// </summary>
    [TestMethod]
    public void EmptyContractAlgorithmsLeaveProductAlgorithmsUnconstrained()
    {
        var definition = new ProcessingRecipeDefinition(
            "preview", "1.0.0", "implementation-v1", ProcessingOperationKind.Transform);
        var pinned = ImmutableArray.Create(new ProcessingAlgorithmIdentity("resize", "1"));
        var produced = ImmutableArray.Create(
            new ProcessingAlgorithmIdentity("stretch", "2"),
            new ProcessingAlgorithmIdentity("encode", "3"));
        var openContract = new ProcessingGraphProductContract(
            FrameArtifactRole.Preview,
            "graph",
            ProcessingProductKind.PixelData,
            definition,
            "product-v1",
            default,
            "application/octet-stream");
        var pinnedContract = openContract with { Algorithms = pinned };
        var descriptor = RecipeIdentityDescriptor.Create(
            definition.Name,
            definition.SemanticVersion,
            definition.ImplementationVersion,
            JsonSerializer.SerializeToElement(new { }));
        var product = new ProcessingProduct(
            openContract.Role,
            openContract.Variant,
            new string('B', 64),
            openContract.MediaType!,
            null,
            new byte[] { 1 },
            new string('C', 64),
            new ProcessingRecipeIdentity(descriptor, new string('D', 64))
            {
                OperationKind = definition.OperationKind
            },
            produced,
            [],
            TimeSpan.Zero,
            new ProcessingCompatibilityIdentity(
                "rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile"))
        {
            SchemaVersion = openContract.SchemaVersion
        };

        Assert.IsTrue(CentralProcessingGraphOutputBinding.AlgorithmsMatch(default, produced));
        Assert.IsTrue(CentralProcessingGraphOutputBinding.AlgorithmsMatch([], produced));
        Assert.IsTrue(CentralProcessingGraphOutputBinding.AlgorithmsMatch(pinned, pinned));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.AlgorithmsMatch(pinned, produced));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.AlgorithmsMatch(pinned, []));

        Assert.IsTrue(CentralProcessingGraphOutputBinding.ProductMatches(openContract, product));
        Assert.IsTrue(CentralProcessingGraphOutputBinding.ProductMatches(openContract, product with { Algorithms = [] }));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.ProductMatches(pinnedContract, product));
        Assert.IsTrue(CentralProcessingGraphOutputBinding.ProductMatches(pinnedContract, product with { Algorithms = pinned }));

        // Contract JSON that omits the algorithms property entirely resolves the same way as an empty array.
        var job = new CentralDerivativeJob { GraphExecutionId = Guid.NewGuid() };
        var slot = CentralDerivativeJobOutput.CreateFromFrozenPlan(job, 0, openContract);
        StringAssert.Contains(slot.ContractJson, "\"algorithms\":[]");
        slot.ContractJson = slot.ContractJson.Replace("\"algorithms\":[],", string.Empty, StringComparison.Ordinal);
        Assert.IsFalse(slot.ContractJson.Contains("algorithms", StringComparison.Ordinal));
        slot.ContractIdentitySha256 = CentralDerivativeJobOutput.ComputeContractIdentitySha256(slot.ContractJson);
        job.Outputs.Add(slot);
        Assert.AreEqual(
            slot.ContractIdentitySha256,
            CentralProcessingGraphOutputBinding.ResolveContractIdentity(job, product));

        var pinnedJob = new CentralDerivativeJob { GraphExecutionId = Guid.NewGuid() };
        pinnedJob.Outputs.Add(CentralDerivativeJobOutput.CreateFromFrozenPlan(pinnedJob, 0, pinnedContract));
        Assert.ThrowsExactly<CentralDerivativeJobStateException>(() =>
            CentralProcessingGraphOutputBinding.ResolveContractIdentity(pinnedJob, product));

        // Durable evidence carrying non-empty algorithms binds to the open slot and is rejected by the pinned slot.
        var artifact = CreateArtifact(
            Guid.NewGuid(), Guid.NewGuid(), openContract.Role, openContract.Variant, "recipe-v1");
        artifact.Recipe = new CentralArtifactRecipe
        {
            Name = definition.Name,
            SemanticVersion = definition.SemanticVersion,
            ImplementationVersion = definition.ImplementationVersion,
            OptionsJson = "{}",
            OptionsSha256 = new string('A', 64)
        };
        var evidence = new CentralArtifactProcessingEvidence
        {
            Artifact = artifact,
            GraphProductContractIdentitySha256 = slot.ContractIdentitySha256,
            ProductKind = openContract.ProductKind,
            ProductSchemaVersion = openContract.SchemaVersion,
            ProductMediaType = openContract.MediaType,
            RecipeOperationKind = definition.OperationKind,
            AlgorithmsJson = JsonSerializer.Serialize(produced)
        };
        Assert.IsTrue(CentralProcessingGraphOutputBinding.ContractMatches(slot, artifact, evidence, null));
        Assert.IsTrue(CentralProcessingGraphOutputBinding.ContractMatches(slot, artifact, evidence, product));
        var pinnedSlot = pinnedJob.Outputs.Single();
        evidence.GraphProductContractIdentitySha256 = pinnedSlot.ContractIdentitySha256;
        Assert.IsFalse(CentralProcessingGraphOutputBinding.ContractMatches(pinnedSlot, artifact, evidence, null));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.ContractMatches(pinnedSlot, artifact, evidence, product));
    }

    [TestMethod]
    public async Task BindAsyncBindsAnnotationEvidenceAgainstTheFrozenExpectedIdentityOnly()
    {
        await using var context = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        var now = DateTimeOffset.UtcNow;
        var deviceId = Guid.NewGuid();
        var provenance = new SceneProvenance(
            "scene-1", "rig-v1", "catalog", "1", new string('A', 64), "model", "1", "1", "1",
            Objects: [new ProjectedObjectProvenance("star:1", "Vega", 10, 12, 0.03)]);
        var frame = new CentralFrame
        {
            RegistrationId = Guid.NewGuid(),
            DevicePublicId = deviceId,
            ObservatoryId = Guid.NewGuid(),
            AgentId = "agent",
            FrameId = Guid.NewGuid(),
            CapturedAtUtc = now,
            FirstReceivedAtUtc = now,
            // Enriched after expansion: the execution froze "no annotation" (expected == requested below).
            SceneProvenanceJson = JsonSerializer.Serialize(provenance, JsonSerializerOptions.Web)
        };
        var anchor = CreateArtifact(frame.Id, deviceId, FrameArtifactRole.Raw, "raw", "raw-v1");
        anchor.Frame = frame;
        var result = CreateArtifact(frame.Id, deviceId, FrameArtifactRole.AnnotatedPreview,
            CentralDerivativeRecipeCatalog.AnnotatedPreviewVariant, CentralDerivativeRecipeCatalog.AnnotatedPreviewRecipeVersion);
        result.Frame = frame;
        result.MediaType = "image/jpeg";
        var execution = CreateExecution(anchor, now);
        var job = CreateJob(execution, anchor, now);
        var options = BuiltInProcessingRecipes.NormalizeOptions(
            BuiltInProcessingRecipes.Annotation,
            CaptureContractJson.SerializeToElement(new AnnotationRecipeOptions()));
        var selector = ProcessingInputSelector.Raw();
        var requested = BuiltInProcessingRecipes.CreateRequestedIdentity(
            BuiltInProcessingRecipes.Annotation, options, selector);
        job.RecipeName = BuiltInProcessingRecipes.Annotation;
        job.TargetRole = result.Role;
        job.TargetVariant = result.Variant!;
        job.TargetRecipeVersion = result.RecipeVersion;
        job.RecipeOptionsJson = CaptureContractJson.Canonicalize(options).GetRawText();
        job.InputSelectorJson = CaptureContractJson.Canonicalize(
            CaptureContractJson.SerializeToElement(selector)).GetRawText();
        job.RequestedRecipeIdentitySha256 = requested.IdentitySha256;
        job.ExpectedRecipeIdentitySha256 = requested.IdentitySha256;
        _ = BuiltInProcessingRecipes.TryGetDefinition(BuiltInProcessingRecipes.Annotation, out var recipeDefinition);
        var contract = new ProcessingGraphProductContract(
            result.Role, result.Variant!, ProcessingProductKind.PixelData, recipeDefinition, null, [], "image/jpeg");
        var output = CentralDerivativeJobOutput.CreateFromFrozenPlan(job, 0, contract);
        job.Outputs.Add(output);
        execution.Jobs.Add(job);
        var annotation = CentralDerivativeJobExecutor.CreateAnnotation(frame.SceneProvenanceJson);
        Assert.IsNotNull(annotation);
        var annotated = BuiltInProcessingRecipes.CreateExecutionIdentity(
            BuiltInProcessingRecipes.Annotation, options, selector, annotation);
        Assert.AreNotEqual(requested.IdentitySha256, annotated.IdentitySha256,
            "the runtime scene annotation is part of the actual recipe identity");
        // The lease withholds provenance the execution never froze, so the executor cannot produce annotated evidence.
        Assert.IsNull(CentralDerivativeJobExecutor.ResolveLeaseSceneProvenance(
            execution.Id, job.RecipeName, job.RequestedRecipeIdentitySha256, job.ExpectedRecipeIdentitySha256,
            frame.SceneProvenanceJson));
        Assert.AreEqual(frame.SceneProvenanceJson, CentralDerivativeJobExecutor.ResolveLeaseSceneProvenance(
            execution.Id, job.RecipeName, job.RequestedRecipeIdentitySha256, annotated.IdentitySha256,
            frame.SceneProvenanceJson), "a frozen annotated identity came from the frame's write-once provenance");
        Assert.AreEqual(frame.SceneProvenanceJson, CentralDerivativeJobExecutor.ResolveLeaseSceneProvenance(
            null, job.RecipeName, job.RequestedRecipeIdentitySha256, job.ExpectedRecipeIdentitySha256,
            frame.SceneProvenanceJson), "legacy jobs keep the live frame provenance");
        Assert.AreEqual(frame.SceneProvenanceJson, CentralDerivativeJobExecutor.ResolveLeaseSceneProvenance(
            execution.Id, BuiltInProcessingRecipes.EncodedPreview, job.RequestedRecipeIdentitySha256,
            job.RequestedRecipeIdentitySha256, frame.SceneProvenanceJson),
            "only annotation-bearing recipes freeze the annotation decision");
        result.Recipe = new CentralArtifactRecipe
        {
            Name = requested.Descriptor.Name,
            SemanticVersion = requested.Descriptor.SemanticVersion,
            ImplementationVersion = requested.Descriptor.ImplementationVersion,
            OptionsJson = "{}",
            OptionsSha256 = requested.Descriptor.OptionsSha256
        };
        var outputIdentity = ProcessingIdentity.CreateOutputIdentity(
            result.Role, result.Variant!, requested.IdentitySha256, [anchor.ArtifactId]);
        var evidence = new CentralArtifactProcessingEvidence
        {
            Artifact = result,
            CentralArtifactId = result.Id,
            DevicePublicId = deviceId,
            OutputIdentitySha256 = outputIdentity,
            RequestedRecipeIdentitySha256 = requested.IdentitySha256,
            RecipeIdentitySha256 = requested.IdentitySha256,
            RecipeOperationKind = requested.OperationKind,
            GraphProductContractIdentitySha256 = output.ContractIdentitySha256,
            ProductKind = ProcessingProductKind.PixelData,
            ProductMediaType = "image/jpeg",
            AlgorithmsJson = "[]",
            CompatibilityJson = "{}",
            CentralDerivativeJobId = job.Id,
            Job = job,
            CreatedAtUtc = now
        };
        context.AddRange(frame, anchor, result, execution, evidence);
        await context.SaveChangesAsync().ConfigureAwait(false);
        execution.ExpandedAtUtc = now;
        await context.SaveChangesAsync().ConfigureAwait(false);

        // Evidence carrying the frozen (unannotated) identity binds even though the frame is now enriched.
        await CentralProcessingGraphOutputBinding.BindAsync(
            context, job.Id, result.Id, null, now, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(result.Id, output.ResultCentralArtifactId);
        Assert.AreEqual(outputIdentity, output.ResultOutputIdentitySha256);

        // Annotated evidence contradicts the frozen decision (and the SQL binding trigger) and is rejected.
        output.ResultCentralArtifactId = null;
        output.ResultOutputIdentitySha256 = null;
        output.BoundAtUtc = null;
        evidence.RecipeIdentitySha256 = annotated.IdentitySha256;
        await context.SaveChangesAsync().ConfigureAwait(false);
        await Assert.ThrowsExactlyAsync<CentralDerivativeJobStateException>(() =>
            CentralProcessingGraphOutputBinding.BindAsync(
                context, job.Id, result.Id, null, now, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNull(output.ResultCentralArtifactId);
    }

    [TestMethod]
    public void ContractMatchesAdoptsLegacyEvidenceOnlyThroughDurableValidation()
    {
        var definition = new ProcessingRecipeDefinition(
            "preview", "1.0.0", "implementation-v1", ProcessingOperationKind.Transform);
        var algorithms = ImmutableArray.Create(new ProcessingAlgorithmIdentity("resize", "1"));
        var contract = new ProcessingGraphProductContract(
            FrameArtifactRole.Preview,
            "graph",
            ProcessingProductKind.PixelData,
            definition,
            "product-v1",
            algorithms,
            "application/octet-stream");
        var job = new CentralDerivativeJob { GraphExecutionId = Guid.NewGuid() };
        var slot = CentralDerivativeJobOutput.CreateFromFrozenPlan(job, 0, contract);
        var artifact = CreateArtifact(
            Guid.NewGuid(), Guid.NewGuid(), contract.Role, contract.Variant, "recipe-v1");
        artifact.Recipe = new CentralArtifactRecipe
        {
            Name = definition.Name,
            SemanticVersion = definition.SemanticVersion,
            ImplementationVersion = definition.ImplementationVersion,
            OptionsJson = "{}",
            OptionsSha256 = new string('A', 64)
        };
        // Legacy-scheduler evidence: durable product facts recorded, no graph contract identity, non-graph job.
        var legacyJob = new CentralDerivativeJob();
        var evidence = new CentralArtifactProcessingEvidence
        {
            Artifact = artifact,
            Job = legacyJob,
            CentralDerivativeJobId = legacyJob.Id,
            GraphProductContractIdentitySha256 = null,
            ProductKind = contract.ProductKind,
            ProductSchemaVersion = contract.SchemaVersion,
            ProductMediaType = contract.MediaType,
            RecipeOperationKind = definition.OperationKind,
            AlgorithmsJson = JsonSerializer.Serialize(algorithms)
        };
        var descriptor = RecipeIdentityDescriptor.Create(
            definition.Name, definition.SemanticVersion, definition.ImplementationVersion,
            JsonSerializer.SerializeToElement(new { }));
        var product = new ProcessingProduct(
            contract.Role,
            contract.Variant,
            new string('B', 64),
            contract.MediaType!,
            null,
            new byte[] { 1 },
            new string('C', 64),
            new ProcessingRecipeIdentity(descriptor, new string('D', 64)) { OperationKind = definition.OperationKind },
            algorithms,
            [],
            TimeSpan.Zero,
            new ProcessingCompatibilityIdentity(
                "rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile"))
        {
            SchemaVersion = contract.SchemaVersion
        };

        Assert.IsTrue(CentralProcessingGraphOutputBinding.ContractMatches(slot, artifact, evidence, null));
        Assert.IsTrue(CentralProcessingGraphOutputBinding.ContractMatches(slot, artifact, evidence, product));

        void Reject(Action mutate, Action restore)
        {
            mutate();
            Assert.IsFalse(CentralProcessingGraphOutputBinding.ContractMatches(slot, artifact, evidence, null));
            Assert.IsFalse(CentralProcessingGraphOutputBinding.ContractMatches(slot, artifact, evidence, product));
            restore();
        }

        Reject(() => evidence.RecipeOperationKind = ProcessingOperationKind.Analyzer,
            () => evidence.RecipeOperationKind = definition.OperationKind);
        Reject(() => evidence.ProductSchemaVersion = "different", () => evidence.ProductSchemaVersion = contract.SchemaVersion);
        Reject(() => evidence.ProductMediaType = "application/json", () => evidence.ProductMediaType = contract.MediaType);
        Reject(() => evidence.ProductKind = ProcessingProductKind.Metadata, () => evidence.ProductKind = contract.ProductKind);
        Reject(() => artifact.Recipe!.ImplementationVersion = "different",
            () => artifact.Recipe!.ImplementationVersion = definition.ImplementationVersion);
        Reject(() => evidence.AlgorithmsJson = "[]", () => evidence.AlgorithmsJson = JsonSerializer.Serialize(algorithms));
        // Untagged evidence of a graph-owned job is inconsistent, not legacy (mirrors the SQL adoption predicate).
        Reject(() => legacyJob.GraphExecutionId = Guid.NewGuid(), () => legacyJob.GraphExecutionId = null);
        Reject(() => evidence.Job = null, () => evidence.Job = legacyJob);

        // Graph-tagged evidence for a different contract is never adopted, even when the durable facts line up.
        evidence.GraphProductContractIdentitySha256 = new string('0', 64);
        Assert.IsFalse(CentralProcessingGraphOutputBinding.ContractMatches(slot, artifact, evidence, null));
        Assert.IsFalse(CentralProcessingGraphOutputBinding.ContractMatches(slot, artifact, evidence, product));
    }

    [TestMethod]
    public void ValidateExistingAdoptsLegacyEvidenceForGraphSlotsAndRejectsForeignGraphContracts()
    {
        var frameId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var anchor = CreateArtifact(frameId, deviceId, FrameArtifactRole.Raw, "raw", "raw-v1");
        var result = CreateArtifact(frameId, deviceId, FrameArtifactRole.Preview, "graph", "recipe-v1");
        var definition = new ProcessingRecipeDefinition(
            "preview", "1.0.0", "implementation-v1", ProcessingOperationKind.Transform);
        var descriptor = RecipeIdentityDescriptor.Create(
            definition.Name, definition.SemanticVersion, definition.ImplementationVersion,
            JsonSerializer.SerializeToElement(new { }));
        var payload = new byte[] { 1, 2, 3 };
        var recipeIdentity = new ProcessingRecipeIdentity(descriptor, new string('D', 64))
        {
            OperationKind = definition.OperationKind
        };
        var product = new ProcessingProduct(
            result.Role,
            result.Variant!,
            new string('B', 64),
            result.MediaType,
            null,
            payload,
            ProcessingIdentity.ComputePayloadSha256(payload),
            recipeIdentity,
            [],
            [anchor.ArtifactId],
            TimeSpan.Zero,
            new ProcessingCompatibilityIdentity(
                "rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile"));
        result.ByteLength = payload.Length;
        result.ChecksumSha256 = product.ChecksumSha256;
        result.Sources.Add(new CentralArtifactSource
        {
            Ordinal = 0,
            SourceArtifactId = anchor.ArtifactId,
            ResolvedCentralArtifactId = anchor.Id
        });
        const string bucket = "skymonitor-artifacts";
        var objectKey = result.StorageReference[$"s3://{bucket}/".Length..];
        var inputSet = new string('1', 64);
        var legacyJob = new CentralDerivativeJob { InputSetIdentitySha256 = inputSet };
        var evidence = new CentralArtifactProcessingEvidence
        {
            Artifact = result,
            DevicePublicId = deviceId,
            OutputIdentitySha256 = product.OutputIdentitySha256,
            RequestedRecipeIdentitySha256 = new string('E', 64),
            RecipeIdentitySha256 = recipeIdentity.IdentitySha256,
            RecipeOperationKind = definition.OperationKind,
            GraphProductContractIdentitySha256 = null,
            ProductKind = product.Kind,
            ProductSchemaVersion = product.SchemaVersion,
            ProductMediaType = product.MediaType,
            AlgorithmsJson = "[]",
            CentralDerivativeJobId = legacyJob.Id,
            Job = legacyJob
        };
        var lease = new CentralDerivativeJobLease(
            JobId: Guid.NewGuid(),
            LeaseToken: Guid.NewGuid(),
            WorkerId: "worker",
            LeaseExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(1),
            SourceDevicePublicId: deviceId,
            SourceArtifactId: anchor.ArtifactId,
            SourceRole: anchor.Role,
            SourceRecipeVersion: anchor.RecipeVersion,
            SourceContentUri: anchor.StorageReference,
            SourceChecksumSha256: anchor.ChecksumSha256,
            SourceMediaType: anchor.MediaType,
            FrameId: Guid.NewGuid(),
            AgentId: "agent",
            CapturedAtUtc: DateTimeOffset.UtcNow,
            RigProfileVersion: null,
            SceneProvenanceJson: null,
            TargetRole: result.Role,
            TargetRecipeVersion: result.RecipeVersion,
            TargetVariant: result.Variant!,
            RecipeName: definition.Name,
            RecipeOptionsJson: "{}",
            InputSelectorJson: "{}",
            RequestedRecipeIdentitySha256: evidence.RequestedRecipeIdentitySha256,
            RequestIdentitySha256: new string('F', 64),
            TraceParent: null,
            TraceState: null,
            AttemptCount: 1,
            MaxAttempts: 3,
            Inputs:
            [
                new CentralDerivativeJobLeaseInput(
                    0, anchor.Id, deviceId, anchor.ArtifactId, anchor.Role, anchor.RecipeVersion,
                    anchor.ChecksumSha256, anchor.MediaType, anchor.ByteLength, Guid.NewGuid(), "agent", null,
                    DateTimeOffset.UtcNow, new string('0', 64))
            ],
            ExpectedRecipeIdentitySha256: evidence.RequestedRecipeIdentitySha256,
            InputSetIdentitySha256: inputSet,
            GraphExecutionId: Guid.NewGuid());
        var graphContractIdentity = new string('9', 64);

        // Legacy evidence with matching frozen input set, recipe, kind, media type, role, variant and checksum binds.
        CentralDerivativeOutputWriter.ValidateExisting(
            evidence, lease, product, result.ArtifactId, objectKey, bucket, graphContractIdentity);

        legacyJob.InputSetIdentitySha256 = new string('2', 64);
        Assert.ThrowsExactly<CentralDerivativeJobStateException>(() => CentralDerivativeOutputWriter.ValidateExisting(
            evidence, lease, product, result.ArtifactId, objectKey, bucket, graphContractIdentity));
        legacyJob.InputSetIdentitySha256 = inputSet;

        evidence.RecipeIdentitySha256 = new string('5', 64);
        Assert.ThrowsExactly<CentralDerivativeJobStateException>(() => CentralDerivativeOutputWriter.ValidateExisting(
            evidence, lease, product, result.ArtifactId, objectKey, bucket, graphContractIdentity));
        evidence.RecipeIdentitySha256 = recipeIdentity.IdentitySha256;

        evidence.ProductMediaType = "application/json";
        Assert.ThrowsExactly<CentralDerivativeJobStateException>(() => CentralDerivativeOutputWriter.ValidateExisting(
            evidence, lease, product, result.ArtifactId, objectKey, bucket, graphContractIdentity));
        evidence.ProductMediaType = product.MediaType;

        // Untagged evidence of a graph-owned job is inconsistent rather than adoptable legacy evidence.
        legacyJob.GraphExecutionId = Guid.NewGuid();
        Assert.ThrowsExactly<CentralDerivativeJobStateException>(() => CentralDerivativeOutputWriter.ValidateExisting(
            evidence, lease, product, result.ArtifactId, objectKey, bucket, graphContractIdentity));
        legacyJob.GraphExecutionId = null;
        Assert.IsTrue(CentralDerivativeOutputWriter.IsLegacyEvidence(evidence));

        // Evidence tagged with another graph contract stays rejected.
        evidence.GraphProductContractIdentitySha256 = new string('0', 64);
        Assert.ThrowsExactly<CentralDerivativeJobStateException>(() => CentralDerivativeOutputWriter.ValidateExisting(
            evidence, lease, product, result.ArtifactId, objectKey, bucket, graphContractIdentity));
        evidence.GraphProductContractIdentitySha256 = graphContractIdentity;
        CentralDerivativeOutputWriter.ValidateExisting(
            evidence, lease, product, result.ArtifactId, objectKey, bucket, graphContractIdentity);
    }

    private static CentralArtifact CreateArtifact(
        Guid frameId,
        Guid deviceId,
        FrameArtifactRole role,
        string variant,
        string recipeVersion)
        => new()
        {
            CentralFrameId = frameId,
            DevicePublicId = deviceId,
            ArtifactId = Guid.NewGuid(),
            Role = role,
            Variant = variant,
            RecipeVersion = recipeVersion,
            ManifestSchemaVersion = "v1",
            MediaType = "application/octet-stream",
            ByteLength = 1,
            ChecksumSha256 = new string('9', 64),
            StorageReference = $"s3://skymonitor-artifacts/{Guid.NewGuid():N}",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ReceivedAtUtc = DateTimeOffset.UtcNow,
            ObjectState = CentralArtifactObjectState.Available,
            ReconstructionState = CentralReconstructionState.Complete
        };

    private static CentralProcessingGraphExecution CreateExecution(CentralArtifact anchor, DateTimeOffset now)
        => new()
        {
            ExecutionClass = CentralProcessingGraphExecutionClass.Replay,
            Status = CentralProcessingGraphExecutionStatus.Pending,
            RequestIdentitySha256 = new string('A', 64),
            RevisionId = Guid.NewGuid(),
            DefinitionIdentitySha256 = new string('B', 64),
            FrozenDefinitionJson = "{}",
            CentralPlanIdentitySha256 = new string('C', 64),
            FrozenCentralPlanJson = "{}",
            ExpectedNodeCount = 1,
            ExpectedOutputCount = 1,
            ObservatoryId = Guid.NewGuid(),
            LogicalCameraId = Guid.NewGuid(),
            LogicalCameraInstallationId = Guid.NewGuid(),
            InstallationPublicId = Guid.NewGuid(),
            AnchorSourceArtifact = anchor,
            AnchorSourceCentralArtifactId = anchor.Id,
            AnchorSourceArtifactId = anchor.ArtifactId,
            AnchorSourceChecksumSha256 = anchor.ChecksumSha256,
            Trigger = CentralProcessingGraphTrigger.Replay,
            ActorId = "operator",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            ReasonCode = "test",
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

    private static CentralDerivativeJob CreateJob(
        CentralProcessingGraphExecution execution,
        CentralArtifact anchor,
        DateTimeOffset now)
        => new()
        {
            GraphExecution = execution,
            GraphExecutionId = execution.Id,
            GraphNodeId = "node",
            GraphNodeOrdinal = 0,
            SharedNodePlanIdentitySha256 = new string('D', 64),
            FrozenNodePlanJson = "{}",
            GraphFailurePolicy = ProcessingGraphNodeFailurePolicy.Required,
            SourceArtifact = anchor,
            SourceCentralArtifactId = anchor.Id,
            TargetRole = FrameArtifactRole.Preview,
            TargetRecipeVersion = "recipe-v1",
            TargetVariant = "graph",
            RecipeName = "preview",
            RequestedRecipeIdentitySha256 = new string('E', 64),
            ExpectedRecipeIdentitySha256 = new string('E', 64),
            RequestIdentitySha256 = new string('F', 64),
            Status = CentralDerivativeJobStatus.Leased,
            InputSetIdentitySha256 = new string('1', 64),
            AttemptCount = 1,
            MaxAttempts = 3,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
}
