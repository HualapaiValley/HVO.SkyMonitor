using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Transients;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class TransientEvidenceProvenanceTests
{
    private const string Head = "97d5cf6256e9a9fc84cca157e995e8341571e58e";

    [TestMethod]
    public void EmbeddedRepositoryRevision_MatchingFullHeadIsAccepted()
    {
        var embedded = TransientEvidenceProvenance.ResolveEmbeddedRepositoryRevision(
            $"1.0.0+{Head}", repositoryCommit: null);

        TransientEvidenceProvenance.RequireEmbeddedRevisionsMatchHead(
            Head, [new AssemblyRepositoryRevision("test", embedded.EmbeddedRepositoryRevision)]);

        Assert.AreEqual(Head, embedded.EmbeddedRepositoryRevision);
    }

    [TestMethod]
    public void EmbeddedRepositoryRevision_MismatchedFullHeadIsRejected()
    {
        const string otherRevision = "a7d5cf6256e9a9fc84cca157e995e8341571e58e";

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            TransientEvidenceProvenance.RequireEmbeddedRevisionsMatchHead(
                Head, [new AssemblyRepositoryRevision("test", otherRevision)]));

        StringAssert.Contains(exception.Message, "does not match recorded HEAD", StringComparison.Ordinal);
    }
}

internal static class TransientEvidenceProvenance
{
    internal static EmbeddedRevisionMetadata ReadEmbeddedRepositoryRevision(Assembly assembly)
    {
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var repositoryCommit = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Where(static attribute => string.Equals(attribute.Key, "RepositoryCommit", StringComparison.Ordinal))
            .Select(static attribute => attribute.Value)
            .SingleOrDefault();
        return ResolveEmbeddedRepositoryRevision(informationalVersion, repositoryCommit);
    }

    internal static EmbeddedRevisionMetadata ResolveEmbeddedRepositoryRevision(
        string? informationalVersion,
        string? repositoryCommit)
    {
        var informationalRevision = ReadInformationalRevision(informationalVersion);
        if (repositoryCommit is not null && !IsFullRevision(repositoryCommit))
        {
            throw new InvalidOperationException("AssemblyMetadata RepositoryCommit is not a full Git revision.");
        }
        if (repositoryCommit is not null && informationalRevision is not null &&
            !string.Equals(repositoryCommit, informationalRevision, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "AssemblyInformationalVersion and AssemblyMetadata RepositoryCommit disagree.");
        }
        var embeddedRevision = repositoryCommit ?? informationalRevision ??
            throw new InvalidOperationException("The assembly does not embed a full repository revision.");
        return new EmbeddedRevisionMetadata(informationalVersion, repositoryCommit, embeddedRevision);
    }

    internal static bool EmbeddedRevisionsMatchHead(
        string head,
        IReadOnlyList<AssemblyRepositoryRevision> revisions)
    {
        if (!IsFullRevision(head))
        {
            throw new InvalidOperationException("Recorded HEAD is not a full Git revision.");
        }
        return revisions.Count > 0 && revisions.All(revision =>
            IsFullRevision(revision.EmbeddedRepositoryRevision) &&
            string.Equals(revision.EmbeddedRepositoryRevision, head, StringComparison.OrdinalIgnoreCase));
    }

    internal static void RequireEmbeddedRevisionsMatchHead(
        string head,
        IReadOnlyList<AssemblyRepositoryRevision> revisions)
    {
        if (!EmbeddedRevisionsMatchHead(head, revisions))
        {
            var actual = string.Join(", ", revisions.Select(static value =>
                $"{value.Assembly}={value.EmbeddedRepositoryRevision}"));
            throw new InvalidOperationException(
                $"An embedded assembly repository revision does not match recorded HEAD {head}: {actual}.");
        }
    }

    private static string? ReadInformationalRevision(string? informationalVersion)
    {
        var separator = informationalVersion?.LastIndexOf('+') ?? -1;
        if (separator < 0 || separator == informationalVersion!.Length - 1)
        {
            return null;
        }
        var candidate = informationalVersion[(separator + 1)..];
        return IsFullRevision(candidate) ? candidate : null;
    }

    private static bool IsFullRevision(string value)
        => value.Length == 40 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}

internal sealed record EmbeddedRevisionMetadata(
    string? InformationalVersion,
    string? RepositoryCommit,
    string EmbeddedRepositoryRevision);

internal sealed record AssemblyRepositoryRevision(string Assembly, string EmbeddedRepositoryRevision);
