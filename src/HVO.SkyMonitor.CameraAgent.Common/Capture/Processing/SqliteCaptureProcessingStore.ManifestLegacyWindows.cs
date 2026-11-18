using System.Text.Json;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    private static Dictionary<string, ProcessingGraphWindowRequirement> ReadLegacyManifestWindows(byte[] frozenPlanJson)
    {
        try
        {
            // Manifest bounds and optional-ledger validation have already run. Use the
            // bounded immutable graph, not today's host cap or an unbounded SQL column.
            using var document = JsonDocument.Parse(frozenPlanJson);
            var windows = new Dictionary<string, ProcessingGraphWindowRequirement>(StringComparer.Ordinal);
            foreach (var node in document.RootElement.GetProperty("nodes").EnumerateArray())
            {
                if (!node.TryGetProperty("windowJson", out var window) || window.ValueKind == JsonValueKind.Null) continue;
                if (window.ValueKind != JsonValueKind.String ||
                    !node.TryGetProperty("nodeId", out var id) || id.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("A frozen manifest window is corrupt.");
                var requirement = JsonSerializer.Deserialize<ProcessingGraphWindowRequirement>(window.GetString()!, ExecutionSerializerOptions)
                    ?? throw new InvalidDataException("A frozen manifest window is missing.");
                if (!windows.TryAdd(id.GetString()!, requirement))
                    throw new InvalidDataException("A frozen manifest window is duplicated.");
            }
            return windows;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException("A frozen manifest window is corrupt.", exception);
        }
    }

    private static bool HasCompleteLegacyManifestWindow(ProcessingGraphWindowRequirement? window,
        IReadOnlyList<ProcessingGraphExecutionInputState> inputs, ProcessingGraphExecutionClass executionClass, int attemptCount)
    {
        // Live retries can resolve different windows; a full surviving latest window
        // does not prove earlier released pins survived. Replay pins freeze once at
        // admission, so their immutable bound applies across attempts as well.
        if (window is null || (executionClass == ProcessingGraphExecutionClass.Live && attemptCount > 1) ||
            window.MaximumInputCount is < 1 or > 4096 || window.MinimumInputCount < 1 ||
            window.MinimumInputCount > window.MaximumInputCount)
            return false;
        var history = inputs.Where(static input => input.Kind == ProcessingGraphExecutionInputKind.ProcessingOutput).ToArray();
        if (history.Length != window.MaximumInputCount - 1 ||
            history.Select(static input => input.ArtifactId).Distinct().Count() != history.Length)
            return false;
        var positions = history.Select(static input => input.WindowPosition).Order().ToArray();
        var preceding = positions.Count(static position => position < 0);
        if (window.Kind is not (ProcessingGraphWindowKind.Trailing or ProcessingGraphWindowKind.Centered) ||
            (window.Kind == ProcessingGraphWindowKind.Trailing && preceding != positions.Length))
            return false;
        var expected = Enumerable.Range(-preceding, preceding).Concat(Enumerable.Range(1, positions.Length - preceding));
        return positions.SequenceEqual(expected) && (window.RequiredPositions.IsDefaultOrEmpty ||
            window.RequiredPositions.Where(static position => position != 0).All(positions.Contains));
    }
}
