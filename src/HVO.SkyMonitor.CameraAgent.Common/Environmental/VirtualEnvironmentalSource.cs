using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public enum VirtualEnvironmentalSourceMode
{
    Normal,
    Missing,
    Failed
}

public sealed record VirtualEnvironmentalSourceOptions(
    [property: JsonRequired] int Seed,
    [property: JsonRequired] DateTimeOffset EpochUtc,
    double? NumericValue = null,
    bool? BooleanValue = null,
    double NoiseAmplitude = 0,
    double? Uncertainty = null,
    EnvironmentalObservationQuality Quality = EnvironmentalObservationQuality.Good,
    VirtualEnvironmentalSourceMode Mode = VirtualEnvironmentalSourceMode.Normal,
    int DelayMilliseconds = 0,
    string AlgorithmVersion = VirtualEnvironmentalSource.AlgorithmVersion);

public sealed class VirtualEnvironmentalSource : IEnvironmentalSource
{
    public const string AlgorithmVersion = "virtual-environment-source-v1";
    private readonly VirtualEnvironmentalSourceOptions _options;

    public VirtualEnvironmentalSource(
        EnvironmentalSourceDescriptor descriptor,
        VirtualEnvironmentalSourceOptions options)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(options);
        Validate(descriptor, options);
        Descriptor = descriptor;
        _options = options;
    }

    public EnvironmentalSourceDescriptor Descriptor { get; }

    public async ValueTask<EnvironmentalSourceAcquisitionResult> AcquireAsync(
        EnvironmentalSourceAcquisitionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_options.DelayMilliseconds > 0)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(_options.DelayMilliseconds), cancellationToken)
                .ConfigureAwait(false);
        }
        if (_options.Mode == VirtualEnvironmentalSourceMode.Missing)
        {
            return new EnvironmentalSourceAcquisitionResult(
                EnvironmentalSourceAcquisitionOutcome.Missing,
                "source-missing");
        }
        if (_options.Mode == VirtualEnvironmentalSourceMode.Failed)
        {
            return new EnvironmentalSourceAcquisitionResult(
                EnvironmentalSourceAcquisitionOutcome.Failed,
                "virtual-source-failure");
        }

        var parameters = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(new
        {
            Schema = AlgorithmVersion,
            _options.Seed,
            _options.EpochUtc,
            Descriptor.Kind,
            _options.NumericValue,
            _options.BooleanValue,
            _options.NoiseAmplitude,
            _options.Uncertainty,
            _options.Quality,
            context.DeploymentLocation.CanonicalSha256
        }));
        var schemaVersion = Descriptor.Kind == EnvironmentalObservationKind.CameraSensorTemperature
            ? EnvironmentalObservationSchemaVersions.V2
            : EnvironmentalObservationSchemaVersions.V1;
        double? numericValue = _options.NumericValue is { } baseline
            ? baseline + _options.NoiseAmplitude * DeterministicNoise(context)
            : null;
        var value = new EnvironmentalObservationValue(
            Descriptor.Kind,
            UnitFor(Descriptor.Kind),
            numericValue,
            _options.BooleanValue,
            _options.Quality,
            _options.Uncertainty);
        var fact = new EnvironmentalObservationFactV1(
            schemaVersion,
            DeterministicObservationId(context),
            new EnvironmentalObservationSource(
                "virtual-environment",
                Descriptor.Id,
                _options.AlgorithmVersion,
                EnvironmentalObservationSourceKind.Simulated,
                new EnvironmentalObservationProvenance(
                    new ProcessingAlgorithmIdentity("virtual-environment-source", _options.AlgorithmVersion),
                    parameters,
                    CaptureContractJson.ComputeCanonicalJsonSha256(parameters))),
            context.ObservedAtUtc,
            null,
            null,
            context.ObservedAtUtc,
            context.ObservedAtUtc.AddSeconds(Descriptor.ValidForSeconds),
            context.ObservedAtUtc.AddSeconds(Descriptor.StaleAfterSeconds),
            value,
            [],
            Descriptor.RigId);
        var validation = EnvironmentalObservationFactJson.Validate(fact);
        return validation.IsValid
            ? new EnvironmentalSourceAcquisitionResult(
                EnvironmentalSourceAcquisitionOutcome.Produced,
                "produced",
                fact)
            : new EnvironmentalSourceAcquisitionResult(
                EnvironmentalSourceAcquisitionOutcome.Failed,
                "invalid-result");
    }

    private double DeterministicNoise(EnvironmentalSourceAcquisitionContext context)
    {
        var hash = Convert.FromHexString(CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            Schema = "virtual-environment-noise-v1",
            Descriptor.Id,
            Descriptor.Kind,
            _options.Seed,
            _options.EpochUtc,
            context.ObservedAtUtc,
            context.DeploymentLocation.CanonicalSha256
        }));
        var unit = BitConverter.ToUInt64(hash, 0) / (double)ulong.MaxValue;
        return unit * 2 - 1;
    }

    private Guid DeterministicObservationId(EnvironmentalSourceAcquisitionContext context)
    {
        var hash = Convert.FromHexString(CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            Schema = "virtual-environment-observation-id-v1",
            Descriptor.Id,
            Descriptor.Kind,
            _options.AlgorithmVersion,
            _options.Seed,
            context.ObservedAtUtc,
            context.DeploymentLocation.CanonicalSha256
        }));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static EnvironmentalObservationUnit UnitFor(EnvironmentalObservationKind kind)
        => kind switch
        {
            EnvironmentalObservationKind.AirTemperature or EnvironmentalObservationKind.CameraSensorTemperature =>
                EnvironmentalObservationUnit.DegreesCelsius,
            EnvironmentalObservationKind.RelativeHumidity => EnvironmentalObservationUnit.Percent,
            EnvironmentalObservationKind.AtmosphericPressure => EnvironmentalObservationUnit.Pascals,
            EnvironmentalObservationKind.WindSpeed or EnvironmentalObservationKind.WindGust =>
                EnvironmentalObservationUnit.MetersPerSecond,
            EnvironmentalObservationKind.WindDirection => EnvironmentalObservationUnit.DegreesTrue,
            EnvironmentalObservationKind.PrecipitationRate => EnvironmentalObservationUnit.MillimetersPerHour,
            EnvironmentalObservationKind.RainState => EnvironmentalObservationUnit.Boolean,
            EnvironmentalObservationKind.SkyBrightness or EnvironmentalObservationKind.SkyQuality =>
                EnvironmentalObservationUnit.MagnitudesPerSquareArcsecond,
            EnvironmentalObservationKind.CloudCover => EnvironmentalObservationUnit.Fraction,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static void Validate(
        EnvironmentalSourceDescriptor descriptor,
        VirtualEnvironmentalSourceOptions options)
    {
        if (string.IsNullOrWhiteSpace(descriptor.Id) || descriptor.Id.Length > 128 ||
            !string.Equals(descriptor.Type, "VirtualEnvironment", StringComparison.Ordinal) ||
            !Enum.IsDefined(descriptor.Kind) || descriptor.Triggers.Count == 0 ||
            descriptor.Triggers.Any(static trigger => !Enum.IsDefined(trigger)) ||
            descriptor.PeriodSeconds is < 1 or > 86_400 || descriptor.EveryNthCapture is < 1 or > 1_000_000 ||
            descriptor.ValidForSeconds is < 1 or > 86_400 || descriptor.StaleAfterSeconds is < 1 or > 86_400 ||
            descriptor.StaleAfterSeconds > descriptor.ValidForSeconds ||
            descriptor.Kind == EnvironmentalObservationKind.CameraSensorTemperature &&
                string.IsNullOrWhiteSpace(descriptor.RigId) ||
            descriptor.RigId is { Length: > 128 } || descriptor.RigId is not null && descriptor.RigId != descriptor.RigId.Trim())
        {
            throw new ArgumentException("The virtual environmental source descriptor is invalid.", nameof(descriptor));
        }
        if (options.EpochUtc == default || options.EpochUtc.Offset != TimeSpan.Zero ||
            !double.IsFinite(options.NoiseAmplitude) || options.NoiseAmplitude < 0 ||
            options.Uncertainty is { } uncertainty && (!double.IsFinite(uncertainty) || uncertainty < 0) ||
            options.DelayMilliseconds is < 0 or > 120_000 || !Enum.IsDefined(options.Quality) ||
            !Enum.IsDefined(options.Mode) || !string.Equals(options.AlgorithmVersion, AlgorithmVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("The virtual environmental source options are invalid.", nameof(options));
        }
        var expectsBoolean = descriptor.Kind == EnvironmentalObservationKind.RainState;
        if (expectsBoolean != options.BooleanValue.HasValue || expectsBoolean == options.NumericValue.HasValue)
        {
            throw new ArgumentException("The virtual environmental source value shape is invalid.", nameof(options));
        }
    }
}
