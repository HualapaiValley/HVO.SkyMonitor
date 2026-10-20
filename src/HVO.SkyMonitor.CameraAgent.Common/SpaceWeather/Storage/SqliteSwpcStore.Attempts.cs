using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.Processing.SpaceWeather;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;

public sealed partial class SqliteSwpcStore
{
    public ValueTask<SwpcWriteResult> BeginAttemptAsync(SwpcAttemptStart request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireIdentity(request.AttemptId, request.ExpectedRevision);
        RequireUtc(request.StartedUtc);
        RequireBounded(request.RunKey, 128);
        ValidateValidators(request.SentValidators);
        if (!Enum.IsDefined(request.Product) || !Enum.IsDefined(request.Trigger))
            throw new ArgumentException("An attempt requires an approved product and trigger.", nameof(request));
        var fingerprint = Fingerprint(request with { ExpectedRevision = 0 });
        return ExecuteAsync(null, (db, tx) =>
        {
            var prior = Scalar(db, tx, "SELECT start_fingerprint FROM swpc_attempts WHERE id=$id;", ("$id", request.AttemptId.ToString("N"))) as string;
            if (prior is not null)
                return prior == fingerprint
                    ? Result(db, tx, request.StartedUtc, SwpcWriteStatus.Replayed)
                    : Result(db, tx, request.StartedUtc, SwpcWriteStatus.Conflict, "space-weather.idempotency-conflict");
            Purge(db, tx, request.StartedUtc);
            if (Revision(db, tx) != request.ExpectedRevision)
                return Result(db, tx, request.StartedUtc, SwpcWriteStatus.Conflict, "space-weather.revision-conflict");
            if (Scalar(db, tx, "SELECT 1 FROM swpc_attempts WHERE completion IS NULL LIMIT 1;") is not null)
                return Result(db, tx, request.StartedUtc, SwpcWriteStatus.Conflict, "space-weather.attempt-in-progress");
            WriteCheckpoint(db, tx, request.Product, request.CheckpointAfterStart);
            Exec(db, tx, "UPDATE swpc_products SET last_attempt=$t WHERE product=$p;",
                ("$t", Ticks(request.StartedUtc)), ("$p", (int)request.Product));
            Exec(db, tx, "INSERT INTO swpc_attempts(id,product,started,start,start_fingerprint) VALUES($id,$p,$t,$s,$f);",
                ("$id", request.AttemptId.ToString("N")), ("$p", (int)request.Product), ("$t", Ticks(request.StartedUtc)),
                ("$s", Json(request)), ("$f", fingerprint));
            Bump(db, tx);
            return Result(db, tx, request.StartedUtc);
        }, ct);
    }

