using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

public sealed partial class CameraAgentProcessingGraphOperationsEndpointsTests
{
    [TestMethod]
    [DataRow("CreateCameraAgentProcessingGraphRevision")]
    [DataRow("ValidateCameraAgentProcessingGraphRevision")]
    [DataRow("ActivateCameraAgentProcessingGraphRevision")]
    [DataRow("RollbackCameraAgentProcessingGraphRevision")]
    public async Task StoragePolicyCommandsReturnSafeConflictGuidanceFromRealCoordinator(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-storage-api-policy");
        try
        {
            using var provider = StoragePolicyRawIngressIntegrationTests.Provider(root);
            var profile = StoragePolicyRawIngressIntegrationTests.Profile(root);
            await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var changed = StoragePolicyRawIngressIntegrationTests.ChangedPipeline(profile);
            string revisionId = "create-target";
            if (!endpoint.StartsWith("Create", StringComparison.Ordinal))
                revisionId = await StoragePolicyRawIngressIntegrationTests.SeedLegacyRevisionAsync(provider, profile, changed).ConfigureAwait(false);
            var before = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            await using var app = CreateApp(operations, provider.GetRequiredService<ICameraAgentArtifactService>());
            object request = endpoint.StartsWith("Create", StringComparison.Ordinal)
                ? new { name = "storage-rejected", revision = "1", pipeline = changed, reason = "test" }
                : new { expectedVersion = before.StateVersion, reason = "test" };
            var (status, body) = await InvokeForBodyAsync(app, endpoint, HttpMethods.Post, request,
                new RouteValueDictionary { ["revisionId"] = revisionId }).ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status409Conflict, status, body);
            using var problem = JsonDocument.Parse(body);
            Assert.AreEqual(StatusCodes.Status409Conflict, problem.RootElement.GetProperty("status").GetInt32());
            Assert.AreEqual(CaptureStoragePolicy.RejectionMessage, problem.RootElement.GetProperty("title").GetString());
            StringAssert.Contains(body, "capture profile", StringComparison.Ordinal);
            Assert.IsFalse(body.Contains(root, StringComparison.Ordinal), "Problem responses carry only fixed public guidance.");
            var after = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(before.ActiveRevisionId, after.ActiveRevisionId);
            Assert.AreEqual(before.StateVersion, after.StateVersion);
            Assert.HasCount(before.Revisions.Count, after.Revisions);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task ArbitraryValidationExceptionDoesNotExposePrivateMessages()
    {
        var operations = new Mock<IProcessingGraphOperations>(MockBehavior.Strict);
        operations.Setup(value => value.CreateRevisionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CapturePipelineConfig>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ValidationException("private-validation-sentinel"));
        await using var app = CreateApp(operations.Object, Mock.Of<ICameraAgentArtifactService>());
        var (status, body) = await InvokeForBodyAsync(app, "CreateCameraAgentProcessingGraphRevision", HttpMethods.Post,
            new { name = "test", revision = "1", pipeline = CapturePipelineConfig.Empty }).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status500InternalServerError, status);
        Assert.IsFalse(body.Contains("private-validation-sentinel", StringComparison.Ordinal));
    }
}
