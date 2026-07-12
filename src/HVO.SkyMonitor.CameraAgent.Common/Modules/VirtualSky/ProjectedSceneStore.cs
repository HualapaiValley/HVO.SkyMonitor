using System.Collections.Concurrent;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;

/// <summary>Shares the immutable capture scene with derivative steps in the same process.</summary>
public interface IProjectedSceneStore
{
    /// <summary>Stores a scene by its deterministic frame scene identifier.</summary>
    void Put(string sceneId, VisibleScene scene);

    /// <summary>Gets a previously captured scene, or returns false after bounded eviction.</summary>
    bool TryGet(string sceneId, out VisibleScene? scene);
}

/// <summary>A bounded, thread-safe scene cache for the in-process capture pipeline.</summary>
public sealed class ProjectedSceneStore : IProjectedSceneStore
{
    private const int Capacity = 32;
    private readonly ConcurrentDictionary<string, VisibleScene> _scenes = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();

    /// <inheritdoc />
    public void Put(string sceneId, VisibleScene scene)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);
        ArgumentNullException.ThrowIfNull(scene);
        _scenes[sceneId] = scene;
        _order.Enqueue(sceneId);
        while (_scenes.Count > Capacity && _order.TryDequeue(out var expired))
        {
            _scenes.TryRemove(expired, out _);
        }
    }

    /// <inheritdoc />
    public bool TryGet(string sceneId, out VisibleScene? scene)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);
        return _scenes.TryGetValue(sceneId, out scene);
    }
}
