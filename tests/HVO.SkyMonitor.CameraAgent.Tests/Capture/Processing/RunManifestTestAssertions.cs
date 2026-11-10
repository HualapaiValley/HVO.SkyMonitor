using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Tests.Endpoints;
using Microsoft.AspNetCore.Http;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

internal static class RunManifestTestAssertions
{
    internal static async Task<string> ResponseAsync(IProcessingGraphOperations operations, Guid executionId, string? unavailableReason = null)
    {
        var app = CameraAgentProcessingGraphOperationsEndpointsTests.CreateApp(operations, Mock.Of<ICameraAgentArtifactService>());
        await using var lifetime = app.ConfigureAwait(false);
        var (status, body) = await CameraAgentProcessingGraphOperationsEndpointsTests.InvokeForBodyAsync(
            app, "DownloadCameraAgentProcessingRunManifest", HttpMethods.Get,
            routeValues: new() { ["executionId"] = executionId.ToString() }).ConfigureAwait(false);
        Assert.AreEqual(unavailableReason is null ? 200 : 409, status, body);
        using var document = JsonDocument.Parse(body);
        if (unavailableReason is null)
            Assert.AreEqual(executionId, document.RootElement.GetProperty("executionId").GetGuid());
        else
        {
            Assert.AreEqual(unavailableReason, document.RootElement.GetProperty("title").GetString());
            Assert.IsFalse(body.Contains("schemaVersion", StringComparison.Ordinal));
        }
        return body;
    }
}
