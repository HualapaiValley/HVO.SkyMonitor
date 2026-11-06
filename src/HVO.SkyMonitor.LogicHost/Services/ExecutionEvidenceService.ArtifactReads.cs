using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed partial class ExecutionEvidenceService
{
    internal const int ArtifactObservationLimit = 50;
    private readonly IDataProtector _artifactCursorProtector = protection.CreateProtector("execution-evidence-unit-artifacts-v1");

    public async Task<ExecutionEvidenceUnitArtifactsView?> ListUnitArtifactsAsync(
        Guid observatoryId, Guid logicalCameraId, Guid installationId, Guid bindingId, long unitId,
        string userId, Guid? observatoryScope, string? cursor = null, int take = 50, CancellationToken cancellationToken = default)
    {
        var association = await AuthorizedUnitAssociationAsync(observatoryId, logicalCameraId, installationId,
            bindingId, unitId, userId, observatoryScope, cancellationToken).ConfigureAwait(false);
        if (association is null) { return null; }
        ValidatePage(0, take);
        var selection = new ArtifactReadCursor(observatoryId, logicalCameraId, installationId, bindingId, unitId, userId, observatoryScope, -1);
        var afterOrdinal = -1;
        if (cursor is not null)
        {
            if (cursor.Length > 4096) { return null; }
            try
            {
                var decoded = JsonSerializer.Deserialize<ArtifactReadCursor>(_artifactCursorProtector.Unprotect(cursor));
                if (decoded is null || decoded.AfterOrdinal < 0 || decoded with { AfterOrdinal = -1 } != selection) { return null; }
                afterOrdinal = decoded.AfterOrdinal;
            }
            catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException) { return null; }
        }
        var rows = await _db.Set<ImportedExecutionEvidenceArtifact>().AsNoTracking()
            .Where(a => a.BindingId == bindingId && a.UnitId == unitId && a.Ordinal > afterOrdinal)
            .OrderBy(a => a.Ordinal).Take(take + 1)
            .Select(a => new ExecutionEvidenceArtifactView(a.Id, a.Ordinal, a.ArtifactId, a.CaptureId,
                a.OutputIdentitySha256, a.PayloadSha256, a.DescriptorSha256, a.PayloadLength, a.MediaType, a.Role, a.Variant))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var more = rows.Count > take;
        var items = rows.Take(take).ToArray();
        var next = more ? _artifactCursorProtector.Protect(JsonSerializer.Serialize(selection with { AfterOrdinal = items[^1].Ordinal })) : null;
        return new(ExecutionEvidenceUnitArtifactsView.CurrentSchemaVersion, association, items, next);
    }

    public async Task<ExecutionEvidenceArtifactReconciliationView?> GetArtifactReconciliationAsync(
        Guid observatoryId, Guid logicalCameraId, Guid installationId, Guid bindingId, long unitId,
        long artifactReferenceId, Guid captureId, Guid artifactId, string userId, Guid? observatoryScope,
        CancellationToken cancellationToken = default)
    {
        var association = await AuthorizedUnitAssociationAsync(observatoryId, logicalCameraId, installationId,
            bindingId, unitId, userId, observatoryScope, cancellationToken).ConfigureAwait(false);
        if (association is null) { return null; }
        var artifact = await _db.Set<ImportedExecutionEvidenceArtifact>().AsNoTracking()
            .Where(a => a.BindingId == bindingId && a.UnitId == unitId && a.Id == artifactReferenceId &&
                a.CaptureId == captureId && a.ArtifactId == artifactId)
            .Select(a => new ExecutionEvidenceArtifactView(a.Id, a.Ordinal, a.ArtifactId, a.CaptureId,
                a.OutputIdentitySha256, a.PayloadSha256, a.DescriptorSha256, a.PayloadLength, a.MediaType, a.Role, a.Variant))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (artifact is null) { return null; }
        // A fresh read always includes the latest retained observation without draining binding history.
        var observations = await _db.Set<ImportedExecutionEvidenceObservation>().AsNoTracking()
            .Where(o => o.BindingId == bindingId && o.ArtifactReferenceId == artifactReferenceId)
            .OrderByDescending(o => o.Id).Take(ArtifactObservationLimit + 1)
            .Select(o => new ExecutionEvidenceObservationView(o.Id, o.ArtifactReferenceId, o.State, o.ReasonCode, o.ObservedAtUtc, o.ObjectGeneration))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new(ExecutionEvidenceArtifactReconciliationView.CurrentSchemaVersion, association, artifact,
            observations.Take(ArtifactObservationLimit).ToArray(), observations.Count > ArtifactObservationLimit);
    }

    private async Task<ExecutionEvidenceUnitAssociationView?> AuthorizedUnitAssociationAsync(
        Guid observatoryId, Guid logicalCameraId, Guid installationId, Guid bindingId, long unitId,
        string userId, Guid? observatoryScope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (observatoryScope is { } scope && scope != observatoryId) { return null; }
        // Resolve retained authority, never the camera's current installation or mutable device status.
        return await (from binding in _db.Set<ExecutionEvidenceBinding>().AsNoTracking()
                      join installation in _db.LogicalCameraInstallations on binding.LogicalCameraInstallationId equals installation.Id
                      join unit in _db.Set<ImportedExecutionEvidenceUnit>() on binding.Id equals unit.BindingId
                      join origin in _db.Set<ImportedExecutionEvidenceOrigin>() on unit.OriginId equals origin.Id
                      where binding.Id == bindingId && binding.ObservatoryId == observatoryId &&
                          installation.Id == installationId && installation.LogicalCameraId == logicalCameraId &&
                          installation.LogicalCamera!.ObservatoryId == observatoryId && installation.RegistrationId == binding.RegistrationId &&
                          installation.InstallationPublicId == binding.InstallationPublicId && unit.Id == unitId && unit.Accepted &&
                          origin.BindingId == bindingId && ObservatoryMembershipAccess.ForUser(_db, userId).Any(m => m.ObservatoryId == observatoryId)
                      select new ExecutionEvidenceUnitAssociationView(binding.Id, binding.InstallationPublicId,
                          binding.OriginInstallationId, binding.AgentInstanceId, origin.IdentitySha256,
                          new ExecutionEvidenceUnitView(unit.Id, unit.OriginId, unit.OriginSequence, unit.EvidenceId, unit.PayloadSha256,
                              unit.Kind, unit.RevisionId, unit.ExecutionId, unit.CaptureId, unit.ProducedAtUtc, unit.ReceivedAtUtc,
                              unit.Accepted, unit.RejectionReasonCode)))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record ArtifactReadCursor(Guid ObservatoryId, Guid LogicalCameraId, Guid InstallationId,
        Guid BindingId, long UnitId, string UserId, Guid? ObservatoryScope, int AfterOrdinal);
}

