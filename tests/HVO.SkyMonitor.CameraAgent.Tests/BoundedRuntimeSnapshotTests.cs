using System.Reflection;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.Fleet.Contracts;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class BoundedRuntimeSnapshotTests
{
    [TestMethod]
    public void FleetTrySnapshotPreservesCaptureAndTimingStateAndReleasesGate()
    {
        var state = new FleetRuntimeState(TimeProvider.System);
        state.CaptureFailed("failure");
        state.ProcessingCompleted(TimeSpan.FromMilliseconds(4));
        var expected = state.Snapshot;

        Assert.IsTrue(state.TrySnapshot(TimeSpan.Zero, out var actual));

        Assert.IsNotNull(actual);
        Assert.AreEqual(expected.Capture, actual.Capture);
        CollectionAssert.AreEqual(expected.Timings.ToArray(), actual.Timings.ToArray());
        AssertGateReleased(state);
    }

    [TestMethod]
    public void ProcessingTrySnapshotPreservesDurableAndFailureStateAndReleasesGate()
    {
        var state = new CaptureProcessingState();
        state.SetDurable(3, 2, 1, 1, DateTimeOffset.UnixEpoch);
        state.SetProcessingEvidence(2, 3);
        state.SetRefreshFailure();
        var expected = state.Snapshot;

        Assert.IsTrue(state.TrySnapshot(TimeSpan.Zero, out var actual));

        Assert.IsNotNull(actual);
        Assert.AreEqual(expected with { EvaluatedUtc = null }, actual with { EvaluatedUtc = null });
        Assert.IsTrue(actual.EvaluatedUtc >= expected.EvaluatedUtc);
        AssertGateReleased(state);
    }

    [TestMethod]
    public void TelemetryTryGetLatestPreservesSampleAndReleasesGate()
    {
        var sink = new CaptureTelemetrySink();
        var sample = new CaptureTelemetrySample(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(10), 1, null, CaptureMode.Still, false, true,
            TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(3), []);
        sink.Report(sample);

        Assert.IsTrue(sink.TryGetLatest(TimeSpan.Zero, out var actual));

        Assert.AreSame(sample, actual);
        Assert.AreSame(sink.Latest, actual);
        AssertGateReleased(sink);
    }

    [TestMethod]
    public void TelemetryTryGetLatestDistinguishesEmptySampleFromAcquisitionTimeout()
    {
        var sink = new CaptureTelemetrySink();
        Assert.IsTrue(sink.TryGetLatest(TimeSpan.Zero, out var sample));
        Assert.IsNull(sample);
    }

    [TestMethod]
    [DataRow("fleet")]
    [DataRow("telemetry")]
    [DataRow("processing")]
    public async Task BoundedReadReturnsWithoutAWaiterWhileRealGateIsStillHeld(string source)
    {
        object state = source switch
        {
            "fleet" => new FleetRuntimeState(TimeProvider.System),
            "telemetry" => new CaptureTelemetrySink(),
            _ => new CaptureProcessingState()
        };
        var gate = Gate(state);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            lock (gate)
            {
                held.Set();
                release.Wait();
            }
        })
        { IsBackground = true };
        Task<bool>? reader = null;
        holder.Start();
        try
        {
            Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(5)));
            reader = Task.Run(() => TryRead(state, TimeSpan.FromMilliseconds(50)));
            Assert.IsFalse(await reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false));
            Assert.IsTrue(reader.IsCompleted, "Acquisition itself must finish before the gate is released.");
            Assert.IsTrue(holder.IsAlive);
            Assert.IsFalse(release.IsSet);
        }
        finally
        {
            release.Set();
            Assert.IsTrue(holder.Join(TimeSpan.FromSeconds(5)));
            if (reader is not null) await reader.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        Assert.IsTrue(TryRead(state, TimeSpan.Zero), "A timed-out acquisition must permit a later successful snapshot.");
        AssertGateReleased(state);
    }

    [TestMethod]
    [DataRow("fleet", -2L)]
    [DataRow("fleet", 2147483648L)]
    [DataRow("telemetry", -2L)]
    [DataRow("telemetry", 2147483648L)]
    [DataRow("processing", -2L)]
    [DataRow("processing", 2147483648L)]
    public void BoundedReadRejectsInvalidTimeoutAndLeavesGateAvailable(string source, long timeoutMilliseconds)
    {
        object state = source switch
        {
            "fleet" => new FleetRuntimeState(TimeProvider.System),
            "telemetry" => new CaptureTelemetrySink(),
            _ => new CaptureProcessingState()
        };
        var timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => TryRead(state, timeout));

        AssertGateReleased(state);
    }

    [TestMethod]
    public void FleetTrySnapshotPropagatesProjectionExceptionAndReleasesGate()
    {
        var state = new FleetRuntimeState(TimeProvider.System);
        var timings = (Dictionary<FleetTimingSegment, Queue<double>>)typeof(FleetRuntimeState)
            .GetField("_timings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;
        // An empty timing queue makes the percentile projection throw inside TrySnapshot's critical section.
        timings.Add(FleetTimingSegment.Processing, new Queue<double>());

        Assert.ThrowsExactly<IndexOutOfRangeException>(() => state.TrySnapshot(TimeSpan.Zero, out _));

        Assert.IsFalse(Monitor.IsEntered(Gate(state)), "The throwing projection must release the caller's gate ownership.");
        AssertGateReleased(state);
    }

    private static bool TryRead(object state, TimeSpan timeout) => state switch
    {
        FleetRuntimeState fleet => fleet.TrySnapshot(timeout, out _),
        CaptureTelemetrySink telemetry => telemetry.TryGetLatest(timeout, out _),
        CaptureProcessingState processing => processing.TrySnapshot(timeout, out _),
        _ => throw new InvalidOperationException("Unknown bounded snapshot source.")
    };

    private static object Gate(object state)
        => state.GetType().GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(state)!;

    private static void AssertGateReleased(object state)
    {
        var acquired = false;
        var probe = new Thread(() =>
        {
            var gate = Gate(state);
            if (!Monitor.TryEnter(gate, TimeSpan.FromMilliseconds(50))) return;
            acquired = true;
            Monitor.Exit(gate);
        })
        { IsBackground = true };
        probe.Start();
        Assert.IsTrue(probe.Join(TimeSpan.FromSeconds(2)));
        Assert.IsTrue(acquired, "The snapshot must release the gate for another thread.");
    }
}
