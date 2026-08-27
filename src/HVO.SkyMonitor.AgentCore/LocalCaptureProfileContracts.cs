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

    public static LocalCaptureProfileDefinition CreateEffectiveForConfiguration(
        CameraModuleConfig configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var schedule = configuration.Schedule ?? throw new InvalidOperationException(
            "An effective local capture profile requires a capture schedule.");
        var profileSchemaVersion = configuration.Pipeline.SchemaVersion switch
        {
            CapturePipelineSchemaVersions.LegacyV1 => LegacySchemaVersion,
            CapturePipelineSchemaVersions.ExplicitV2 => CurrentSchemaVersion,
            _ => throw new InvalidOperationException(
                $"Unsupported effective capture pipeline schema '{configuration.Pipeline.SchemaVersion}'.")
        };
        return new LocalCaptureProfileDefinition(
            profileSchemaVersion,
            configuration.Module,
            configuration.Rig,
            configuration.Pipeline.Steps,
            schedule,
            configuration.Pipeline.DependencyPolicy);
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
            Rig = Rig,
            Pipeline = new CapturePipelineConfig(
                ProcessingSteps,
                explicitV2 ? CapturePipelineSchemaVersions.ExplicitV2 : CapturePipelineSchemaVersions.LegacyV1,
                DependencyPolicy),
            Schedule = Schedule
        };
    }

    public LocalCaptureProfileDefinition NormalizePersistedRevisionForRead()
    {
        if (SchemaVersion is not (LegacySchemaVersion or CurrentSchemaVersion))
        {
            throw new InvalidOperationException($"Unsupported local capture profile schema '{SchemaVersion}'.");
        }
        return this with
        {
            Rig = Rig with { ControlPolicy = NormalizeLegacyControlPolicy(Rig.ControlPolicy) }
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
                GainControl = ResolveLegacyOwnership(policy.GainControl, policy.AutoGain),
                AutoExposure = null,
                AutoGain = null
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
        if (profile is null)
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidSchedule,
                "localProfile");
        }
        var validation = ValidatePersistedRevision(profile);
        if (!validation.IsValid)
        {
            return validation;
        }
        if (!string.Equals(
                profile.SchemaVersion,
                LocalCaptureProfileDefinition.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidSchedule,
                "localProfile.schemaVersion");
        }
        if (profile.Schedule.WeeklyWindows.Count == 0 || profile.Schedule.LegacyAlwaysOpen ||
            profile.Schedule.LegacySetpointProfileId is not null)
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidSchedule,
                "localProfile.schedule.weeklyWindows");
        }
        return ValidateEffectiveControlPolicy(profile);
    }

    private static CaptureContractValidationResult ValidateEffectiveControlPolicy(
        LocalCaptureProfileDefinition profile)
    {
        var policy = profile.Rig.ControlPolicy;
        if (policy is null)
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidSchedule,
                "localProfile.rig.controlPolicy");
        }
        if (!Enum.IsDefined(policy.ExposureControl) ||
            policy.ExposureControl == AutomaticControlOwnership.Unspecified)
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidSchedule,
                "localProfile.rig.controlPolicy.exposureControl");
        }
        if (!Enum.IsDefined(policy.GainControl) ||
            policy.GainControl == AutomaticControlOwnership.Unspecified)
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidSchedule,
                "localProfile.rig.controlPolicy.gainControl");
        }
        if (policy.AutoExposure is not null || policy.AutoGain is not null)
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidSchedule,
                "localProfile.rig.controlPolicy.legacyDirective");
        }
        return CaptureContractValidationResult.Success;
    }

    public static CaptureContractValidationResult ValidatePersistedRevision(
        LocalCaptureProfileDefinition? profile)
    {
        if (profile is null || profile.Module is null || string.IsNullOrWhiteSpace(profile.Module.Type) ||
            profile.Rig is null || profile.ProcessingSteps is null)
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidSchedule,
                "localProfile");
        }
        if (!string.Equals(
                profile.SchemaVersion,
                LocalCaptureProfileDefinition.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidSchedule,
                "localProfile.schemaVersion");
        }
        if (profile.DependencyPolicy != CapturePipelineDependencyPolicy.RejectEnabledDependent)
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidSchedule,
                "localProfile.dependencyPolicy");
        }
        var policy = profile.Rig.ControlPolicy;
        if (policy?.AutoExposure is { } autoExposure && !Enum.IsDefined(autoExposure))
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidSchedule,
                "localProfile.rig.controlPolicy.autoExposure");
        }
        if (policy?.AutoGain is { } autoGain && !Enum.IsDefined(autoGain))
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidSchedule,
                "localProfile.rig.controlPolicy.autoGain");
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

    public static string ComputePersistedRevisionSha256(LocalCaptureProfileDefinition profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var validation = ValidatePersistedRevision(profile);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"The persisted local capture profile is invalid ({validation.FieldPath}).",
                nameof(profile));
        }
        return CaptureContractJson.ComputeCanonicalJsonSha256(profile);
    }

    public static string ComputeEffectiveSha256(LocalCaptureProfileDefinition profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var validation = ValidatePersistedRevision(profile);
        if (validation.IsValid)
        {
            validation = ValidateEffectiveControlPolicy(profile);
        }
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                $"The effective local capture profile is invalid ({validation.FieldPath}).",
                nameof(profile));
        }
        return CaptureContractJson.ComputeCanonicalJsonSha256(profile);
    }

    public static string ComputeEffectiveSha256(CameraModuleConfig configuration)
        => ComputeEffectiveSha256(LocalCaptureProfileDefinition.CreateEffectiveForConfiguration(configuration));
}
