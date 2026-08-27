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
            configuration.Pipeline.Steps,
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
            configuration.Pipeline.Steps,
            schedule,
            CapturePipelineDependencyPolicy.RejectEnabledDependent);
    }

    public static LocalCaptureProfileDefinition CreateForConfiguration(
        CameraModuleConfig configuration,
        CaptureScheduleDefinition schedule)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(schedule);
        return CreateV2(configuration, schedule);
    }

    public CameraModuleConfig ApplyTo(CameraModuleConfig configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (SchemaVersion is not (LegacySchemaVersion or CurrentSchemaVersion))
        {
            throw new InvalidOperationException($"Unsupported local capture profile schema '{SchemaVersion}'.");
        }
        var explicitV2 = string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal);
        return configuration with
        {
            Module = Module,
            Rig = Rig with { ControlPolicy = NormalizeLegacyControlPolicy(Rig.ControlPolicy) },
            Pipeline = new CapturePipelineConfig(
                ProcessingSteps,
                explicitV2 ? CapturePipelineSchemaVersions.ExplicitV2 : CapturePipelineSchemaVersions.LegacyV1,
                DependencyPolicy),
            Schedule = Schedule
        };
    }

    private static CameraControlPolicy NormalizeLegacyControlPolicy(CameraControlPolicy? policy)
        => policy is null
            ? new CameraControlPolicy
            {
                ExposureControl = AutomaticControlOwnership.Disabled,
                GainControl = AutomaticControlOwnership.Disabled
            }
            : policy with
            {
                ExposureControl = ResolveLegacyOwnership(policy.ExposureControl, policy.AutoExposure),
                GainControl = ResolveLegacyOwnership(policy.GainControl, policy.AutoGain)
            };

    private static AutomaticControlOwnership ResolveLegacyOwnership(
        AutomaticControlOwnership ownership,
        CameraFeatureDirective? legacy)
        => ownership != AutomaticControlOwnership.Unspecified
            ? ownership
            : legacy == CameraFeatureDirective.Enabled
                ? AutomaticControlOwnership.HostMetered
                : AutomaticControlOwnership.Disabled;
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
