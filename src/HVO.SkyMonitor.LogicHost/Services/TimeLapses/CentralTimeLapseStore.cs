using System.Data;
using HVO.SkyMonitor.LogicHost.Data;
using Microsoft.EntityFrameworkCore;

namespace HVO.SkyMonitor.LogicHost.Services.TimeLapses;

/// <summary>SQL remains publication and lease authority. The single encoding lane is fenced across host replicas.</summary>
internal sealed class CentralTimeLapseStore(ApplicationDbContext db, TimeProvider clock)
{
    internal const int MaximumQueuedJobs = 256;
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    internal async Task<Guid?> FindDiscoveredAsync(Guid deviceId, DateOnly reportDate, string identity, CancellationToken token)
        => await db.CentralTimeLapseJobs.FromSqlInterpolated($"""
            SELECT * FROM [CentralTimeLapseJobs]
            WHERE [DevicePublicId]={deviceId} AND [ReportDate]={reportDate}
              AND JSON_VALUE([RequestJson], '$.discoveryIdentity')={identity}
            """).AsNoTracking().OrderBy(job => job.CreatedUtc).Select(job => (Guid?)job.Id).FirstOrDefaultAsync(token).ConfigureAwait(false);

    internal Task<Guid> EnqueueAsync(Func<CancellationToken, Task<CentralTimeLapseRequest>> freeze, CancellationToken token)
        => LockedAsync(async () =>
        {
            var request = await freeze(token).ConfigureAwait(false);
            Validate(request);
            var id = request.JobId;
            var json = CentralTimeLapseJson.Serialize(request);
            var sha = CentralTimeLapseJson.Hash(json);
            var previous = await db.CentralTimeLapseJobs.SingleOrDefaultAsync(job => job.Id == id, token).ConfigureAwait(false);
            if (previous is not null)
            {
                if (previous.RequestSha256 != sha) throw new InvalidDataException("Conflicting immutable video request.");
                return id;
            }
            if (await db.CentralTimeLapseJobs.CountAsync(job => job.State == CentralTimeLapseState.Queued ||
                    job.State == CentralTimeLapseState.Working, token).ConfigureAwait(false) >= MaximumQueuedJobs)
                throw new InvalidOperationException("The central time-lapse queue is full.");
            var job = new CentralTimeLapseJob
            {
                Id = id, DevicePublicId = request.DevicePublicId, ObservatoryId = request.ObservatoryId,
                ReportDate = request.Period.ReportDate, StartUtc = request.StartUtc, EndUtc = request.EndUtc,
                IsDaily = request.IsDaily, RequestJson = json, RequestSha256 = sha,
                State = CentralTimeLapseState.Queued, CreatedUtc = clock.GetUtcNow(), UpdatedUtc = clock.GetUtcNow()
            };
            foreach (var source in request.Sources)
                job.Inputs.Add(new() { JobId = id, CentralArtifactId = source.CentralArtifactId });
            foreach (var child in request.HourlyJobIds)
                job.Dependencies.Add(new() { JobId = id, HourlyJobId = child });
            db.CentralTimeLapseJobs.Add(job);
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            return id;
        }, token);

    internal Task<CentralTimeLapseLease?> ClaimAsync(CancellationToken token)
        => LockedAsync<CentralTimeLapseLease?>(async () =>
        {
            var now = clock.GetUtcNow();
            if (await db.CentralTimeLapseJobs.AnyAsync(job => job.State == CentralTimeLapseState.Working && job.LeaseExpiresUtc > now, token)
                    .ConfigureAwait(false)) return null;
            await db.CentralTimeLapseJobs.Where(job => job.State == CentralTimeLapseState.Working && job.LeaseExpiresUtc <= now && job.AttemptCount >= 3)
                .ExecuteUpdateAsync(set => set.SetProperty(job => job.State, CentralTimeLapseState.Failed)
                    .SetProperty(job => job.ReasonCode, "timelapse.attempts-exhausted").SetProperty(job => job.UpdatedUtc, now)
                    .SetProperty(job => job.LeaseToken, (Guid?)null).SetProperty(job => job.LeaseExpiresUtc, (DateTimeOffset?)null), token).ConfigureAwait(false);
            var job = await db.CentralTimeLapseJobs.Where(job => (job.State == CentralTimeLapseState.Queued ||
                    job.State == CentralTimeLapseState.Working && job.LeaseExpiresUtc <= now) && job.AttemptCount < 3 &&
                    !job.Dependencies.Any(dependency => dependency.HourlyJob!.State == CentralTimeLapseState.Queued ||
                        dependency.HourlyJob.State == CentralTimeLapseState.Working))
                .OrderBy(job => job.IsDaily).ThenBy(job => job.CreatedUtc).ThenBy(job => job.Id)
                .FirstOrDefaultAsync(token).ConfigureAwait(false);
            if (job is null) return null;
            var request = ReadRequest(job);
            job.State = CentralTimeLapseState.Working;
            job.LeaseToken = Guid.NewGuid();
            job.LeaseExpiresUtc = now + LeaseDuration;
            job.AttemptCount++;
            job.UpdatedUtc = now;
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            return new(job.Id, job.LeaseToken.Value, request, job.AttemptCount);
        }, token);

