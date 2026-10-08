using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Data;

/// <summary>
/// One revision of the seeded canonical central graph, pinned by the identities recorded when it shipped. The revision
/// row, its global-default central assignment and both timestamps are fixed so every database converges on the same
/// rows regardless of when it was initialized.
/// </summary>
internal sealed record CanonicalCentralGraphSeedRevision(
    string Revision,
    Guid RevisionId,
    Guid AssignmentId,
    DateTimeOffset SeededAtUtc,
    string DefinitionJsonSha256,
    string DefinitionIdentitySha256,
    string PortablePlanIdentitySha256,
    string CentralPlanIdentitySha256);

/// <summary>
/// The ordered, append-only supersession chain of the seeded canonical central graph.
/// </summary>
/// <remarks>
/// <para>
/// The last entry is current: the seeder generates it from code and requires the generated definition to equal both
/// the recorded constants and any existing row exactly. Every earlier entry is historical: a retained row is validated
/// against its recorded constants and is never regenerated, because the code that produced it no longer exists.
/// </para>
/// <para>
/// Every change to a seeded recipe definition, normalized option default, or graph shape changes the generated
/// definition, so it must append the next entry here with new fixed revision and assignment IDs and a
/// <see cref="CanonicalCentralGraphSeedRevision.SeededAtUtc"/> one tick after its predecessor. Central resolution
/// (<c>ProcessingGraphCatalogService.ResolveAsync</c>) orders global-default assignments by the latest
/// <c>EffectiveFromUtc</c>, and every caller resolves at the wall-clock scheduling instant, so the newest entry wins
/// for every frame not already expanded while every earlier row stays untouched (assignments are immutable by
/// trigger). Operator assignments cannot be backdated and therefore still take precedence over every seed entry.
/// </para>
/// </remarks>
internal static class CanonicalCentralGraphSeedChain
{
    internal const string GraphName = "logic-host-basic";
    internal const string SeedActorUserId = "database-seed";
    internal const string AssignmentReasonCode = "canonical-basic-central";

    internal static ImmutableArray<CanonicalCentralGraphSeedRevision> Revisions { get; } =
    [
        new(
            "1",
            Guid.Parse("8d8f8df2-fd82-4dbf-8679-4ce21f6d637e"),
            Guid.Parse("4ea2c2cb-6c92-4386-9ce2-2798c544b61f"),
            DateTimeOffset.UnixEpoch,
            DefinitionJsonSha256: "C1365A0FF6614B1AED2FED96D2CB5B858764676FEAAE3CFD19403F63B24B7785",
            DefinitionIdentitySha256: "C1365A0FF6614B1AED2FED96D2CB5B858764676FEAAE3CFD19403F63B24B7785",
            PortablePlanIdentitySha256: "26D02C61ECD97F4DFC54CCC651267D670AD76781ED07E58CB3FDCF8EC9FDA64E",
            CentralPlanIdentitySha256: "26D02C61ECD97F4DFC54CCC651267D670AD76781ED07E58CB3FDCF8EC9FDA64E"),
        // Revision 2 (#526): Annotation Definition projected-annotation-v4 adds the measured stellar association
        // requirement and the stellar label policy, so its implementation version and the graph identity change.
        new(
            "2",
            Guid.Parse("80f3fea0-859d-4ad5-9585-f422977c67c2"),
            Guid.Parse("5d26fce5-a0af-4cd9-b566-0bf91839c87f"),
            DateTimeOffset.UnixEpoch.AddTicks(1),
            DefinitionJsonSha256: "325B6E5A604642C8F08E30B0008F4687BA679A0DF20FADF544F5B51838988523",
            DefinitionIdentitySha256: "325B6E5A604642C8F08E30B0008F4687BA679A0DF20FADF544F5B51838988523",
            PortablePlanIdentitySha256: "16692249A028631B94E13A0B025C8CC02D1B3CB4B805CD47079C3F4D9B1CDEC9",
            CentralPlanIdentitySha256: "16692249A028631B94E13A0B025C8CC02D1B3CB4B805CD47079C3F4D9B1CDEC9")
    ];

    internal static CanonicalCentralGraphSeedRevision Current => Revisions[^1];

    internal static bool IsSeededRevision(Guid revisionId)
        => Revisions.Any(entry => entry.RevisionId == revisionId);

    internal static bool IsSeededAssignment(Guid assignmentId)
        => Revisions.Any(entry => entry.AssignmentId == assignmentId);

    internal static string ComputeDefinitionJsonSha256(string definitionJson)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definitionJson)));

    /// <summary>Rejects a chain that is empty, reuses an identifier, or is not strictly ordered in time.</summary>
    internal static void Validate(IReadOnlyList<CanonicalCentralGraphSeedRevision> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Count == 0 ||
            chain.Select(static entry => entry.Revision).Distinct(StringComparer.Ordinal).Count() != chain.Count ||
            chain.Select(static entry => entry.RevisionId)
                .Concat(chain.Select(static entry => entry.AssignmentId))
                .Distinct().Count() != chain.Count * 2 ||
            chain.Select(static entry => entry.DefinitionIdentitySha256)
                .Distinct(StringComparer.Ordinal).Count() != chain.Count)
        {
            throw new InvalidOperationException("The canonical central graph seed chain is malformed.");
        }
        for (var index = 1; index < chain.Count; index++)
        {
            if (chain[index].SeededAtUtc <= chain[index - 1].SeededAtUtc)
            {
                throw new InvalidOperationException("The canonical central graph seed chain is not ordered in time.");
            }
        }
    }

    /// <summary>The identities the seeder records for a definition, computed exactly as they are stored.</summary>
    internal static (string DefinitionJson, string DefinitionJsonSha256, ProcessingGraphCompilationResult Portable,
        ProcessingGraphCompilationResult Central) Compile(ProcessingGraphDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var definitionJson = Encoding.UTF8.GetString(ProcessingGraphJson.SerializeCanonical(definition));
        return (
            definitionJson,
            ComputeDefinitionJsonSha256(definitionJson),
            ProcessingGraphCompiler.Compile(definition),
            ProcessingGraphCompiler.Compile(definition, new(ProcessingGraphHosts.LogicHost, [])));
    }
}
