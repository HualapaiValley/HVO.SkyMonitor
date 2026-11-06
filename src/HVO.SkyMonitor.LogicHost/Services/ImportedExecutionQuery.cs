using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.LogicHost.Services;

// URL state only. These identities never grant authority and are revalidated by the read composition.
[SuppressMessage("Maintainability", "CA1515:Consider making public types internal", Justification = "The host-only URL state is the type of a public Blazor component parameter, which requires a public type.")]
public sealed record ImportedExecutionQuery
{
    public Guid? Installation { get; init; }
    public Guid? Binding { get; init; }
    public long? Unit { get; init; }
    public string? Node { get; init; }
    public long? Artifact { get; init; }
    public Guid? Central { get; init; }
    public string? Kind { get; init; }
    public string? Origin { get; init; }
    public string? InstallationsPage { get; init; }
    public string? OriginsPage { get; init; }
    public string? UnitsPage { get; init; }
    public string? ArtifactsPage { get; init; }
    public string? NodesPage { get; init; }
    public string? AttemptsPage { get; init; }
    public string? InputsPage { get; init; }
    public string? OutputsPage { get; init; }
    public string? CentralNodesPage { get; init; }
    public string? Focus { get; init; }
}
