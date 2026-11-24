using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed partial class SqliteRawCaptureJournal
{
    [SuppressMessage("Performance", "CA1810:Initialize reference type static fields inline",
        Justification = "Register processing-owned index names after the shared allowlist has been initialized.")]
    static SqliteRawCaptureJournal()
        => SharedSchemaObjectNames.UnionWith(CenteredViewRetentionSchema.IndexNames);
}
