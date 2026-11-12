using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

public sealed partial class CameraAgentProcessingGraphOperationsEndpointsTests
{
    [TestMethod]
    public void OrdinaryDetailJsonOmitsManifestOnlySnapshotFields()
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(ManifestDetail(), JsonSerializerOptions.Web));
        Assert.IsFalse(json.RootElement.TryGetProperty("frozenNodeCount", out _));
        var node = json.RootElement.GetProperty("nodes")[0];
        Assert.IsFalse(node.TryGetProperty("artifactBindingsRetained", out _));
        Assert.IsFalse(node.TryGetProperty("committedOutputCount", out _));
        Assert.IsFalse(node.TryGetProperty("inputEvidenceIncomplete", out _));
        Assert.IsFalse(node.TryGetProperty("calibrationInputs", out _));
    }

    [TestMethod]
    public async Task ManifestContainsFrozenIdentityAttemptsChecksumsAndLineageWithoutPrivateDetails()
    {
        var detail = ManifestDetail();
        var operations = new Mock<IProcessingGraphOperations>(MockBehavior.Strict);
        operations.Setup(value => value.ReadRunManifestAsync(detail.Execution.ExecutionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(detail);
        await using var app = CreateApp(operations.Object, Mock.Of<ICameraAgentArtifactService>());
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .Single(candidate => candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "DownloadCameraAgentProcessingRunManifest");
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = HttpMethods.Get;
        context.Request.RouteValues["executionId"] = detail.Execution.ExecutionId.ToString();
        using var body = new MemoryStream();
        context.Response.Body = body;
        await endpoint.RequestDelegate!(context).ConfigureAwait(false);
        Assert.AreEqual(200, context.Response.StatusCode);
        Assert.AreEqual("application/json", context.Response.ContentType);
        Assert.AreEqual("no-store", context.Response.Headers.CacheControl.ToString());
        StringAssert.Contains(context.Response.Headers.ContentDisposition.ToString(), $"pipeline-run-{detail.Execution.ExecutionId:D}-manifest-v1.json", StringComparison.Ordinal);
        var json = Encoding.UTF8.GetString(body.ToArray());
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.AreEqual(CameraAgentRunManifest.SchemaVersion, root.GetProperty("schemaVersion").GetString());
        Assert.AreEqual(detail.Execution.LocalPlanIdentitySha256, root.GetProperty("localPlanIdentitySha256").GetString());
        Assert.AreEqual(detail.Execution.GraphRevisionId, root.GetProperty("graphRevisionId").GetString());
        var node = root.GetProperty("nodes")[0];
        Assert.AreEqual("Produced", node.GetProperty("attempts")[0].GetProperty("outcome").GetString());
        Assert.AreEqual(new string('D', 64), node.GetProperty("inputs")[0].GetProperty("payloadSha256").GetString());
        Assert.AreEqual(new string('E', 64), node.GetProperty("outputs")[0].GetProperty("payloadSha256").GetString());
        Assert.AreEqual(detail.Execution.PrimaryArtifactId, node.GetProperty("outputs")[0].GetProperty("sourceArtifactIds")[0].GetGuid());
        Assert.IsFalse(json.Contains("private-sentinel", StringComparison.Ordinal));
        foreach (var forbidden in new[] { "reason", "leaseOwner", "triggerReference", "diagnostics", "relativePath", "credential" })
            Assert.IsFalse(json.Contains(forbidden, StringComparison.OrdinalIgnoreCase), forbidden);
    }

    [TestMethod]
    [DataRow("missing", 404)]
    [DataRow("expired", 409)]
    [DataRow("incomplete", 409)]
    [DataRow("oversized", 409)]
    [DataRow("corrupt", 409)]
    [DataRow("failure", 500)]
    [DataRow("retention", 409)]
    [DataRow("retention-input", 409)]
    public async Task ManifestUnavailableIsSafeAndNeverReturnsPartialJson(string scenario, int expected)
    {
        var detail = ManifestDetail();
        var operations = new Mock<IProcessingGraphOperations>(MockBehavior.Strict);
        var setup = operations.Setup(value => value.ReadRunManifestAsync(detail.Execution.ExecutionId, It.IsAny<CancellationToken>()));
        if (scenario == "failure") setup.ThrowsAsync(new IOException("/private-sentinel?password=private-sentinel"));
        else if (scenario == "oversized") setup.ThrowsAsync(new ProcessingRunManifestBoundsException());
        else if (scenario == "corrupt") setup.ThrowsAsync(new InvalidDataException("private-sentinel"));
        else setup.ReturnsAsync(scenario switch
        {
            "missing" => null,
            "expired" => detail with { Nodes = [], FrozenNodeCount = 1 },
            "retention-input" => detail with
            {
                Nodes = [detail.Nodes[0] with { Outputs = [detail.Nodes[0].Outputs[0] with
                { SourceArtifactIds = [detail.Execution.PrimaryArtifactId, Guid.NewGuid()] }] }]
            },
            "retention" => detail with
            {
                Nodes = [detail.Nodes[0] with
                { Outputs = [], OutputContracts = [new(FrameArtifactRole.Preview, "display", ProcessingProductKind.PixelData)] }]
            },
            "incomplete" => detail with { Nodes = [detail.Nodes[0] with { Outputs = [detail.Nodes[0].Outputs[0] with { PayloadSha256 = null }] }] },
            _ => detail
        });
        await using var app = CreateApp(operations.Object, Mock.Of<ICameraAgentArtifactService>());
        var (status, body) = await InvokeForBodyAsync(app, "DownloadCameraAgentProcessingRunManifest", HttpMethods.Get,
            routeValues: new() { ["executionId"] = detail.Execution.ExecutionId.ToString() }).ConfigureAwait(false);
        Assert.AreEqual(expected, status);
        Assert.IsFalse(body.Contains("private-sentinel", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("schemaVersion", StringComparison.Ordinal));
        if (scenario is "oversized" or "corrupt")
        {
            using var problem = JsonDocument.Parse(body);
            Assert.AreEqual(scenario == "oversized" ? ProcessingRunManifestBounds.UnavailableReason : CameraAgentRunManifest.CorruptReason,
                problem.RootElement.GetProperty("title").GetString());
        }
    }

    [TestMethod]
    public void ManifestRejectsNodeAndEncodedByteLimits()
    {
        var detail = ManifestDetail();
        Assert.AreEqual(ProcessingRunManifestBounds.UnavailableReason,
            CameraAgentRunManifest.Create(detail with { Nodes = Enumerable.Repeat(detail.Nodes[0], 65).ToArray() }).Reason);
        var sources = Enumerable.Range(0, 512).Select(_ => Guid.NewGuid()).ToArray();
        var inputs = sources.Select((id, index) => detail.Nodes[0].Inputs[0] with { ArtifactId = id, Ordinal = index }).ToArray();
        var outputs = Enumerable.Range(0, 120).Select(index => detail.Nodes[0].Outputs[0] with
        { Ordinal = index, SourceArtifactIds = sources }).ToArray();
        Assert.AreEqual(ProcessingRunManifestBounds.UnavailableReason,
            CameraAgentRunManifest.Create(detail with { Nodes = [detail.Nodes[0] with { Inputs = inputs, Outputs = outputs, CommittedOutputCount = outputs.Length }] }).Reason);
    }

    [TestMethod]
    public void ManifestRefusesPathLikeNodeIdentifiers()
    {
        var detail = ManifestDetail();
        var result = CameraAgentRunManifest.Create(detail with { Nodes = [detail.Nodes[0] with { NodeId = "/private-sentinel?password=secret" }] });
        Assert.IsNull(result.Payload);
        Assert.AreEqual(CameraAgentRunManifest.IdentifierUnavailableReason, result.Reason);
    }

    [TestMethod]
    public void ManifestIncludesRetainedEmptyGraphInsteadOfClaimingEvidenceWasPruned()
    {
        var result = CameraAgentRunManifest.Create(ManifestDetail() with { Nodes = [], FrozenNodeCount = 0 });
        Assert.IsNull(result.Reason);
        using var json = JsonDocument.Parse(result.Payload!);
        Assert.AreEqual(0, json.RootElement.GetProperty("nodes").GetArrayLength());
    }

    [TestMethod]
    [DataRow("Skipped")]
    [DataRow("Completed")]
    public void ManifestKeepsGenuinelyOutputlessOutcomesAvailable(string status)
    {
        var detail = ManifestDetail();
        var node = detail.Nodes[0] with
        {
            Status = status,
            CommittedOutputCount = 0,
            Outputs = [],
            OutputContracts = status == "Skipped" ? [new(FrameArtifactRole.Preview, "display", ProcessingProductKind.PixelData)] : [],
            Attempts = [detail.Nodes[0].Attempts[0] with { Status = status, Outcome = status == "Skipped" ? ProcessingOutcomeStatus.Skipped : null }]
        };
        Assert.IsNotNull(CameraAgentRunManifest.Create(detail with { Nodes = [node] }).Payload);
    }

    [TestMethod]
    public void ManifestDistinguishesUnrecordedLegacyOutcomesFromDeletedEvidence()
    {
        var detail = ManifestDetail();
        var result = CameraAgentRunManifest.Create(detail with
        { Nodes = [detail.Nodes[0] with { CommittedOutputCount = null }] });
        Assert.IsNull(result.Payload);
        Assert.AreEqual(CameraAgentRunManifest.UnrecordedOutputReason, result.Reason);
    }

    internal static ProcessingGraphExecutionDetail ManifestDetail()
    {
        var execution = CreateExecutionDetail().Execution with { FailureReason = "private-sentinel", TriggerReference = "private-sentinel", GraphRevisionId = new string('F', 64) };
        return new(execution, [new("preview", true, new string('A', 64), "Completed", "private-sentinel", 1,
            execution.AcceptedUtc, execution.CompletedUtc,
            [new(0, 0, ProcessingGraphExecutionInputKind.RawCapture, execution.CaptureId, execution.PrimaryArtifactId, new string('C', 64), new string('D', 64), null)],
            [new(1, "private-sentinel", execution.AcceptedUtc, execution.CompletedUtc, "Completed", ProcessingOutcomeStatus.Produced,
                "private-sentinel", TimeSpan.FromSeconds(1), ProcessingNodeExecutionRoute.InProcess)],
            [new(0, new string('B', 64), Guid.NewGuid(), FrameArtifactRole.Preview, "private-sentinel", "Available", "private-sentinel", false)
                { PayloadSha256 = new string('E', 64), SourceArtifactIds = [execution.PrimaryArtifactId] }]) { CommittedOutputCount = 1 }]);
    }
}
