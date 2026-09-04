using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.ProcessingRunner.Contracts;

namespace HVO.SkyMonitor.ProcessingRunner.Tests;

[TestClass]
public sealed class ProcessingRunnerContractsTests
{
    private static readonly string[] ExpectedLabels = ["rack:a", "site:lab"];

    internal static ProcessingRunnerWarmupStages CompletedWarmup()
        => ProcessingRunnerWarmupStages.NotRun() with
        {
            RuntimeJit = new("runtime-jit", ProcessingRunnerWarmupStatus.Completed, TimeSpan.FromMilliseconds(10), "ok"),
            NativeLibraries = new("native-libraries", ProcessingRunnerWarmupStatus.Completed, TimeSpan.FromMilliseconds(20), "ok")
        };

    [TestMethod]
    [TestCategory("Unit")]
    public void CurrentProcessCapabilitiesAdvertiseVersionMatchedBuiltInRecipes()
    {
        var capabilities = ProcessingRunnerCapabilities.CreateForCurrentProcess(
            2, 1024, "gpu-lab", "interactive", ["site:lab", "rack:a"], CompletedWarmup());

        Assert.AreEqual(ProcessingRunnerProtocol.Version, capabilities.ProtocolVersion);
        Assert.AreEqual(ProcessingRunnerWarmState.Warm, capabilities.WarmState);
        CollectionAssert.AreEqual(ExpectedLabels, capabilities.Labels.ToArray());
        CollectionAssert.AreEquivalent(
            ProcessingRunnerCapabilities.BuiltInRecipeNames,
            capabilities.ResolveVersionMatchedRecipes().ToArray());
        var json = JsonSerializer.Serialize(capabilities, ProcessingRunnerProtocol.SerializerOptions);
        var roundTrip = JsonSerializer.Deserialize<ProcessingRunnerCapabilities>(json, ProcessingRunnerProtocol.SerializerOptions);
        Assert.AreEqual(json, JsonSerializer.Serialize(roundTrip, ProcessingRunnerProtocol.SerializerOptions));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ColdCapabilitiesReportColdAndDegradedWarmupReportsDegraded()
    {
        var cold = ProcessingRunnerCapabilities.CreateForCurrentProcess(1, 1024, null, null, null, null);
        Assert.AreEqual(ProcessingRunnerWarmState.Cold, cold.WarmState);
        var failed = ProcessingRunnerWarmupStages.NotRun() with
        {
            RuntimeJit = new("runtime-jit", ProcessingRunnerWarmupStatus.Failed, TimeSpan.Zero, "boom")
        };
        var degraded = ProcessingRunnerCapabilities.CreateForCurrentProcess(1, 1024, null, null, null, failed);
        Assert.AreEqual(ProcessingRunnerWarmState.Degraded, degraded.WarmState);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void RecipeVersionMismatchIsNeverMatched()
    {
        var capabilities = ProcessingRunnerCapabilities.CreateForCurrentProcess(1, 1024, null, null, null, null);
        var altered = capabilities with
        {
            BuiltInRecipes = capabilities.BuiltInRecipes
                .Select(recipe => recipe.Name == BuiltInProcessingRecipes.EncodedPreview
                    ? recipe with { ImplementationVersion = recipe.ImplementationVersion + "-fork" }
                    : recipe)
                .ToArray()
        };
        var matched = altered.ResolveVersionMatchedRecipes();
        Assert.IsFalse(matched.Contains(BuiltInProcessingRecipes.EncodedPreview));
        Assert.IsTrue(matched.Contains(BuiltInProcessingRecipes.ImageQuality));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CapabilityValidationRejectsProtocolViolations()
    {
        var valid = ProcessingRunnerCapabilities.CreateForCurrentProcess(1, 1024, null, null, null, null);
        Assert.ThrowsExactly<ProcessingRunnerProtocolException>(() => (valid with { ProtocolVersion = 2 }).Validate());
        Assert.ThrowsExactly<ProcessingRunnerProtocolException>(() => (valid with { MaxConcurrency = 0 }).Validate());
        Assert.ThrowsExactly<ProcessingRunnerProtocolException>(() => (valid with { Labels = ["Bad Label"] }).Validate());
        Assert.ThrowsExactly<ProcessingRunnerProtocolException>(() => (valid with { BuiltInRecipes = [] }).Validate());
        Assert.ThrowsExactly<ProcessingRunnerProtocolException>(() =>
            (valid with { MaxTransferBytes = ProcessingRunnerProtocol.MaximumTransferBytes + 1 }).Validate());
        Assert.ThrowsExactly<ProcessingRunnerProtocolException>(() => (valid with { ResourceClass = "GPU" }).Validate());
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void JobRequirementsMatchEveryDeclaredAxis()
    {
        var capabilities = ProcessingRunnerCapabilities.CreateForCurrentProcess(
            1, 1024, "gpu-lab", "interactive", ["site:lab"], null, gpuAvailable: true);
        var matched = capabilities.ResolveVersionMatchedRecipes();
        Assert.IsTrue(new ProcessingRunnerJobRequirement(BuiltInProcessingRecipes.EncodedPreview).IsSatisfiedBy(capabilities, matched));
        Assert.IsTrue(new ProcessingRunnerJobRequirement(
            BuiltInProcessingRecipes.EncodedPreview, "gpu-lab", "interactive", true, capabilities.ProcessArchitecture, ["site:lab"])
            .IsSatisfiedBy(capabilities, matched));
        Assert.IsFalse(new ProcessingRunnerJobRequirement(BuiltInProcessingRecipes.EncodedPreview, ResourceClass: "standard").IsSatisfiedBy(capabilities, matched));
        Assert.IsFalse(new ProcessingRunnerJobRequirement(BuiltInProcessingRecipes.EncodedPreview, LatencyClass: "background").IsSatisfiedBy(capabilities, matched));
        Assert.IsFalse(new ProcessingRunnerJobRequirement(BuiltInProcessingRecipes.EncodedPreview, Labels: ["site:remote"]).IsSatisfiedBy(capabilities, matched));
        Assert.IsFalse(new ProcessingRunnerJobRequirement(BuiltInProcessingRecipes.EncodedPreview, ProcessArchitecture: "Wasm").IsSatisfiedBy(capabilities, matched));
        Assert.IsFalse(new ProcessingRunnerJobRequirement("central-transient-validation").IsSatisfiedBy(capabilities, matched));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ArtifactAndProductMetadataRoundTripAndVerifyPayloads()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var artifact = new ProcessingArtifact(
            Guid.NewGuid(), FrameArtifactRole.Raw, "native", new string('a', 64), "application/x-hvo-linear-frame",
            null, payload, DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1),
            new ProcessingCompatibilityIdentity("r", "o", "c", "m", "s", "p", "pp"), 7, [Guid.NewGuid()],
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(1), new ProcessingCaptureConditions(1, 2, 3))
        {
            SchemaVersion = "v1",
            ContentIdentitySha256 = new string('b', 64),
            CaptureId = Guid.NewGuid()
        };
        var metadata = ProcessingRunnerProjection.ProjectArtifact(
            artifact, Guid.NewGuid(), "/api/v1.0/devices/x/artifacts/y/content", payload.Length,
            ProcessingRunnerProtocol.ComputeSha256(payload));
        var json = ProcessingRunnerProjection.SerializeMetadata(metadata);
        var restored = ProcessingRunnerProjection.DeserializeMetadata<ProcessingRunnerArtifactMetadata>(json);
        var rebuilt = ProcessingRunnerProjection.ReconstructArtifact(restored, payload);
        Assert.AreEqual(artifact.ArtifactId, rebuilt.ArtifactId);
        Assert.AreEqual(artifact.Role, rebuilt.Role);
        Assert.AreEqual(artifact.Variant, rebuilt.Variant);
        Assert.AreEqual(artifact.RecipeIdentitySha256, rebuilt.RecipeIdentitySha256);
        Assert.AreEqual(artifact.MediaType, rebuilt.MediaType);
        Assert.AreEqual(artifact.CreatedUtc, rebuilt.CreatedUtc);
        Assert.AreEqual(artifact.Integration, rebuilt.Integration);
        Assert.AreEqual(artifact.Compatibility, rebuilt.Compatibility);
        Assert.AreEqual(artifact.CaptureSequence, rebuilt.CaptureSequence);
        CollectionAssert.AreEqual(artifact.SourceArtifactIds!.ToArray(), rebuilt.SourceArtifactIds!.ToArray());
        Assert.AreEqual(artifact.ObservationStartedUtc, rebuilt.ObservationStartedUtc);
        Assert.AreEqual(artifact.ObservationEndedUtc, rebuilt.ObservationEndedUtc);
        Assert.AreEqual(artifact.Conditions, rebuilt.Conditions);
        Assert.AreEqual(artifact.SchemaVersion, rebuilt.SchemaVersion);
        Assert.AreEqual(artifact.ContentIdentitySha256, rebuilt.ContentIdentitySha256);
        Assert.AreEqual(artifact.CaptureId, rebuilt.CaptureId);
        CollectionAssert.AreEqual(payload, rebuilt.Payload.ToArray());

        var recipe = ProcessingIdentity.CreateRecipeIdentity(
            RecipeIdentityDescriptor.Create("encoded-preview", "1.0.0", "impl", JsonSerializer.SerializeToElement(new { })));
        var product = new ProcessingProduct(
            FrameArtifactRole.Preview, "native",
            ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Preview, "native", recipe.IdentitySha256, [artifact.ArtifactId]),
            "image/jpeg", null, payload,
            ProcessingIdentity.ComputePayloadSha256(payload),
            recipe,
            [new ProcessingAlgorithmIdentity("jpeg", "1")], [artifact.ArtifactId], TimeSpan.FromSeconds(1),
            artifact.Compatibility);
        var (request, payloads) = ProcessingRunnerProjection.ProjectOutcome(
            Guid.NewGuid(), ProcessingOutcome.Produced(product), 4, TimeSpan.FromMilliseconds(5));
        Assert.AreEqual(1, payloads.Count);
        var outcome = ProcessingRunnerProjection.ReconstructOutcome(
            ProcessingRunnerProjection.DeserializeMetadata<ProcessingRunnerCompletionRequest>(
                ProcessingRunnerProjection.SerializeMetadata(request)),
            payloads);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        Assert.AreEqual(product.OutputIdentitySha256, outcome.Products[0].OutputIdentitySha256);
        Assert.AreEqual(product.Recipe.IdentitySha256, outcome.Products[0].Recipe.IdentitySha256);

        var tampered = new byte[] { 1, 2, 3, 5 };
        var failure = Assert.ThrowsExactly<ProcessingRunnerProtocolException>(() =>
            ProcessingRunnerProjection.ReconstructOutcome(request, [tampered]));
        Assert.AreEqual(ProcessingRunnerReasonCodes.PayloadChecksumMismatch, failure.ReasonCode);
        var shortFailure = Assert.ThrowsExactly<ProcessingRunnerProtocolException>(() =>
            ProcessingRunnerProjection.ReconstructOutcome(request, [new byte[] { 1 }]));
        Assert.AreEqual(ProcessingRunnerReasonCodes.PayloadLengthMismatch, shortFailure.ReasonCode);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ProductIdentitiesAreRecomputedFromProvenanceBeforeAcceptance()
    {
        var payload = new byte[] { 5, 6, 7 };
        var source = Guid.NewGuid();
        var recipe = ProcessingIdentity.CreateRecipeIdentity(
            RecipeIdentityDescriptor.Create("encoded-preview", "1.0.0", "impl", JsonSerializer.SerializeToElement(new { })));
        var metadata = new ProcessingRunnerProductMetadata(
            FrameArtifactRole.Preview, "native",
            ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Preview, "native", recipe.IdentitySha256, [source]),
            "image/jpeg", null, 0, payload.Length, ProcessingRunnerProtocol.ComputeSha256(payload),
            ProcessingIdentity.ComputePayloadSha256(payload), recipe, [], [source], TimeSpan.Zero,
            new ProcessingCompatibilityIdentity("r", "o", "c", "m", "s", "p", "pp"), ProcessingProductKind.PixelData, null, null);

        _ = ProcessingRunnerProjection.ReconstructProduct(metadata, payload);

        var forgedOutput = metadata with { OutputIdentitySha256 = new string('f', 64) };
        Assert.AreEqual(
            ProcessingRunnerReasonCodes.OutputIdentityMismatch,
            Assert.ThrowsExactly<ProcessingRunnerProtocolException>(() =>
                ProcessingRunnerProjection.ReconstructProduct(forgedOutput, payload)).ReasonCode);
        var forgedSources = metadata with { SourceArtifactIds = [Guid.NewGuid()] };
        Assert.AreEqual(
            ProcessingRunnerReasonCodes.OutputIdentityMismatch,
            Assert.ThrowsExactly<ProcessingRunnerProtocolException>(() =>
                ProcessingRunnerProjection.ReconstructProduct(forgedSources, payload)).ReasonCode);
        var forgedRecipe = metadata with { Recipe = recipe with { IdentitySha256 = new string('a', 64) } };
        Assert.AreEqual(
            ProcessingRunnerReasonCodes.RecipeIdentityMismatch,
            Assert.ThrowsExactly<ProcessingRunnerProtocolException>(() =>
                ProcessingRunnerProjection.ReconstructProduct(forgedRecipe, payload)).ReasonCode);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CanonicalAuxiliaryInputsVerifyTheirIdentity()
    {
        var canonical = "{\"a\":1}"u8.ToArray();
        var input = new ProcessingAuxiliaryInput(
            "context", ProcessingAuxiliaryInputKind.CanonicalJson, null, "v1",
            ProcessingIdentity.ComputePayloadSha256(canonical), canonical);
        var metadata = ProcessingRunnerProjection.ProjectAuxiliaryInput(input);
        var restored = ProcessingRunnerProjection.ReconstructAuxiliaryInput(metadata);
        CollectionAssert.AreEqual(canonical, restored.Payload.ToArray());
        var forged = metadata with { CanonicalJson = "{\"a\":2}" };
        Assert.AreEqual(
            ProcessingRunnerReasonCodes.PayloadChecksumMismatch,
            Assert.ThrowsExactly<ProcessingRunnerProtocolException>(() =>
                ProcessingRunnerProjection.ReconstructAuxiliaryInput(forged)).ReasonCode);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void MetadataDeserializationRejectsUnknownMembersAndOversizedBodies()
    {
        var unknown = Encoding.UTF8.GetBytes("{\"leaseToken\":\"00000000-0000-0000-0000-000000000000\",\"extra\":1}");
        Assert.ThrowsExactly<ProcessingRunnerProtocolException>(() =>
            ProcessingRunnerProjection.DeserializeMetadata<ProcessingRunnerLeaseRenewalRequest>(unknown));
        var oversized = new byte[ProcessingRunnerProtocol.MaximumMetadataBytes + 1];
        Assert.AreEqual(
            ProcessingRunnerReasonCodes.TransferTooLarge,
            Assert.ThrowsExactly<ProcessingRunnerProtocolException>(() =>
                ProcessingRunnerProjection.DeserializeMetadata<ProcessingRunnerLeaseRenewalRequest>(oversized)).ReasonCode);
    }
}
