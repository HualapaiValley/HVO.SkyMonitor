using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;

namespace HVO.SkyMonitor.IntegrationTests.Infrastructure;

internal static class QualificationDiagnostics
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Preserve the subject failure while retaining and reporting every cleanup failure.")]
    internal static async Task RunWithCleanupAsync(
        Func<Task> subject, Func<ValueTask> cleanup, Action<string> report)
    {
        ExceptionDispatchInfo? primary = null;
        try { await subject().ConfigureAwait(false); }
        catch (Exception exception) { primary = ExceptionDispatchInfo.Capture(exception); }

        try { await cleanup().ConfigureAwait(false); }
        catch (Exception cleanupFailure)
        {
            // Retain structured evidence even if the diagnostic output sink fails.
            if (primary is not null) primary.SourceException.Data["QualificationCleanupFailure"] = cleanupFailure;
            try { report($"Qualification cleanup failed: {cleanupFailure}"); }
            catch (Exception reportingFailure)
            {
                cleanupFailure.Data["QualificationReportingFailure"] = reportingFailure;
            }
            if (primary is null) throw;
        }
        primary?.Throw();
    }
}
