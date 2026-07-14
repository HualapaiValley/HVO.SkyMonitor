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
    private readonly Dictionary<string, VisibleScene> _scenes = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = [];
    private readonly object _gate = new();

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _scenes.Count;
            }
        }
    }

    /// <inheritdoc />
    public void Put(string sceneId, VisibleScene scene)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);
        ArgumentNullException.ThrowIfNull(scene);
        lock (_gate)
        {
            if (_scenes.ContainsKey(sceneId))
            {
                _order.Remove(sceneId);
            }

            _scenes[sceneId] = scene;
            _order.AddLast(sceneId);
            while (_scenes.Count > Capacity)
            {
                var expired = _order.First!.Value;
                _order.RemoveFirst();
                _scenes.Remove(expired);
            }
        }
    }

    /// <inheritdoc />
    public bool TryGet(string sceneId, out VisibleScene? scene)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneId);
        lock (_gate)
        {
            return _scenes.TryGetValue(sceneId, out scene);
        }
    }
}