    internal async Task<bool> RenewAsync(CentralTimeLapseLease lease, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        return await Current(lease, now).ExecuteUpdateAsync(set => set.SetProperty(job => job.LeaseExpiresUtc, now + LeaseDuration)
            .SetProperty(job => job.UpdatedUtc, now), token).ConfigureAwait(false) == 1;
    }

    internal Task CompleteAsync(CentralTimeLapseLease lease, CentralTimeLapseState state, string? reason,
        Guid? productId, CancellationToken token, IReadOnlyDictionary<string, int>? exclusions = null)
        => LockedAsync(async () =>
        {
            if (state is CentralTimeLapseState.Queued or CentralTimeLapseState.Working ||
                state == CentralTimeLapseState.Produced && productId is null)
                throw new ArgumentException("Invalid video completion.", nameof(state));
            var job = await Current(lease, clock.GetUtcNow()).SingleAsync(token).ConfigureAwait(false);
            if (productId.HasValue && !await db.CentralTimeLapseVideos.AnyAsync(video => video.Id == productId && video.JobId == job.Id, token).ConfigureAwait(false))
                throw new InvalidDataException("Video completion requires its own committed product.");
            job.State = state;
            job.ReasonCode = reason;
            job.ProductId = productId;
            job.ExclusionsJson = CentralTimeLapseJson.Serialize(exclusions ?? lease.Request.Exclusions);
            job.ExclusionsSha256 = CentralTimeLapseJson.Hash(job.ExclusionsJson);
            job.LeaseToken = null;
            job.LeaseExpiresUtc = null;
            job.UpdatedUtc = clock.GetUtcNow();
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            return true;
        }, token);

    internal Task<CentralTimeLapseProduct> CommitAsync(CentralTimeLapseLease authority, CentralTimeLapseProduct product, CancellationToken token)
        => LockedAsync(async () =>
        {
            CentralTimeLapseProductIdentity.Validate(product);
            if (product.PublicationJobId != authority.JobId || product.PublicationAttempt != authority.Attempt)
                throw new InvalidDataException("The output belongs to another publication attempt.");
            if (!await Current(authority, clock.GetUtcNow()).AnyAsync(token).ConfigureAwait(false))
                throw new InvalidOperationException("Time-lapse publication lease expired.");
            var owner = await db.CentralTimeLapseJobs.SingleAsync(job => job.Id == product.JobId, token).ConfigureAwait(false);
            if (owner.Id != authority.JobId && (owner.State != CentralTimeLapseState.NoSources || !product.IsGapFiller ||
                !authority.Request.HourlyJobIds.Contains(owner.Id))) throw new InvalidDataException("Invalid video publication authority.");
            var request = ReadRequest(owner);
            if (product.RequestIdentity != request.Identity || product.DevicePublicId != request.DevicePublicId ||
                product.ObservatoryId != request.ObservatoryId || product.Period != request.Period ||
                product.StartUtc != request.StartUtc || product.EndUtc != request.EndUtc || product.IsDaily != request.IsDaily ||
                product.Encoding.PayloadBytes != product.Chunks.Sum(static chunk => chunk.Bytes))
                throw new InvalidDataException("Video provenance does not match its retained request.");
            var previous = await db.CentralTimeLapseVideos.SingleOrDefaultAsync(video => video.JobId == product.JobId, token).ConfigureAwait(false);
            if (previous is not null) return ReadProduct(previous);
            var json = CentralTimeLapseJson.Serialize(product);
            db.CentralTimeLapseVideos.Add(new()
            {
                Id = product.ProductId, JobId = product.JobId, DevicePublicId = product.DevicePublicId,
                ObservatoryId = product.ObservatoryId, ReportDate = product.Period.ReportDate, IsDaily = product.IsDaily,
                IsGapFiller = product.IsGapFiller, ProductJson = json, ProductSha256 = CentralTimeLapseJson.Hash(json),
                PayloadBytes = product.Encoding.PayloadBytes, CreatedUtc = product.CreatedUtc
            });
            await db.SaveChangesAsync(token).ConfigureAwait(false);
            return product;
        }, token);

