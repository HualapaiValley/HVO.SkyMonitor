using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.LogicHost.Services;

// Navigation state only; every identity and continuation is checked by the delivered services.
[SuppressMessage("Maintainability", "CA1515:Consider making public types internal", Justification = "Public Blazor component parameter type.")]
public sealed record CentralOperationsQuery
{
    public string View { get; init; } = "executions";
    public string Target { get; init; } = "Central";
    public string? Page { get; init; }
    public Guid? Execution { get; init; }
    public Guid? Job { get; init; }
    public Guid? Proposal { get; init; }
    public Guid? Revision { get; init; }
    public Guid? Event { get; init; }
    public Guid? Capture { get; init; }
    public string? NodesPage { get; init; }
    public string? AttemptsPage { get; init; }
    public string? InputsPage { get; init; }
    public string? OutputsPage { get; init; }
    public string? FactsPage { get; init; }
    public string? Request { get; init; }
    public string? Focus { get; init; }
}
