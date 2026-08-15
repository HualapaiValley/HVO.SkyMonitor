using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HVO.SkyMonitor.Imaging;

/// <summary>One weighted, generic optical profile used by deterministic recurring transient rendering.</summary>
public sealed record VirtualTransientRecurringProfile
{
    public int Weight { get; init; } = 1;
    public IReadOnlyList<VirtualTransientSkyTrack> SkyTracks { get; init; } = Array.Empty<VirtualTransientSkyTrack>();

    internal void Validate()
    {
        if (Weight is < 1 or > 1_000 || SkyTracks is null || SkyTracks.Count is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(VirtualTransientRecurringProfile));
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in SkyTracks)
        {
            ArgumentNullException.ThrowIfNull(track);
            track.Validate();
            if (!identities.Add(track.PrimitiveId) || track.Keyframes[0].OffsetSeconds != 0 ||
                track.Keyframes.Any(static keyframe => keyframe.OffsetSeconds < 0) ||
                track.Keyframes[^1].OffsetSeconds > VirtualTransientRecurrenceDefinition.MaximumEventDurationSeconds)
            {
                throw new ArgumentException("Recurring profiles require unique, zero-based, bounded sky tracks.", nameof(SkyTracks));
            }
        }
    }
}

/// <summary>Finite, bounded recurrence parameters expanded lazily for one exposure.</summary>
public sealed record VirtualTransientRecurrenceDefinition
{
    public const string CurrentSchemaVersion = "virtual-transient-recurrence-v1";
    public const string CurrentAlgorithmVersion = "virtual-transient-recurrence-grid-v1";
    public const int MaximumEventDurationSeconds = 20;

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public int MinimumIntervalSeconds { get; init; } = 1_800;
    public int MaximumIntervalSeconds { get; init; } = 3_600;
    public int EventCount { get; init; } = 256;
    public int MaximumLookaheadSeconds { get; init; } = 86_400;
    public IReadOnlyList<VirtualTransientRecurringProfile> Profiles { get; init; } = Array.Empty<VirtualTransientRecurringProfile>();

    internal void Validate(int staticSkyTracks, int staticKeyframes, DateTimeOffset epochUtc)
    {
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal) ||
            MinimumIntervalSeconds is < 20 or > 86_400 ||
            MaximumIntervalSeconds < MinimumIntervalSeconds || MaximumIntervalSeconds > 604_800 ||
            EventCount is < 1 or > 4_096 || MaximumLookaheadSeconds is < 20 or > 604_800 ||
            Profiles is null || Profiles.Count is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(staticSkyTracks), "Recurrence parameters are outside supported bounds.");
        }

        var totalWeight = 0;
        var maximumTracks = 0;
        var maximumKeyframes = 0;
        var maximumDuration = 0d;
        foreach (var profile in Profiles)
        {
            ArgumentNullException.ThrowIfNull(profile);
            profile.Validate();
            totalWeight = checked(totalWeight + profile.Weight);
            maximumTracks = Math.Max(maximumTracks, profile.SkyTracks.Count);
            maximumKeyframes = Math.Max(maximumKeyframes, profile.SkyTracks.Sum(static track => track.Keyframes.Count));
            maximumDuration = Math.Max(maximumDuration, profile.SkyTracks.Max(static track => track.Keyframes[^1].OffsetSeconds));
        }
        if (totalWeight > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(staticSkyTracks), "Recurring profile weights exceed supported bounds.");
        }

        var candidateCount = Math.Min(EventCount,
            checked((int)Math.Ceiling((MaximumLookaheadSeconds + maximumDuration + MaximumIntervalSeconds) /
                MinimumIntervalSeconds) + 2));
        if (checked(staticSkyTracks + candidateCount * maximumTracks) > 64 ||
            checked(staticKeyframes + candidateCount * maximumKeyframes) > VirtualTransientScenarioDefinition.MaximumKeyframeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(staticSkyTracks), "Recurring expansion exceeds scenario work bounds.");
        }

        var periodTicks = PeriodTicks;
        var jitterTicks = JitterTicks;
        _ = epochUtc.AddTicks(checked((EventCount - 1L) * periodTicks + jitterTicks +
            TimeSpan.FromSeconds(maximumDuration).Ticks));
    }

    internal long PeriodTicks => checked((TimeSpan.FromSeconds(MinimumIntervalSeconds).Ticks +
        TimeSpan.FromSeconds(MaximumIntervalSeconds).Ticks) / 2);

    internal long JitterTicks => checked((TimeSpan.FromSeconds(MaximumIntervalSeconds).Ticks -
        TimeSpan.FromSeconds(MinimumIntervalSeconds).Ticks) / 2);
}

