using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.CameraAgent.Common.Automation;

/// <summary>Known terminal evidence from an adapter that has killed and fully drained canceled work.</summary>
[SuppressMessage("Design", "CA1032:Implement standard exception constructors",
    Justification = "This internal control-flow exception requires drained terminal evidence; empty constructors would discard it.")]
internal sealed class LocalAutomationDrainedCancellationException(
    LocalAutomationExecution execution, DateTimeOffset completedAtUtc, CancellationToken cancellationToken)
    : OperationCanceledException("The automation drained after cancellation.", cancellationToken)
{
    internal LocalAutomationExecution Execution { get; } = execution;
    internal DateTimeOffset CompletedAtUtc { get; } = completedAtUtc;
}
