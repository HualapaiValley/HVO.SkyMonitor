using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.Storage.FileSystem;
using HVO.SkyMonitor.Video.FFmpeg;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeLapses;

/// <summary>CameraAgent authority for queued windows, frozen inputs and immutable local-only video publication.</summary>
internal sealed partial class SqliteTimeLapseStore(IOptions<CameraAgentHostOptions> options, TimeProvider clock)
    : ICameraAgentTimeLapseCatalog, ICameraAgentTimeLapseCommands, IProcessingRetentionHolds, IDisposable
{
    private const int MaximumPendingJobs = 64;
    private const int MaximumDocumentBytes = 32 * 1024 * 1024;
    private const string JobColumns = "job_id, occurrence_json, window_json, parent_job_id, preset_json, state, updated_ticks, reason, product_id, revision, exclusions_json, exclusions_sha256";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly SemaphoreSlim _writes = new(1, 1);
    private bool _initialized;

    public void Dispose() => _writes.Dispose();

    internal ValueTask RequireWorkspaceCapacityAsync(CancellationToken token) =>
        RequireCapacityAsync(options.Value.TimeLapses.Encoder.MaximumScratchBytes,
            options.Value.TimeLapses.Encoder.MaximumOutputBytes, token);

    internal async ValueTask RequireCapacityAsync(long scratchBytes, long publicationBytes, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        var published = Convert.ToInt64(await ScalarAsync(connection, null,
            "SELECT COALESCE(SUM(payload_bytes),0) FROM products;", token).ConfigureAwait(false), CultureInfo.InvariantCulture);
        var policy = options.Value.TimeLapses;
        var capacity = new FileSystemStorageCapacityProvider().GetCapacity(options.Value.RawIngressRoot);
        var reserve = Math.Max(policy.MinimumFreeBytes, checked((long)(capacity.TotalBytes * options.Value.DiskPressureThresholdPercent / 100)));
        if (publicationBytes < 0 || scratchBytes < 0 || published > policy.MaximumPublishedBytes - publicationBytes)
            throw new TimeLapseEncodingException("timelapse.publication-byte-budget", "The local video publication budget is exhausted.");
        if (capacity.AvailableBytes < scratchBytes + publicationBytes + reserve)
            throw new TimeLapseEncodingException("timelapse.storage-reserve", "Video work would consume storage reserved for acquisition.");
    }

    internal async ValueTask InitializeAsync(CancellationToken token)
    {
        if (Volatile.Read(ref _initialized)) return;
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            using var connection = await OpenAsync(token).ConfigureAwait(false);
            using var transaction = Begin(connection);
            var version = Convert.ToInt32(await ScalarAsync(connection, transaction, "PRAGMA user_version;", token).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (version == 0)
            {
                if (Convert.ToInt32(await ScalarAsync(connection, transaction,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';", token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
                    throw new InvalidDataException("Unversioned time-lapse storage cannot be adopted.");
                await ExecuteAsync(connection, transaction, Schema, token).ConfigureAwait(false);
            }
            else if (version != 1) throw new InvalidDataException("Unsupported time-lapse storage schema.");
            if (Convert.ToInt32(await ScalarAsync(connection, transaction,
                    "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';", token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 12 ||
                (string?)await ScalarAsync(connection, transaction, "PRAGMA integrity_check;", token).ConfigureAwait(false) != "ok")
                throw new InvalidDataException("Time-lapse storage integrity or schema check failed.");
            await transaction.CommitAsync(token).ConfigureAwait(false);
            Volatile.Write(ref _initialized, true);
        }
        finally { _writes.Release(); }
    }

    /// <summary>A worker must hold this process-external lease before recovering or claiming work.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned stream transfers the exclusive lease to the worker; every non-return path disposes it in finally.")]
    internal async ValueTask<FileStream> AcquireWorkerAsync(CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        var root = Root();
        FileStream? lease = null;
        try
        {
            lease = new FileStream(root.Resolve("worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            using var connection = await OpenAsync(token).ConfigureAwait(false);
            await RecoverPublicationsAsync(connection, token).ConfigureAwait(false);
            await ExecuteAsync(connection, null, "UPDATE jobs SET state = 'Queued', reason = 'timelapse.recovering', revision=revision+1 WHERE state = 'Working';", token)
                .ConfigureAwait(false);
            var acquired = lease;
            lease = null;
            return acquired;
        }
        finally { if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false); }
    }

    internal async ValueTask EnqueueAsync(LocalAutomationOccurrence occurrence, CameraAgentTimeLapsePreset preset,
        IReadOnlyList<LocalAutomationSourceWindow> hourlyWindows, CancellationToken token)
    {
        if (!occurrence.IsValid() || occurrence.SourceWindow is not { } window || !window.IsEligibleForFinal(clock.GetUtcNow()))
            throw new ArgumentException("Video generation requires a retained settled source-window occurrence.", nameof(occurrence));
        if (window.Policy.Kind == LocalAutomationSourceWindowKind.CompletedCivilHour && hourlyWindows.Count != 0 ||
            hourlyWindows.Count > 50 || hourlyWindows.Any(hour => !hour.IsValid() || hour.Policy.Kind != LocalAutomationSourceWindowKind.CompletedCivilHour ||
                hour.ReportingPeriod != window.ReportingPeriod || hour.StartUtc < window.StartUtc || hour.EndUtc > window.EndUtc) ||
            window.Policy.Kind == LocalAutomationSourceWindowKind.SunriseDay &&
                (hourlyWindows.Count == 0 || hourlyWindows[0].StartUtc != window.StartUtc || hourlyWindows[^1].EndUtc != window.EndUtc ||
                 hourlyWindows.Zip(hourlyWindows.Skip(1)).Any(pair => pair.First.EndUtc != pair.Second.StartUtc)))
            throw new ArgumentException("Daily video requires the complete retained hourly partition.", nameof(hourlyWindows));
        if (!TimeLapseOptions.Matches(preset) || occurrence.Definition.TaskKind != LocalAutomationTaskKind.TimeLapseGeneration ||
            occurrence.Definition.TaskTarget != "time-lapse:" + preset.IdentitySha256)
            throw new ArgumentException("The frozen video preset does not match its occurrence.", nameof(preset));
        await InitializeAsync(token).ConfigureAwait(false);
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(token).ConfigureAwait(false);
            using var transaction = Begin(connection);
            var parentId = JobId(occurrence, window);
            var jobs = hourlyWindows.Select(hour => (Window: hour, Parent: (Guid?)parentId))
                .Append((Window: window, Parent: (Guid?)null)).DistinctBy(static item => item.Window.IdentitySha256).ToArray();
            var missing = new List<(LocalAutomationSourceWindow Window, Guid? Parent)>();
            foreach (var item in jobs)
            {
                var existingPreset = await ScalarAsync(connection, transaction, "SELECT preset_json FROM jobs WHERE job_id = $id;", token,
                    ("$id", JobId(occurrence, item.Window).ToString("N"))).ConfigureAwait(false);
                if (existingPreset is null) missing.Add(item);
                else if (existingPreset is not string retained || retained != Serialize(preset))
                    throw new InvalidDataException("The occurrence already has a different frozen video preset.");
            }
            var pending = Convert.ToInt64(await ScalarAsync(connection, transaction,
                "SELECT COUNT(*) FROM jobs WHERE state IN ('Queued','Working');", token).ConfigureAwait(false), CultureInfo.InvariantCulture);
            if (pending + missing.Count > MaximumPendingJobs) throw new InvalidOperationException("The bounded video queue is full.");
            foreach (var item in missing)
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO jobs(job_id, occurrence_json, window_json, parent_job_id, preset_json, report_date, state, updated_ticks)
                    VALUES($id,$occurrence,$window,$parent,$preset,$date,'Queued',$now);
                    """, token,
                    ("$id", JobId(occurrence, item.Window).ToString("N")), ("$occurrence", Serialize(occurrence)),
                    ("$window", Serialize(item.Window)), ("$parent", item.Parent?.ToString("N")), ("$preset", Serialize(preset)),
                    ("$date", Date(window.ReportingPeriod.ReportDate)), ("$now", clock.GetUtcNow().UtcTicks)).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }
        finally { _writes.Release(); }
    }

    internal async ValueTask<CameraAgentTimeLapseJob?> ClaimAsync(CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(token).ConfigureAwait(false);
            await RecoverPublicationsAsync(connection, token).ConfigureAwait(false);
            using var transaction = Begin(connection);
            using var command = Command(connection, transaction, "SELECT " + JobColumns + """
                 FROM jobs AS job WHERE state = 'Queued'
                   AND NOT EXISTS(SELECT 1 FROM jobs AS child WHERE child.parent_job_id = job.job_id AND child.state IN ('Queued','Working'))
                 ORDER BY CASE WHEN parent_job_id IS NULL THEN 1 ELSE 0 END, updated_ticks, job_id LIMIT 1;
                """);
            CameraAgentTimeLapseJob? job;
            using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
                job = await reader.ReadAsync(token).ConfigureAwait(false) ? ReadJob(reader) : null;
            if (job is null) return null;
            await ExecuteAsync(connection, transaction, "UPDATE jobs SET state='Working', reason=NULL, updated_ticks=$now, revision=revision+1 WHERE job_id=$id;",
                token, ("$now", clock.GetUtcNow().UtcTicks), ("$id", job.JobId.ToString("N"))).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return job with { State = CameraAgentTimeLapseState.Working, ReasonCode = null, Revision = job.Revision + 1 };
        }
        finally { _writes.Release(); }
    }

    internal async ValueTask CompleteAsync(Guid jobId, CameraAgentTimeLapseState state, string? reason, Guid? productId, CancellationToken token)
    {
        if (!Enum.IsDefined(state) || state is CameraAgentTimeLapseState.Queued or CameraAgentTimeLapseState.Working ||
            state == CameraAgentTimeLapseState.Produced != productId.HasValue || reason?.Length > 128)
            throw new ArgumentException("Invalid terminal video state.", nameof(state));
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        if (productId is { } id)
        {
            var job = await ReadJobAsync(connection, jobId, token).ConfigureAwait(false);
            var product = await GetAsync(id, token).ConfigureAwait(false) ?? throw new InvalidDataException("Video completion requires a committed product.");
            RequireProductWindow(product, job);
            if (product.IsGapFiller) throw new InvalidDataException("An empty hour is not a produced standalone video.");
        }
        var changed = await ExecuteAsync(connection, null, "UPDATE jobs SET state=$state, reason=$reason, product_id=$product, updated_ticks=$now, revision=revision+1 WHERE job_id=$id AND state='Working';", token,
            ("$state", state.ToString()), ("$reason", reason), ("$product", productId?.ToString("N")),
            ("$now", clock.GetUtcNow().UtcTicks), ("$id", jobId.ToString("N"))).ConfigureAwait(false);
        if (changed != 1) throw new InvalidOperationException("Only the claimed working video job can complete.");
    }

    internal async ValueTask<CameraAgentTimeLapseSourcePlan?> GetPlanAsync(Guid jobId, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = Command(connection, null, "SELECT plan_json, plan_sha256 FROM jobs WHERE job_id=$id;", ("$id", jobId.ToString("N")));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false) || await reader.IsDBNullAsync(0, token).ConfigureAwait(false)) return null;
        return DeserializeVerified<CameraAgentTimeLapseSourcePlan>(reader.GetString(0), reader.GetString(1));
    }

    internal async ValueTask FreezePlanAsync(Guid jobId, CameraAgentTimeLapseSourcePlan plan, CancellationToken token)
    {
        if (plan.Sources.Count > 8192 || plan.Sources.Select(source => source.Descriptor.Artifact.ArtifactId).Distinct().Count() != plan.Sources.Count ||
            plan.Exclusions.Count > 32 || plan.Exclusions.Any(pair => pair.Key.Length > 128 || pair.Value < 0))
            throw new ArgumentException("Invalid bounded video source plan.", nameof(plan));
        var json = Serialize(plan);
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        var changed = await ExecuteAsync(connection, null,
            "UPDATE jobs SET plan_json=$plan, plan_sha256=$sha WHERE job_id=$id AND state='Working' AND plan_json IS NULL;", token,
            ("$plan", json), ("$sha", Hash(json)), ("$id", jobId.ToString("N"))).ConfigureAwait(false);
        if (changed != 1) throw new InvalidOperationException("The source plan was already frozen or its job is missing.");
    }

    internal async ValueTask RecordExclusionsAsync(Guid jobId, IReadOnlyDictionary<string, int> exclusions, CancellationToken token)
    {
        if (exclusions.Count > 32 || exclusions.Any(pair => pair.Key.Length > 128 || pair.Value < 0))
            throw new ArgumentException("Invalid bounded video exclusions.", nameof(exclusions));
        var json = Serialize(exclusions);
        var hash = Hash(json);
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        var previous = await ScalarAsync(connection, null, "SELECT exclusions_sha256 FROM jobs WHERE job_id=$id;", token,
            ("$id", jobId.ToString("N"))).ConfigureAwait(false);
        if (previous is string priorHash && priorHash == hash) return;
        if (await ExecuteAsync(connection, null, """
                UPDATE jobs SET exclusions_json=$json, exclusions_sha256=$hash
                WHERE job_id=$id AND state='Working' AND exclusions_json IS NULL;
                """, token, ("$json", json), ("$hash", hash), ("$id", jobId.ToString("N"))).ConfigureAwait(false) != 1)
            throw new InvalidDataException("Video admission differs from its retained evaluation.");
    }

    internal async ValueTask<CameraAgentTimeLapseProduct> PublishAsync(CameraAgentTimeLapseProduct product, EncodedTimeLapse encoded, CancellationToken token)
    {
        CameraAgentTimeLapseProductIdentity.Validate(product);
        if (product.ProductId != ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256) || product.Encoding != encoded.Evidence)
            throw new ArgumentException("Video publication identity is invalid.", nameof(product));
        await InitializeAsync(token).ConfigureAwait(false);
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var authorityConnection = await OpenAsync(token).ConfigureAwait(false);
            var owner = await ReadJobAsync(authorityConnection, product.JobId, token).ConfigureAwait(false);
            RequireProductWindow(product, owner);
            if (owner.State != CameraAgentTimeLapseState.Working && !(owner.State == CameraAgentTimeLapseState.NoSources && product.IsGapFiller &&
                owner.ParentJobId is { } parent && (await ReadJobAsync(authorityConnection, parent, token).ConfigureAwait(false)).State == CameraAgentTimeLapseState.Working))
                throw new InvalidDataException("The video publisher does not own an active generation.");
            if (await GetAsync(product.ProductId, token).ConfigureAwait(false) is { } existing)
            {
                using var verified = await OpenVideoAsync(existing.ProductId, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("A previously published video is missing.");
                return existing;
            }
            await RequireCapacityAsync(0, encoded.Evidence.PayloadBytes, token).ConfigureAwait(false);
            var root = Root();
            var name = "products/" + product.ProductId.ToString("N") + ".mp4";
            var json = Serialize(product);
            await ExecuteAsync(authorityConnection, null, "INSERT INTO publications(product_id) VALUES($id);", token,
                ("$id", product.ProductId.ToString("N"))).ConfigureAwait(false);
            await AtomicPublisher.PublishAsync(root, name, PublishMode.Replace, async (output, cancellationToken) =>
            {
                using var input = encoded.OpenRead();
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            if (await HashPathAsync(root.Resolve(name), token).ConfigureAwait(false) != product.Encoding.PayloadSha256 ||
                new FileInfo(root.Resolve(name)).Length != product.Encoding.PayloadBytes)
                throw new InvalidDataException("Published video bytes differ from encoder evidence.");
            await AtomicPublisher.PublishAsync(root, "products/" + product.ProductId.ToString("N") + ".json", PublishMode.Replace,
                (output, cancellationToken) => output.WriteAsync(Encoding.UTF8.GetBytes(json), cancellationToken).AsTask(), token).ConfigureAwait(false);
            using var connection = await OpenAsync(token).ConfigureAwait(false);
            using var transaction = Begin(connection);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO products(product_id, output_sha256, job_id, report_date, period_sha256, window_sha256, start_ticks, end_ticks,
                    preset_sha256, is_daily, document_json, document_sha256, payload_path, payload_bytes, created_ticks, width, height, has_gaps)
                VALUES($id,$output,$job,$date,$period,$window,$start,$end,$preset,$daily,$json,$sha,$path,$bytes,$created,$width,$height,$gaps);
                """, token, ("$id", product.ProductId.ToString("N")), ("$output", product.OutputIdentitySha256),
                ("$job", product.JobId.ToString("N")), ("$date", Date(product.ReportDate)), ("$period", product.ReportingPeriodIdentitySha256),
                ("$window", product.WindowIdentitySha256),
                ("$start", product.StartUtc.UtcTicks), ("$end", product.EndUtc.UtcTicks), ("$preset", product.PresetIdentitySha256),
                ("$daily", product.IsDaily ? 1 : 0), ("$json", json), ("$sha", Hash(json)), ("$path", name),
                ("$bytes", encoded.Evidence.PayloadBytes),
                ("$width", encoded.Evidence.Media.Width), ("$height", encoded.Evidence.Media.Height), ("$gaps", product.HasGaps ? 1 : 0),
                ("$created", product.CreatedUtc.UtcTicks)).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "DELETE FROM publications WHERE product_id=$id;", token,
                ("$id", product.ProductId.ToString("N"))).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return product;
        }
        finally { _writes.Release(); }
    }

    public async ValueTask<CameraAgentTimeLapseProduct?> GetAsync(Guid productId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = Command(connection, null, "SELECT document_json, document_sha256 FROM products WHERE product_id=$id;", ("$id", productId.ToString("N")));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? DeserializeVerified<CameraAgentTimeLapseProduct>(reader.GetString(0), reader.GetString(1)) : null;
    }

    public async ValueTask<LocalAutomationSourceWindow?> GetWindowAsync(Guid productId, CancellationToken cancellationToken)
    {
        var product = await GetAsync(productId, cancellationToken).ConfigureAwait(false);
        if (product is null) return null;
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var job = await ReadJobAsync(connection, product.JobId, cancellationToken).ConfigureAwait(false);
        RequireProductWindow(product, job);
        return job.Window;
    }

    internal async ValueTask<CameraAgentTimeLapseProduct?> FindWindowAsync(CameraAgentTimeLapseJob job, LocalAutomationSourceWindow window, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = Command(connection, null, """
            SELECT document_json, document_sha256 FROM products
            WHERE period_sha256=$period AND window_sha256=$window AND start_ticks=$start AND end_ticks=$end AND preset_sha256=$preset AND is_daily=$daily
                AND EXISTS(SELECT 1 FROM jobs AS owner WHERE owner.job_id=products.job_id AND owner.preset_json=$presetDocument)
            ORDER BY created_ticks DESC, product_id DESC LIMIT 1;
            """, ("$period", window.ReportingPeriod.IdentitySha256), ("$start", window.StartUtc.UtcTicks), ("$end", window.EndUtc.UtcTicks),
            ("$window", window.IdentitySha256),
            ("$preset", job.Preset.IdentitySha256), ("$presetDocument", Serialize(job.Preset)),
            ("$daily", window.Policy.Kind == LocalAutomationSourceWindowKind.SunriseDay ? 1 : 0));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? DeserializeVerified<CameraAgentTimeLapseProduct>(reader.GetString(0), reader.GetString(1)) : null;
    }

    public async ValueTask<Stream?> OpenVideoAsync(Guid productId, CancellationToken cancellationToken)
    {
        var product = await GetAsync(productId, cancellationToken).ConfigureAwait(false);
        if (product is null) return null;
        var path = Root().Resolve("products/" + productId.ToString("N") + ".mp4");
        if (!File.Exists(path)) return null;
        DurableSync.RequireRegularFile(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        try
        {
            if (stream.Length != product.Encoding.PayloadBytes ||
                Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)) != product.Encoding.PayloadSha256)
                throw new InvalidDataException("Stored video differs from its immutable checksum.");
            stream.Position = 0;
            return stream;
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async ValueTask<CameraAgentTimeLapseDay> GetDayAsync(DateOnly reportDate, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var jobs = new List<CameraAgentTimeLapseJob>();
        using (var command = Command(connection, null, "SELECT " + JobColumns + " FROM jobs WHERE report_date=$date ORDER BY updated_ticks LIMIT 1025;", ("$date", Date(reportDate))))
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) jobs.Add(ReadJob(reader));
        var products = await ReadProductsAsync(connection,
            "SELECT document_json, document_sha256 FROM products WHERE report_date=$date ORDER BY start_ticks, created_ticks LIMIT 1025;",
            cancellationToken, ("$date", Date(reportDate))).ConfigureAwait(false);
        if (jobs.Count > 1024 || products.Count > 1024) throw new InvalidDataException("Time-lapse day exceeds its listing bound.");
        return new(reportDate, jobs, products);
    }

    public async ValueTask<IReadOnlyList<CameraAgentTimeLapseSummary>> ListDailyAsync(DateOnly? before, int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 62) throw new ArgumentOutOfRangeException(nameof(limit));
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = Command(connection, null, """
            WITH ranked AS (
                SELECT product_id, report_date, width, height, has_gaps,
                    ROW_NUMBER() OVER(PARTITION BY report_date ORDER BY created_ticks DESC, product_id DESC) AS revision
                FROM products WHERE is_daily=1 AND ($before IS NULL OR report_date < $before)
            )
            SELECT product_id, report_date, width, height, has_gaps FROM ranked WHERE revision=1 ORDER BY report_date DESC LIMIT $limit;
            """, ("$before", before is { } date ? Date(date) : null), ("$limit", limit));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<CameraAgentTimeLapseSummary>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(Guid.ParseExact(reader.GetString(0), "N"), DateOnly.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4) != 0));
        return result;
    }

    public async ValueTask<IReadOnlyList<ProcessingRetentionHold>> GetRetentionHoldsAsync(string storageRoot, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetFullPath(storageRoot), Path.GetFullPath(options.Value.RawIngressRoot), StringComparison.Ordinal)) return [];
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = Command(connection, null, "SELECT plan_json, plan_sha256 FROM jobs WHERE state IN ('Queued','Working') AND plan_json IS NOT NULL LIMIT 65;");
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var holds = new List<ProcessingRetentionHold>();
        var plans = 0;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (++plans > MaximumPendingJobs) throw new InvalidDataException("Time-lapse retention holds exceed the queue bound.");
            var plan = DeserializeVerified<CameraAgentTimeLapseSourcePlan>(reader.GetString(0), reader.GetString(1));
            holds.AddRange(plan.Sources.Select(source => new ProcessingRetentionHold(source.Descriptor.Artifact.ArtifactId,
                source.PayloadRelativePath, source.SidecarRelativePath)));
        }
        return holds.Distinct().ToArray();
    }

    private PhysicalRoot Root()
    {
        var basePath = Path.GetFullPath(options.Value.RawIngressRoot);
        Directory.CreateDirectory(basePath);
        var parent = PhysicalRoot.Open(basePath);
        var directory = parent.Resolve(".time-lapses");
        AtomicPublisher.EnsureDirectory(parent, directory);
        return PhysicalRoot.Open(directory);
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var root = Root();
        var path = root.Resolve("time-lapses.db");
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) root.Verify(path + suffix, "open-video-store");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = options.Value.RawIngressSqliteBusyTimeoutSeconds
        }.ToString());
        await Sqlite.SqliteConnectionConfigurationGate.OpenAndConfigureAsync(connection, async (configured, cancellationToken) =>
        {
            await ExecuteAsync(configured, null, "PRAGMA synchronous=FULL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(configured, null, "PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
            if (!string.Equals((string?)await ScalarAsync(configured, null, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false), "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Time-lapse storage requires WAL.");
        }, token).ConfigureAwait(false);
        return connection;
    }

    private static CameraAgentTimeLapseJob ReadJob(SqliteDataReader reader)
    {
        var job = new CameraAgentTimeLapseJob(
        Guid.ParseExact(reader.GetString(0), "N"), Deserialize<LocalAutomationOccurrence>(reader.GetString(1)),
        Deserialize<LocalAutomationSourceWindow>(reader.GetString(2)), reader.IsDBNull(3) ? null : Guid.ParseExact(reader.GetString(3), "N"),
        Deserialize<CameraAgentTimeLapsePreset>(reader.GetString(4)), Enum.Parse<CameraAgentTimeLapseState>(reader.GetString(5)),
        new(reader.GetInt64(6), TimeSpan.Zero), reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : Guid.ParseExact(reader.GetString(8), "N"))
        {
            Revision = reader.GetInt64(9),
            Exclusions = reader.IsDBNull(10) ? new Dictionary<string, int>() : DeserializeVerified<Dictionary<string, int>>(reader.GetString(10), reader.GetString(11))
        };
        if (!job.Occurrence.IsValid() || !job.Window.IsValid() || !TimeLapseOptions.Matches(job.Preset) ||
            job.JobId != JobId(job.Occurrence, job.Window) || !Enum.IsDefined(job.State) || job.Revision < 1 ||
            job.Occurrence.Definition.TaskKind != LocalAutomationTaskKind.TimeLapseGeneration ||
            job.Occurrence.Definition.TaskTarget != "time-lapse:" + job.Preset.IdentitySha256 ||
            job.ParentJobId is null && job.Window != job.Occurrence.SourceWindow ||
            job.ParentJobId.HasValue && (job.Occurrence.SourceWindow is not { } parent || job.ParentJobId != JobId(job.Occurrence, parent) ||
                job.Window.ReportingPeriod != parent.ReportingPeriod || job.Window.StartUtc < parent.StartUtc || job.Window.EndUtc > parent.EndUtc))
            throw new InvalidDataException("The retained local video request identity is invalid.");
        return job;
    }

    private static async ValueTask<CameraAgentTimeLapseJob> ReadJobAsync(SqliteConnection connection, Guid jobId, CancellationToken token)
    {
        using var command = Command(connection, null, "SELECT " + JobColumns + " FROM jobs WHERE job_id=$id;", ("$id", jobId.ToString("N")));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadJob(reader) : throw new InvalidDataException("The retained video job is missing.");
    }

    private static void RequireProductWindow(CameraAgentTimeLapseProduct product, CameraAgentTimeLapseJob job)
    {
        if (product.WindowIdentitySha256 != job.Window.IdentitySha256 || product.PresetIdentitySha256 != job.Preset.IdentitySha256 ||
            product.ReportingPeriodIdentitySha256 != job.Window.ReportingPeriod.IdentitySha256 || product.ReportDate != job.Window.ReportingPeriod.ReportDate ||
            product.StartUtc != job.Window.StartUtc || product.EndUtc != job.Window.EndUtc ||
            product.IsDaily != (job.Window.Policy.Kind == LocalAutomationSourceWindowKind.SunriseDay))
            throw new InvalidDataException("The retained product does not belong to this video window and preset.");
    }

    private static async ValueTask<IReadOnlyList<CameraAgentTimeLapseProduct>> ReadProductsAsync(SqliteConnection connection, string sql,
        CancellationToken token, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, null, sql, parameters);
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var products = new List<CameraAgentTimeLapseProduct>();
        long totalDocumentBytes = 0;
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var json = reader.GetString(0);
            totalDocumentBytes += Encoding.UTF8.GetByteCount(json);
            if (totalDocumentBytes > 64L * 1024 * 1024) throw new InvalidDataException("The video day provenance exceeds its bounded read size.");
            products.Add(DeserializeVerified<CameraAgentTimeLapseProduct>(json, reader.GetString(1)));
        }
        return products;
    }

    private static Guid JobId(LocalAutomationOccurrence occurrence, LocalAutomationSourceWindow window) =>
        ProcessingIdentity.CreateArtifactId(CaptureContractJson.ComputeCanonicalJsonSha256(new { occurrence.IdentitySha256, window = window.IdentitySha256 }));
    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Serialize<T>(T value)
    {
        var json = CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(value, Json)).GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > MaximumDocumentBytes) throw new InvalidDataException("Time-lapse document exceeds its bound.");
        return json;
    }
    private static T Deserialize<T>(string json) => Encoding.UTF8.GetByteCount(json) <= MaximumDocumentBytes
        ? JsonSerializer.Deserialize<T>(json, Json) ?? throw new InvalidDataException("Invalid time-lapse document.")
        : throw new InvalidDataException("Time-lapse document exceeds its bound.");
    private static T DeserializeVerified<T>(string json, string hash)
    {
        if (Hash(json) != hash) throw new InvalidDataException("Time-lapse document checksum differs from its committed evidence.");
        var value = Deserialize<T>(json);
        if (value is CameraAgentTimeLapseProduct product) CameraAgentTimeLapseProductIdentity.Validate(product);
        return value;
    }
    private static async Task<string> HashPathAsync(string path, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    [SuppressMessage("Reliability", "CA1849:Call async methods when in an async method", Justification = "SQLite immediate transactions use the synchronous API.")]
    private static SqliteTransaction Begin(SqliteConnection connection) => connection.BeginTransaction(deferred: false);
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only repository-owned SQL literals are passed; every value is a parameter.")]
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }
    private static async ValueTask<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken token,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    private static async ValueTask<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken token,
        params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, transaction, sql, parameters);
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }

    private const string Schema = """
        CREATE TABLE jobs(
            job_id TEXT PRIMARY KEY, occurrence_json TEXT NOT NULL, window_json TEXT NOT NULL, parent_job_id TEXT,
            preset_json TEXT NOT NULL, report_date TEXT NOT NULL, state TEXT NOT NULL
                CHECK(state IN ('Queued','Working','Produced','NoSources','Unavailable','Failed')),
            updated_ticks INTEGER NOT NULL, reason TEXT, product_id TEXT, plan_json TEXT, plan_sha256 TEXT,
            exclusions_json TEXT, exclusions_sha256 TEXT, revision INTEGER NOT NULL DEFAULT 1 CHECK(revision > 0)
        ) STRICT;
        CREATE INDEX jobs_by_date ON jobs(report_date,updated_ticks);
        CREATE INDEX jobs_by_state ON jobs(state,updated_ticks);
        CREATE TABLE retries(request_id TEXT PRIMARY KEY, job_id TEXT NOT NULL REFERENCES jobs(job_id), expected_revision INTEGER NOT NULL,
            actor TEXT NOT NULL, reason TEXT NOT NULL, previous_state TEXT NOT NULL, previous_reason TEXT, requested_ticks INTEGER NOT NULL) STRICT;
        CREATE INDEX retries_by_job ON retries(job_id);
        CREATE TABLE publications(product_id TEXT PRIMARY KEY) STRICT;
        CREATE TABLE products(
            product_id TEXT PRIMARY KEY, output_sha256 TEXT NOT NULL UNIQUE, job_id TEXT NOT NULL REFERENCES jobs(job_id),
            report_date TEXT NOT NULL, period_sha256 TEXT NOT NULL, window_sha256 TEXT NOT NULL, start_ticks INTEGER NOT NULL, end_ticks INTEGER NOT NULL,
            preset_sha256 TEXT NOT NULL, is_daily INTEGER NOT NULL CHECK(is_daily IN (0,1)), document_json TEXT NOT NULL,
            document_sha256 TEXT NOT NULL, payload_path TEXT NOT NULL UNIQUE, payload_bytes INTEGER NOT NULL CHECK(payload_bytes > 0), created_ticks INTEGER NOT NULL,
            width INTEGER NOT NULL CHECK(width BETWEEN 2 AND 1280), height INTEGER NOT NULL CHECK(height BETWEEN 2 AND 1280),
            has_gaps INTEGER NOT NULL CHECK(has_gaps IN (0,1))
        ) STRICT;
        CREATE INDEX products_by_date ON products(report_date,created_ticks);
        CREATE INDEX products_by_window ON products(period_sha256,start_ticks,end_ticks,preset_sha256,is_daily);
        CREATE TRIGGER immutable_products_update BEFORE UPDATE ON products BEGIN SELECT RAISE(ABORT,'Video publications are immutable.'); END;
        CREATE TRIGGER immutable_products_delete BEFORE DELETE ON products BEGIN SELECT RAISE(ABORT,'Video publications are immutable.'); END;
        CREATE TRIGGER immutable_job_inputs BEFORE UPDATE OF occurrence_json,window_json,preset_json,plan_json,plan_sha256,exclusions_json,exclusions_sha256 ON jobs
        WHEN OLD.occurrence_json != NEW.occurrence_json OR OLD.window_json != NEW.window_json OR OLD.preset_json != NEW.preset_json
          OR (OLD.plan_json IS NOT NULL AND (OLD.plan_json IS NOT NEW.plan_json OR OLD.plan_sha256 IS NOT NEW.plan_sha256))
          OR (OLD.exclusions_json IS NOT NULL AND (OLD.exclusions_json IS NOT NEW.exclusions_json OR OLD.exclusions_sha256 IS NOT NEW.exclusions_sha256))
        BEGIN SELECT RAISE(ABORT,'Retained video inputs are immutable.'); END;
        PRAGMA user_version=1;
        """;
}
