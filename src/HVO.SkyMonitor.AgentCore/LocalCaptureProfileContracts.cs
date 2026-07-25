using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.AgentCore;

/// <summary>A coordinate-free immutable local acquisition and processing profile.</summary>
public sealed record LocalCaptureProfileDefinition(
    [property: JsonRequired] string SchemaVersion,
    [property: JsonRequired] CameraModuleDescriptor Module,
    [property: JsonRequired] CameraRigConfig Rig,
    [property: JsonRequired] IReadOnlyList<CaptureProcessingStepConfig> ProcessingSteps,
    [property: JsonRequired] CaptureScheduleDefinition Schedule)
{
    public const string CurrentSchemaVersion = "cameraagent-local-profile-v1";

    public static LocalCaptureProfileDefinition Create(
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
            schedule);
    }

    public CameraModuleConfig ApplyTo(CameraModuleConfig configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration with
        {
            Module = Module,
            Rig = Rig,
            ProcessingSteps = ProcessingSteps,
            Pipeline = null,
            Schedule = Schedule
        };
    }
}

public static class LocalCaptureProfileContract
{
    public static CaptureContractValidationResult Validate(LocalCaptureProfileDefinition? profile)
    {
        if (profile is null ||
            !string.Equals(profile.SchemaVersion, LocalCaptureProfileDefinition.CurrentSchemaVersion, StringComparison.Ordinal) ||
            profile.Module is null || string.IsNullOrWhiteSpace(profile.Module.Type) ||
            profile.Rig is null || profile.ProcessingSteps is null)
        {
            return CaptureContractValidationResult.Failure(
                CaptureContractReasonCodes.InvalidSchedule,
                "localProfile");
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
