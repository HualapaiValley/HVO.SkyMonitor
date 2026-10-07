using System.Text;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.Tests.LogicHost.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CentralProcessingGraphPlanVerificationTests
{
    private static readonly CentralProcessingGraphNodeRegistry Registry = new(new CentralDerivativeRecipeCatalog());

    [TestMethod]
    public void CurrentCanonicalSeedVerifies()
    {
        var current = CanonicalCentralGraphSeedChain.Current;
        var json = Serialize(DatabaseSeeder.CreateBasicCentralProcessingGraph());

        var result = CentralProcessingGraphPlanVerification.Verify(
            json, current.DefinitionIdentitySha256, current.CentralPlanIdentitySha256, Registry);

        Assert.IsNull(result.FailureReasonCode);
        Assert.IsNull(result.UnsupportedNode);
        Assert.IsNotNull(result.Plan);
        Assert.AreEqual(current.CentralPlanIdentitySha256, result.Plan.PlanIdentitySha256);
    }

    [TestMethod]
    public void RevisionEmbeddingAnEarlierRecipeImplementationVersionNamesTheNode()
    {
        var json = WithStaleAnnotationImplementationVersion(Serialize(DatabaseSeeder.CreateBasicCentralProcessingGraph()));
        var parsed = ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(json));
        Assert.IsTrue(parsed.IsValid);
        var compiled = LogicHostProcessingGraphAdapter.Compile(parsed.Definition!, Registry.Capabilities);
        Assert.IsTrue(compiled.IsValid, string.Join(Environment.NewLine, compiled.Diagnostics));

        // Identities are the revision's own, so only the registry's node check can reject it.
        var result = CentralProcessingGraphPlanVerification.Verify(
            json, compiled.Plan!.DefinitionIdentitySha256, compiled.Plan.PlanIdentitySha256, Registry);

        Assert.IsNull(result.Plan);
        Assert.AreEqual(CentralProcessingGraphPlanVerification.UnsupportedReason, result.FailureReasonCode);
        Assert.AreEqual(BuiltInProcessingRecipes.Annotation, result.UnsupportedNode);
        Assert.AreEqual(result.UnsupportedNode, Registry.FindUnsupported(compiled.Plan));
        Assert.IsFalse(Registry.Validate(compiled.Plan));
    }

    [TestMethod]
    public void FailuresKeepTheSchedulerDistinctionBetweenDefinitionAndIdentity()
    {
        var current = CanonicalCentralGraphSeedChain.Current;
        var json = Serialize(DatabaseSeeder.CreateBasicCentralProcessingGraph());

        var unparsable = CentralProcessingGraphPlanVerification.Verify(
            "{", current.DefinitionIdentitySha256, current.CentralPlanIdentitySha256, Registry);
        var definitionMismatch = CentralProcessingGraphPlanVerification.Verify(
            json, new string('0', 64), current.CentralPlanIdentitySha256, Registry);
        var planMismatch = CentralProcessingGraphPlanVerification.Verify(
            json, current.DefinitionIdentitySha256, new string('0', 64), Registry);

        Assert.AreEqual(CentralProcessingGraphPlanVerification.DefinitionInvalidReason, unparsable.FailureReasonCode);
        Assert.AreEqual(CentralProcessingGraphPlanVerification.IdentityMismatchReason, definitionMismatch.FailureReasonCode);
        Assert.AreEqual(CentralProcessingGraphPlanVerification.IdentityMismatchReason, planMismatch.FailureReasonCode);
        Assert.IsTrue(new[] { unparsable, definitionMismatch, planMismatch }.All(
            static item => item.Plan is null && item.UnsupportedNode is null));
    }

    [TestMethod]
    public void OnlyTheVerifierSchedulingFailureIsAnUnexpandableRevision()
    {
        var json = WithStaleAnnotationImplementationVersion(Serialize(DatabaseSeeder.CreateBasicCentralProcessingGraph()));
        var compiled = LogicHostProcessingGraphAdapter.Compile(
            ProcessingGraphJson.Parse(Encoding.UTF8.GetBytes(json)).Definition!, Registry.Capabilities);
        var failure = CentralProcessingGraphPlanVerification.Verify(
            json, compiled.Plan!.DefinitionIdentitySha256, compiled.Plan.PlanIdentitySha256, Registry);
        var revisionId = Guid.NewGuid();
        var assignmentId = Guid.NewGuid();

        var tagged = CentralProcessingGraphPlanVerification.CreateUnexpandableRevisionException(
            failure, revisionId, assignmentId);

        Assert.IsTrue(CentralProcessingGraphPlanVerification.IsUnexpandableRevision(tagged, out var revision));
        Assert.AreEqual(
            new CentralProcessingGraphUnexpandableRevision(
                assignmentId, revisionId, CentralProcessingGraphPlanVerification.UnsupportedReason,
                BuiltInProcessingRecipes.Annotation),
            revision);
        Assert.AreEqual("The published processing graph plan identity is invalid.", tagged.Message);
        var definitionInvalid = CentralProcessingGraphPlanVerification.CreateUnexpandableRevisionException(
            CentralProcessingGraphPlanVerification.Verify("{", new string('0', 64), null, Registry), revisionId, null);
        Assert.AreEqual("The published processing graph definition is invalid.", definitionInvalid.Message);
        Assert.IsTrue(CentralProcessingGraphPlanVerification.IsUnexpandableRevision(definitionInvalid, out var named));
        Assert.IsNull(named.AssignmentId);

        // Every other CentralDerivativeJobStateException, a stale lease for one, must still propagate.
        Assert.IsFalse(CentralProcessingGraphPlanVerification.IsUnexpandableRevision(
            new CentralDerivativeJobStateException("The derivative job lease is stale or invalid."), out var leaseStale));
        Assert.IsNull(leaseStale);
        Assert.IsFalse(CentralProcessingGraphPlanVerification.IsUnexpandableRevision(
            new InvalidOperationException(tagged.Message), out _));
        Assert.ThrowsExactly<ArgumentException>(() => CentralProcessingGraphPlanVerification
            .CreateUnexpandableRevisionException(
                CentralProcessingGraphPlanVerification.Verify(
                    Serialize(DatabaseSeeder.CreateBasicCentralProcessingGraph()),
                    CanonicalCentralGraphSeedChain.Current.DefinitionIdentitySha256,
                    CanonicalCentralGraphSeedChain.Current.CentralPlanIdentitySha256,
                    Registry),
                revisionId,
                assignmentId));
    }

    internal static string WithStaleAnnotationImplementationVersion(string definitionJson)
    {
        Assert.IsTrue(BuiltInProcessingRecipes.TryGetDefinition(BuiltInProcessingRecipes.Annotation, out var annotation));
        var current = $"\"{annotation!.ImplementationVersion}\"";
        Assert.Contains(current, definitionJson, StringComparison.Ordinal);
        return definitionJson.Replace(current, "\"projected-annotation-v0\"", StringComparison.Ordinal);
    }

    private static string Serialize(ProcessingGraphDefinition definition)
        => Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(definition));
}
