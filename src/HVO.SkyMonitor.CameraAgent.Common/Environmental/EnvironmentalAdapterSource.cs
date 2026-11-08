using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

/// <summary>Shared validation and fact construction; no cached value is substituted on device failure.</summary>
public abstract class EnvironmentalAdapterSource : IEnvironmentalSource
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    protected EnvironmentalAdapterSource(EnvironmentalSourceDescriptor descriptor, EnvironmentalDeviceOptions options,
        IEnvironmentalDevice? device, string alias, params EnvironmentalObservationKind[] kinds)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(kinds);
        if (descriptor.Type != alias || !kinds.Contains(descriptor.Kind) || string.IsNullOrWhiteSpace(options.DeviceId) ||
            options.DeviceId.Length > 128 || options.DeviceId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_') ||
            options.Simulation is { } simulation && (simulation.Payload is null || simulation.Payload.Length > 16_384 ||
                !Enum.IsDefined(simulation.Mode) || simulation.SampleAgeSeconds is < -120 or > 86_400 || simulation.IntervalSeconds is < 0 or > 86_400) ||
            descriptor.StaleAfterSeconds is < 1 or > 86_400 || descriptor.ValidForSeconds < descriptor.StaleAfterSeconds)
            throw new ArgumentException("Environmental device configuration is invalid.");
        Descriptor = descriptor;
        DeviceOptions = options;
        Device = options.Simulation is { } simulated ? new SimulatedEnvironmentalDevice(simulated) : device;
    }

    public EnvironmentalSourceDescriptor Descriptor { get; }
    protected EnvironmentalDeviceOptions DeviceOptions { get; }
    private IEnvironmentalDevice? Device { get; }

    public async ValueTask<EnvironmentalSourceAcquisitionResult> AcquireAsync(EnvironmentalSourceAcquisitionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (Device is null) return Unavailable("device-unconfigured");
        EnvironmentalDeviceSample sample;
        try
        {
            sample = await Device.ReadAsync(DeviceOptions.DeviceId, Descriptor.Type == "SkyQualityMeter" ? "rx" : "bridge-v1",
                context.ObservedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException) { return Unavailable("device-disconnected"); }
        cancellationToken.ThrowIfCancellationRequested();
        if (sample.Status != EnvironmentalDeviceStatus.Ready)
            return Unavailable(sample.Status switch
            {
                EnvironmentalDeviceStatus.Missing => "device-missing",
                EnvironmentalDeviceStatus.Disconnected => "device-disconnected",
                EnvironmentalDeviceStatus.OutOfRange => "device-out-of-range",
                _ => "device-faulted"
            });
        if (!sample.Simulated && sample.ReceivedAtUtc is null) return Unavailable("device-receipt-missing");
        if (sample.Payload.Length > 16_384) return Unavailable("device-oversized");
        var received = sample.ReceivedAtUtc ?? context.ObservedAtUtc;
        if (received == default || received.Offset != TimeSpan.Zero) return Unavailable("device-receipt-invalid");
        if (sample.SampledAtUtc == default || sample.SampledAtUtc.Offset != TimeSpan.Zero ||
            sample.SampledAtUtc > received.AddSeconds(120)) return Unavailable("device-clock-skew");
        if (sample.SampledAtUtc.AddSeconds(Descriptor.StaleAfterSeconds) <= received)
            return Unavailable("device-stale");
        try { return await ParseAsync(sample, context, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException or OverflowException or KeyNotFoundException)
        { return Unavailable("device-malformed"); }
    }

    protected abstract ValueTask<EnvironmentalSourceAcquisitionResult> ParseAsync(EnvironmentalDeviceSample sample,
        EnvironmentalSourceAcquisitionContext context, CancellationToken cancellationToken);

    internal static EnvironmentalSourceAcquisitionResult Unavailable(string reason)
        => new(EnvironmentalSourceAcquisitionOutcome.Missing, reason);

    protected EnvironmentalObservationFactV1 Fact(EnvironmentalDeviceSample sample, EnvironmentalSourceAcquisitionContext context,
        EnvironmentalObservationValue value, string? suffix = null, EnvironmentalObservationSourceKind? sourceKind = null,
        IReadOnlyList<EnvironmentalObservationReference>? lineage = null, DateTimeOffset? from = null)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(value);
        var parameters = CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            protocol = Descriptor.Type == "SkyQualityMeter" ? "sqm-rx-v1" : "environmental-bridge-v1",
            deviceGroup = DeviceOptions.DeviceId,
            acquisitionSourceId = Descriptor.Id,
            DeviceOptions.ModelVersion,
            DeviceOptions.ClearDeltaCelsius,
            DeviceOptions.CloudyDeltaCelsius
        }, SerializerOptions));
        var parametersIdentity = CaptureContractJson.ComputeCanonicalJsonSha256(parameters);
        var sourceVersion = CaptureContractJson.ComputeCanonicalJsonSha256(new
        { Algorithm = "environmental-adapter-v1", sample.Simulated, ParametersSha256 = parametersIdentity });
        var source = new EnvironmentalObservationSource(Descriptor.Type, Descriptor.Id + suffix, sourceVersion,
            sourceKind ?? (sample.Simulated ? EnvironmentalObservationSourceKind.Simulated : EnvironmentalObservationSourceKind.Measured),
            new(new("environmental-adapter", "adapter-v1"), parameters, parametersIdentity));
        var withMetadata = value with
        {
            Metadata = (value.Metadata ?? new()) with { DeviceReceivedAtUtc = sample.ReceivedAtUtc ?? context.ObservedAtUtc }
        };
        var identity = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            source,
            value = withMetadata,
            sample.SampledAtUtc,
            context.DeploymentLocation.CanonicalSha256,
            from
        });
        return new(EnvironmentalObservationSchemaVersions.V3, new Guid(Convert.FromHexString(identity).AsSpan(0, 16)),
            source, sample.SampledAtUtc, from, from.HasValue ? sample.SampledAtUtc : null,
            sample.SampledAtUtc, sample.SampledAtUtc.AddSeconds(Descriptor.ValidForSeconds),
            sample.SampledAtUtc.AddSeconds(Descriptor.StaleAfterSeconds), withMetadata, lineage ?? [], Descriptor.RigId);
    }

    protected static EnvironmentalSourceAcquisitionResult Produced(EnvironmentalObservationFactV1 fact)
        => EnvironmentalObservationFactJson.Validate(fact).IsValid
            ? new(EnvironmentalSourceAcquisitionOutcome.Produced, "produced", fact) : Unavailable("device-out-of-range");

    protected static JsonDocument ParseJson(ReadOnlyMemory<byte> payload)
        => EnvironmentalPayloadJson.Parse(payload, 8);
}
