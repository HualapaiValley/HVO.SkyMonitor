namespace HVO.SkyMonitor.IntegrationTests.Infrastructure;

/// <summary>Preserves the LogicHost fixture entry point while sharing direct observations with combined tests.</summary>
internal static class SqlWaitEvidence
{
    internal static Task WhileAsync(Func<Task> operation, string connectionString, Action<string> write)
        => SqlDiagnosticObservation.WhileAsync(operation, connectionString, write);
}
