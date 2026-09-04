using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.ProcessingRunner.Contracts;

/// <summary>
/// The provider-neutral self-hosted processing runner protocol (<c>processing-runner-v1</c>). LogicHost is the only
/// authority for durable job state; runners register capabilities, claim eligible jobs, fetch inputs and upload
/// products under job-scoped lease credentials, and never hold database, object-store, or identity credentials.
/// </summary>
public static partial class ProcessingRunnerProtocol
{
    public const int Version = 1;

    public const string ContractName = "processing-runner-v1";

    public const string Scope = "api.runner";

    public const string ArtifactReadScope = "api.artifacts.read";

    /// <summary>The runner-facing route root, relative to the LogicHost base address.</summary>
    public const string RoutePrefix = "api/v1.0/processing-runners";

    public const string RunnerIdHeader = "X-HVO-Runner-Id";

    public const string JobIdHeader = "X-HVO-Job-Id";

    public const string LeaseTokenHeader = "X-HVO-Lease-Token";

    public const string ChecksumHeader = "X-Artifact-SHA256";

    public const string OutcomePartName = "outcome";

    public const string PayloadPartPrefix = "payload-";

    public const int MaximumRunnerIdLength = 128;

    public const int MaximumDisplayNameLength = 256;

    public const int MaximumLabelCount = 32;

    public const int MaximumLabelLength = 64;

    public const int MaximumRecipeCount = 64;

    public const int MaximumInputCount = 256;

    public const int MaximumAuxiliaryInputCount = 32;

    public const int MaximumProductCount = 64;

    public const int MaximumConcurrency = 32;

    public const int MaximumActiveJobReport = 64;

    /// <summary>Matches the LogicHost single-object PUT limit so a product can always be written back.</summary>
    public const long MaximumTransferBytes = 100L * 1024 * 1024;

    public const int MaximumMetadataBytes = 4 * 1024 * 1024;

    public static readonly TimeSpan MinimumLeaseDuration = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromHours(1);

    public static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex RunnerIdPattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._:/-]{0,63}$")]
    private static partial Regex LabelPattern();

    public static bool IsValidRunnerId(string? runnerId)
        => !string.IsNullOrWhiteSpace(runnerId) && RunnerIdPattern().IsMatch(runnerId);

    public static bool IsValidLabel(string? label)
        => !string.IsNullOrWhiteSpace(label) && LabelPattern().IsMatch(label);

    public static string ComputeSha256(ReadOnlySpan<byte> payload)
        => Convert.ToHexString(SHA256.HashData(payload));

    public static bool ChecksumEquals(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
            && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            MaxDepth = 32
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }
}

/// <summary>
/// Job classes are explicit in the protocol so eligibility is enforced by contract and authorization rather than
/// convention: only <see cref="CentralRecipe"/> is claimable through LogicHost, <see cref="CameraAgentArchivedReplay"/>
/// is reserved for the CameraAgent adoption of this protocol, and <see cref="CameraAgentLive"/> can never be claimed.
/// </summary>
public enum ProcessingRunnerJobClass
{
    CentralRecipe,
    CameraAgentArchivedReplay,
    CameraAgentLive
}

public enum ProcessingRunnerWarmState
{
    Cold,
    Warming,
    Warm,
    Degraded
}

public enum ProcessingRunnerRegistrationStatus
{
    Active,
    Stale,
    Retired
}

public static class ProcessingRunnerReasonCodes
{
    public const string JobClassNotClaimable = "runner.job-class-not-claimable";
    public const string RegistrationRequired = "runner.registration-required";
    public const string RegistrationNotOwned = "runner.registration-not-owned";
    public const string RegistrationRetired = "runner.registration-retired";
    public const string CapabilityMismatch = "runner.capability-mismatch";
    public const string RecipeVersionMismatch = "runner.recipe-version-mismatch";
    public const string InvalidRunnerId = "runner.invalid-runner-id";
    public const string InvalidCapabilities = "runner.invalid-capabilities";
    public const string LeaseStale = "runner.lease-stale";
    public const string LeaseCanceled = "runner.lease-canceled";
    public const string PayloadChecksumMismatch = "runner.payload-checksum-mismatch";
    public const string PayloadLengthMismatch = "runner.payload-length-mismatch";
    public const string TransferTooLarge = "runner.transfer-too-large";
    public const string InvalidCompletion = "runner.invalid-completion";
    public const string InputUnavailable = "runner.input-unavailable";
    public const string RunnersDisabled = "runner.disabled";
    public const string Unavailable = "runner.unavailable";
}
