using System.Collections.Immutable;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Services.Processing;

internal sealed record LogicHostProcessingGraphNodeBinding(
    string NodeId,
    string StepAlias,
    string StepVersion,
    ProcessingOperationKind OperationKind,
    string NodeIdentitySha256,
    ImmutableArray<ProcessingGraphInputBinding> Inputs,
    ImmutableArray<ProcessingGraphProductContract> Outputs);

internal static class LogicHostProcessingGraphAdapter
{
    internal static ProcessingGraphCompilationResult Compile(
        ProcessingGraphDefinition definition,
        IEnumerable<string>? availableCapabilities = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return ProcessingGraphCompiler.Compile(
            definition,
            new(
                ProcessingGraphHosts.LogicHost,
                availableCapabilities?.Distinct(StringComparer.Ordinal).ToImmutableArray() ?? []));
    }

    internal static ImmutableArray<LogicHostProcessingGraphNodeBinding> Bind(ProcessingGraphExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Nodes.Select(static node => new LogicHostProcessingGraphNodeBinding(
            node.Definition.Id,
            node.Definition.StepAlias,
            node.Definition.StepVersion,
            node.Definition.OperationKind,
            node.IdentitySha256,
            node.InputBindings,
            node.Definition.Outputs)).ToImmutableArray();
    }

    internal static string FreezePlan(ProcessingGraphExecutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(new
        {
            schema = "hvo-logic-host-processing-graph-plan-v1",
            plan.DefinitionIdentitySha256,
            plan.PlanIdentitySha256,
            sources = plan.Sources,
            nodes = plan.Nodes.Select(static node => new
            {
                node.IdentitySha256,
                node.Definition,
                node.InputBindings
            }).ToArray()
        })).GetRawText();
    }

    internal static string FreezeNode(ProcessingGraphPlanNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(new
        {
            schema = "hvo-logic-host-processing-graph-node-v1",
            node.IdentitySha256,
            node.Definition,
            node.InputBindings
        })).GetRawText();
    }
}