/// <summary>One reproducible recurring event selected without retaining schedule state.</summary>
public sealed record VirtualTransientScheduledEvent(
    string EventId,
    int EventOrdinal,
    int ProfileIndex,
    DateTimeOffset AnchorUtc,
    DateTimeOffset SignalStartUtc,
    DateTimeOffset SignalEndUtc,
    double AzimuthRotationDegrees);

internal static class VirtualTransientRecurrenceScheduler
{
    private static readonly byte[] PhaseDomain = Encoding.UTF8.GetBytes("virtual-transient-event-phase-v1");
    private static readonly byte[] ProfileDomain = Encoding.UTF8.GetBytes("virtual-transient-event-profile-v1");
    private static readonly byte[] RotationDomain = Encoding.UTF8.GetBytes("virtual-transient-event-rotation-v1");
    private static readonly byte[] IdentityDomain = Encoding.UTF8.GetBytes("virtual-transient-event-identity-v1");

    internal static VirtualTransientScheduledEvent Create(
        VirtualTransientScenarioDefinition definition,
        int ordinal,
        ReadOnlySpan<byte> scheduleHash)
    {
        var recurrence = definition.Recurrence ?? throw new ArgumentException("Recurrence is required.", nameof(definition));
        if (ordinal < 0 || ordinal >= recurrence.EventCount)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        var phase = Hash(PhaseDomain, scheduleHash, ordinal);
        var phaseTicks = recurrence.JitterTicks == 0
            ? 0
            : checked((long)(BinaryPrimitives.ReadUInt64LittleEndian(phase) % (ulong)(recurrence.JitterTicks + 1)));
        var anchor = definition.EpochUtc.AddTicks(checked(ordinal * recurrence.PeriodTicks + phaseTicks));
        var profileHash = Hash(ProfileDomain, scheduleHash, ordinal);
        var ticket = BinaryPrimitives.ReadUInt64LittleEndian(profileHash) %
            (ulong)recurrence.Profiles.Sum(static profile => profile.Weight);
        var profileIndex = 0;
        var cumulative = 0UL;
        for (; profileIndex < recurrence.Profiles.Count; profileIndex++)
        {
            cumulative += (ulong)recurrence.Profiles[profileIndex].Weight;
            if (ticket < cumulative)
            {
                break;
            }
        }
        var profile = recurrence.Profiles[profileIndex];
        var duration = TimeSpan.FromSeconds(profile.SkyTracks.Max(static track => track.Keyframes[^1].OffsetSeconds));
        var rotationHash = Hash(RotationDomain, scheduleHash, ordinal);
        var rotation = BinaryPrimitives.ReadUInt32LittleEndian(rotationHash) / 4_294_967_296d * 360d;
        var identity = Convert.ToHexString(Hash(IdentityDomain, scheduleHash, ordinal));
        return new($"evt-{identity[..24]}", ordinal, profileIndex, anchor, anchor, anchor + duration, rotation);
    }

    private static byte[] Hash(ReadOnlySpan<byte> domain, ReadOnlySpan<byte> scheduleHash, int ordinal)
    {
        Span<byte> ordinalBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(ordinalBytes, ordinal);
        var input = new byte[domain.Length + scheduleHash.Length + ordinalBytes.Length];
        domain.CopyTo(input);
        scheduleHash.CopyTo(input.AsSpan(domain.Length));
        ordinalBytes.CopyTo(input.AsSpan(domain.Length + scheduleHash.Length));
        return SHA256.HashData(input);
    }
}
