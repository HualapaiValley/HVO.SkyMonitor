using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;

/// <summary>
/// A 16x16 zenith fisheye rig at the Hualapai Valley observatory, its tiny Mono8 preview frames, and the observing day
/// of 2026-10-01 (America/Phoenix noon to noon, 19:00Z to 19:00Z).
/// </summary>
internal static class NightlyProductFixture
{
    internal const string NodeId = "preview";
    internal const string PreviewVariant = "preview";
    internal const int Size = 16;
    internal static readonly string PreviewRecipe = new('A', 64);

    internal static readonly ObservatoryLocation Observatory = new(35.5599378, -113.9119818, 520, "America/Phoenix");

    internal static readonly ObservingDayCalendar Calendar = ObservingDayCalendar.Create("America/Phoenix");

    internal static readonly DateOnly ObservingDate = new(2026, 10, 1);

    internal static readonly DateTimeOffset DayStartUtc = new(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);

    internal static readonly DateTimeOffset DayEndUtc = DayStartUtc.AddDays(1);

    internal static readonly CameraRigConfig Rig = new(
        new SensorProfile("NightlyFixture", Size, Size, 5.86, SensorColorMode.Mono, CameraPixelFormat.Mono16,
            StrideBytes: Size * 2),
        new OpticsProfile(
            "EquidistantFisheye", 2.5, 180, 0, LensKind.Fisheye,
            PrincipalPointX: Size / 2.0, PrincipalPointY: Size / 2.0, ImageCircleRadiusPixels: Size / 2.0 - 0.5,
            FocalLengthXPixels: (Size / 2.0 - 0.5) * 2 / Math.PI, FocalLengthYPixels: (Size / 2.0 - 0.5) * 2 / Math.PI,
            CalibrationVersion: "nightly-fixture-calibration-v1"),
        new RigOrientation(90, 0, 0),
        new PipelineExposureProfile(
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), 1, 1),
        ProfileVersion: "nightly-fixture-rig-v1");

    internal static readonly string RigProfileSha256 = RigProjectionContextFactory.CreateProfileHashSha256(Rig);

    internal static readonly ProcessingCompatibilityIdentity Compatibility = new(
        RigProfileSha256, "zenith-v1", "none-v1", "full-v1", "sensor-v1", "night-v1", "pipeline-v1");

    internal static CameraModuleConfig Configuration(bool redactedLocation = false) =>
        new(Observatory, new CameraModuleDescriptor("VirtualSky", JsonSerializer.SerializeToElement(new { })), Rig,
            CapturePipelineConfig.Empty, "nightly-fixture")
        {
            DeploymentLocationRedacted = redactedLocation
        };

    internal static NightlyProductOptions Options(
        int maximumSegmentSources = 32,
        int maximumSegmentsPerRun = 64,
        bool enabled = true) => new()
        {
            Enabled = enabled,
            SourceNodeId = NodeId,
            SegmentMinutes = 60,
            SettleSeconds = 300,
            MaximumSegmentSources = maximumSegmentSources,
            MaximumSegmentsPerRun = maximumSegmentsPerRun,
            KeogramMaximumGapSeconds = 300,
            KeogramMaximumGapColumnCount = 8,
            KeogramMaximumColumnCount = 4096
        };

    internal static IOptions<CameraAgentHostOptions> HostOptions(string root, NightlyProductOptions nightly) =>
        Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            NightlyProducts = nightly
        });

    /// <summary>A deterministic frame whose pixels vary with its index so lighten and sampling are observable.</summary>
    internal static NightlyFrame Frame(int index, DateTimeOffset exposureStartedUtc, string? rig = null)
    {
        var pixels = new byte[Size * Size];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i * 7 + index * 31) % 251);
        }
        var artifactId = Guid.Parse($"93000000-0000-0000-0000-{index:D12}");
        var captureId = Guid.Parse($"93100000-0000-0000-0000-{index:D12}");
        var artifact = new ProcessingArtifact(
            artifactId,
            FrameArtifactRole.Preview,
            PreviewVariant,
            PreviewRecipe,
            JournalNightlyProductSourceReader.PackedImageMediaType,
            Layout(),
            pixels,
            exposureStartedUtc.AddSeconds(61),
            TimeSpan.FromSeconds(60),
            rig is null ? Compatibility : Compatibility with { Rig = rig },
            ObservationStartedUtc: exposureStartedUtc);
        var candidate = new NightlyProductCandidate(
            artifactId,
            captureId,
            ProcessingIdentity.CreateOutputIdentity(FrameArtifactRole.Preview, PreviewVariant, PreviewRecipe, [captureId]),
            PreviewVariant,
            PreviewRecipe,
            JournalNightlyProductSourceReader.PackedImageMediaType,
            exposureStartedUtc,
            rig ?? RigProfileSha256,
            null);
        return new NightlyFrame(candidate, artifact);
    }

    internal static FrameLayoutDescriptor Layout() => new(
        Size, Size, Size, CameraPixelFormat.Mono8, FrameByteOrder.NotApplicable, 8, 8, FrameSamplePacking.ByteAligned,
        ColorFilterArrayPattern.None, null, byte.MaxValue, Size * Size);
}

