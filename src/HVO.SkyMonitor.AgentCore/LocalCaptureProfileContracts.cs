using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.AgentCore;

/// <summary>A coordinate-free immutable local acquisition and processing profile.</summary>
public sealed record LocalCaptureProfileDefinition(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] CameraModuleDescriptor Module,
    [property: JsonRequired] CameraRigConfig Rig,
    [property: JsonRequired] IReadOnlyList<CaptureProcessingStepConfig> ProcessingSteps,
    [property: JsonRequired] CaptureScheduleDefinition Schedule,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    CapturePipelineDependencyPolicy DependencyPolicy = CapturePipelineDependencyPolicy.LegacyInference)
{
    public const string LegacySchemaVersion = "cameraagent-local-profile-v1";
    public const string CurrentSchemaVersion = "cameraagent-local-profile-v2";

    public static LocalCaptureProfileDefinition Create(
        CameraModuleConfig configuration,
        CaptureScheduleDefinition schedule)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(schedule);
        return new LocalCaptureProfileDefinition(
            LegacySchemaVersion,
            configuration.Module,
            configuration.Rig,
            configuration.ResolveProcessingSteps(),
            schedule);
    }

    public static LocalCaptureProfileDefinition CreateV2(
        CameraModuleConfig configuration,
        CaptureScheduleDefinition schedule)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(schedule);
        return new LocalCaptureProfileDefinition(
            CurrentSchemaVersion,
            configuration.Module,
            configuration.Rig,
            configuration.ResolveProcessingSteps(),
            schedule,
            CapturePipelineDependencyPolicy.RejectEnabledDependent);
    }

    public static LocalCaptureProfileDefinition CreateForConfiguration(
        CameraModuleConfig configuration,
        CaptureScheduleDefinition schedule)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(schedule);
        return configuration.Pipeline is { SchemaVersion: CapturePipelineSchemaVersions.ExplicitV2 }
            ? CreateV2(configuration, schedule)
            : Create(configuration, schedule);
    }

    public CameraModuleConfig ApplyTo(CameraModuleConfig configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var explicitV2 = string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal);
        return configuration with
        {
            Module = Module,
            Rig = Rig,
            ProcessingSteps = explicitV2 ? null : ProcessingSteps,
            Pipeline = explicitV2
                ? new CapturePipelineConfig(
                    ProcessingSteps,
                    CapturePipelineSchemaVersions.ExplicitV2,
                    DependencyPolicy)
                : null,
            Schedule = Schedule
        };
    }
}

public static class LocalCaptureProfileContract
{
    public static CaptureContractValidationResult Validate(LocalCaptureProfileDefinition? profile)
    {
        if (profile is null ||
            profile.SchemaVersion is not (LocalCaptureProfileDefinition.LegacySchemaVersion or
                LocalCaptureProfileDefinition.CurrentSchemaVersion) ||
            profile.Module is null || string.IsNullOrWhiteSpace(profile.Module.Type) ||
            profile.Rig is null || profile.ProcessingSteps is null)
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidSchedule,
                "localProfile");
        }
        if (string.Equals(profile.SchemaVersion, LocalCaptureProfileDefinition.CurrentSchemaVersion, StringComparison.Ordinal) &&
            profile.DependencyPolicy != CapturePipelineDependencyPolicy.RejectEnabledDependent)
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidSchedule,
                "localProfile.dependencyPolicy");
        }
        if (string.Equals(profile.SchemaVersion, LocalCaptureProfileDefinition.LegacySchemaVersion, StringComparison.Ordinal) &&
            profile.DependencyPolicy != CapturePipelineDependencyPolicy.LegacyInference)
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidSchedule,
                "localProfile.dependencyPolicy");
        }
        return CaptureScheduleContract.Validate(profile.Schedule);
    }

    public static string ComputeSha256(LocalCaptureProfileDefinition profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var validation = Validate(profile);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"The local capture profile is invalid ({validation.FieldPath}).",
                nameof(profile));
        }
        return CaptureContractJson.ComputeCanonicalJsonSha256(profile);
    }
}
