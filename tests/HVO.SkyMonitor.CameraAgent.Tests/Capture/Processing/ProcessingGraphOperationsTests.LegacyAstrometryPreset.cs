using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Endpoints;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    private const string LegacyPresetCreateEndpoint = "CreateCameraAgentReferenceAstrometryRevision";
    private const string LegacyPresetCreateKey = "legacy-preset-create";

    private static async Task<(string Receipt, RawCaptureReceipt Capture)> ConfigureSharedLegacyAstrometryAsync(
        string root, CameraModuleConfig configuration, ServiceProvider provider, ProcessingGraphRevisionSnapshot successor,
        string sourceRevisionId, string canonicalMeasurementId, string predictionId, int lastCaptureSequence,
        AstrometricCatalogData catalog,
        Func<DateTimeOffset, VirtualSkyCameraModule> createCamera, DateTimeOffset captureUtc, CancellationToken token)
    {
        const string fitId = "fit";
        var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var artifacts = provider.GetRequiredService<ICameraAgentArtifactService>();
        var app = CameraAgentProcessingGraphOperationsEndpointsTests.CreateApp(operations, artifacts);
        await using var appLifetime = app.ConfigureAwait(false);

        var (status, response) = await CameraAgentProcessingGraphOperationsEndpointsTests.InvokeForBodyAsync(
            app, "GetCameraAgentProcessingGraphRegistry", HttpMethods.Get, cancellationToken: token).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status200OK, status, response);
        using var registry = JsonDocument.Parse(response);
        var basis = registry.RootElement.GetProperty("activeRevisionId").GetString();
        var version = registry.RootElement.GetProperty("stateVersion").GetInt64();
        Assert.AreEqual(sourceRevisionId, basis);
        Assert.AreNotEqual(successor.State.RevisionId, basis);
        var request = new LegacyAstrometryPresetRequest(basis!);
        var creation = await CameraAgentProcessingGraphOperationsEndpointsTests.InvokeForBodyAsync(
            app, LegacyPresetCreateEndpoint, HttpMethods.Post, request,
            idempotencyKey: LegacyPresetCreateKey, antiforgeryValidated: true, cancellationToken: token).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status200OK, creation.Status, creation.Body);
        using var receipt = JsonDocument.Parse(creation.Body);
        var revisionId = receipt.RootElement.GetProperty("revisionId").GetString()!;
        var pin = receipt.RootElement.GetProperty("catalogSelection").Deserialize<MeasuredAstrometryCatalogSelection>(ReferenceAstrometryJson);
        Assert.AreEqual(new MeasuredAstrometryCatalogSelection(catalog.IdentitySha256, catalog.SelectionIdentitySha256, 4.5), pin);
        Assert.AreEqual(creation, await CameraAgentProcessingGraphOperationsEndpointsTests.InvokeForBodyAsync(
            app, LegacyPresetCreateEndpoint, HttpMethods.Post, request,
            idempotencyKey: LegacyPresetCreateKey, antiforgeryValidated: true, cancellationToken: token).ConfigureAwait(false));

        var preset = await operations.ReadRevisionSnapshotAsync(revisionId, token).ConfigureAwait(false);
        CollectionAssert.AreEqual(successor.Pipeline.Steps.Select(step => step.Id).ToArray(),
            preset.Pipeline.Steps.Select(step => step.Id).ToArray());
        foreach (var original in successor.Nodes)
        {
            var saved = preset.Nodes.Single(node => node.NodeId == original.NodeId);
            Assert.AreEqual(original.DependenciesJson, saved.DependenciesJson, original.NodeId);
            Assert.AreEqual(original.InputsJson, saved.InputsJson, original.NodeId);
            Assert.AreEqual(original.OutputsJson, saved.OutputsJson, original.NodeId);
            if (original.NodeId != predictionId && original.NodeId != fitId)
            {
                Assert.AreEqual(original, saved, original.NodeId);
                Assert.AreEqual(
                    CaptureContractJson.ComputeCanonicalJsonSha256(successor.Pipeline.Steps.Single(step => step.Id == original.NodeId)),
                    CaptureContractJson.ComputeCanonicalJsonSha256(preset.Pipeline.Steps.Single(step => step.Id == original.NodeId)),
                    original.NodeId);
            }
        }
        var fit = preset.Pipeline.Steps.Single(step => step.Id == fitId);
        var fitOptions = fit.Options!.Value.Deserialize<MeasuredAstrometryCaptureOptions>(ReferenceAstrometryJson)!;
        Assert.AreEqual(pin, fitOptions.CatalogSelection);
        Assert.AreEqual(4.5, fitOptions.Recipe.Solver!.MaximumCatalogMagnitude);
        Assert.AreEqual(4.5, preset.Pipeline.Steps.Single(step => step.Id == predictionId)
            .Options!.Value.GetProperty("maximumMagnitude").GetDouble());

        var validated = await CameraAgentProcessingGraphOperationsEndpointsTests.InvokeForBodyAsync(
            app, "ValidateCameraAgentProcessingGraphRevision", HttpMethods.Post, new { }, new() { ["revisionId"] = revisionId },
            idempotencyKey: "legacy-preset-validate", antiforgeryValidated: true, cancellationToken: token).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status200OK, validated.Status, validated.Body);
        var activation = await CameraAgentProcessingGraphOperationsEndpointsTests.InvokeForBodyAsync(
            app, "ActivateCameraAgentProcessingGraphRevision", HttpMethods.Post, new { expectedVersion = version },
            new() { ["revisionId"] = revisionId }, idempotencyKey: "legacy-preset-activate",
            antiforgeryValidated: true, cancellationToken: token).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status200OK, activation.Status, activation.Body);
        using var activated = JsonDocument.Parse(activation.Body);
        Assert.AreEqual(revisionId, activated.RootElement.GetProperty("activeRevisionId").GetString());

        // Leave the cadence at its production bound and advance the fixture's sequence to the next eligible capture.
        var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 1);
        await journal.InitializeAsync(token).ConfigureAwait(false);
        Assert.IsNotNull(configuration.AgentId);
        for (var sequence = lastCaptureSequence + 1; sequence <= 60; sequence++)
            _ = await journal.ReserveIdentityAsync(configuration.AgentId, Guid.NewGuid(), Guid.NewGuid(), token).ConfigureAwait(false);
        var module = createCamera(captureUtc);
        await using var moduleLifetime = module.ConfigureAwait(false);
        await module.InitializeAsync(configuration, token).ConfigureAwait(false);
        var captureRequest = new CaptureRequest(captureUtc, TimeSpan.FromSeconds(2), CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(2.06), 1, null, null));
        var capture = await module.CaptureAsync(captureRequest, token).ConfigureAwait(false);
        var raw = await provider.GetRequiredService<IRawCaptureIngress>().AcceptAsync(configuration,
            new CaptureLoopSubmission(captureRequest, capture, captureUtc, captureRequest.TargetInterval, TimeSpan.Zero), token).ConfigureAwait(false);
        Assert.IsNotNull(raw);
        Assert.AreEqual(RawIngressOutcome.Committed, raw.Outcome);
        Assert.AreEqual(61L, raw.Manifest.Descriptor.Capture.CaptureSequence);
        Assert.AreEqual(revisionId, raw.Manifest.CaptureGraphRevisionId);
        await CompleteLiveAsync(provider, configuration).ConfigureAwait(false);

        var captureId = raw.Manifest.Descriptor.Capture.CaptureId;
        var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
        var fitOutput = await ReadOutputAsync(fitId, BuiltInProcessingRecipes.MeasuredAstrometry).ConfigureAwait(false);
        var solution = MeasuredAstrometryJson.Parse(await ReadPayloadAsync(fitOutput).ConfigureAwait(false));
        Assert.IsTrue(solution.Assessment.HasMeasuredMapping, solution.Assessment.ReasonCode);
        Assert.AreEqual((await ReadOutputAsync(fit.DependsOn!.Single(id => id != predictionId)).ConfigureAwait(false)).ArtifactId,
            solution.MeasuredArtifactId);
        Assert.AreEqual((await ReadOutputAsync(predictionId).ConfigureAwait(false)).ContentIdentitySha256, solution.SceneIdentitySha256);
        var canonicalMeasurement = await ReadOutputAsync(canonicalMeasurementId).ConfigureAwait(false);
        var association = MeasuredStellarAssociationJson.Parse(await ReadPayloadAsync(canonicalMeasurement).ConfigureAwait(false)).Associations!;
        Assert.AreEqual(ProjectedSceneKind.VirtualRenderAuthoritative, association.Scene.Kind);
        Assert.AreEqual((await ReadOutputAsync("projected-scene").ConfigureAwait(false)).ContentIdentitySha256, association.Scene.SceneIdentitySha256);
        var annotation = await ReadOutputAsync("annotation").ConfigureAwait(false);
        Assert.Contains(canonicalMeasurement.ArtifactId, annotation.Artifact.SourceArtifactIds);
        Assert.DoesNotContain(solution.MeasuredArtifactId, annotation.Artifact.SourceArtifactIds);
        Console.WriteLine($"Preset {revisionId}; accepted capture {captureId} sequence 61; solution {solution.SolutionIdentitySha256}; canonical association {canonicalMeasurement.ArtifactId}.");

        return (creation.Body, raw);

        async Task<DurableProcessingOutput> ReadOutputAsync(string nodeId, string? recipe = null)
        {
            var node = await store.ReadNodeAsync(captureId, nodeId, token).ConfigureAwait(false);
            Assert.IsNotNull(node);
            Assert.AreEqual(DurableProcessingNodeStatus.Completed, node.Status, node.Reason);
            return recipe is null ? node.Outputs.Single() : node.Outputs.Single(output => output.Artifact.Recipe.Name == recipe);
        }

        Task<byte[]> ReadPayloadAsync(DurableProcessingOutput output)
            => SiloInfrastructureFixture.ReadArtifactBytesAsync(provider, output.ArtifactId, token);
    }

    private static async Task VerifySharedLegacyPresetReceiptAfterRestartAsync(
        string root, CameraModuleConfig configuration, string sourceRevisionId, string successorRevisionId,
        string committedReceipt, CancellationToken token)
    {
        using var withoutCatalog = CreateProvider(root);
        await withoutCatalog.GetRequiredService<IRawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
        var restartedOperations = withoutCatalog.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        await restartedOperations.EnsureConfiguredBasicAsync(configuration, token).ConfigureAwait(false);
        var restarted = CameraAgentProcessingGraphOperationsEndpointsTests.CreateApp(
            restartedOperations, withoutCatalog.GetRequiredService<ICameraAgentArtifactService>());
        await using var restartedLifetime = restarted.ConfigureAwait(false);
        var request = new LegacyAstrometryPresetRequest(sourceRevisionId);
        Assert.AreEqual((StatusCodes.Status200OK, committedReceipt), await CameraAgentProcessingGraphOperationsEndpointsTests.InvokeForBodyAsync(
            restarted, LegacyPresetCreateEndpoint, HttpMethods.Post, request,
            idempotencyKey: LegacyPresetCreateKey, antiforgeryValidated: true, cancellationToken: token).ConfigureAwait(false));
        var changedBasis = await CameraAgentProcessingGraphOperationsEndpointsTests.InvokeForBodyAsync(
            restarted, LegacyPresetCreateEndpoint, HttpMethods.Post, request with { BasisRevisionId = successorRevisionId },
            idempotencyKey: LegacyPresetCreateKey, antiforgeryValidated: true, cancellationToken: token).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status409Conflict, changedBasis.Status, changedBasis.Body);
        var newRequest = await CameraAgentProcessingGraphOperationsEndpointsTests.InvokeForBodyAsync(
            restarted, LegacyPresetCreateEndpoint, HttpMethods.Post, request with { Revision = "missing-catalog-preset" },
            idempotencyKey: "legacy-preset-missing-catalog", antiforgeryValidated: true, cancellationToken: token).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status422UnprocessableEntity, newRequest.Status, newRequest.Body);
        var persisted = await restartedOperations.GetRegistryAsync(token).ConfigureAwait(false);
        using var receipt = JsonDocument.Parse(committedReceipt);
        Assert.AreEqual(receipt.RootElement.GetProperty("revisionId").GetString(), persisted.ActiveRevisionId);
        Assert.AreEqual(1, persisted.Revisions.Count(revision => revision.Name == ReferenceAstrometryPreset.GraphName));
    }

    private sealed record LegacyAstrometryPresetRequest(
        string BasisRevisionId, string Revision = "legacy-magnitude-4-5", double ReferenceMagnitude = 4.5);
}
