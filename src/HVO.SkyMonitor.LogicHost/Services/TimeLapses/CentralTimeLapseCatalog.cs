using System.Security.Claims;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services.TimeLapses;

internal sealed record CentralTimeLapseJobView(Guid JobId, Guid DevicePublicId, DateOnly ReportDate,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, bool IsDaily, CentralTimeLapseState State,
    string? ReasonCode, Guid? ProductId, DateTimeOffset UpdatedUtc);

/// <summary>Every read re-evaluates current observatory membership and credential scope, including Blazor reads.</summary>
internal sealed class CentralTimeLapseCatalog(IServiceScopeFactory scopes)
{
    internal async Task<IReadOnlyList<CentralTimeLapseJobView>> ListAsync(ClaimsPrincipal principal,
        Guid? deviceId, DateOnly? reportDate, DateTimeOffset? before, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var allowed = Allowed(db, principal);
        var query = db.CentralTimeLapseJobs.AsNoTracking().Where(job => allowed.Contains(job.ObservatoryId));
        if (deviceId.HasValue) query = query.Where(job => job.DevicePublicId == deviceId);
        if (reportDate.HasValue) query = query.Where(job => job.ReportDate == reportDate);
        if (before.HasValue) query = query.Where(job => job.CreatedUtc < before);
        return await query.OrderByDescending(job => job.CreatedUtc).ThenByDescending(job => job.Id).Take(100)
            .Select(job => new CentralTimeLapseJobView(job.Id, job.DevicePublicId, job.ReportDate,
                job.StartUtc, job.EndUtc, job.IsDaily, job.State, job.ReasonCode, job.ProductId, job.UpdatedUtc))
            .ToArrayAsync(token).ConfigureAwait(false);
    }

    internal async Task<CentralTimeLapseProduct?> GetAsync(ClaimsPrincipal principal, Guid productId, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var allowed = Allowed(db, principal);
        var row = await db.CentralTimeLapseVideos.AsNoTracking().SingleOrDefaultAsync(video => video.Id == productId &&
            allowed.Contains(video.ObservatoryId), token).ConfigureAwait(false);
        return row is null ? null : CentralTimeLapseStore.ReadProduct(row);
    }

    internal static IQueryable<Guid> Allowed(ApplicationDbContext db, ClaimsPrincipal principal)
    {
        var owner = CentralArtifactCredentialAccess.GetOwnerId(principal);
        if (owner is null || !CentralArtifactCredentialAccess.HasOwnerCredential(principal))
            return db.Observatories.Where(_ => false).Select(item => item.Id);
        var query = ObservatoryMembershipAccess.ForUser(db, owner).Select(item => item.ObservatoryId);
        return CentralArtifactCredentialAccess.GetObservatoryScope(principal) is { } scope
            ? query.Where(id => id == scope) : query;
    }
}