    public ValueTask<SwpcWriteResult> CompleteAttemptAsync(SwpcAttemptCompletion request, SwpcCommitProjection project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(project);
        RequireIdentity(request.AttemptId, request.ExpectedRevision);
        RequireUtc(request.AtUtc); RequireUtc(request.EndedUtc); RequireCode(request.ReasonCode);
        if (request.RetrievedUtc is { } retrieved) RequireUtc(retrieved);
        if (request.BytesRead < 0 || request.HttpStatus is < 100 or > 599 ||
            request.Outcome is { } outcome && !Enum.IsDefined(outcome) || !Enum.IsDefined(request.ValidatorOutcome))
            throw new ArgumentException("Invalid completion metadata.", nameof(request));
        if (request.Candidate is { } candidate) ValidateCandidate(candidate);
        if (request.Outcome is null && (request.ReasonCode is not ("interrupted" or "storage-failure") ||
            request.ValidatorOutcome != SwpcValidatorOutcome.Preserved || request.BytesRead != 0 ||
            request.Candidate is not null || request.RetrievedUtc is not null || request.HttpStatus is not null))
            throw new ArgumentException("A local settlement must preserve the durable fetch state.", nameof(request));
        var fingerprint = Fingerprint(request with { ExpectedRevision = 0 });
        return ExecuteAsync(null, (db, tx) =>
        {
            SwpcAttemptStart? start = null;
            using (var command = Command(db, tx, "SELECT start,completion_fingerprint,added,diagnostics FROM swpc_attempts WHERE id=$id;",
                ("$id", request.AttemptId.ToString("N"))))
            using (var reader = command.ExecuteReader())
                if (reader.Read())
                {
                    start = Parse<SwpcAttemptStart>(reader.GetString(0));
                    if (!reader.IsDBNull(1))
                        return reader.GetString(1) == fingerprint
                            ? Result(db, tx, request.AtUtc, SwpcWriteStatus.Replayed, added: reader.GetInt32(2),
                                diagnostics: Parse<ImmutableArray<string>>(reader.GetString(3)))
                            : Result(db, tx, request.AtUtc, SwpcWriteStatus.Conflict, "space-weather.idempotency-conflict");
                }
            Purge(db, tx, request.AtUtc);
            if (start is null) return Result(db, tx, request.AtUtc, SwpcWriteStatus.NotFound, "space-weather.attempt-not-found");
            if (Revision(db, tx) != request.ExpectedRevision)
                return Result(db, tx, request.AtUtc, SwpcWriteStatus.Conflict, "space-weather.revision-conflict");
            if (request.EndedUtc < start.StartedUtc) throw new ArgumentException("Attempt end precedes its start.", nameof(request));
            var before = ReadView(db, tx, request.AtUtc);
            var added = 0;
            var touched = ImmutableHashSet<SwpcMessageNode>.Empty;
            var diagnostics = ImmutableArray<string>.Empty;
            var after = before;
            if (request.Candidate is { } snapshot)
            {
                if (snapshot.Product != start.Product || snapshot.FetchStartedUtc < start.StartedUtc ||
                    snapshot.RetrievedUtc != request.RetrievedUtc || snapshot.RetrievedUtc > request.EndedUtc ||
                    snapshot.HttpStatus != request.HttpStatus)
                    return Result(db, tx, request.AtUtc, SwpcWriteStatus.Conflict, "space-weather.snapshot-conflict");
                if (request.Outcome is not (SwpcFetchOutcome.Updated or SwpcFetchOutcome.Unchanged or SwpcFetchOutcome.StaleSnapshot))
                    throw new ArgumentException("An accepted candidate requires a body outcome.", nameof(request));
                if (SwpcSnapshotComparison.Classify(before.Products[start.Product].LastKnownGood, snapshot) != request.Outcome)
                    return Result(db, tx, request.AtUtc, SwpcWriteStatus.Conflict, "space-weather.snapshot-conflict");
                (added, touched, diagnostics) = SaveSnapshot(db, tx, snapshot, request.Outcome!.Value, request.AtUtc);
                after = ReadView(db, tx, request.AtUtc);
            }
            else if (request.Outcome is SwpcFetchOutcome.Updated or SwpcFetchOutcome.Unchanged or SwpcFetchOutcome.StaleSnapshot)
                throw new ArgumentException("This outcome requires a candidate.", nameof(request));
            else if (request.Outcome == SwpcFetchOutcome.NotModified &&
                (before.Products[start.Product].LastKnownGood is null || request.HttpStatus != 304 || request.RetrievedUtc is null ||
                    !HasRecordedValidator(start.SentValidators, before.Products[start.Product].Validators)))
                throw new ArgumentException("Not-modified requires a prior snapshot, retrieval time and sent recorded validator.", nameof(request));

            SwpcCommitEffects effects;
            try { effects = project(new(request, before, after, touched)); }
            catch (Exception exception)
            {
                ProjectionFailed(_logger, exception.GetType().Name);
                throw new ProjectionFailureException(exception);
            }
            ArgumentNullException.ThrowIfNull(effects);
            ValidateEffects(effects, after.Notices);
            if (request.Outcome is null)
            {
                if (effects.ProductCheckpoint != before.Products[start.Product].Checkpoint ||
                    effects.HaltChange is { } halt && halt.Value != (halt.Product is { } p ? before.Products[p].ProductHalt : before.ProviderHalt) ||
                    !effects.ProviderAttention.IsEmpty ||
                    effects.NoticeAttention is { } attention && (!attention.Events.IsEmpty ||
                        attention.RaisedRanks.Count != before.Notices.RaisedRanks.Count ||
                        attention.RaisedRanks.Any(pair => !before.Notices.RaisedRanks.TryGetValue(pair.Key, out var rank) || rank != pair.Value)))
                    throw new ArgumentException("A local settlement cannot change durable policy state.", nameof(project));
            }
            else
            {
                WriteCheckpoint(db, tx, start.Product, effects.ProductCheckpoint);
                if (effects.HaltChange is { } halt) WriteHalt(db, tx, halt);
                if (effects.NoticeAttention is { } attention) WriteRanks(db, tx, attention.RaisedRanks);
                if (request.Candidate is null)
                {
                    Exec(db, tx, "UPDATE swpc_products SET last_outcome=$o WHERE product=$p;", ("$o", (int)request.Outcome.Value), ("$p", (int)start.Product));
                    if (request.Outcome == SwpcFetchOutcome.NotModified)
                        Exec(db, tx, "UPDATE swpc_products SET last_success=$t WHERE product=$p;", ("$t", Ticks(request.RetrievedUtc!.Value)), ("$p", (int)start.Product));
                }
            }
            // Never duplicate the decoded body or parsed facts into the journal.
            Exec(db, tx, "UPDATE swpc_attempts SET completion=$c,completion_fingerprint=$f,snapshot_sha=$s,added=$a,diagnostics=$d WHERE id=$id;",
                ("$c", Json(request with { Candidate = null })), ("$f", fingerprint), ("$s", request.Candidate?.Sha256),
                ("$a", added), ("$d", Json(diagnostics)), ("$id", request.AttemptId.ToString("N")));
            PruneAudit(db, tx, request.AtUtc);
            Bump(db, tx);
            return Result(db, tx, request.AtUtc, added: added, diagnostics: diagnostics,
                attention: [.. effects.NoticeAttention?.Events ?? [], .. effects.ProviderAttention]);
        }, ct);
    }

