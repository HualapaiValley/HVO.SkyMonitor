using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public sealed class EnvironmentalAssociationService(
    ILocalEnvironmentalObservationStore observations,
    ILocalEnvironmentalAssociationStore associations,
    IOptions<CameraAgentHostOptions> options,
    TimeProvider timeProvider,
    EnvironmentalAcquisitionTelemetry? telemetry = null)
{
    private const string AlgorithmVersion = "local-environment-association-v1";
    private static readonly string[] SourcePriority = ["Measured", "Imported", "Manual", "Derived", "Simulated"];
    private static readonly string[] QualityPriority = ["Good", "Suspect", "Unknown"];

    public async ValueTask<IReadOnlyList<LocalEnvironmentalCaptureAssociation>> AssociateAsync(
        Guid captureId,
        long captureSequence,
        DateTimeOffset exposureFromUtc,
        DateTimeOffset exposureThroughUtc,
        string? rigId,
        IReadOnlyList<EnvironmentalObservationKind> kinds,
        CancellationToken cancellationToken)
    {
        using var activity = EnvironmentalAcquisitionTelemetry.ActivitySource.StartActivity("environment.associate");
        ValidateRequest(captureId, captureSequence, exposureFromUtc, exposureThroughUtc, rigId, kinds);
        exposureFromUtc = DateTimeOffset.FromUnixTimeMilliseconds(exposureFromUtc.ToUnixTimeMilliseconds());
        exposureThroughUtc = DateTimeOffset.FromUnixTimeMilliseconds(exposureThroughUtc.ToUnixTimeMilliseconds());
        if (exposureThroughUtc <= exposureFromUtc)
        {
            exposureThroughUtc = exposureFromUtc.AddMilliseconds(1);
        }
        var root = options.Value.RawIngressRoot;
        var expectedKinds = kinds.Distinct().Order().ToArray();
        var policyIdentity = CreatePolicyIdentity(expectedKinds);
        var persisted = (await associations.ReadAssociationsAsync(root, captureId, cancellationToken).ConfigureAwait(false))
            .Where(association => string.Equals(
                association.PolicyIdentitySha256, policyIdentity, StringComparison.Ordinal))
            .ToArray();
        ValidatePersisted(
            persisted, captureSequence, exposureFromUtc, exposureThroughUtc, rigId, expectedKinds);
        if (persisted.Length == expectedKinds.Length)
        {
            return persisted.OrderBy(static association => association.Kind).ToArray();
        }
        var existingKinds = persisted.Select(static association => association.Kind).ToHashSet();
        var additions = new List<LocalEnvironmentalCaptureAssociation>(expectedKinds.Length - persisted.Length);
        foreach (var kind in expectedKinds.Where(kind => !existingKinds.Contains(kind)))
        {
            var candidates = await observations.ReadLocalCandidatesAsync(
                root, kind, rigId, exposureFromUtc, exposureThroughUtc, 1_000, cancellationToken).ConfigureAwait(false);
            var association = CreateAssociation(
                captureId,
                captureSequence,
                kind,
                rigId,
                exposureFromUtc,
                exposureThroughUtc,
                policyIdentity,
                candidates);
            additions.Add(association);
        }
        await associations.SaveAssociationSetAsync(root, additions, cancellationToken).ConfigureAwait(false);
        var completed = persisted.Concat(additions).OrderBy(static association => association.Kind).ToArray();
        foreach (var association in additions)
        {
            telemetry?.RecordAssociation(association.Status);
        }
        return completed;
    }

    public async ValueTask<IReadOnlyList<LocalEnvironmentalCaptureAssociation>?> ReadCompletedAsync(
        Guid captureId,
        long captureSequence,
        DateTimeOffset exposureFromUtc,
        DateTimeOffset exposureThroughUtc,
        string? rigId,
        IReadOnlyList<EnvironmentalObservationKind> kinds,
        CancellationToken cancellationToken)
    {
        ValidateRequest(captureId, captureSequence, exposureFromUtc, exposureThroughUtc, rigId, kinds);
        exposureFromUtc = DateTimeOffset.FromUnixTimeMilliseconds(exposureFromUtc.ToUnixTimeMilliseconds());
        exposureThroughUtc = DateTimeOffset.FromUnixTimeMilliseconds(exposureThroughUtc.ToUnixTimeMilliseconds());
        if (exposureThroughUtc <= exposureFromUtc)
        {
            exposureThroughUtc = exposureFromUtc.AddMilliseconds(1);
        }
        var expectedKinds = kinds.Distinct().Order().ToArray();
        var allPersisted = await associations.ReadAssociationsAsync(
                options.Value.RawIngressRoot,
                captureId,
                cancellationToken).ConfigureAwait(false);
        var persisted = allPersisted
            .GroupBy(static association => association.PolicyIdentitySha256, StringComparer.Ordinal)
            .Select(group => group.ToArray())
            .Where(group => expectedKinds.All(kind => group.Any(association => association.Kind == kind)))
            .OrderBy(static group => group.Length)
            .ThenBy(static group => group[0].PolicyIdentitySha256, StringComparer.Ordinal)
            .FirstOrDefault() ?? [];
        persisted = persisted
            .Where(association => expectedKinds.Contains(association.Kind))
            .ToArray();
        ValidatePersisted(
            persisted, captureSequence, exposureFromUtc, exposureThroughUtc, rigId, expectedKinds);
        return persisted.Length == expectedKinds.Length
            ? persisted.OrderBy(static association => association.Kind).ToArray()
            : null;
    }

    private static void ValidateRequest(
        Guid captureId,
        long captureSequence,
        DateTimeOffset exposureFromUtc,
        DateTimeOffset exposureThroughUtc,
        string? rigId,
        IReadOnlyList<EnvironmentalObservationKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (captureId == Guid.Empty || captureSequence < 1 || exposureFromUtc.Offset != TimeSpan.Zero ||
            exposureThroughUtc.Offset != TimeSpan.Zero || exposureFromUtc >= exposureThroughUtc ||
            rigId is { Length: > 128 } || rigId is not null && rigId != rigId.Trim() ||
            kinds.Count == 0 || kinds.Any(static kind => !Enum.IsDefined(kind)))
        {
            throw new ArgumentException("The environmental capture association request is invalid.");
        }
    }

    private static string CreatePolicyIdentity(IReadOnlyList<EnvironmentalObservationKind> expectedKinds)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            Schema = AlgorithmVersion,
            SourcePriority,
            QualityPriority,
            Kinds = expectedKinds
        });

    private static void ValidatePersisted(
        LocalEnvironmentalCaptureAssociation[] persisted,
        long captureSequence,
        DateTimeOffset exposureFromUtc,
        DateTimeOffset exposureThroughUtc,
        string? rigId,
        IReadOnlyCollection<EnvironmentalObservationKind> expectedKinds)
    {
        if (persisted.Any(association => association.CaptureSequence != captureSequence ||
            association.ExposureFromUtc != exposureFromUtc || association.ExposureThroughUtc != exposureThroughUtc ||
            !string.Equals(association.RigId, rigId, StringComparison.Ordinal) ||
            !expectedKinds.Contains(association.Kind)) ||
            persisted.Select(static association => association.Kind).Distinct().Count() != persisted.Length)
        {
            throw new EnvironmentalObservationIdentityConflictException(
                "The durable environmental association set conflicts with the capture context or policy.");
        }
    }

    private LocalEnvironmentalCaptureAssociation CreateAssociation(
        Guid captureId,
        long captureSequence,
        EnvironmentalObservationKind kind,
        string? rigId,
        DateTimeOffset exposureFromUtc,
        DateTimeOffset exposureThroughUtc,
        string policyIdentity,
        IReadOnlyList<LocalEnvironmentalObservationRecord> candidates)
    {
        var covering = candidates
            .Where(candidate => candidate.Fact.ValidFromUtc <= exposureFromUtc &&
                candidate.Fact.ValidThroughUtc >= exposureThroughUtc)
            .ToArray();
        var fresh = covering
            .Where(candidate => candidate.Fact.StaleAfterUtc >= exposureThroughUtc)
            .ToArray();
        var pool = fresh.Length > 0 ? fresh : covering.Length > 0 ? covering : candidates.ToArray();
        LocalEnvironmentalAssociationStatus status;
        LocalEnvironmentalObservationRecord? selected;
        IReadOnlyList<long> conflicts;
        IReadOnlyList<AssociationEvidenceIdentity> conflictIdentities;
        if (pool.Length > 0)
        {
            var ordered = pool
                .GroupBy(static candidate => candidate.SourceIdentitySha256, StringComparer.Ordinal)
                .Select(static group => group
                    .OrderByDescending(static candidate => candidate.Fact.ObservedAtUtc)
                    .ThenBy(static candidate => candidate.Fact.ObservationId)
                    .First())
                .OrderBy(candidate => SourceRank(candidate.Fact.Source.Kind))
                .ThenBy(candidate => QualityRank(candidate.Fact.Value.Quality))
                .ThenBy(candidate => TargetRank(candidate.Fact.RigId, rigId))
                .ThenByDescending(candidate => candidate.Fact.ObservedAtUtc)
                .ThenBy(candidate => candidate.SourceIdentitySha256, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Fact.ObservationId)
                .ToArray();
            var winning = ordered
                .Where(candidate => SourceRank(candidate.Fact.Source.Kind) == SourceRank(ordered[0].Fact.Source.Kind) &&
                    QualityRank(candidate.Fact.Value.Quality) == QualityRank(ordered[0].Fact.Value.Quality) &&
                    TargetRank(candidate.Fact.RigId, rigId) == TargetRank(ordered[0].Fact.RigId, rigId))
                .ToArray();
            var contradictory = IsContradictory(winning);
            status = contradictory
                ? LocalEnvironmentalAssociationStatus.Contradictory
                : fresh.Length > 0
                    ? LocalEnvironmentalAssociationStatus.Fresh
                    : LocalEnvironmentalAssociationStatus.Stale;
            selected = contradictory ? null : winning[0];
            conflicts = contradictory ? winning.Select(static candidate => candidate.RecordId).Order().ToArray() : [];
            conflictIdentities = contradictory
                ? winning.Select(static candidate => new AssociationEvidenceIdentity(
                        candidate.SourceIdentitySha256,
                        candidate.Fact.ObservationId,
                        candidate.ContentSha256))
                    .OrderBy(static identity => identity.SourceIdentitySha256, StringComparer.Ordinal)
                    .ThenBy(static identity => identity.ObservationId)
                    .ToArray()
                : [];
        }
        else
        {
            status = LocalEnvironmentalAssociationStatus.Missing;
            selected = null;
            conflicts = [];
            conflictIdentities = [];
        }
        var identity = CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            Schema = AlgorithmVersion,
            captureId,
            captureSequence,
            kind,
            rigId,
            exposureFromUtc,
            exposureThroughUtc,
            policyIdentity,
            status,
            Selected = selected is null ? null : new
            {
                selected.SourceIdentitySha256,
                selected.Fact.ObservationId,
                selected.ContentSha256
            },
            Conflicts = conflictIdentities
        });
        return new LocalEnvironmentalCaptureAssociation(
            captureId,
            captureSequence,
            kind,
            rigId,
            exposureFromUtc,
            exposureThroughUtc,
            policyIdentity,
            status,
            selected?.RecordId,
            conflicts,
            identity,
            timeProvider.GetUtcNow());
    }

    private static bool IsContradictory(LocalEnvironmentalObservationRecord[] candidates)
    {
        if (candidates.Length < 2)
        {
            return false;
        }
        if (candidates[0].Fact.Value.Kind == EnvironmentalObservationKind.RainState)
        {
            return candidates.Select(static candidate => candidate.Fact.Value.BooleanValue).Distinct().Count() > 1;
        }
        var lower = candidates.Max(static candidate =>
            candidate.Fact.Value.NumericValue!.Value - (candidate.Fact.Value.Uncertainty ?? 0));
        var upper = candidates.Min(static candidate =>
            candidate.Fact.Value.NumericValue!.Value + (candidate.Fact.Value.Uncertainty ?? 0));
        return lower > upper;
    }

    private static int SourceRank(EnvironmentalObservationSourceKind kind)
        => kind switch
        {
            EnvironmentalObservationSourceKind.Measured => 0,
            EnvironmentalObservationSourceKind.Imported => 1,
            EnvironmentalObservationSourceKind.Manual => 2,
            EnvironmentalObservationSourceKind.Derived => 3,
            EnvironmentalObservationSourceKind.Simulated => 4,
            _ => int.MaxValue
        };

    private static int QualityRank(EnvironmentalObservationQuality quality)
        => quality switch
        {
            EnvironmentalObservationQuality.Good => 0,
            EnvironmentalObservationQuality.Suspect => 1,
            EnvironmentalObservationQuality.Unknown => 2,
            _ => int.MaxValue
        };

    private static int TargetRank(string? candidateRigId, string? captureRigId)
        => candidateRigId is not null && string.Equals(candidateRigId, captureRigId, StringComparison.Ordinal) ? 0 : 1;

    private sealed record AssociationEvidenceIdentity(
        string SourceIdentitySha256,
        Guid ObservationId,
        string ContentSha256);
}