internal sealed record ExecutionEvidenceUnitAssociationView(Guid BindingId, Guid InstallationPublicId,
    Guid OriginInstallationId, Guid AgentInstanceId, string OriginIdentitySha256, ExecutionEvidenceUnitView Unit);

internal sealed record ExecutionEvidenceArtifactView(long ArtifactReferenceId, int Ordinal, Guid ArtifactId, Guid CaptureId,
    string? OutputIdentitySha256, string? PayloadSha256, string? DescriptorSha256, long? PayloadLength,
    string? MediaType, string? Role, string? Variant);

internal sealed record ExecutionEvidenceUnitArtifactsView(string SchemaVersion, ExecutionEvidenceUnitAssociationView Association,
    IReadOnlyList<ExecutionEvidenceArtifactView> Artifacts, string? NextCursor)
{
    public const string CurrentSchemaVersion = "hvo-imported-unit-artifacts-v1";
}

internal sealed record ExecutionEvidenceArtifactReconciliationView(string SchemaVersion, ExecutionEvidenceUnitAssociationView Association,
    ExecutionEvidenceArtifactView Artifact, IReadOnlyList<ExecutionEvidenceObservationView> Observations, bool ObservationsTruncated)
{
    public const string CurrentSchemaVersion = "hvo-imported-artifact-reconciliation-v1";
}
