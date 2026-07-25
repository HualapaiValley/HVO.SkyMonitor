using System.Data;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services;

internal sealed record CentralClearReferenceSummary(
    Guid DevicePublicId,
    string RigId,
    Guid ArtifactId,
    Guid FrameId,
    FrameArtifactRole Role,
    string Variant,
    string ChecksumSha256,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string UpdatedBy);

internal interface ICentralClearReferenceService
{
    Task<CentralClearReferenceSummary?> GetAsync(
        Guid devicePublicId,
        string rigId,
        CancellationToken cancellationToken);

    Task<CentralClearReferenceSummary> SetAsync(
        Guid devicePublicId,
        string rigId,
        Guid artifactId,
        string actor,
        CancellationToken cancellationToken);

    Task<bool> DeleteAsync(
        Guid devicePublicId,
        string rigId,
        CancellationToken cancellationToken);
}

internal sealed class CentralClearReferenceService(
    ApplicationDbContext dbContext,
    TimeProvider timeProvider) : ICentralClearReferenceService
{
    public async Task<CentralClearReferenceSummary?> GetAsync(
        Guid devicePublicId,
        string rigId,
        CancellationToken cancellationToken)
    {
        ValidateKey(devicePublicId, rigId);
        var designation = await QueryDesignations()
            .AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.Registration!.DevicePublicId == devicePublicId && item.RigId == rigId,
                cancellationToken).ConfigureAwait(false);
        return designation is null ? null : CreateSummary(designation, devicePublicId);
    }

    public async Task<CentralClearReferenceSummary> SetAsync(
        Guid devicePublicId,
        string rigId,
        Guid artifactId,
        string actor,
        CancellationToken cancellationToken)
    {
        ValidateKey(devicePublicId, rigId);
        ArgumentOutOfRangeException.ThrowIfEqual(artifactId, Guid.Empty);
        actor = actor?.Trim() ?? string.Empty;
        if (actor.Length is < 1 or > 256)
        {
            throw new ArgumentException("Designation actor is required and must not exceed 256 characters.", nameof(actor));
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken).ConfigureAwait(false);
        var registrationId = await dbContext.DeviceRegistrations
            .Where(registration => registration.DevicePublicId == devicePublicId)
            .Select(registration => (Guid?)registration.Id)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CentralClearReferenceException("clear-reference.registration-not-found");
        var centralArtifactId = await dbContext.CentralArtifacts
            .Where(artifact => artifact.ArtifactId == artifactId && artifact.DevicePublicId == devicePublicId)
            .Select(artifact => (Guid?)artifact.Id)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new CentralClearReferenceException("clear-reference.artifact-not-found");
        if (await CentralArtifactRetentionLock.AcquireAsync(
                dbContext, centralArtifactId, cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new CentralClearReferenceException("clear-reference.artifact-not-found");
        }
        var artifact = await QueryArtifacts().SingleAsync(
            candidate => candidate.Id == centralArtifactId,
            cancellationToken).ConfigureAwait(false);
        ValidateArtifact(artifact, registrationId, rigId);

        var now = timeProvider.GetUtcNow();
        var designation = await dbContext.CentralClearReferenceDesignations.SingleOrDefaultAsync(item =>
            item.RegistrationId == registrationId && item.RigId == rigId,
            cancellationToken).ConfigureAwait(false);
        if (designation is null)
        {
            designation = new CentralClearReferenceDesignation
            {
                RegistrationId = registrationId,
                RigId = rigId,
                CentralArtifactId = artifact.Id,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                UpdatedBy = actor
            };
            dbContext.CentralClearReferenceDesignations.Add(designation);
        }
        else if (designation.CentralArtifactId != artifact.Id ||
                 !string.Equals(designation.UpdatedBy, actor, StringComparison.Ordinal))
        {
            designation.CentralArtifactId = artifact.Id;
            designation.UpdatedAtUtc = now;
            designation.UpdatedBy = actor;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        designation.Artifact = artifact;
        return CreateSummary(designation, devicePublicId);
    }

    public async Task<bool> DeleteAsync(
        Guid devicePublicId,
        string rigId,
        CancellationToken cancellationToken)
    {
        ValidateKey(devicePublicId, rigId);
        var designation = await dbContext.CentralClearReferenceDesignations
            .Where(item => item.Registration!.DevicePublicId == devicePublicId && item.RigId == rigId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (designation is null)
        {
            return false;
        }
        dbContext.CentralClearReferenceDesignations.Remove(designation);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static IQueryable<CentralClearReferenceDesignation> QueryDesignations(
        ApplicationDbContext dbContext) => dbContext.CentralClearReferenceDesignations
            .Include(item => item.Registration)
            .Include(item => item.Artifact)!.ThenInclude(artifact => artifact!.Frame);

    private IQueryable<CentralClearReferenceDesignation> QueryDesignations() => QueryDesignations(dbContext);

    private IQueryable<CentralArtifact> QueryArtifacts() => dbContext.CentralArtifacts
        .Include(artifact => artifact.Layout)
        .Include(artifact => artifact.Recipe)
        .Include(artifact => artifact.Sources)
        .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Timing)
        .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Control)
        .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Profiles)
        .Include(artifact => artifact.Frame)!.ThenInclude(frame => frame!.Location)
        .AsSplitQuery();

    internal static void ValidateArtifact(CentralArtifact artifact, Guid registrationId, string rigId)
    {
        if (artifact.ObjectState != CentralArtifactObjectState.Available ||
            artifact.ReconstructionState != CentralReconstructionState.Complete ||
            artifact.ManifestSchemaVersion != ArtifactUploadManifest.CurrentSchemaVersion ||
            artifact.Role != FrameArtifactRole.Raw || artifact.Frame is not { } frame ||
            frame.RegistrationId != registrationId ||
            !string.Equals(frame.RigId, rigId, StringComparison.Ordinal) ||
            artifact.Layout?.PixelFormat is not { } pixelFormat ||
            pixelFormat != CameraPixelFormat.Mono16.ToString() &&
            pixelFormat != CameraPixelFormat.BayerRggb16.ToString())
        {
            throw new CentralClearReferenceException("clear-reference.artifact-ineligible");
        }
        try
        {
            var descriptor = CentralReconstructionDescriptorFactory.Create(frame, artifact);
            if (!descriptor.Validate().IsValid)
            {
                throw new CentralClearReferenceException("clear-reference.artifact-ineligible");
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or
                                          ArgumentException or KeyNotFoundException or JsonException)
        {
            throw new CentralClearReferenceException("clear-reference.artifact-ineligible", exception);
        }
    }

    private static CentralClearReferenceSummary CreateSummary(
        CentralClearReferenceDesignation designation,
        Guid devicePublicId)
    {
        var artifact = designation.Artifact ?? throw new InvalidOperationException("Designation artifact was not loaded.");
        var frame = artifact.Frame ?? throw new InvalidOperationException("Designation frame was not loaded.");
        return new CentralClearReferenceSummary(
            devicePublicId,
            designation.RigId,
            artifact.ArtifactId,
            frame.FrameId,
            artifact.Role,
            artifact.Variant ?? string.Empty,
            artifact.ChecksumSha256,
            frame.CapturedAtUtc,
            designation.CreatedAtUtc,
            designation.UpdatedAtUtc,
            designation.UpdatedBy);
    }

    private static void ValidateKey(Guid devicePublicId, string rigId)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(devicePublicId, Guid.Empty);
        if (string.IsNullOrWhiteSpace(rigId) || rigId.Length > 128)
        {
            throw new ArgumentException("Rig identity is required and must not exceed 128 characters.", nameof(rigId));
        }
    }
}

internal sealed class CentralClearReferenceException : Exception
{
    public CentralClearReferenceException()
        : this("clear-reference.rejected")
    {
    }

    public CentralClearReferenceException(string reasonCode) : base(reasonCode)
    {
        ReasonCode = reasonCode;
    }

    public CentralClearReferenceException(string reasonCode, Exception innerException) : base(reasonCode, innerException)
    {
        ReasonCode = reasonCode;
    }

    public CentralClearReferenceException(Exception innerException)
        : this("clear-reference.rejected", innerException)
    {
    }

    public string ReasonCode { get; }
}
