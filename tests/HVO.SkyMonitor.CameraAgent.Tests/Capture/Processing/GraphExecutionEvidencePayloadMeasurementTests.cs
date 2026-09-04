using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

/// <summary>
/// Measures the canonical evidence payload sizes and cardinalities produced by the representative `W6` standalone
/// CameraAgent graph (ASI676MC 3552x3552 Bayer12-in-16, 25,233,408 raw bytes, 14 effective nodes) against the
/// limits declared by <see cref="GraphExecutionEvidenceLimits"/>. The contract carries no image bytes, so the
/// measured payload is a function of the graph and its recorded attempts/inputs/outputs, not of frame size; the
/// harness records the raw byte count alongside each measurement so that independence is visible rather than
/// assumed. Manual because it drives a full-resolution live execution through the durable SQLite store.
/// </summary>
[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class GraphExecutionEvidencePayloadMeasurementTests
{
    private const string ConfigurationFileName = "cameraagent.standalone-w6.json";
    private static readonly ObservatoryLocation Location = new(35.5599378, -113.9119818, 520, "America/Phoenix");
    private static readonly DateTimeOffset FixtureUtc = new(2026, 8, 31, 4, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task W6EvidencePayloadsAndCardinalitiesStayWithinTheDeclaredLimits()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-issue-536-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var measurement = await MeasureAsync(root).ConfigureAwait(false);

            Assert.IsTrue(
                measurement.RevisionEnvelopeBytes <= GraphExecutionEvidenceLimits.MaximumEnvelopeBytes,
                $"revision envelope {measurement.RevisionEnvelopeBytes}");
            Assert.IsTrue(
                measurement.CanonicalDefinitionBytes <= GraphExecutionEvidenceLimits.MaximumDefinitionBytes,
                $"canonical definition {measurement.CanonicalDefinitionBytes}");
            Assert.IsTrue(
                measurement.FrozenPlanBytes <= GraphExecutionEvidenceLimits.MaximumFrozenPlanBytes,
                $"frozen plan {measurement.FrozenPlanBytes}");
            Assert.IsTrue(
                measurement.ExecutionEnvelopeBytes <= GraphExecutionEvidenceLimits.MaximumEnvelopeBytes,
                $"execution envelope {measurement.ExecutionEnvelopeBytes}");
            Assert.IsTrue(
                measurement.AvailabilityEnvelopeBytes <= GraphExecutionEvidenceLimits.MaximumEnvelopeBytes,
                $"availability envelope {measurement.AvailabilityEnvelopeBytes}");
            Assert.IsTrue(
                measurement.NodeCount <= GraphExecutionEvidenceLimits.MaximumNodeCount,
                $"nodes {measurement.NodeCount}");
            Assert.IsTrue(
                measurement.MaximumAttemptsPerNode <= GraphExecutionEvidenceLimits.MaximumAttemptsPerNode,
                $"attempts {measurement.MaximumAttemptsPerNode}");
            Assert.IsTrue(
                measurement.InputCount <= GraphExecutionEvidenceLimits.MaximumInputsPerExecution,
                $"inputs {measurement.InputCount}");
            Assert.IsTrue(
                measurement.OutputCount <= GraphExecutionEvidenceLimits.MaximumOutputsPerExecution,
                $"outputs {measurement.OutputCount}");
            Assert.IsTrue(
                measurement.ObservationCount <= GraphExecutionEvidenceLimits.MaximumAvailabilityObservations,
                $"observations {measurement.ObservationCount}");
            Assert.IsTrue(
                measurement.DeclaredNodeCount <= GraphExecutionEvidenceLimits.MaximumNodeCount,
                $"declared nodes {measurement.DeclaredNodeCount}");
            Assert.IsTrue(
                measurement.DeclaredInputBound <= GraphExecutionEvidenceLimits.MaximumInputsPerExecution,
                $"declared inputs {measurement.DeclaredInputBound}");
            Assert.IsTrue(
                measurement.DeclaredOutputBound <= GraphExecutionEvidenceLimits.MaximumOutputsPerExecution,
                $"declared outputs {measurement.DeclaredOutputBound}");
            Assert.AreEqual(14, measurement.DeclaredNodeCount, "The W6 graph must contribute all 14 nodes.");
            Assert.AreEqual(
                measurement.DeclaredNodeCount, measurement.NodeCount, "Every node plan must be exported.");
            Assert.AreEqual(0, measurement.EmbeddedPayloadBytes, "The contract must carry no image bytes.");

            await WriteEvidenceAsync(measurement).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task<Measurement> MeasureAsync(string root)
    {
        var configuration = await LoadAsync().ConfigureAwait(false);
        var layout = SensorReadoutResolver.Resolve(configuration.Rig.Sensor, configuration.Rig.Readout!).Layout;
        using var provider = CreateProvider(root);
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var registry = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None)
            .ConfigureAwait(false);

        var snapshot = await operations
            .ReadRevisionSnapshotAsync(registry.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
        var origin = CreateOrigin();
        var revisionEvidence = ProcessingGraphEvidenceProjection.CreateRevisionEvidence(snapshot, assignment: null);
        var revisionEnvelope = ProcessingGraphEvidenceProjection.CreateEnvelope(
            origin, 1, Guid.NewGuid(), FixtureUtc, revisionEvidence, ExecutionEvidenceRedactionPolicyV1.None);
        var revisionBytes = GraphExecutionEvidenceJson.Serialize(revisionEnvelope);

        var capture = await RunLiveCaptureAsync(provider, operations, configuration).ConfigureAwait(false);
        Assert.AreEqual(layout.ByteLength, capture.RawPayloadBytes);
        var state = (await operations.ReadExecutionsAsync(
                ProcessingGraphExecutionClass.Live, 1, CancellationToken.None).ConfigureAwait(false))
            .Single();
        var detail = await operations.ReadExecutionDetailAsync(state.ExecutionId, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsNotNull(detail);

        var executionEvidence = ProcessingGraphEvidenceProjection.CreateExecutionEvidence(detail);
        var executionEnvelope = ProcessingGraphEvidenceProjection.CreateEnvelope(
            origin,
            2,
            Guid.NewGuid(),
            FixtureUtc,
            executionEvidence,
            ExecutionEvidenceRedactionPolicyV1.OperatorIdentity);
        var executionBytes = GraphExecutionEvidenceJson.Serialize(executionEnvelope);
        var availability = ProcessingGraphEvidenceProjection.CreateAvailabilityReport(detail, FixtureUtc);
        Assert.IsNotNull(availability);
        var availabilityEnvelope = ProcessingGraphEvidenceProjection.CreateEnvelope(
            origin, 3, Guid.NewGuid(), FixtureUtc, availability, ExecutionEvidenceRedactionPolicyV1.None);
        var availabilityBytes = GraphExecutionEvidenceJson.Serialize(availabilityEnvelope);

        foreach (var envelope in new[] { revisionEnvelope, executionEnvelope, availabilityEnvelope })
        {
            var parsed = GraphExecutionEvidenceJson.ParseEnvelope(GraphExecutionEvidenceJson.Serialize(envelope));
            Assert.IsNotNull(parsed.Value, parsed.Validation.ReasonCode);
        }

        var declared = ComputeDeclaredBounds(snapshot);
        return new(
            capture.RawPayloadBytes,
            revisionBytes.Length,
            JsonSerializer.SerializeToUtf8Bytes(revisionEvidence.CanonicalDefinition).Length,
            JsonSerializer.SerializeToUtf8Bytes(revisionEvidence.FrozenPlan).Length,
            executionBytes.Length,
            availabilityBytes.Length,
            executionEvidence.Nodes.Length,
            executionEvidence.Nodes.Max(static node => node.Attempts.Length),
            executionEvidence.Nodes.Sum(static node => node.Inputs.Length),
            executionEvidence.Nodes.Sum(static node => node.Outputs.Length),
            availability.Observations.Length,
            declared.NodeCount,
            declared.InputCount,
            declared.OutputCount,
            EmbeddedPayloadBytes: 0,
            state.Status.ToString(),
            state.FailureReason,
            capture.LaneOutcome,
            capture.LaneReason);
    }

    /// <summary>
    /// Drives one real `W6` VirtualSky capture through raw ingress and the standard lane so the durable store
    /// records a genuine execution of the 14-node standalone graph, including its projected scene stage.
    /// </summary>
    private static async Task<CaptureObservation> RunLiveCaptureAsync(
        ServiceProvider provider,
        ProcessingGraphOperationsCoordinator operations,
        CameraModuleConfig configuration)
    {
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var laneHandler = provider.GetServices<ICaptureLaneHandler>().Single(
            static handler => handler.Lane == "standard");
        var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
            static lane => lane.Name == "standard");

        var module = new VirtualSkyCameraModule(
            TimeProvider.System,
            CreateCatalog(),
            provider.GetRequiredService<IProjectedSceneStore>(),
            provider.GetRequiredService<IConstellationTopology>(),
            provider.GetRequiredService<IPlanetEphemeris>(),
            provider.GetRequiredService<IProjectedSceneStagingStore>());
        await module.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
        var setpoint = new CaptureSetpoint(
            configuration.Rig.Pipeline.NightExposure, configuration.Rig.Pipeline.NightGain, null, null);
        var request = new CaptureRequest(
            FixtureUtc, configuration.Rig.Pipeline.CaptureInterval, CaptureMode.Still, setpoint);
        var result = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(result.Frame);
        var submission = new CaptureLoopSubmission(
            request,
            result,
            FixtureUtc,
            configuration.Rig.Pipeline.NightExposure,
            configuration.Rig.Pipeline.CaptureInterval);

        var receipt = await ingress.AcceptAsync(configuration, submission, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        var lease = await laneStore.ClaimAsync(standard, "issue-536", configuration, CancellationToken.None)
            .ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var handled = await laneHandler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
        Assert.AreNotEqual(CaptureLaneHandlerOutcome.RetryableFailure, handled.Outcome, handled.Reason);
        await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        operations.NotifyLiveWorkChanged();
        return new(new FileInfo(receipt.StoredFrame.AbsolutePath).Length, handled.Outcome.ToString(), handled.Reason);
    }

    /// <summary>
    /// Computes the cardinality the frozen plan can ever produce for this revision: one entry per declared node,
    /// per declared output, and per declared input multiplied by its window's maximum input count. These are hard
    /// upper bounds for any execution of the revision, so they bound the contract limits independently of whether
    /// one measured execution reached every node.
    /// </summary>
    private static DeclaredBounds ComputeDeclaredBounds(ProcessingGraphRevisionSnapshot snapshot)
    {
        var inputs = 0;
        var outputs = 0;
        foreach (var node in snapshot.Nodes)
        {
            using var outputDocument = JsonDocument.Parse(node.OutputsJson);
            outputs += outputDocument.RootElement.GetArrayLength();
            using var inputDocument = JsonDocument.Parse(node.InputsJson);
            var window = 1;
            if (node.WindowJson is { Length: > 0 } windowJson)
            {
                using var windowDocument = JsonDocument.Parse(windowJson);
                window = windowDocument.RootElement.TryGetProperty("maximumInputCount", out var maximum)
                    ? Math.Max(1, maximum.GetInt32())
                    : 1;
            }
            inputs += inputDocument.RootElement.GetArrayLength() * window;
        }
        return new(snapshot.Nodes.Length, inputs, outputs);
    }

    private static InMemoryCelestialCatalog CreateCatalog()
        => new([
            new CelestialCatalogObject("HIP 32349", "Sirius", 101.28715533 / 15, -16.71611586, -1.46, 0.009, "32349"),
            new CelestialCatalogObject("HIP 24608", "Capella", 79.17232794 / 15, 45.99799147, 0.08, 0.795, "24608"),
            new CelestialCatalogObject("HIP 37279", "Procyon", 114.8254935 / 15, 5.22499307, 0.34, 0.42, "37279"),
            new CelestialCatalogObject("HIP 27989", "Betelgeuse", 88.792939 / 15, 7.407064, 0.45, 1.5, "27989")
        ]);

    private static ExecutionEvidenceOriginV1 CreateOrigin()
        => GraphExecutionEvidenceJson.BindIdentity(new(
            ExecutionEvidenceOriginV1.CurrentSchemaVersion,
            new("11111111-1111-4111-8111-111111111111"),
            new("22222222-2222-4222-8222-222222222222"),
            new("33333333-3333-4333-8333-333333333333"),
            "issue-536-measurement",
            GraphExecutionEvidenceJson.UnhashedPayloadSha256));

    private static async Task<CameraModuleConfig> LoadAsync()
    {
        var loader = new FileCameraAgentConfigurationLoader(Options.Create(new CameraAgentHostOptions
        {
            ConfigFilePath = Path.Combine(AppContext.BaseDirectory, ConfigurationFileName),
            CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
            Observatory = Location
        }), NullLogger<FileCameraAgentConfigurationLoader>.Instance);
        return await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static ServiceProvider CreateProvider(string root)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false"
            }).Build());
        return services.BuildServiceProvider();
    }

    private async Task WriteEvidenceAsync(Measurement measurement)
    {
        var outputRoot = Environment.GetEnvironmentVariable("HVO_ISSUE536_EVIDENCE_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "TestResults", "issue-536");
        Directory.CreateDirectory(outputRoot);
        var path = Path.Combine(outputRoot, "w6-evidence-payload-measurement.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            SchemaVersion = "issue-536-evidence-payload-measurement-v1",
            RecordedUtc = DateTimeOffset.UtcNow,
            Workload = new
            {
                Id = "W6",
                Configuration = ConfigurationFileName,
                measurement.RawPayloadBytes
            },
            Environment = new
            {
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Framework = RuntimeInformation.FrameworkDescription
            },
            Limits = ExecutionEvidenceLimitsV1.Current,
            Measurement = measurement
        }, EvidenceOptions)).ConfigureAwait(false);
        TestContext.WriteLine($"Evidence written to {path}");
        TestContext.WriteLine(
            $"W6 raw bytes {measurement.RawPayloadBytes}; revision envelope {measurement.RevisionEnvelopeBytes}; " +
            $"execution envelope {measurement.ExecutionEnvelopeBytes}; availability envelope " +
            $"{measurement.AvailabilityEnvelopeBytes}; nodes {measurement.NodeCount}; " +
            $"inputs {measurement.InputCount}/{measurement.DeclaredInputBound}; " +
            $"outputs {measurement.OutputCount}/{measurement.DeclaredOutputBound}; " +
            $"status {measurement.ExecutionStatus} ({measurement.ExecutionFailureReason ?? "none"}); " +
            $"lane {measurement.LaneOutcome} ({measurement.LaneReason ?? "none"})");
    }

    private static void TryDelete(string root)
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record Measurement(
        long RawPayloadBytes,
        int RevisionEnvelopeBytes,
        int CanonicalDefinitionBytes,
        int FrozenPlanBytes,
        int ExecutionEnvelopeBytes,
        int AvailabilityEnvelopeBytes,
        int NodeCount,
        int MaximumAttemptsPerNode,
        int InputCount,
        int OutputCount,
        int ObservationCount,
        int DeclaredNodeCount,
        int DeclaredInputBound,
        int DeclaredOutputBound,
        int EmbeddedPayloadBytes,
        string ExecutionStatus,
        string? ExecutionFailureReason,
        string LaneOutcome,
        string? LaneReason);

    private sealed record DeclaredBounds(int NodeCount, int InputCount, int OutputCount);

    private sealed record CaptureObservation(long RawPayloadBytes, string LaneOutcome, string? LaneReason);
}
