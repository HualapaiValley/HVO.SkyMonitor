using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

/// <summary>Shared rolling-stack traversal. Hosts supply verified pixels; only the current stack is retained.</summary>
public sealed class TimeLapseFrameSequence
{
    private readonly IReadOnlyList<ProcessingArtifact> _descriptions;
    private readonly Dictionary<Guid, int> _indexes;
    private readonly Dictionary<int, ProcessingArtifact> _cache = [];
    private readonly Func<Guid, CancellationToken, ValueTask<ProcessingArtifact>> _restore;
    private readonly Func<DateTimeOffset, bool> _daytime;
    private readonly CameraRigConfig _rig;
    private readonly TimeLapseFrameOptions _options;
    private readonly int _width;
    private readonly int _height;

    public TimeLapseFrameSequence(IReadOnlyList<ProcessingArtifact> descriptions, CameraRigConfig rig,
        TimeLapseFrameOptions options, int width, int height,
        Func<Guid, CancellationToken, ValueTask<ProcessingArtifact>> restore, Func<DateTimeOffset, bool> daytime)
    {
        ArgumentNullException.ThrowIfNull(descriptions);
        ArgumentNullException.ThrowIfNull(restore);
        ArgumentNullException.ThrowIfNull(daytime);
        if (descriptions.Count is < 1 or > 8192 || descriptions.Any(static source => source.ObservationStartedUtc is null || !source.Payload.IsEmpty) ||
            descriptions.Zip(descriptions.Skip(1)).Any(pair => pair.First.ObservationStartedUtc > pair.Second.ObservationStartedUtc))
            throw new ArgumentException("A bounded ordered payload-free source plan is required.", nameof(descriptions));
        _descriptions = descriptions;
        _indexes = descriptions.Select((source, index) => (source.ArtifactId, index)).ToDictionary(static pair => pair.ArtifactId, static pair => pair.index);
        _restore = restore;
        _daytime = daytime;
        _rig = rig;
        _options = options;
        _width = width;
        _height = height;
    }

    public async ValueTask<TimeLapseRenderedFrame> RenderAsync(Guid artifactId, CancellationToken token)
    {
        var index = _indexes[artifactId];
        var latest = _descriptions[index];
        var daytime = _daytime(latest.ObservationStartedUtc!.Value);
        var start = index;
        while (start > 0 && index - start + 1 < _options.StackCount)
        {
            var candidate = _descriptions[start - 1];
            if (candidate.Compatibility != latest.Compatibility || candidate.Layout != latest.Layout ||
                candidate.ObservationStartedUtc >= _descriptions[start].ObservationStartedUtc ||
                _descriptions[start].ObservationStartedUtc - candidate.ObservationStartedUtc > TimeSpan.FromMinutes(1) ||
                _daytime(candidate.ObservationStartedUtc!.Value) != daytime) break;
            start--;
        }
        foreach (var stale in _cache.Keys.Where(key => key < start || key > index).ToArray()) _cache.Remove(stale);
        var sources = new List<ProcessingArtifact>(3);
        for (var current = start; current <= index; current++)
        {
            if (!_cache.TryGetValue(current, out var restored))
            {
                var expected = _descriptions[current];
                restored = await _restore(expected.ArtifactId, token).ConfigureAwait(false);
                if (!(restored.SourceArtifactIds ?? []).SequenceEqual(expected.SourceArtifactIds ?? []) ||
                    restored with { Payload = expected.Payload, SourceArtifactIds = expected.SourceArtifactIds } != expected)
                    throw new InvalidDataException("Restored time-lapse metadata changed from its frozen description.");
                _cache.Add(current, restored);
            }
            sources.Add(restored);
        }
        return TimeLapseFrameRenderer.Render(sources, _rig, _options, daytime, _width, _height, cancellationToken: token);
    }
}
