using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Reflection;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class VirtualSkyCloudObservationProcessingStep(
    CaptureProcessingStepMetadata metadata,
    VirtualSkyCloudObservationProcessingStepOptions options,
    IEnvironmentalObservationPublisher publisher)
    : ConfigurableCaptureProcessingStep<VirtualSkyCloudObservationProcessingStepOptions>(metadata, options)
{
    internal const string Provider = "HVO.SkyMonitor.VirtualSky";
    internal const string ProducerVersion = "virtual-cloud-observation-v1";
    internal const string CoverageAlgorithmVersion = "equal-area-dome-cover-v1";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly JsonSerializerOptions ModuleSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public override async ValueTask ProcessAsync(
        CaptureProcessingContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Options.Enabled || context.ProcessingExecution?.AllowAutomaticPublication == false ||
            context.Frame?.Metadata.Scene?.CloudScenario is not { } provenance)
        {
            return;
        }

        var definition = provenance.Parameters.Deserialize<VirtualCloudScenarioDefinition>(SerializerOptions)
            ?? throw new InvalidDataException("Cloud scenario provenance parameters are missing.");
        definition.Validate();
        var configuredDefinition = ResolveConfiguredDefinition(context);
        var parameters = CaptureContractJson.Canonicalize(provenance.Parameters);
        var parametersSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(parameters);
        if (!string.Equals(parametersSha256, provenance.ParametersSha256, StringComparison.Ordinal) ||
            !string.Equals(definition.ComputeCanonicalScenarioId(), provenance.ScenarioId, StringComparison.Ordinal) ||
            !string.Equals(configuredDefinition.ComputeParametersSha256(), parametersSha256, StringComparison.Ordinal) ||
            !string.Equals(definition.SchemaVersion, provenance.SchemaVersion, StringComparison.Ordinal) ||
            !string.Equals(definition.ScenarioId, provenance.ScenarioId, StringComparison.Ordinal) ||
            !string.Equals(definition.ScenarioVersion, provenance.ScenarioVersion, StringComparison.Ordinal) ||
            definition.Seed != provenance.Seed || definition.EpochUtc != provenance.EpochUtc ||
            definition.TemporalSampleCount != provenance.TemporalSampleCount ||
            !string.Equals(provenance.AlgorithmVersion, VirtualCloudScenarioDefinition.CurrentAlgorithmVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Cloud scenario provenance conflicts with its canonical parameters.");
        }

        var duration = provenance.IntegrationEndUtc - provenance.IntegrationStartUtc;
        if (duration < TimeSpan.Zero ||
            provenance.IntegrationStartUtc.Offset != TimeSpan.Zero ||
            provenance.IntegrationEndUtc.Offset != TimeSpan.Zero ||
            provenance.IntegrationStartUtc != context.Submission.Request.RequestedStartUtc ||
            provenance.IntegrationStartUtc.ToUnixTimeMilliseconds() != context.Frame.TimestampUtc.ToUnixTimeMilliseconds() ||
            duration != context.Frame.Metadata.Exposure ||
            context.Submission.Request.RequestedSetpoint is { } requestedSetpoint && duration != requestedSetpoint.Exposure)
        {
            throw new InvalidDataException("Cloud scenario integration interval conflicts with the captured frame.");
        }
        var field = new VirtualCloudField(definition);
        var coverage = field.ComputeSkyCoverage(provenance.IntegrationStartUtc, duration);
        var observedAtUtc = provenance.IntegrationStartUtc + TimeSpan.FromTicks(duration.Ticks / 2);
        var validThroughUtc = provenance.IntegrationEndUtc > provenance.IntegrationStartUtc
            ? provenance.IntegrationEndUtc
            : provenance.IntegrationStartUtc.AddTicks(1);
        var fact = new EnvironmentalObservationFactV1(
            EnvironmentalObservationFactV1.CurrentSchemaVersion,
            CreateObservationId(parametersSha256, provenance.IntegrationStartUtc, provenance.IntegrationEndUtc),
            new EnvironmentalObservationSource(
                Provider,
                $"cloud-{parametersSha256[..24]}",
                ProducerVersion,
                EnvironmentalObservationSourceKind.Simulated,
                new EnvironmentalObservationProvenance(
                    new ProcessingAlgorithmIdentity("virtual-cloud-dome-cover", CoverageAlgorithmVersion),
                    parameters,
                    parametersSha256)),
            observedAtUtc,
            provenance.IntegrationStartUtc,
            provenance.IntegrationEndUtc,
            provenance.IntegrationStartUtc,
            validThroughUtc,
            validThroughUtc,
            new EnvironmentalObservationValue(
                EnvironmentalObservationKind.CloudCover,
                EnvironmentalObservationUnit.Fraction,
                coverage,
                BooleanValue: null,
                EnvironmentalObservationQuality.Good),
            []);
        _ = await publisher.PublishAsync(fact, cancellationToken).ConfigureAwait(false);
    }

    private static VirtualCloudScenarioDefinition ResolveConfiguredDefinition(CaptureProcessingContext context)
    {
        if (!IsVirtualSkyModule(context.Config.ModuleType) ||
            context.Config.ModuleOptions is not { } moduleOptions ||
            !TryGetProperty(moduleOptions, "cloudScenario", out var scenario) ||
            scenario.Deserialize<VirtualCloudScenarioDefinition>(ModuleSerializerOptions) is not { } configured)
        {
            throw new InvalidDataException("Cloud scenario provenance has no matching VirtualSky configuration.");
        }

        configured.Validate();
        return configured with { ScenarioId = configured.ComputeCanonicalScenarioId() };
    }

    private static bool IsVirtualSkyModule(string moduleType)
        => string.Equals(moduleType, "VirtualSky", StringComparison.OrdinalIgnoreCase) ||
            TypeResolution.TryResolve(moduleType) == typeof(VirtualSkyCameraModule);

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    internal static Guid CreateObservationId(
        string parametersSha256,
        DateTimeOffset integrationStartUtc,
        DateTimeOffset integrationEndUtc)
    {
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(
            CaptureContractJson.SerializeToElement(new
            {
                Schema = "hvo-virtual-cloud-observation-identity-v1",
                ParametersSha256 = parametersSha256,
                IntegrationStartUtc = integrationStartUtc,
                IntegrationEndUtc = integrationEndUtc,
                Kind = EnvironmentalObservationKind.CloudCover,
                AlgorithmVersion = CoverageAlgorithmVersion
            }))));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, bytes.Length).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }
}

public sealed class VirtualSkyCloudObservationProcessingStepOptions
{
    public bool Enabled { get; init; } = true;
}
