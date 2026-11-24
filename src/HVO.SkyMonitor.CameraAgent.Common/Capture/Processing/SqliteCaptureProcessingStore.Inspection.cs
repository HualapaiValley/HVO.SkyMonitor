using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    /// <summary>Allows tests to exercise the read-only opener's own path guards.</summary>
    internal ValueTask<SqliteConnection> OpenReadOnlyInspectionConnectionForTestsAsync(CancellationToken cancellationToken)
        => OpenReadOnlyWithoutCheckpointAsync(cancellationToken);
}
