using System.Diagnostics;

namespace HVO.SkyMonitor.CameraAgent.Common.Deployment;

public sealed record CapturePipelineTrace(
    long CaptureSequence,
    Guid CaptureId,
    Guid ArtifactId,
    string TraceId,
    string SpanId);

public sealed class CapturePipelineTraceStore
{
    public const int Capacity = 64;

    private readonly object _sync = new();
    private readonly LinkedList<CapturePipelineTrace> _entries = [];

    public void Record(long captureSequence, Guid captureId, Guid artifactId, ActivityContext context)
    {
        if (captureSequence < 1 || captureId == Guid.Empty || artifactId == Guid.Empty ||
            context.TraceId == default || context.SpanId == default)
        {
            return;
        }

        var entry = new CapturePipelineTrace(
            captureSequence,
            captureId,
            artifactId,
            context.TraceId.ToHexString(),
            context.SpanId.ToHexString());
        lock (_sync)
        {
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                if (node.Value.CaptureSequence != captureSequence && node.Value.ArtifactId != artifactId) continue;
                _entries.Remove(node);
                break;
            }
            _entries.AddLast(entry);
            while (_entries.Count > Capacity) _entries.RemoveFirst();
        }
    }

    public CapturePipelineTrace? Find(long captureSequence, Guid artifactId)
    {
        if (captureSequence < 1 || artifactId == Guid.Empty) return null;
        lock (_sync)
        {
            for (var node = _entries.Last; node is not null; node = node.Previous)
            {
                if (node.Value.CaptureSequence == captureSequence && node.Value.ArtifactId == artifactId) return node.Value;
            }
        }
        return null;
    }

    public IReadOnlyList<CapturePipelineTrace> Snapshot()
    {
        lock (_sync) return _entries.ToArray();
    }
}
