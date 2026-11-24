using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

/// <summary>Read-only progress for graph products that can still contribute to an issued view.</summary>
public interface ICenteredViewLayerProgressReader
{
    ValueTask<IReadOnlySet<CenteredViewLayerKey>> ReadPendingAsync(CenteredView view, CancellationToken cancellationToken);
}

internal sealed class CenteredViewLayerProgressReader(SqliteCaptureProcessingStore store, IProcessingGraphOperations executions)
    : ICenteredViewLayerProgressReader
{
    public async ValueTask<IReadOnlySet<CenteredViewLayerKey>> ReadPendingAsync(CenteredView view, CancellationToken cancellationToken)
    {
        var pending = new HashSet<CenteredViewLayerKey>();
        var definition = view.Base.Definition;
        var bindings = await store.ReadCenteredLayerBindingsAsync(definition.Stream.GenerationId, cancellationToken).ConfigureAwait(false);
        if (bindings is null || bindings.Count == 0)
            return pending;

        var settled = view.Revision.Layers.Where(static layer => layer.Readiness != CenteredViewReadiness.Waiting)
            .Select(static layer => layer.Key).ToHashSet();
        foreach (var contributor in definition.Contributors)
        {
            var source = contributor.Exposure;
            var captureId = source.Capture.CaptureId;
            var isReference = source.Input.Key == definition.Reference.Input.Key;
            var relevant = bindings.Where(binding =>
                (binding.Value.Scope == CenteredViewLayerScope.Contributor ||
                    (isReference && binding.Value.Scope == CenteredViewLayerScope.Reference)) &&
                !settled.Contains(binding.Value.Key(captureId))).ToArray();
            if (relevant.Length == 0)
                continue;

            var run = await executions.ReadRunManifestAsync(source.ExecutionId, cancellationToken).ConfigureAwait(false);
            if (!IsActiveSource(run, definition.Stream, source))
                continue;

            foreach (var (port, binding) in relevant)
            {
                if (run.Nodes.Any(node => node.NodeId == port.Producer && node.Status is "Pending" or "Running" &&
                    node.OutputContracts.Any(output => output.Role == FrameArtifactRole.Metadata &&
                        output.SchemaVersion == port.Schema && output.Variant == port.Variant)))
                    pending.Add(binding.Key(captureId));
            }
        }
        return pending;
    }

    internal static bool IsActiveSource([NotNullWhen(true)] ProcessingGraphExecutionDetail? run,
        CenteredViewStream stream, CenteredViewExposure source)
        => run?.Execution is
        {
            ExecutionClass: ProcessingGraphExecutionClass.Live,
            CancellationRequested: false,
            Status: ProcessingGraphExecutionStatus.Pending or ProcessingGraphExecutionStatus.Running
        } execution && source.Input.Key.Camera == stream.Camera && source.Input.Key.RunId == stream.RunId &&
        source.Input.Key.CaptureId == source.Capture.CaptureId && execution.CaptureId == source.Capture.CaptureId &&
        execution.ExecutionId == source.ExecutionId && execution.PrimaryArtifactId == source.Input.Raw.Artifact.ArtifactId &&
        execution.GraphRevisionId == stream.GenerationId;
}
