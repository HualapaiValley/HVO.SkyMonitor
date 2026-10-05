using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Video.FFmpeg;

namespace HVO.SkyMonitor.LogicHost.Services.TimeLapses;

internal enum CentralTimeLapseState { Queued, Working, Produced, NoSources, Unavailable, Failed }

/// <summary>Explicit opt-in per received rig and deployment; never guesses from a device's current settings.</summary>
internal sealed record CentralTimeLapseTarget(Guid DevicePublicId, string RigProfileSha256, string LocationId, long LocationVersion, int Generation = 1);

internal sealed class CentralTimeLapseOptions
{
    public bool Enabled { get; set; }
    public List<CentralTimeLapseTarget> Targets { get; set; } = [];
    public TimeLapseFrameOptions Frames { get; set; } = new();
    public TimeLapseTimingOptions Timing { get; set; } = new();
    public int MaximumDimension { get; set; } = 4096;
    public bool PreferNvidia { get; set; } = true;
    public TimeSpan SettleAllowance { get; set; } = TimeSpan.FromMinutes(10);
    public int CatchUpDays { get; set; } = 2;
    public FFmpegOptions Encoder { get; set; } = new()
    {
        ScratchDirectory = "/tmp/hvo-central-time-lapses", Timeout = TimeSpan.FromMinutes(30),
        MaximumOutputBytes = 16L * 1024 * 1024 * 1024, MaximumScratchBytes = 48L * 1024 * 1024 * 1024
    };

    internal void Validate()
    {
        if (Targets.Count > 32 || Targets.Select(static t => t.DevicePublicId).Distinct().Count() != Targets.Count ||
            Targets.Any(static t => t.DevicePublicId == Guid.Empty || t.RigProfileSha256 is not { Length: 64 } ||
                !t.RigProfileSha256.All(char.IsAsciiHexDigit) || string.IsNullOrWhiteSpace(t.LocationId) || t.LocationVersion < 1 || t.Generation < 1) ||
            MaximumDimension is < 128 or > 4096 || CatchUpDays is < 1 or > 7 ||
            SettleAllowance < TimeSpan.FromMinutes(1) || SettleAllowance > TimeSpan.FromHours(6))
            throw new ArgumentException("Invalid central time-lapse configuration.");
        _ = TimeLapseFrameRenderer.RecipeIdentity(Frames);
        _ = TimeLapseTimelinePlanner.Create(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(1), [], Timing);
    }
}

internal sealed record CentralTimeLapsePreset(CameraRigConfig Rig, TimeLapseFrameOptions Frames,
    TimeLapseTimingOptions Timing, int MaximumDimension, TimeLapseEncoderProfile EncoderProfile, string CapabilityIdentity, int Generation = 1);

internal sealed record CentralTimeLapseSource(Guid CentralArtifactId, ReconstructionDescriptor Descriptor, string StorageReference, long ByteLength);

/// <summary>Frozen request. Daily requests reference exact independently produced hourly requests.</summary>
internal sealed record CentralTimeLapseRequest(Guid DevicePublicId, Guid ObservatoryId, SunriseReportingPeriod Period,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset EligibleUtc, bool IsDaily,
    CentralTimeLapsePreset Preset, IReadOnlyList<CentralTimeLapseSource> Sources,
    IReadOnlyDictionary<string, int> Exclusions, IReadOnlyList<Guid> HourlyJobIds)
{
    public string? DiscoveryIdentity { get; init; }
    internal string Identity => CaptureContractJson.ComputeCanonicalJsonSha256(this);
    internal Guid JobId => ProcessingIdentity.CreateArtifactId(Identity);
}

internal sealed record CentralTimeLapseFrameLineage(Guid ArtifactId, string RenderingIdentitySha256,
    IReadOnlyList<Guid> StackSourceIds, IReadOnlyList<string> StackPayloadSha256, TimeSpan TotalIntegration);

internal sealed record CentralTimeLapseChunk(string Key, long Bytes, string Sha256);

internal sealed record CentralTimeLapseProduct(Guid ProductId, Guid JobId, Guid DevicePublicId, Guid ObservatoryId,
    SunriseReportingPeriod Period, DateTimeOffset StartUtc, DateTimeOffset EndUtc, bool IsDaily, bool IsGapFiller,
    bool HasGaps, string RequestIdentity, TimeLapseTimeline? Timeline, IReadOnlyList<CentralTimeLapseFrameLineage> Frames,
    IReadOnlyList<Guid> SegmentProductIds, IReadOnlyDictionary<string, int> Exclusions,
    TimeLapseEncodingEvidence Encoding, IReadOnlyList<CentralTimeLapseChunk> Chunks, DateTimeOffset CreatedUtc,
    Guid PublicationJobId, int PublicationAttempt);

internal sealed record CentralTimeLapseLease(Guid JobId, Guid Token, CentralTimeLapseRequest Request, int Attempt);

internal static class CentralTimeLapseJson
{
    private const int MaximumDocumentBytes = 64 * 1024 * 1024;
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
    internal static string Serialize<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        RequireBound(json);
        return json;
    }
    internal static T Read<T>(string json, string sha256)
    {
        RequireBound(json);
        if (!string.Equals(CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.Deserialize<JsonElement>(json)),
                sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Time-lapse metadata checksum mismatch.");
        return JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("Missing time-lapse metadata.");
    }
    internal static string Hash(string json) => CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.Deserialize<JsonElement>(json));

    private static void RequireBound(string json)
    {
        if (json.Length > MaximumDocumentBytes || System.Text.Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes)
            throw new InvalidDataException("Time-lapse metadata exceeds its bounded document size.");
    }
}
