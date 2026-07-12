using System.Net;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class VirtualSkyPipelineTests
{
    private static readonly string[] ExpectedProcessingSteps =
        ["RollingCombination", "Preview", "Annotation", "LocalStorage"];
    private static readonly FrameArtifactRole[] ExpectedArtifactRoles =
        [FrameArtifactRole.Raw, FrameArtifactRole.Combined, FrameArtifactRole.Preview, FrameArtifactRole.AnnotatedPreview];
    private static CameraAgentIntegrationFixture Fixture => AssemblyHooks.Fixture;

    [TestMethod]
    public async Task ConfiguredPipelinePublishesPersistsReportsAndQueuesVirtualFrame()
    {
        using var scope = Fixture.CreateCameraAgentScope();
        var services = scope.ServiceProvider;
        var telemetry = services.GetRequiredService<ICaptureTelemetryProvider>();
        var latest = services.GetRequiredService<ILatestFrameAccessor>();

        await WaitUntilAsync(() => telemetry.Latest is { FrameStored: true } &&
            latest.TryGetSnapshot(FrameArtifactRole.Raw, out _) &&
            latest.TryGetSnapshot(FrameArtifactRole.Combined, out _) &&
            latest.TryGetSnapshot(out _), TimeSpan.FromSeconds(20)).ConfigureAwait(false);

        var sample = telemetry.Latest!;
        Assert.IsTrue(sample.FrameStored);
        CollectionAssert.AreEqual(
            ExpectedProcessingSteps,
            sample.ProcessingSteps.Select(step => step.Name).ToArray());
        Assert.IsTrue(sample.ProcessingSteps.All(step => step.Succeeded));

        Assert.IsTrue(latest.TryGetSnapshot(FrameArtifactRole.Raw, out var raw));
        Assert.AreEqual(CameraPixelFormat.Mono16, raw.PixelFormat);
        Assert.AreEqual("VirtualSky", raw.Metadata!.SourceId);
        Assert.IsNotNull(raw.Metadata.Scene);

        Assert.IsTrue(latest.TryGetSnapshot(FrameArtifactRole.Combined, out var combined));
        Assert.AreEqual(CameraPixelFormat.Mono16, combined.PixelFormat);
        Assert.IsTrue(latest.TryGetSnapshot(out var preview));
        Assert.AreEqual(CameraPixelFormat.Mono8, preview.PixelFormat);
        Assert.AreEqual("AnnotatedPreview", preview.Metadata!.SourceId);
        Assert.AreEqual("integration-annotation-v2", preview.RecipeVersion);
        Assert.AreEqual("integration-annotation-v2", preview.Metadata.Extra!["annotationRecipeVersion"]);

        var stored = services.GetRequiredService<IFrameStorageService>().List(
            Fixture.StorageRoot, DateOnly.FromDateTime(raw.TimestampUtc.UtcDateTime), null, 1000);
        CollectionAssert.IsSubsetOf(
            ExpectedArtifactRoles, stored.Select(item => item.Role).Distinct().ToArray());
        Assert.IsTrue(stored.All(item => File.Exists(item.AbsolutePath)));

        var pending = new FileSystemArtifactOutbox().List(Fixture.StorageRoot, 1000);
        CollectionAssert.IsSubsetOf(
            ExpectedArtifactRoles, pending.Select(item => item.Role).Distinct().ToArray());
        Assert.IsTrue(pending.All(item => item.AgentId == "cameraagent-integration-test"));
        Assert.IsTrue(pending.All(item => File.Exists(Path.Combine(Fixture.StorageRoot, item.RelativeArtifactPath))));
        Assert.IsTrue(pending.Any(item => item.Scene is not null));

        using var client = Fixture.CreateCameraAgentClient();
        using var response = await client.GetAsync(new Uri("/api/v1.0/frames/latest", UriKind.Relative)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("image/jpeg", response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail("Timed out waiting for the configured VirtualSky pipeline.");
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
    }
}