internal sealed record NightlyFrame(NightlyProductCandidate Candidate, ProcessingArtifact Artifact);

/// <summary>A source reader over in-memory frames that records how often payloads were restored.</summary>
internal sealed class InMemoryNightlySourceReader : INightlyProductSourceReader
{
    private readonly List<NightlyFrame> _frames = [];

    internal int Restores { get; private set; }

    internal int UnsupportedPerWindow { get; set; }

    internal Exception? RestoreFailure { get; set; }

    internal int? CandidateOverride { get; set; }

    internal void Add(params NightlyFrame[] frames) => _frames.AddRange(frames);

    public ValueTask<IReadOnlyList<NightlyProductCandidate>> ReadCandidatesAsync(
        string nodeId,
        DateTimeOffset startUtc,
        DateTimeOffset endUtc,
        int maximumCount,
        Action<string> unsupported,
        CancellationToken cancellationToken)
    {
        Assert.AreEqual(NightlyProductFixture.NodeId, nodeId);
        var inWindow = _frames
            .Where(frame => frame.Candidate.ExposureStartedUtc >= startUtc && frame.Candidate.ExposureStartedUtc < endUtc)
            .Select(static frame => frame.Candidate)
            .ToList();
        if (inWindow.Count > 0)
        {
            for (var i = 0; i < UnsupportedPerWindow; i++)
            {
                unsupported(NightlyProductContract.ExcludedUnsupportedSourceReasonCode);
            }
            if (CandidateOverride is { } count)
            {
                inWindow = [.. Enumerable.Repeat(inWindow[0], count)];
            }
        }
        return ValueTask.FromResult<IReadOnlyList<NightlyProductCandidate>>([.. inWindow.Take(maximumCount + 1)]);
    }

    public ValueTask<IReadOnlyList<ProcessingArtifact>> RestoreAsync(
        IReadOnlyList<NightlyProductCandidate> candidates,
        CancellationToken cancellationToken)
    {
        Restores++;
        if (RestoreFailure is { } failure)
        {
            return ValueTask.FromException<IReadOnlyList<ProcessingArtifact>>(failure);
        }
        return ValueTask.FromResult<IReadOnlyList<ProcessingArtifact>>(
            [.. candidates.Select(candidate => _frames.Single(frame => frame.Candidate.ArtifactId == candidate.ArtifactId).Artifact)]);
    }
}

internal sealed class FixedConfigurationAccessor(CameraModuleConfig? configuration) : ICameraAgentConfigurationAccessor
{
    public bool IsConfigured => configuration is not null;

    public void SetConfiguration(CameraModuleConfig config) => throw new NotSupportedException();

    public ValueTask<CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken) =>
        configuration is null
            ? ValueTask.FromException<CameraModuleConfig>(new InvalidOperationException("Not configured."))
            : ValueTask.FromResult(configuration);
}

internal sealed class NightlyClock(DateTimeOffset utcNow) : TimeProvider
{
    internal DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}
