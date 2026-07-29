using System.Security.Cryptography;
using System.Text;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Transients;

internal sealed class SqliteCameraAgentTransientOperatorProjection : ICameraAgentTransientOperatorProjection
{
    internal const int DefaultPageSize = 25;
    internal const int MaximumPageSize = 100;
    private const int MaximumExtractionBytes = 1024 * 1024;
    private const int MaximumAssessmentBytes = 256 * 1024;
    private readonly string _root;
    private readonly string _databasePath;
    private readonly int _busyTimeoutSeconds;
    private readonly IRawCaptureIngress _rawIngress;

    public SqliteCameraAgentTransientOperatorProjection(
        IOptions<CameraAgentHostOptions> options,
        IRawCaptureIngress rawIngress)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(rawIngress);
        _root = Path.GetFullPath(options.Value.RawIngressRoot);
        _databasePath = Path.Combine(_root, "journal", "raw-ingress.db");
        _busyTimeoutSeconds = options.Value.RawIngressSqliteBusyTimeoutSeconds;
        _rawIngress = rawIngress;
    }

    public async ValueTask<CameraAgentTransientOperatorPage> GetPageAsync(
        CameraAgentTransientOperatorQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = query.PageSize ?? DefaultPageSize;
        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new CameraAgentTransientOperatorQueryException(
                $"Page size must be between 1 and {MaximumPageSize}.");
        }
        var cursor = DecodeCursor(query.Cursor);
        await _rawIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        var hasRuntime = await RuntimeTableExistsAsync(connection, cancellationToken).ConfigureAwait(false);
        using var command = CreateSummaryCommand(connection, cursor, pageSize, hasRuntime);
        var rows = new List<SummaryRow>(pageSize + 1);
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(await ReadSummaryRowAsync(reader, cancellationToken).ConfigureAwait(false));
            }
        }
        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }
        return new CameraAgentTransientOperatorPage(
            rows.Select(static row => row.Candidate).ToArray(),
            hasMore && rows.Count > 0 ? EncodeCursor(rows[^1]) : null);
    }

    public async ValueTask<CameraAgentTransientOperatorDetail?> GetCandidateAsync(
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        if (candidateId == Guid.Empty)
        {
            return null;
        }
        await _rawIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        ProjectedRow row;
        var hasRuntime = await RuntimeTableExistsAsync(connection, cancellationToken).ConfigureAwait(false);
        using (var command = CreateDetailCommand(connection, candidateId, hasRuntime))
        {
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            row = await ReadAndValidateRowAsync(reader, cancellationToken).ConfigureAwait(false);
        }

        return new CameraAgentTransientOperatorDetail(
            ProjectSummary(row),
            ProjectCandidateEvidence(row.Candidate, row.Phase),
            ProjectExtraction(row.Causal, row.Phase),
            ProjectExtraction(row.Centered, row.Phase),
            ProjectAssessment(row.Assessment, row.Phase),
            ProjectFinal(row.Finalization, row.Phase));
    }

    private static async ValueTask<ProjectedRow> ReadAndValidateRowAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(reader.GetString(0), "N", out var candidateId) || candidateId == Guid.Empty ||
            !Guid.TryParseExact(reader.GetString(1), "N", out var eventId) || eventId == Guid.Empty || candidateId == eventId)
        {
            throw Corrupt();
        }
        var eventState = ReadEventState(reader.GetString(2));
        var phase = ReadPhase(reader.GetString(3));
        var created = ReadTime(reader.GetInt64(4));
        var updated = ReadTime(reader.GetInt64(5));
        if (updated < created)
        {
            throw Corrupt();
        }

        var candidatePayload = await ReadPayloadAsync(
            reader, 6, 7, 8, TransientContractJson.MaximumCandidateBytes, cancellationToken).ConfigureAwait(false);
        var finalizationPayload = await ReadPayloadAsync(
            reader, 9, 10, 11, TransientCandidateDeliveryJson.MaximumFinalizationBytes, cancellationToken).ConfigureAwait(false);
        var submissionPayload = await ReadPayloadAsync(
            reader, 12, 13, 14, TransientCandidateDeliveryJson.MaximumSubmissionBytes, cancellationToken).ConfigureAwait(false);
        var acknowledgementPayload = await ReadPayloadAsync(
            reader, 15, 16, 17, TransientCandidateDeliveryJson.MaximumAcknowledgementBytes, cancellationToken).ConfigureAwait(false);

        var causalPayload = await ReadPayloadAsync(
            reader, 18, 19, 20, MaximumExtractionBytes, cancellationToken).ConfigureAwait(false);
        var centeredPayload = await ReadPayloadAsync(
            reader, 21, 22, 23, MaximumExtractionBytes, cancellationToken).ConfigureAwait(false);
        var assessmentPayload = await ReadPayloadAsync(
            reader, 24, 25, 26, MaximumAssessmentBytes, cancellationToken).ConfigureAwait(false);

        var candidate = ParseCandidate(candidatePayload, candidateId, eventId);
        var finalization = ParseFinalization(finalizationPayload, candidateId, eventId);
        var submission = ParseSubmission(submissionPayload, candidateId, eventId);
        var acknowledgement = ParseAcknowledgement(acknowledgementPayload, candidateId, eventId);
        var candidateStateNull = await reader.IsDBNullAsync(27, cancellationToken).ConfigureAwait(false);
        if (candidate is null && phase is not ("reserved" or "quarantined") ||
            (candidate is null) != candidateStateNull ||
            candidate is not null && !string.Equals(
                candidate.State.ToString(), reader.GetString(27), StringComparison.Ordinal) ||
            phase is "finalized" && finalization is null ||
            phase is "handoff_pending" && submission is null ||
            phase is "acknowledged" && (submission is null || acknowledgement is null) ||
            finalization is not null && !string.Equals(finalization.Event.State.ToString(), eventState, StringComparison.Ordinal) ||
            submission is not null && (candidatePayload is null ||
                !string.Equals(
                    ComputeSha256(TransientContractJson.Serialize(submission.Candidate)),
                    candidatePayload.Identity,
                    StringComparison.Ordinal)) ||
            acknowledgement is not null && (submission is null ||
                !string.Equals(
                    acknowledgement.SubmissionIdentitySha256,
                    submission.SubmissionIdentitySha256,
                    StringComparison.Ordinal)))
        {
            throw Corrupt();
        }
        var row = new ProjectedRow(
            candidateId,
            eventId,
            eventState,
            phase,
            created,
            updated,
            candidate?.State.ToString() ?? (phase == "quarantined" ? "Unavailable" : "PendingContext"),
            candidate,
            finalization,
            null,
            null,
            null);
        var causal = ParseExtraction(causalPayload, row, TransientTemporalBackgroundKind.CausalProvisional);
        return row with
        {
            Causal = causal,
            Centered = ParseObservationExtraction(centeredPayload, causalPayload, row),
            Assessment = ParseAssessment(assessmentPayload, row)
        };
    }

    private static async ValueTask<SummaryRow> ReadSummaryRowAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(reader.GetString(0), "N", out var candidateId) || candidateId == Guid.Empty ||
            !Guid.TryParseExact(reader.GetString(1), "N", out var eventId) || eventId == Guid.Empty || candidateId == eventId)
        {
            throw Corrupt();
        }
        var eventState = ReadEventState(reader.GetString(2));
        var phase = ReadPhase(reader.GetString(3));
        var created = ReadTime(reader.GetInt64(4));
        var updated = ReadTime(reader.GetInt64(5));
        if (updated < created)
        {
            throw Corrupt();
        }
        var hasCandidate = reader.GetBoolean(7);
        var hasFinalization = reader.GetBoolean(8);
        var hasSubmission = reader.GetBoolean(9);
        var hasAcknowledgement = reader.GetBoolean(10);
        if (!hasCandidate && phase is not ("reserved" or "quarantined") ||
            hasCandidate && await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ||
            !hasCandidate && !await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ||
            phase == "finalized" && !hasFinalization ||
            phase == "handoff_pending" && !hasSubmission ||
            phase == "acknowledged" && (!hasSubmission || !hasAcknowledgement))
        {
            throw Corrupt();
        }
        var candidateState = !hasCandidate
            ? phase == "quarantined" ? "Unavailable" : "PendingContext"
            : Enum.TryParse<TransientCandidateState>(reader.GetString(6), ignoreCase: false, out var parsedState)
                ? parsedState.ToString()
                : throw Corrupt();
        var candidate = new CameraAgentTransientOperatorCandidate(
            candidateId,
            eventId,
            candidateState,
            eventState,
            phase,
            created,
            updated,
            StageState(reader.GetBoolean(11), phase),
            StageState(reader.GetBoolean(12), phase),
            StageState(reader.GetBoolean(13), phase),
            StageState(hasFinalization, phase));
        return new SummaryRow(candidate, created, candidateId);
    }

    private static async ValueTask<Payload?> ReadPayloadAsync(
        SqliteDataReader reader,
        int identityOrdinal,
        int lengthOrdinal,
        int payloadOrdinal,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var identityNull = await reader.IsDBNullAsync(identityOrdinal, cancellationToken).ConfigureAwait(false);
        var lengthNull = await reader.IsDBNullAsync(lengthOrdinal, cancellationToken).ConfigureAwait(false);
        var payloadNull = await reader.IsDBNullAsync(payloadOrdinal, cancellationToken).ConfigureAwait(false);
        if (identityNull && lengthNull && payloadNull)
        {
            return null;
        }
        if (identityNull || lengthNull || payloadNull || reader.GetInt64(lengthOrdinal) is < 1 ||
            reader.GetInt64(lengthOrdinal) > maximumBytes)
        {
            throw Corrupt();
        }
        var identity = reader.GetString(identityOrdinal);
        var bytes = await reader.GetFieldValueAsync<byte[]>(payloadOrdinal, cancellationToken).ConfigureAwait(false);
        if (bytes.Length != reader.GetInt64(lengthOrdinal) || !CanonicalSha256(identity))
        {
            throw Corrupt();
        }
        return new(identity, bytes);
    }

    private static TransientCandidateV1? ParseCandidate(Payload? payload, Guid candidateId, Guid eventId)
    {
        if (payload is null)
        {
            return null;
        }
        if (!string.Equals(ComputeSha256(payload.Bytes), payload.Identity, StringComparison.Ordinal))
        {
            throw Corrupt();
        }
        var parsed = TransientContractJson.ParseCandidate(payload.Bytes);
        return parsed.Validation.IsValid && parsed.Value is { } candidate &&
            candidate.CandidateId == candidateId && candidate.EventId == eventId
                ? candidate
                : throw Corrupt();
    }

    private static TransientFinalizationReceiptV1? ParseFinalization(Payload? payload, Guid candidateId, Guid eventId)
    {
        if (payload is null)
        {
            return null;
        }
        var parsed = TransientCandidateDeliveryJson.ParseFinalization(payload.Bytes);
        return parsed.Validation.IsValid && parsed.Value is { } receipt &&
            receipt.CandidateId == candidateId && receipt.EventId == eventId &&
            string.Equals(receipt.ReceiptIdentitySha256, payload.Identity, StringComparison.Ordinal)
                ? receipt
                : throw Corrupt();
    }

    private static TransientCandidateSubmissionEnvelopeV1? ParseSubmission(
        Payload? payload,
        Guid candidateId,
        Guid eventId)
    {
        if (payload is null)
        {
            return null;
        }
        var parsed = TransientCandidateDeliveryJson.ParseSubmission(payload.Bytes);
        if (!parsed.Validation.IsValid || parsed.Value is not { } submission ||
            submission.CandidateId != candidateId || submission.EventId != eventId ||
            !string.Equals(submission.SubmissionIdentitySha256, payload.Identity, StringComparison.Ordinal))
        {
            throw Corrupt();
        }
        return submission;
    }

    private static TransientCandidateSubmissionAcknowledgementV1? ParseAcknowledgement(
        Payload? payload,
        Guid candidateId,
        Guid eventId)
    {
        if (payload is null)
        {
            return null;
        }
        if (!string.Equals(ComputeSha256(payload.Bytes), payload.Identity, StringComparison.Ordinal))
        {
            throw Corrupt();
        }
        var parsed = TransientCandidateDeliveryJson.ParseAcknowledgement(payload.Bytes);
        if (!parsed.Validation.IsValid || parsed.Value is not { } acknowledgement ||
            acknowledgement.CandidateId != candidateId || acknowledgement.EventId != eventId)
        {
            throw Corrupt();
        }
        return acknowledgement;
    }

    private static TransientCandidateExtractionDescriptorV1? ParseExtraction(
        Payload? payload,
        ProjectedRow row,
        TransientTemporalBackgroundKind expectedKind)
    {
        if (payload is null)
        {
            return null;
        }
        if (!string.Equals(ComputeSha256(payload.Bytes), payload.Identity, StringComparison.Ordinal))
        {
            throw Corrupt();
        }
        try
        {
            var extraction = TransientCandidateExtractionJson.Parse(payload.Bytes);
            return extraction.Background.Kind == expectedKind && extraction.Candidates.Any(candidate =>
                candidate.CandidateId == row.CandidateId && candidate.EventId == row.EventId)
                    ? extraction
                    : throw Corrupt();
        }
        catch (ArgumentException)
        {
            throw Corrupt();
        }
    }

    private static TransientCandidateExtractionDescriptorV1? ParseObservationExtraction(
        Payload? payload,
        Payload? causalPayload,
        ProjectedRow row)
    {
        if (payload is null)
        {
            return null;
        }
        var causalFallback = string.Equals(payload.Identity, causalPayload?.Identity, StringComparison.Ordinal) &&
            (row.EventState is "NeedsReview" or "Rejected" || row.Phase != "finalized");
        var expectedKind = causalFallback
                ? TransientTemporalBackgroundKind.CausalProvisional
                : TransientTemporalBackgroundKind.CenteredFinal;
        return ParseExtraction(payload, row, expectedKind);
    }

    private static TransientAssessmentExecutionDescriptorV1? ParseAssessment(Payload? payload, ProjectedRow row)
    {
        if (payload is null)
        {
            return null;
        }
        if (!string.Equals(ComputeSha256(payload.Bytes), payload.Identity, StringComparison.Ordinal))
        {
            throw Corrupt();
        }
        try
        {
            var assessment = TransientAssessmentJson.Parse(payload.Bytes);
            return assessment.EventId == row.EventId ? assessment : throw Corrupt();
        }
        catch (ArgumentException)
        {
            throw Corrupt();
        }
    }

    private static CameraAgentTransientOperatorCandidate ProjectSummary(ProjectedRow row) => new(
        row.CandidateId,
        row.EventId,
        row.CandidateState,
        row.EventState,
        row.Phase,
        row.CreatedUtc,
        row.UpdatedUtc,
        StageState(row.Causal is not null, row.Phase),
        StageState(row.Centered is not null, row.Phase),
        StageState(row.Assessment is not null, row.Phase),
        StageState(row.Finalization is not null, row.Phase));

    private static CameraAgentTransientCandidateEvidence ProjectCandidateEvidence(
        TransientCandidateV1? candidate,
        string phase) => new(
        candidate is null ? phase == "quarantined" ? "Absent" : "Pending" : "Available",
        candidate?.CreatedUtc,
        candidate?.CenterEvidenceId,
        candidate?.ContextSources.Count ?? 0,
        candidate?.Geometry is { } geometry
            ? new(
                geometry.CoordinateWidth,
                geometry.CoordinateHeight,
                geometry.Bounds.X,
                geometry.Bounds.Y,
                geometry.Bounds.Width,
                geometry.Bounds.Height,
                geometry.Polyline.Count)
            : null,
        candidate?.Features is { } features
            ? new(
                features.LengthPixels,
                features.MeanWidthPixels,
                features.MaximumWidthPixels,
                features.IntegratedSignalAdu,
                features.PeakSignalAdu,
                features.SaturatedSampleCount,
                features.FragmentCount)
            : null,
        candidate?.Reasons.Select(static reason => reason.Code).ToArray() ?? []);

    private static CameraAgentTransientExtractionEvidence ProjectExtraction(
        TransientCandidateExtractionDescriptorV1? extraction,
        string phase) => extraction is null
            ? new(StageState(false, phase))
            : new(
                "Available",
                extraction.CenteredContextConverged,
                extraction.OrderedSources.Count,
                extraction.Candidates.Count,
                extraction.ExtractionIdentitySha256);

    private static CameraAgentTransientAssessmentEvidence ProjectAssessment(
        TransientAssessmentExecutionDescriptorV1? execution,
        string phase) => execution is null
            ? new(StageState(false, phase), ReasonCodes: [])
            : new(
                "Available",
                execution.Assessment.AssessmentId,
                execution.Assessment.Authority.ToString(),
                execution.Assessment.Classification.ToString(),
                execution.Assessment.MeteorSeverity?.ToString(),
                execution.Assessment.ConfidenceMillionths,
                execution.Assessment.EvidenceObservationIds.Count,
                execution.Assessment.Reasons.Select(static reason => reason.Code).ToArray(),
                execution.ExecutionIdentitySha256);

    private static CameraAgentTransientFinalEvidence ProjectFinal(
        TransientFinalizationReceiptV1? receipt,
        string phase) => receipt is null
            ? new(StageState(false, phase))
            : new(
                "Available",
                receipt.Event.State.ToString(),
                receipt.Event.Version,
                receipt.Event.FirstObservedUtc,
                receipt.Event.LastObservedUtc,
                receipt.Event.Observations.Count,
                receipt.Event.Assessments.Count,
                receipt.ReceiptIdentitySha256);

    private static string StageState(bool available, string phase) => available
        ? "Available"
        : phase is "finalized" or "acknowledged" or "quarantined" ? "Absent" : "Pending";

    private static string ReadEventState(string value) => value switch
    {
        "pending" => "Pending",
        "provisional" => "Provisional",
        "validated" => "Validated",
        "rejected" => "Rejected",
        "needs_review" => "NeedsReview",
        _ => throw Corrupt()
    };

    private static string ReadPhase(string value) => value switch
    {
        "reserved" => "reserved",
        "candidate_persisted" => "candidate_persisted",
        "finalized" => "finalized",
        "handoff_pending" => "handoff_pending",
        "acknowledged" => "acknowledged",
        "quarantined" => "quarantined",
        _ => throw Corrupt()
    };

    private static DateTimeOffset ReadTime(long unixMilliseconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw Corrupt();
        }
    }

    private static string EncodeCursor(SummaryRow row)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{row.CreatedUtc.ToUnixTimeMilliseconds()}:{row.CandidateId:N}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static Cursor? DecodeCursor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (value.Length > 128)
        {
            throw new CameraAgentTransientOperatorQueryException("The cursor is invalid.");
        }
        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            var separator = decoded.IndexOf(':', StringComparison.Ordinal);
            if (separator < 1 || !long.TryParse(
                    decoded.AsSpan(0, separator),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var created) ||
                !Guid.TryParseExact(decoded[(separator + 1)..], "N", out var candidateId) ||
                candidateId == Guid.Empty)
            {
                throw new CameraAgentTransientOperatorQueryException("The cursor is invalid.");
            }
            _ = DateTimeOffset.FromUnixTimeMilliseconds(created);
            return new(created, candidateId.ToString("N"));
        }
        catch (CameraAgentTransientOperatorQueryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentOutOfRangeException)
        {
            throw new CameraAgentTransientOperatorQueryException("The cursor is invalid.");
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The interpolated value is a validated integer host option used only for SQLite PRAGMA configuration.")]
    private async ValueTask<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken)
    {
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, _databasePath);
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, string.Concat(_databasePath, "-wal"));
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, string.Concat(_databasePath, "-shm"));
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA query_only=ON; PRAGMA busy_timeout={checked(_busyTimeoutSeconds * 1000)};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async ValueTask<bool> RuntimeTableExistsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'transient_worker_candidates');";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "SQL is assembled only from fixed query fragments; cursor and limit values remain parameterized.")]
    private static SqliteCommand CreateSummaryCommand(
        SqliteConnection connection,
        Cursor? cursor,
        int pageSize,
        bool hasRuntime)
    {
        var command = connection.CreateCommand();
        var runtimeColumns = hasRuntime
            ? "w.causal_extraction_json IS NOT NULL, w.observation_extraction_json IS NOT NULL, w.assessment_execution_json IS NOT NULL"
            : "0, 0, 0";
        var runtimeJoin = hasRuntime
            ? "LEFT JOIN transient_worker_candidates w ON w.candidate_id = j.candidate_id"
            : string.Empty;
        var cursorPredicate = cursor is null
            ? string.Empty
            : "WHERE j.created_unix_ms < $cursor_created OR (j.created_unix_ms = $cursor_created AND j.candidate_id < $cursor_candidate)";
        command.CommandText = $"""
            SELECT j.candidate_id, j.event_id, j.state, j.phase,
                   j.created_unix_ms, j.updated_unix_ms,
                   j.candidate_state,
                   j.candidate_payload IS NOT NULL,
                   j.finalization_payload IS NOT NULL,
                   j.submission_payload IS NOT NULL,
                   j.acknowledgement_payload IS NOT NULL,
                   {runtimeColumns}
            FROM transient_candidates j
            {runtimeJoin}
            {cursorPredicate}
            ORDER BY j.created_unix_ms DESC, j.candidate_id DESC
            LIMIT $limit;
            """;
        if (cursor is not null)
        {
            command.Parameters.AddWithValue("$cursor_created", cursor.CreatedUnixMilliseconds);
            command.Parameters.AddWithValue("$cursor_candidate", cursor.CandidateId);
        }
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        return command;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "SQL is assembled only from fixed query fragments; the candidate identifier remains parameterized.")]
    private static SqliteCommand CreateDetailCommand(
        SqliteConnection connection,
        Guid candidateId,
        bool hasRuntime)
    {
        var command = connection.CreateCommand();
        var runtimeColumns = hasRuntime
            ? "w.causal_extraction_sha256, length(w.causal_extraction_json), w.causal_extraction_json, w.observation_extraction_sha256, length(w.observation_extraction_json), w.observation_extraction_json, w.assessment_execution_sha256, length(w.assessment_execution_json), w.assessment_execution_json"
            : "NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL";
        var runtimeJoin = hasRuntime
            ? "LEFT JOIN transient_worker_candidates w ON w.candidate_id = j.candidate_id"
            : string.Empty;
        command.CommandText = $"""
            SELECT j.candidate_id, j.event_id, j.state, j.phase,
                   j.created_unix_ms, j.updated_unix_ms,
                   j.candidate_payload_sha256, length(j.candidate_payload), j.candidate_payload,
                   j.finalization_receipt_identity_sha256, length(j.finalization_payload), j.finalization_payload,
                   j.submission_identity_sha256, length(j.submission_payload), j.submission_payload,
                   j.acknowledgement_payload_sha256, length(j.acknowledgement_payload), j.acknowledgement_payload,
                   {runtimeColumns}, j.candidate_state
            FROM transient_candidates j
            {runtimeJoin}
            WHERE j.candidate_id = $candidate
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
        return command;
    }

    private static bool CanonicalSha256(string value)
        => value.Length == 64 && value.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static string ComputeSha256(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload));

    private static InvalidDataException Corrupt()
        => new("Durable transient operator evidence is corrupt.");

    private sealed record Payload(string Identity, byte[] Bytes);
    private sealed record Cursor(long CreatedUnixMilliseconds, string CandidateId);
    private sealed record SummaryRow(
        CameraAgentTransientOperatorCandidate Candidate,
        DateTimeOffset CreatedUtc,
        Guid CandidateId);
    private sealed record ProjectedRow(
        Guid CandidateId,
        Guid EventId,
        string EventState,
        string Phase,
        DateTimeOffset CreatedUtc,
        DateTimeOffset UpdatedUtc,
        string CandidateState,
        TransientCandidateV1? Candidate,
        TransientFinalizationReceiptV1? Finalization,
        TransientCandidateExtractionDescriptorV1? Causal,
        TransientCandidateExtractionDescriptorV1? Centered,
        TransientAssessmentExecutionDescriptorV1? Assessment);
}
