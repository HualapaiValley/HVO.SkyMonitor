using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed class CenteredRecentExposureIndex
{
    internal const int CapacityPerCamera = 16;
    private const int MaximumCameras = 32;
    private readonly Lock _gate = new();
    private readonly Dictionary<CameraKey, LinkedList<ExposureKey>> _cameras = new();
    private readonly Dictionary<CameraKey, Guid> _activeRuns = new();

    internal void Activate(CameraKey camera, Guid runId)
    {
        lock (_gate)
        {
            if (_activeRuns.TryGetValue(camera, out var activeRun) && activeRun == runId) return;
            if (_activeRuns.Count >= MaximumCameras && !_activeRuns.ContainsKey(camera))
                _activeRuns.Remove(_activeRuns.First().Key);
            _activeRuns[camera] = runId;
        }
    }

    internal void Deactivate(CameraModuleConfig configuration)
    {
        lock (_gate)
        {
            if (configuration is { AgentId: { } agent, CenteredExposure.CameraId: { } camera })
                _activeRuns.Remove(new(agent, camera));
        }
    }

    internal (Guid? RunId, ExposureKey? ObservedHead, int? Occupancy) ReadActive(CameraKey camera)
    {
        lock (_gate)
        {
            if (!_activeRuns.TryGetValue(camera, out var run)) return (null, null, null);
            if (!_cameras.TryGetValue(camera, out var recent)) return (run, null, 0);
            var members = recent.Where(key => key.RunId == run).ToArray();
            return (run, members.FirstOrDefault(), members.Length);
        }
    }

    internal void Observe(ExposureKey key)
    {
        lock (_gate)
        {
            if (!_cameras.TryGetValue(key.Camera, out var recent))
            {
                if (_cameras.Count >= MaximumCameras)
                    _cameras.Remove(_cameras.First().Key);

                recent = new();
                _cameras.Add(key.Camera, recent);
            }

            if (recent.Contains(key)) return;
            recent.AddFirst(key);
            if (recent.Count > CapacityPerCamera) recent.RemoveLast();
        }
    }

    internal IReadOnlyList<ExposureKey> Read(CameraKey camera)
    {
        lock (_gate)
        {
            return _cameras.TryGetValue(camera, out var recent) ? recent.ToArray() : [];
        }
    }
}
