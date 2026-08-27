using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
[TestCategory("Unit")]
public sealed class FrameProcessingChannelTests
{
    [TestMethod]
    public async Task WriteAsync_WhenCapacityReached_WaitsAndDrainsExactlyOnce()
    {
        var channel = new FrameProcessingChannel(2);
        await channel.WriteAsync(CreateItem(), CancellationToken.None).ConfigureAwait(false);
        await channel.WriteAsync(CreateItem(), CancellationToken.None).ConfigureAwait(false);

        var blockedWrite = channel.WriteAsync(CreateItem(), CancellationToken.None).AsTask();
        Assert.IsFalse(blockedWrite.IsCompleted);
        Assert.AreEqual(2, channel.CurrentDepth);

        var reader = channel.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        try
        {
            Assert.IsTrue(await reader.MoveNextAsync().ConfigureAwait(false));
            await blockedWrite.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            channel.Complete();
            while (await reader.MoveNextAsync().ConfigureAwait(false))
            {
            }
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }

        Assert.AreEqual(2, channel.Capacity);
        Assert.AreEqual(3L, channel.AcceptedCount);
        Assert.AreEqual(3L, channel.DequeuedCount);
        Assert.AreEqual(0, channel.CurrentDepth);
    }

    private static FrameProcessingItem CreateItem()
    {
        var config = new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"), new CameraModuleDescriptor("Test"),
            new CameraRigConfig(
                new SensorProfile("Test", 1, 1, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("EquidistantFisheye", 0, 180, 0), new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 0, 0)),
            CapturePipelineConfig.Empty);
        var request = new CaptureRequest(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1), CaptureMode.Still);
        var result = new CaptureResult(
            null, new CaptureSetpoint(TimeSpan.FromSeconds(1), 0, null, null),
            TimeSpan.Zero, CaptureMode.Still, false);
        return new FrameProcessingItem(config,
            new CaptureLoopSubmission(request, result, request.RequestedStartUtc, request.TargetInterval, TimeSpan.Zero));
    }
}
