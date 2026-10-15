using System.Diagnostics.CodeAnalysis;
using System.Text;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Services;

/// <summary>The verified central plan of a published revision, or why this binary cannot expand it.</summary>
/// <param name="Plan">The verified plan, or <see langword="null"/> when verification failed.</param>
/// <param name="FailureReasonCode">One of the <see cref="CentralProcessingGraphPlanVerification"/> reason codes.</param>
/// <param name="UnsupportedNode">
/// The step alias the node registry rejected (<see cref="ICentralProcessingGraphNodeRegistry.FindUnsupported"/>), when
/// the failure is <see cref="CentralProcessingGraphPlanVerification.UnsupportedReason"/>.
/// </param>
internal sealed record CentralProcessingGraphPlanVerificationResult(
    ProcessingGraphExecutionPlan? Plan,
    string? FailureReasonCode,
    string? UnsupportedNode);

/// <summary>A published central revision this binary refused to expand, as carried by the scheduling failure.</summary>
/// <param name="AssignmentId">The resolved assignment, or <see langword="null"/> when the revision was named directly.</param>
/// <param name="RevisionId">The revision that failed verification.</param>
/// <param name="ReasonCode">One of the <see cref="CentralProcessingGraphPlanVerification"/> reason codes.</param>
/// <param name="UnsupportedNode">The step alias the node registry rejected, when there is one.</param>
internal sealed record CentralProcessingGraphUnexpandableRevision(
    Guid? AssignmentId,
    Guid RevisionId,
    string ReasonCode,
    string? UnsupportedNode);

/// <summary>
/// The single decision of whether this binary can expand a published central revision. Live and replay scheduling
/// expand only through it, and startup diagnostics use it to find assignments that would fail, so the two cannot
/// disagree.
/// </summary>
internal static class CentralProcessingGraphPlanVerification
{
    internal const string DefinitionInvalidReason = "definition-invalid";
    internal const string CompilationInvalidReason = "compilation-invalid";
    internal const string UnsupportedReason = "node-unsupported";
    internal const string IdentityMismatchReason = "plan-identity-mismatch";
    private const string UnexpandableRevisionMarker = "HVO.SkyMonitor.CentralProcessingGraphUnexpandableRevision";

    internal static CentralProcessingGraphPlanVerificationResult Verify(
        string definitionJson,
        string definitionIdentitySha256,
        string? centralPlanIdentitySha256,
        ICentralProcessingGraphNodeRegistry nodeRegistry)
    {
        ArgumentNullException.ThrowIfNull(definitionJson);
        ArgumentNullException.ThrowIfNull(nodeRegistry);
        var parsed = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(definitionJson));
        if (!parsed.IsValid)
        {
            return new(null, DefinitionInvalidReason, null);
        }
        var compiled = LogicHostProcessingGraphAdapter.Compile(parsed.Definition!, nodeRegistry.Capabilities);
        if (!compiled.IsValid || compiled.Plan is not { } plan)
        {
            return new(null, CompilationInvalidReason, null);
        }
        if (nodeRegistry.FindUnsupported(plan) is { } unsupported)
        {
            return new(null, UnsupportedReason, unsupported);
        }
        if (!string.Equals(plan.DefinitionIdentitySha256, definitionIdentitySha256, StringComparison.Ordinal) ||
            !string.Equals(plan.PlanIdentitySha256, centralPlanIdentitySha256, StringComparison.Ordinal))
        {
            return new(null, IdentityMismatchReason, null);
        }
        return new(plan, null, null);
    }

    /// <summary>
    /// The scheduling failure for a revision <see cref="Verify"/> rejected, tagged so that a caller outside a request
    /// can contain exactly this failure (<see cref="IsUnexpandableRevision"/>) while every other
    /// <see cref="CentralDerivativeJobStateException"/>, a stale lease for one, still propagates.
    /// </summary>
    internal static CentralDerivativeJobStateException CreateUnexpandableRevisionException(
        CentralProcessingGraphPlanVerificationResult failure,
        Guid revisionId,
        Guid? assignmentId)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.Plan is not null || failure.FailureReasonCode is not { } reasonCode)
        {
            throw new ArgumentException("A verified plan is not a scheduling failure.", nameof(failure));
        }
        var exception = new CentralDerivativeJobStateException(reasonCode == DefinitionInvalidReason
            ? "The published processing graph definition is invalid."
            : "The published processing graph plan identity is invalid.");
        exception.Data[UnexpandableRevisionMarker] =
            new CentralProcessingGraphUnexpandableRevision(assignmentId, revisionId, reasonCode, failure.UnsupportedNode);
        return exception;
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is the scheduling failure for a published revision this binary cannot
    /// expand. Every site that contains that failure decides it here rather than by inspecting the exception.
    /// </summary>
    internal static bool IsUnexpandableRevision(
        Exception exception,
        [NotNullWhen(true)] out CentralProcessingGraphUnexpandableRevision? revision)
    {
        ArgumentNullException.ThrowIfNull(exception);
        revision = exception is CentralDerivativeJobStateException
            ? exception.Data[UnexpandableRevisionMarker] as CentralProcessingGraphUnexpandableRevision
            : null;
        return revision is not null;
    }
}
