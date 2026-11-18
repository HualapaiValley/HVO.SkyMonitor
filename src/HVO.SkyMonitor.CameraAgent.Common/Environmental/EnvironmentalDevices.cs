using System.Text;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public enum EnvironmentalDeviceStatus { Ready, Missing, Disconnected, Faulted, OutOfRange }
public enum SimulatedEnvironmentalDeviceMode { Normal, Missing, Disconnected, Faulted, Stale, ClockSkewed, OutOfRange, Timeout }

/// <summary>Bytes are a bounded bridge-v1 JSON payload, or the SQM rx response. Time is the device sample time.</summary>
public sealed record EnvironmentalDeviceSample(ReadOnlyMemory<byte> Payload, DateTimeOffset SampledAtUtc,
    bool Simulated, EnvironmentalDeviceStatus Status = EnvironmentalDeviceStatus.Ready, DateTimeOffset? ObservedFromUtc = null, DateTimeOffset? ReceivedAtUtc = null);

/// <summary>Physical transport seam. Implementations must honor cancellation, preserve sample time, and bound reads to 16 KiB.</summary>
public interface IEnvironmentalDevice
{
    ValueTask<EnvironmentalDeviceSample> ReadAsync(string deviceId, string command, DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken);
}

public sealed record SimulatedEnvironmentalDeviceOptions(string Payload,
    SimulatedEnvironmentalDeviceMode Mode = SimulatedEnvironmentalDeviceMode.Normal, int SampleAgeSeconds = 0, int IntervalSeconds = 60);

public sealed record EnvironmentalDeviceOptions(string DeviceId, SimulatedEnvironmentalDeviceOptions? Simulation = null,
    double ClearDeltaCelsius = -25, double CloudyDeltaCelsius = -5,
    string ModelVersion = "sky-ambient-linear-v1");

/// <summary>A stateless deterministic device. Recreating it after a restart reproduces the same read.</summary>
public sealed class SimulatedEnvironmentalDevice(SimulatedEnvironmentalDeviceOptions options) : IEnvironmentalDevice
{
    public async ValueTask<EnvironmentalDeviceSample> ReadAsync(string deviceId, string command,
        DateTimeOffset receivedAtUtc, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Mode == SimulatedEnvironmentalDeviceMode.Timeout)
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        var status = options.Mode switch
        {
            SimulatedEnvironmentalDeviceMode.Missing => EnvironmentalDeviceStatus.Missing,
            SimulatedEnvironmentalDeviceMode.Disconnected => EnvironmentalDeviceStatus.Disconnected,
            SimulatedEnvironmentalDeviceMode.Faulted => EnvironmentalDeviceStatus.Faulted,
            SimulatedEnvironmentalDeviceMode.OutOfRange => EnvironmentalDeviceStatus.OutOfRange,
            _ => EnvironmentalDeviceStatus.Ready
        };
        var age = options.Mode switch
        {
            SimulatedEnvironmentalDeviceMode.Stale => 86_401,
            SimulatedEnvironmentalDeviceMode.ClockSkewed => -121,
            _ => options.SampleAgeSeconds
        };
        return new(Encoding.UTF8.GetBytes(options.Payload), receivedAtUtc.AddSeconds(-age), true, status,
            options.IntervalSeconds > 0 ? receivedAtUtc.AddSeconds(-age - options.IntervalSeconds) : null, receivedAtUtc);
    }
}