    internal async Task<CentralTimeLapseProduct?> FindForJobAsync(Guid jobId, CancellationToken token)
    {
        var row = await db.CentralTimeLapseVideos.AsNoTracking().SingleOrDefaultAsync(video => video.JobId == jobId, token).ConfigureAwait(false);
        return row is null ? null : ReadProduct(row);
    }

    internal async Task<IReadOnlyList<CentralTimeLapseJob>> ChildrenAsync(CentralTimeLapseLease lease, CancellationToken token)
        => await db.CentralTimeLapseJobs.AsNoTracking().Where(job => lease.Request.HourlyJobIds.Contains(job.Id))
            .OrderBy(job => job.StartUtc).ToArrayAsync(token).ConfigureAwait(false);

    internal async Task<IReadOnlySet<string>> RetainedChunkKeysAsync(CentralTimeLapseLease lease, CancellationToken token)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var jobId in lease.Request.HourlyJobIds.Prepend(lease.JobId))
            if (await FindForJobAsync(jobId, token).ConfigureAwait(false) is { } product)
                foreach (var chunk in product.Chunks) keys.Add(chunk.Key);
        return keys;
    }

    internal static CentralTimeLapseRequest ReadRequest(CentralTimeLapseJob job)
    {
        var request = CentralTimeLapseJson.Read<CentralTimeLapseRequest>(job.RequestJson, job.RequestSha256);
        Validate(request);
        if (request.JobId != job.Id || request.DevicePublicId != job.DevicePublicId || request.ObservatoryId != job.ObservatoryId ||
            request.StartUtc != job.StartUtc || request.EndUtc != job.EndUtc || request.IsDaily != job.IsDaily || request.Period.ReportDate != job.ReportDate)
            throw new InvalidDataException("Invalid retained time-lapse request identity.");
        return request;
    }

    internal static CentralTimeLapseProduct ReadProduct(CentralTimeLapseVideo video)
    {
        var product = CentralTimeLapseJson.Read<CentralTimeLapseProduct>(video.ProductJson, video.ProductSha256);
        CentralTimeLapseProductIdentity.Validate(product);
        if (product.ProductId != video.Id || product.JobId != video.JobId || product.DevicePublicId != video.DevicePublicId ||
            product.ObservatoryId != video.ObservatoryId || product.Encoding.PayloadBytes != video.PayloadBytes ||
            product.IsDaily != video.IsDaily || product.IsGapFiller != video.IsGapFiller || product.Period.ReportDate != video.ReportDate)
            throw new InvalidDataException("Invalid retained time-lapse product identity.");
        return product;
    }

    internal static IReadOnlyDictionary<string, int> ReadExclusions(CentralTimeLapseJob job)
        => job.ExclusionsJson is { } json && job.ExclusionsSha256 is { } sha
            ? CentralTimeLapseJson.Read<Dictionary<string, int>>(json, sha) : ReadRequest(job).Exclusions;

    private IQueryable<CentralTimeLapseJob> Current(CentralTimeLapseLease lease, DateTimeOffset now)
        => db.CentralTimeLapseJobs.Where(job => job.Id == lease.JobId && job.State == CentralTimeLapseState.Working &&
            job.LeaseToken == lease.Token && job.LeaseExpiresUtc > now);

    private async Task<T> LockedAsync<T>(Func<Task<T>> operation, CancellationToken token)
        => await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);
            // A short transaction, never held during rendering, object I/O, process execution or lease renewal.
            await db.Database.ExecuteSqlRawAsync("""
                DECLARE @result int;
                EXEC @result = sys.sp_getapplock @Resource=N'hvo.central-time-lapse.queue.v1',
                    @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=5000;
                IF @result < 0 THROW 50730, 'Central time-lapse queue is busy.', 1;
                """, token).ConfigureAwait(false);
            var result = await operation().ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);

    private static void Validate(CentralTimeLapseRequest request)
    {
        if (!request.Period.IsValid() || request.DevicePublicId == Guid.Empty || request.ObservatoryId == Guid.Empty ||
            request.StartUtc.Offset != TimeSpan.Zero || request.EndUtc.Offset != TimeSpan.Zero || request.EligibleUtc.Offset != TimeSpan.Zero ||
            request.StartUtc < request.Period.StartUtc || request.EndUtc > request.Period.EndUtc || request.EndUtc <= request.StartUtc ||
            request.EligibleUtc < request.EndUtc || request.EligibleUtc - request.EndUtc > TimeSpan.FromHours(6) ||
            request.Sources.Count > 8192 || request.Sources.Select(static source => source.CentralArtifactId).Distinct().Count() != request.Sources.Count ||
            request.HourlyJobIds.Count > 50 || request.HourlyJobIds.Distinct().Count() != request.HourlyJobIds.Count ||
            request.IsDaily && (request.HourlyJobIds.Count == 0 || request.Sources.Count != 0 || request.StartUtc != request.Period.StartUtc || request.EndUtc != request.Period.EndUtc) ||
            !request.IsDaily && (request.HourlyJobIds.Count != 0 || request.EndUtc - request.StartUtc > TimeSpan.FromHours(2)))
            throw new InvalidDataException("Invalid bounded time-lapse request.");
        _ = HVO.SkyMonitor.Processing.TimeLapseFrameRenderer.RecipeIdentity(request.Preset.Frames);
        _ = HVO.SkyMonitor.Processing.TimeLapseTimelinePlanner.Create(request.StartUtc, request.EndUtc, [], request.Preset.Timing);
        if (request.Preset.MaximumDimension is < 128 or > 4096 || request.Preset.Generation < 1 || !Enum.IsDefined(request.Preset.EncoderProfile) ||
            request.DiscoveryIdentity is { } discovery && (discovery.Length != 64 || !discovery.All(char.IsAsciiHexDigit)) ||
            request.Preset.CapabilityIdentity is not { Length: 64 } || !request.Preset.CapabilityIdentity.All(char.IsAsciiHexDigit) ||
            request.Exclusions.Count > 32 || request.Exclusions.Any(static pair => pair.Key.Length is < 1 or > 128 || pair.Value < 0) ||
            request.Sources.Select(static source => source.Descriptor.Artifact.ArtifactId).Distinct().Count() != request.Sources.Count ||
            request.Sources.Zip(request.Sources.Skip(1)).Any(pair => pair.First.Descriptor.Timing.ExposureStartedUtc > pair.Second.Descriptor.Timing.ExposureStartedUtc))
            throw new InvalidDataException("Invalid bounded video preset or source order.");
        foreach (var source in request.Sources)
        {
            if (source.CentralArtifactId == Guid.Empty || !source.Descriptor.Validate().IsValid ||
                source.Descriptor.Artifact.Role != HVO.SkyMonitor.AgentCore.FrameArtifactRole.Raw ||
                source.ByteLength is < 1 or > 64L * 1024 * 1024 || source.ByteLength != source.Descriptor.Layout.ByteLength ||
                source.Descriptor.Layout.Width != request.Preset.Rig.Sensor.WidthPixels || source.Descriptor.Layout.Height != request.Preset.Rig.Sensor.HeightPixels ||
                source.Descriptor.Timing.ExposureStartedUtc < request.StartUtc.AddMinutes(-1) || source.Descriptor.Timing.ExposureStartedUtc >= request.EndUtc ||
                string.IsNullOrWhiteSpace(source.StorageReference) || source.StorageReference.Length > 4096)
                throw new InvalidDataException("Invalid frozen central video source.");
        }
    }
}