    private static bool HasRecordedValidator(SwpcValidators sent, SwpcValidators recorded)
        => (sent.ETag is not null && sent.ETag == recorded.ETag ||
            sent.LastModified is not null && sent.LastModified == recorded.LastModified) &&
            (sent.ETag is null || sent.ETag == recorded.ETag) &&
            (sent.LastModified is null || sent.LastModified == recorded.LastModified);

    private static void RequireIdentity(Guid id, long revision)
    {
        if (id == Guid.Empty || revision < 0) throw new ArgumentException("A durable operation requires an identity and revision.");
    }
    private static void ValidateValidators(SwpcValidators validators)
    {
        ArgumentNullException.ThrowIfNull(validators);
        foreach (var value in new[] { validators.ETag, validators.LastModified })
            if (value is not null && (value.Length > 65536 || value.Any(c => c is '\r' or '\n' or '\0')))
                throw new ArgumentException("Invalid recorded validator.", nameof(validators));
    }
    private static void ValidateCandidate(SwpcSnapshot candidate)
    {
        RequireUtc(candidate.FetchStartedUtc); RequireUtc(candidate.RetrievedUtc);
        if (candidate.InFileStampUtc is { } stamp) RequireUtc(stamp);
        ValidateValidators(candidate.ResponseValidators);
        var path = candidate.Product switch
        {
            SwpcProduct.Alerts => "/products/alerts.json",
            SwpcProduct.Scales => "/products/noaa-scales.json",
            SwpcProduct.KpForecast => "/products/noaa-planetary-k-index-forecast.json",
            SwpcProduct.ThreeDayForecast => "/text/3-day-forecast.txt",
            _ => throw new ArgumentException("Unsupported candidate product.", nameof(candidate))
        };
        if (candidate.CanonicalPath != path || candidate.HttpStatus != 200 || candidate.DecodedBody.IsDefault ||
            candidate.DecodedByteCount != candidate.DecodedBody.Length || candidate.RetrievedUtc < candidate.FetchStartedUtc)
            throw new ArgumentException("Invalid candidate envelope.", nameof(candidate));
        SwpcParsedBody verified = candidate.Product switch
        {
            SwpcProduct.Alerts => new SwpcAlertsBody(SwpcAlertsParser.Parse(candidate.DecodedBody.AsSpan(), candidate.MediaType)),
            SwpcProduct.Scales => new SwpcScalesBody(SwpcScalesParser.Parse(candidate.DecodedBody.AsSpan(), candidate.MediaType)),
            SwpcProduct.KpForecast => new SwpcKpBody(SwpcKpForecastParser.Parse(candidate.DecodedBody.AsSpan(), candidate.MediaType)),
            _ => new SwpcThreeDayBody(SwpcThreeDayForecastParser.Parse(candidate.DecodedBody.AsSpan(), candidate.MediaType))
        };
        var (accepted, hash, time) = verified switch
        {
            SwpcAlertsBody b => (b.Value.Accepted, b.Value.SnapshotSha256, b.Value.StampUtc),
            SwpcScalesBody b => (b.Value.Accepted, b.Value.SnapshotSha256, b.Value.StampUtc),
            SwpcKpBody b => (b.Value.Accepted, b.Value.SnapshotSha256, b.Value.StampUtc),
            SwpcThreeDayBody b => (b.Value.Accepted, b.Value.SnapshotSha256, b.Value.StampUtc),
            _ => throw new ArgumentException("Unsupported candidate body.", nameof(candidate))
        };
        if (!accepted || hash != candidate.Sha256 || time != candidate.InFileStampUtc ||
            candidate.Parsed is null || candidate.Parsed.GetType() != verified.GetType() || Json<SwpcParsedBody>(candidate.Parsed) != Json(verified))
            throw new ArgumentException("Candidate bytes, hash and typed facts disagree.", nameof(candidate));
    }
    private static void ValidateEffects(SwpcCommitEffects effects, SwpcNoticeData notices)
    {
        ArgumentNullException.ThrowIfNull(effects.ProductCheckpoint);
        if (effects.HaltChange is { } change) ValidateHalt(change);
        if (effects.ProviderAttention.IsDefault) throw new ArgumentException("Provider attention must be initialized.", nameof(effects));
        foreach (var item in effects.ProviderAttention) ValidateEvent(item);
        if (effects.NoticeAttention is not { } attention) return;
        ArgumentNullException.ThrowIfNull(attention.RaisedRanks);
        var nodes = notices.Groups.SelectMany(g => g.Nodes).ToHashSet();
        if (attention.Events.IsDefault || attention.RaisedRanks.Any(p => !nodes.Contains(p.Key) || p.Value is < 0 or > 5) ||
            notices.RaisedRanks.Any(p => !attention.RaisedRanks.TryGetValue(p.Key, out var rank) || rank < p.Value))
            throw new ArgumentException("Invalid replacement raised ranks.", nameof(effects));
        foreach (var item in attention.Events)
        {
            ValidateEvent(item);
            if (!notices.Groups.Any(g => g.Identity == item.Group)) throw new ArgumentException("Attention references a missing group.", nameof(effects));
        }
    }
    private static void ValidateEvent(SwpcAttentionEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        RequireBounded(item.Group, 128); RequireCode(item.Reason);
        if (item.Level is { } level) RequireBounded(level, 64);
    }
    private static void WriteRanks(SqliteConnection db, SqliteTransaction tx, ImmutableDictionary<SwpcMessageNode, int> ranks)
    {
        Exec(db, tx, "DELETE FROM swpc_ranks;");
        foreach (var (node, rank) in ranks)
            Exec(db, tx, "INSERT INTO swpc_ranks(code,serial,rank) VALUES($c,$s,$r);", ("$c", node.Code), ("$s", node.Serial), ("$r", rank));
    }
    [SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Private dispatch marker always requires the original projection exception and never escapes.")]
    [SuppressMessage("Design", "CA1064:Exceptions should be public", Justification = "Private dispatch marker is unwrapped before leaving the store.")]
    private sealed class ProjectionFailureException(Exception original) : Exception
    {
        public Exception Original { get; } = original;
    }
    [LoggerMessage(EventId = 7812, Level = LogLevel.Warning, Message = "Space-weather completion projection failed with type {ExceptionType}.")]
    private static partial void ProjectionFailed(ILogger logger, string exceptionType);
}
