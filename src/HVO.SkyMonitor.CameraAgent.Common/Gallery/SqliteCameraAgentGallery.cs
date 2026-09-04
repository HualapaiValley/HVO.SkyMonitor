using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal sealed class SqliteCameraAgentGallery : ICameraAgentGallery, ICameraAgentArchive
{
    internal const int DefaultPageSize = 24;
    internal const int MaximumPageSize = 100;
    internal const int MaximumProcessingNodesPerCapture = 64;
    internal const int MaximumArtifactsPerCapture = 128;
    private const int CursorVersion = 1;
    private const int MaximumDetailArtifacts = MaximumArtifactsPerCapture;
    private const long MaximumCloudAssessmentBytes = 4L * 1024 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static readonly HashSet<string> RawStates = new(StringComparer.Ordinal)
    {
        "committed",
        "missing_evidence",
        "quarantined"
    };

    private readonly string _root;
    private readonly string _databasePath;
    private readonly int _busyTimeoutSeconds;
    private readonly bool _centralIntegrationDisabled;
    private readonly SqliteCaptureProcessingStore _processingStore;
    private readonly ICameraAgentStorageResolver? _storageResolver;
    private readonly IObservingDayCalendarProvider _observingDays;

    public SqliteCameraAgentGallery(
        IOptions<CameraAgentHostOptions> options,
        SqliteCaptureProcessingStore processingStore,
        ICameraAgentStorageResolver? storageResolver = null,
        IObservingDayCalendarProvider? observingDays = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _processingStore = processingStore ?? throw new ArgumentNullException(nameof(processingStore));
        _storageResolver = storageResolver;
        _observingDays = observingDays ?? new FixedObservingDayCalendarProvider(ObservingDayCalendar.Create(null));
        _root = Path.GetFullPath(options.Value.RawIngressRoot);
        _databasePath = Path.Combine(_root, "journal", "raw-ingress.db");
        _busyTimeoutSeconds = options.Value.RawIngressSqliteBusyTimeoutSeconds;
        _centralIntegrationDisabled = options.Value.CentralIntegration.Mode == CentralIntegrationMode.Disabled;
    }

    public async ValueTask<CameraAgentGalleryPage> GetPageAsync(
        CameraAgentGalleryQuery query,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(query);
        var cursor = DecodeCursor(normalized.Cursor, normalized.FilterHash);
        await _processingStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await ReadPageRowsAsync(connection, normalized, cursor, cancellationToken).ConfigureAwait(false);
        var hasMore = candidates.Count > normalized.PageSize;
        if (hasMore)
        {
            candidates.RemoveAt(candidates.Count - 1);
        }
        var captures = await ProjectAsync(candidates, cancellationToken).ConfigureAwait(false);
        var nextCursor = hasMore && candidates.Count > 0
            ? EncodeCursor(candidates[^1], normalized.FilterHash)
            : null;
        return new CameraAgentGalleryPage(captures, nextCursor);
    }

    public async ValueTask<CameraAgentGalleryCapture?> GetCaptureAsync(
        Guid captureId,
        CancellationToken cancellationToken)
    {
        if (captureId == Guid.Empty)
        {
            return null;
        }
        await _processingStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            {RawSelectSql}
            WHERE raw.capture_id = $capture_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        var row = await ReadRawRowAsync(reader, cancellationToken).ConfigureAwait(false);
        var capture = (await ProjectAsync([row], cancellationToken).ConfigureAwait(false))[0];
        return capture with
        {
            Detail = await BuildDetailAsync(row, capture, cancellationToken).ConfigureAwait(false)
        };
    }

    private async ValueTask<CameraAgentGalleryCaptureDetail> BuildDetailAsync(
        RawGalleryRow row,
        CameraAgentGalleryCapture capture,
        CancellationToken cancellationToken)
    {
        var manifest = TryReadTrustedManifest(row);
        var descriptor = manifest?.Descriptor;
        var processingDetails = await _processingStore.ReadGalleryNodeDetailsAsync(
            row.CaptureId, cancellationToken).ConfigureAwait(false);
        var retention = await _processingStore.ReadGalleryRetentionStatesAsync(
            row.CaptureId, cancellationToken).ConfigureAwait(false);
        var delivery = await ReadDeliveryStatusesAsync(
            capture.Artifacts.Select(static artifact => artifact.ArtifactId).ToArray(), cancellationToken)
            .ConfigureAwait(false);
        var artifactStates = capture.Artifacts.Select(artifact => new CameraAgentGalleryArtifactState(
            artifact.ArtifactId,
            artifact.Role == FrameArtifactRole.Raw
                ? row.RetentionHold ? "Held" : "Released"
                : retention.TryGetValue(artifact.ArtifactId, out var held)
                    ? held ? "Held" : "Unpinned"
                    : "Unavailable",
             delivery.Availability,
             delivery.Availability != "Unavailable"
                 ? delivery.Statuses.TryGetValue(artifact.ArtifactId, out var statuses)
                     ? statuses
                     : []
                 : []))
            .ToArray();
        var nodeDetails = processingDetails.Select(detail => new CameraAgentGalleryProcessingNodeDetail(
            detail.NodeId,
            detail.Dependencies,
            detail.Attempt,
            detail.CompletedUtc,
            SanitizeFailureCategory(detail.Reason),
            detail.ProcessingProfileIdentitySha256,
            detail.StartedUtc,
            detail.Duration?.TotalMilliseconds,
            detail.Outcome?.ToString(),
            detail.Inputs?.Select(static input => new CameraAgentGalleryProcessingNodeInput(
                input.Ordinal, input.Kind, input.Name, input.ArtifactId, input.Role, input.Variant,
                input.RecipeIdentitySha256, input.SchemaVersion, input.IdentitySha256, input.Selected)).ToArray(),
            detail.InputsTruncated))
            .ToArray();

        return new CameraAgentGalleryCaptureDetail(
            descriptor is null ? "Unavailable" : "Available",
            manifest?.SchemaVersion,
            descriptor is null ? null : new CameraAgentGalleryLayout(
                descriptor.Layout.Width,
                descriptor.Layout.Height,
                descriptor.Layout.StrideBytes,
                descriptor.Layout.PixelFormat.ToString(),
                descriptor.Layout.ByteOrder.ToString(),
                descriptor.Layout.SampleDepthBits,
                descriptor.Layout.ContainerDepthBits,
                descriptor.Layout.Packing.ToString(),
                descriptor.Layout.CfaPattern.ToString(),
                descriptor.Layout.BlackLevel,
                descriptor.Layout.WhiteLevel,
                descriptor.Layout.ByteLength),
            descriptor is null ? null : new CameraAgentGalleryTiming(
                descriptor.Timing.RequestedStartUtc,
                descriptor.Timing.ExposureStartedUtc,
                descriptor.Timing.ExposureEndedUtc,
                descriptor.Timing.ReadoutCompletedUtc,
                descriptor.Timing.DurableIngressUtc,
                descriptor.Timing.SetpointAppliedUtc),
            descriptor is null ? null : new CameraAgentGalleryControls(
                descriptor.Controls.RequestedExposure.TotalMilliseconds,
                descriptor.Controls.EffectiveExposure.TotalMilliseconds,
                descriptor.Controls.RequestedGain,
                descriptor.Controls.EffectiveGain,
                descriptor.Controls.RequestedOffset,
                descriptor.Controls.EffectiveOffset,
                descriptor.Controls.TemperatureSetpointC,
                descriptor.Controls.EffectiveTemperatureC),
            row.RetentionHold,
            artifactStates,
            nodeDetails,
            await ReadCloudAssessmentAsync(row.CaptureId, cancellationToken).ConfigureAwait(false),
            descriptor?.Profiles.Processing);
    }

    private async ValueTask<CameraAgentGalleryCloudAssessment> ReadCloudAssessmentAsync(
        Guid captureId,
        CancellationToken cancellationToken)
    {
        var projection = await _processingStore.ReadGalleryNodesAsync(
            [captureId], MaximumProcessingNodesPerCapture, MaximumArtifactsPerCapture - 1, cancellationToken)
            .ConfigureAwait(false);
        var outputs = projection.Nodes.SelectMany(static node => node.Outputs)
            .Where(static output => output.Artifact.Role == FrameArtifactRole.Metadata &&
                string.Equals(output.Artifact.Recipe.Name, BuiltInProcessingRecipes.CloudAssessment, StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (outputs.Length == 0)
        {
            return UnavailableCloudAssessment("Unavailable");
        }
        if (outputs.Length != 1)
        {
            return UnavailableCloudAssessment("Malformed");
        }

        var output = outputs[0];
        var expectedLength = output.ProductManifest?.ByteLength ?? output.Descriptor?.Layout.ByteLength;
        if (expectedLength is null or < 1 or > MaximumCloudAssessmentBytes)
        {
            return UnavailableCloudAssessment("Malformed");
        }
        var lifecycleGate = StorageLifecycleLock.ForRoot(_root);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = ResolveSafePath(output.PayloadRelativePath);
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, path);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != expectedLength.Value)
            {
                return UnavailableCloudAssessment("Unavailable");
            }
            var payload = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    PayloadChecksum.ComputeSha256(payload),
                    output.Artifact.ChecksumSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                return UnavailableCloudAssessment("Malformed");
            }
            var parsed = CloudAssessmentJson.Parse(payload);
            if (!parsed.Validation.IsValid || parsed.Assessment is not { } assessment)
            {
                return UnavailableCloudAssessment("Malformed");
            }
            return new CameraAgentGalleryCloudAssessment(
                "Available",
                assessment.Status.ToString(),
                assessment.Quality.ToString(),
                assessment.CoverageMillionths,
                assessment.ConfidenceMillionths,
                assessment.ReasonCodes.ToArray(),
                assessment.Mask is not null,
                assessment.Mask?.Width,
                assessment.Mask?.Height,
                assessment.Mask is null ? null : PayloadChecksum.ComputeSha256(assessment.Mask.Bits.Span));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        {
            return UnavailableCloudAssessment("Malformed");
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Generated placeholders contain bounded integer ordinals and all artifact identities remain parameterized.")]
    private async ValueTask<DeliveryStatusLookup> ReadDeliveryStatusesAsync(
        Guid[] artifactIds,
        CancellationToken cancellationToken)
    {
        if (artifactIds.Length is 0 or > MaximumDetailArtifacts)
        {
            return new("Unavailable", new Dictionary<Guid, IReadOnlyList<CameraAgentGalleryArtifactDelivery>>());
        }
        if (_centralIntegrationDisabled)
        {
            return new("Disabled", new Dictionary<Guid, IReadOnlyList<CameraAgentGalleryArtifactDelivery>>());
        }
        IReadOnlyList<CameraAgentStorageLocation> locations = _storageResolver is null
            ? new[] { new CameraAgentStorageLocation("raw-ingress", _root) }
            : await _storageResolver.GetUploadLocationsAsync(cancellationToken).ConfigureAwait(false);
        if (locations.Count == 0)
        {
            return new("Unavailable", new Dictionary<Guid, IReadOnlyList<CameraAgentGalleryArtifactDelivery>>());
        }
        var statuses = artifactIds.ToDictionary(
            static artifactId => artifactId,
            static _ => new List<CameraAgentGalleryArtifactDelivery>());
        var availableRoots = 0;
        foreach (var location in locations)
        {
            var root = Path.GetFullPath(location.Root);
            var path = Path.Combine(root, "outbox", "artifact-outbox.db");
            if (!File.Exists(path))
            {
                foreach (var artifactId in artifactIds)
                {
                    statuses[artifactId].Add(new(location.Alias, "Unavailable"));
                }
                continue;
            }
            try
            {
                RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString());
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                using var command = connection.CreateCommand();
                var placeholders = string.Join(", ", Enumerable.Range(0, artifactIds.Length).Select(static index => $"$artifact{index}"));
                command.CommandText = $"SELECT artifact_id, status FROM artifact_outbox_records WHERE artifact_id IN ({placeholders}) ORDER BY record_id;";
                for (var index = 0; index < artifactIds.Length; index++)
                {
                    command.Parameters.AddWithValue($"$artifact{index}", artifactIds[index].ToString("N"));
                }
                var rootStatuses = new Dictionary<Guid, HashSet<string>>();
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (Guid.TryParseExact(reader.GetString(0), "N", out var artifactId))
                    {
                        if (!rootStatuses.TryGetValue(artifactId, out var values))
                        {
                            values = new(StringComparer.Ordinal);
                            rootStatuses.Add(artifactId, values);
                        }
                        values.Add(SanitizeDeliveryStatus(reader.GetString(1)));
                    }
                }
                foreach (var artifactId in artifactIds)
                {
                    if (rootStatuses.TryGetValue(artifactId, out var values))
                    {
                        statuses[artifactId].AddRange(values.Select(status =>
                            new CameraAgentGalleryArtifactDelivery(location.Alias, status)));
                    }
                    else
                    {
                        statuses[artifactId].Add(new(location.Alias, "NotQueued"));
                    }
                }
                availableRoots++;
            }
            catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
            {
                foreach (var artifactId in artifactIds)
                {
                    statuses[artifactId].Add(new(location.Alias, "Unavailable"));
                }
            }
        }
        var availability = availableRoots == locations.Count ? "Available" : availableRoots == 0 ? "Unavailable" : "Partial";
        return new(availability, statuses.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<CameraAgentGalleryArtifactDelivery>)pair.Value.ToArray()));
    }

    private string ResolveSafePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\\', StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath) || relativePath.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidDataException("Artifact path is invalid.");
        }
        var fullPath = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = string.Concat(Path.TrimEndingDirectorySeparator(_root), Path.DirectorySeparatorChar);
        if (!fullPath.StartsWith(prefix, PathComparison))
        {
            throw new InvalidDataException("Artifact path is invalid.");
        }
        return fullPath;
    }

    private static string? SanitizeFailureCategory(string? reason)
        => reason is { Length: > 0 and <= 64 } &&
           (reason.StartsWith("processing.", StringComparison.Ordinal) ||
             reason.StartsWith("cloud.", StringComparison.Ordinal) ||
             reason.StartsWith("environment.", StringComparison.Ordinal) ||
             reason.StartsWith("calibration.", StringComparison.Ordinal)) &&
           reason.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-')
            ? reason
            : reason is null ? null : "unavailable";

    private static string SanitizeDeliveryStatus(string status) => status switch
    {
        "pending" => "Pending",
        "leased" => "Leased",
        "retry" => "Retry",
        "acknowledged" => "Acknowledged",
        "quarantined" => "Quarantined",
        "abandoned" => "Abandoned",
        _ => "Unknown"
    };

    private static CameraAgentGalleryCloudAssessment UnavailableCloudAssessment(string availability)
        => new(availability, null, null, null, null, [], null, null, null, null);

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "All appended SQL clauses are fixed statements selected by normalized filters; values remain parameterized.")]
    private static async Task<List<RawGalleryRow>> ReadPageRowsAsync(
        SqliteConnection connection,
        NormalizedQuery query,
        GalleryCursor? cursor,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        var sql = new StringBuilder(RawSelectSql).AppendLine().AppendLine("WHERE 1 = 1");
        AppendFilterClauses(sql, command, query);
        if (cursor is not null)
        {
            sql.AppendLine("""
                AND (
                    raw.capture_sequence < $cursor_sequence
                    OR (raw.capture_sequence = $cursor_sequence AND raw.raw_capture_row_id < $cursor_row)
                )
                """);
            command.Parameters.AddWithValue("$cursor_sequence", cursor.CaptureSequence);
            command.Parameters.AddWithValue("$cursor_row", cursor.RawRowId);
        }
        sql.AppendLine("ORDER BY raw.capture_sequence DESC, raw.raw_capture_row_id DESC");
        sql.AppendLine("LIMIT $limit;");
        command.Parameters.AddWithValue("$limit", query.PageSize + 1);
        command.CommandText = sql.ToString();

        var rows = new List<RawGalleryRow>(query.PageSize + 1);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(await ReadRawRowAsync(reader, cancellationToken).ConfigureAwait(false));
        }
        return rows;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "All appended SQL clauses are fixed statements selected by normalized filters; values remain parameterized.")]
    private static void AppendFilterClauses(StringBuilder sql, SqliteCommand command, NormalizedQuery query)
    {
        if (query.FromUnixMilliseconds is { } from)
        {
            sql.AppendLine("AND raw.exposure_started_unix_ms >= $from");
            command.Parameters.AddWithValue("$from", from);
        }
        if (query.ToUnixMilliseconds is { } to)
        {
            sql.AppendLine("AND raw.exposure_started_unix_ms <= $to");
            command.Parameters.AddWithValue("$to", to);
        }
        if (query.MinimumSequence is { } minimumSequence)
        {
            sql.AppendLine("AND raw.capture_sequence >= $minimum_sequence");
            command.Parameters.AddWithValue("$minimum_sequence", minimumSequence);
        }
        if (query.MaximumSequence is { } maximumSequence)
        {
            sql.AppendLine("AND raw.capture_sequence <= $maximum_sequence");
            command.Parameters.AddWithValue("$maximum_sequence", maximumSequence);
        }
        if (query.RawState is not null)
        {
            sql.AppendLine("AND raw.state = $raw_state");
            command.Parameters.AddWithValue("$raw_state", query.RawState);
        }
        if (query.EvidenceOrigin is { } evidenceOrigin)
        {
            sql.AppendLine("AND raw.evidence_origin = $evidence_origin");
            command.Parameters.AddWithValue("$evidence_origin", evidenceOrigin.ToString());
        }
        if (query.ProcessingRole is { } processingRole)
        {
            sql.AppendLine("""
                AND EXISTS (
                    SELECT 1 FROM processing_outputs output
                    WHERE output.capture_id = raw.capture_id AND output.role = $processing_role
                      AND (NOT EXISTS (SELECT 1 FROM processing_execution_outputs association
                                       WHERE association.output_identity_sha256 = output.output_identity_sha256)
                           OR EXISTS (SELECT 1 FROM processing_execution_outputs association
                                      WHERE association.output_identity_sha256 = output.output_identity_sha256
                                        AND association.published_flag = 1)))
                """);
            command.Parameters.AddWithValue("$processing_role", processingRole.ToString());
        }
        if (query.Recipe is not null)
        {
            sql.AppendLine("""
                AND (
                    EXISTS (SELECT 1 FROM processing_nodes node
                            WHERE node.capture_id = raw.capture_id AND upper(node.recipe_name) = $recipe)
                     OR EXISTS (SELECT 1 FROM processing_outputs output
                                  WHERE output.capture_id = raw.capture_id AND output.recipe_identity_sha256 = $recipe_identity
                                    AND (NOT EXISTS (SELECT 1 FROM processing_execution_outputs association
                                                     WHERE association.output_identity_sha256 = output.output_identity_sha256)
                                         OR EXISTS (SELECT 1 FROM processing_execution_outputs association
                                                    WHERE association.output_identity_sha256 = output.output_identity_sha256
                                                      AND association.published_flag = 1)))
                )
                """);
            command.Parameters.AddWithValue("$recipe", query.Recipe);
            command.Parameters.AddWithValue("$recipe_identity", query.Recipe.ToUpperInvariant());
        }
        if (query.ProcessingStatus is not null)
        {
            sql.AppendLine("AND EXISTS (SELECT 1 FROM processing_nodes node WHERE node.capture_id = raw.capture_id AND node.status = $processing_status)");
            command.Parameters.AddWithValue("$processing_status", query.ProcessingStatus);
        }
    }

    public ObservingDayCalendar ObservingDays => _observingDays.Current;

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The per-day count statement is fixed apart from normalized filter clauses; values remain parameterized.")]
    public async ValueTask<CameraAgentGalleryCalendar> GetCalendarAsync(
        CameraAgentGalleryCalendarQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var calendar = _observingDays.Current;
        IReadOnlyList<ObservingDay> days;
        try
        {
            days = calendar.Range(query.FromDate, query.ToDate);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new CameraAgentGalleryQueryException(exception.Message);
        }
        // The calendar owns the time range; other filters apply within each day.
        var filters = (query.Filters ?? new CameraAgentGalleryQuery()) with { FromUtc = null, ToUtc = null, Cursor = null, PageSize = null };
        var normalized = Normalize(filters);
        await _processingStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        // One deferred read transaction gives every day the same WAL snapshot.
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes deferred transactions only through the synchronous overload.
        using var snapshot = connection.BeginTransaction(deferred: true);
#pragma warning restore CA1849
        var result = new CameraAgentGalleryCalendarDay[days.Count];
        for (var index = 0; index < days.Count; index++)
        {
            var day = days[index];
            var start = day.StartUtc.ToUnixTimeMilliseconds();
            var end = day.EndUtc.ToUnixTimeMilliseconds();
            long captures;
            DateTimeOffset? first = null;
            DateTimeOffset? last = null;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = snapshot;
                // The exposure-time index is pinned so a leading-equality filter
                // index cannot win the plan and scan the whole state set per day.
                var sql = new StringBuilder("SELECT COUNT(*), MIN(raw.exposure_started_unix_ms), MAX(raw.exposure_started_unix_ms)")
                    .AppendLine()
                    .AppendLine("FROM raw_captures raw INDEXED BY ix_raw_captures_gallery_time")
                    .AppendLine("WHERE raw.exposure_started_unix_ms >= $day_start AND raw.exposure_started_unix_ms < $day_end");
                command.Parameters.AddWithValue("$day_start", start);
                command.Parameters.AddWithValue("$day_end", end);
                AppendFilterClauses(sql, command, normalized);
                command.CommandText = sql.ToString();
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                captures = reader.GetInt64(0);
                if (captures > 0)
                {
                    first = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
                    last = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
                }
            }
            long candidates;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = snapshot;
                command.CommandText = "SELECT COUNT(*) FROM transient_candidates WHERE created_unix_ms >= $day_start AND created_unix_ms < $day_end;";
                command.Parameters.AddWithValue("$day_start", start);
                command.Parameters.AddWithValue("$day_end", end);
                candidates = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
            }
            result[index] = new CameraAgentGalleryCalendarDay(day, captures, candidates, first, last);
        }
        return new CameraAgentGalleryCalendar(calendar.TimeZoneId, calendar.TimeZoneFallback, result);
    }

    public async ValueTask<CameraAgentGalleryNeighbours?> GetNeighboursAsync(
        Guid captureId,
        CameraAgentGalleryQuery filters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filters);
        if (captureId == Guid.Empty)
        {
            return null;
        }
        var normalized = Normalize(filters with { Cursor = null, PageSize = null });
        await _processingStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        long sequence;
        long rowId;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT capture_sequence, raw_capture_row_id FROM raw_captures WHERE capture_id = $capture_id LIMIT 1;";
            command.Parameters.AddWithValue("$capture_id", captureId.ToString("N"));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            sequence = reader.GetInt64(0);
            rowId = reader.GetInt64(1);
        }
        var newer = await ReadNeighbourAsync(connection, normalized, sequence, rowId, newer: true, cancellationToken).ConfigureAwait(false);
        var older = await ReadNeighbourAsync(connection, normalized, sequence, rowId, newer: false, cancellationToken).ConfigureAwait(false);
        return new CameraAgentGalleryNeighbours(captureId, newer, older);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The direction clauses are fixed statements; values remain parameterized.")]
    private static async Task<Guid?> ReadNeighbourAsync(
        SqliteConnection connection,
        NormalizedQuery query,
        long sequence,
        long rowId,
        bool newer,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        var sql = new StringBuilder("SELECT raw.capture_id FROM raw_captures raw").AppendLine().AppendLine("WHERE 1 = 1");
        AppendFilterClauses(sql, command, query);
        sql.AppendLine(newer
            ? "AND (raw.capture_sequence > $sequence OR (raw.capture_sequence = $sequence AND raw.raw_capture_row_id > $row))"
            : "AND (raw.capture_sequence < $sequence OR (raw.capture_sequence = $sequence AND raw.raw_capture_row_id < $row))");
        sql.AppendLine(newer
            ? "ORDER BY raw.capture_sequence ASC, raw.raw_capture_row_id ASC"
            : "ORDER BY raw.capture_sequence DESC, raw.raw_capture_row_id DESC");
        sql.AppendLine("LIMIT 1;");
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$row", rowId);
        command.CommandText = sql.ToString();
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is string text ? Guid.ParseExact(text, "N") : null;
    }

    internal const string MaterializationNodePrefix = "gallery-materialization-";
    private const int ProductCursorVersion = 1;
    private const int MaximumProductPredecessors = 8;
    private static readonly HashSet<string> ProductAvailabilities = new(StringComparer.Ordinal) { "Available", "Missing", "Quarantined" };
    private static readonly HashSet<string> ProductKinds = new(StringComparer.Ordinal) { "PixelData", "Metadata" };

    // Outputs of an unpublished replay execution stay invisible, exactly as in
    // the capture gallery, so a product never appears before it is published.
    private const string ProductVisibilitySql = """
        AND (NOT EXISTS (SELECT 1 FROM processing_execution_outputs association
                         WHERE association.output_identity_sha256 = output.output_identity_sha256)
             OR EXISTS (SELECT 1 FROM processing_execution_outputs association
                        WHERE association.output_identity_sha256 = output.output_identity_sha256
                          AND association.published_flag = 1))
        """;

    private const string ProductRowColumnsSql = """
        SELECT output.output_identity_sha256, output.committed_unix_ms,
               (SELECT execution.execution_class FROM processing_execution_outputs association
                JOIN processing_executions execution ON execution.execution_id = association.execution_id
                WHERE association.output_identity_sha256 = output.output_identity_sha256
                ORDER BY association.published_flag DESC, execution.execution_id DESC LIMIT 1)
        """;

    // Product pages ride the two partial retention indexes that already exist
    // in the shipped schema, so no schema change is needed: available outputs
    // walk their commit-ordered index directly, and unavailable outputs (a
    // small set that retention expires) are read through their own index and
    // sorted. The detail lookup keys on the unique artifact id instead.
    private const string ProductAvailablePageSelectSql = ProductRowColumnsSql + """

        FROM processing_outputs output INDEXED BY ix_processing_outputs_retention_available
        """;

    private const string ProductUnavailablePageSelectSql = ProductRowColumnsSql + """

        FROM processing_outputs output INDEXED BY ix_processing_outputs_retention_unavailable
        """;

    private const string ProductRowSelectSql = ProductRowColumnsSql + """

        FROM processing_outputs output
        """;

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "All appended SQL clauses are fixed statements selected by normalized filters; values remain parameterized.")]
    public async ValueTask<CameraAgentProductPage> GetProductPageAsync(
        CameraAgentProductQuery query,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeProducts(query);
        var cursor = DecodeProductCursor(normalized.Cursor, normalized.FilterHash);
        await _processingStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        var keys = new List<ProductKey>(normalized.PageSize + 1);
        using (var command = connection.CreateCommand())
        {
            // The literal availability term is exactly the partial index predicate, so the pinned index always applies.
            var available = string.Equals(normalized.Availability, "Available", StringComparison.Ordinal);
            var sql = new StringBuilder(available ? ProductAvailablePageSelectSql : ProductUnavailablePageSelectSql)
                .AppendLine()
                .AppendLine(available ? "WHERE output.availability_state = 'Available'" : "WHERE output.availability_state <> 'Available'")
                .Append(ProductVisibilitySql).AppendLine();
            AppendProductFilterClauses(sql, command, normalized);
            if (cursor is not null)
            {
                sql.AppendLine("""
                    AND (output.committed_unix_ms < $cursor_committed
                         OR (output.committed_unix_ms = $cursor_committed AND output.output_identity_sha256 < $cursor_identity))
                    """);
                command.Parameters.AddWithValue("$cursor_committed", cursor.CommittedUnixMilliseconds);
                command.Parameters.AddWithValue("$cursor_identity", cursor.OutputIdentitySha256);
            }
            sql.AppendLine("ORDER BY output.committed_unix_ms DESC, output.output_identity_sha256 DESC");
            sql.AppendLine("LIMIT $limit;");
            command.Parameters.AddWithValue("$limit", normalized.PageSize + 1);
            command.CommandText = sql.ToString();
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                keys.Add(new ProductKey(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? "Unassociated" : reader.GetString(2)));
            }
        }
        var hasMore = keys.Count > normalized.PageSize;
        if (hasMore)
        {
            keys.RemoveAt(keys.Count - 1);
        }
        var products = await HydrateProductsAsync(connection, keys, cancellationToken).ConfigureAwait(false);
        var nextCursor = hasMore && keys.Count > 0 ? EncodeProductCursor(keys[^1], normalized.FilterHash) : null;
        // A durable record the reader cannot decode is omitted, and the page
        // says so rather than shrinking silently.
        return new CameraAgentProductPage(products, nextCursor, keys.Count - products.Count);
    }

    public async ValueTask<CameraAgentProductDetail?> GetProductAsync(
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        if (artifactId == Guid.Empty)
        {
            return null;
        }
        await _processingStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenReadOnlyAsync(cancellationToken).ConfigureAwait(false);
        ProductKey? key = null;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                {ProductRowSelectSql}
                WHERE output.artifact_id = $artifact_id
                {ProductVisibilitySql}
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$artifact_id", artifactId.ToString("N"));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                key = new ProductKey(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? "Unassociated" : reader.GetString(2));
            }
        }
        if (key is null)
        {
            return null;
        }
        var products = await HydrateProductsAsync(connection, [key], cancellationToken).ConfigureAwait(false);
        if (products.Count == 0)
        {
            return null;
        }
        var product = products[0];
        var sources = await ReadProductSourcesAsync(connection, product.OutputIdentitySha256, cancellationToken).ConfigureAwait(false);
        var node = await _processingStore.ReadNodeAsync(product.CaptureId, product.NodeId, cancellationToken).ConfigureAwait(false);
        var predecessors = await ReadProductPredecessorsAsync(connection, product, cancellationToken).ConfigureAwait(false);
        DateTimeOffset exposureStartedUtc;
        string? rigId;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                {RawSelectSql}
                WHERE raw.capture_id = $capture_id
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$capture_id", product.CaptureId.ToString("N"));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            var row = await ReadRawRowAsync(reader, cancellationToken).ConfigureAwait(false);
            exposureStartedUtc = DateTimeOffset.FromUnixTimeMilliseconds(row.ExposureUnixMilliseconds);
            // Only a manifest that verifies against the journal row is trusted for facts.
            rigId = TryReadTrustedManifest(row)?.Descriptor.Capture.RigId;
        }
        return new CameraAgentProductDetail(
            product,
            exposureStartedUtc,
            _observingDays.Current.Resolve(exposureStartedUtc),
            rigId,
            sources.Sources,
            sources.Truncated,
            node is null
                ? null
                : new CameraAgentProductNode(
                    node.NodeId,
                    node.Status.ToString(),
                    node.Attempt,
                    node.StartedUtc,
                    node.CompletedUtc,
                    node.Duration is { } duration ? (long)duration.TotalMilliseconds : null,
                    node.Outcome?.ToString()),
            predecessors);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Generated placeholders contain only bounded ordinals; identities remain parameterized.")]
    private static async Task<IReadOnlyList<CameraAgentProduct>> HydrateProductsAsync(
        SqliteConnection connection,
        IReadOnlyList<ProductKey> keys,
        CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return [];
        }
        using var command = connection.CreateCommand();
        var placeholders = new string[keys.Count];
        for (var index = 0; index < keys.Count; index++)
        {
            placeholders[index] = $"$identity{index}";
            command.Parameters.AddWithValue(placeholders[index], keys[index].OutputIdentitySha256);
        }
        command.CommandText = $"""
            SELECT output_identity_sha256, artifact_id, payload_relative_path, sidecar_relative_path,
                   descriptor_json, capture_id, agent_id, node_id, role, variant, recipe_identity_sha256,
                   algorithms_json, compatibility_json, total_integration_ticks, capture_sequence,
                   product_kind, product_schema_version, content_identity_sha256,
                   availability_state, availability_reason, frame_artifact_recipe_version
            FROM processing_outputs
            WHERE output_identity_sha256 IN ({string.Join(", ", placeholders)});
            """;
        var rows = await SqliteCaptureProcessingStore.ReadOutputRowsAsync(command, cancellationToken, skipInvalid: true).ConfigureAwait(false);
        var byIdentity = rows.ToDictionary(static row => row.Output.OutputIdentitySha256, StringComparer.Ordinal);
        var products = new List<CameraAgentProduct>(keys.Count);
        foreach (var key in keys)
        {
            if (!byIdentity.TryGetValue(key.OutputIdentitySha256, out var row))
            {
                continue;
            }
            var output = row.Output;
            var artifact = output.Artifact;
            var encoded = output.ProductManifest as DurableEncodedProductManifestV2;
            products.Add(new CameraAgentProduct(
                artifact.ArtifactId,
                output.OutputIdentitySha256,
                row.CaptureId,
                output.CaptureSequence,
                output.Capture.AgentId,
                row.NodeId,
                artifact.Role,
                artifact.Variant,
                DateTimeOffset.FromUnixTimeMilliseconds(key.CommittedUnixMilliseconds),
                artifact.CreatedUtc,
                artifact.MediaType,
                artifact.ChecksumSha256,
                output.Descriptor?.Layout.ByteLength ?? output.ProductManifest?.ByteLength,
                new CameraAgentGalleryRecipe(
                    artifact.Recipe.Name,
                    artifact.Recipe.SemanticVersion,
                    artifact.Recipe.ImplementationVersion,
                    artifact.Recipe.OptionsSha256,
                    output.RecipeIdentitySha256),
                output.Algorithms.Select(static algorithm => new CameraAgentGalleryAlgorithm(algorithm.Name, algorithm.Version)).ToArray(),
                output.ProductKind?.ToString(),
                output.ProductSchemaVersion,
                output.ContentIdentitySha256,
                output.AvailabilityState,
                output.AvailabilityReason,
                output.TotalIntegration,
                artifact.SourceArtifactIds.Count,
                encoded?.EncodedWidth,
                encoded?.EncodedHeight,
                key.ExecutionClass,
                row.NodeId.StartsWith(MaterializationNodePrefix, StringComparison.Ordinal)));
        }
        return products;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Generated placeholders contain only bounded ordinals; artifact identities remain parameterized.")]
    private async Task<(IReadOnlyList<CameraAgentProductSource> Sources, bool Truncated)> ReadProductSourcesAsync(
        SqliteConnection connection,
        string outputIdentitySha256,
        CancellationToken cancellationToken)
    {
        var durable = await _processingStore.ReadOutputSourcesAsync(
            outputIdentitySha256, SqliteCaptureProcessingStore.MaximumOutputSourceCount, cancellationToken).ConfigureAwait(false);
        var truncated = durable.Count >= SqliteCaptureProcessingStore.MaximumOutputSourceCount;
        var resolved = new Dictionary<Guid, (Guid CaptureId, long CaptureSequence, FrameArtifactRole? Role)>();
        // One batched lookup resolves every source against the raw captures and
        // the earlier outputs; both artifact-id columns are unique indexes.
        for (var offset = 0; offset < durable.Count; offset += 64)
        {
            var batch = durable.Skip(offset).Take(64).ToArray();
            using var command = connection.CreateCommand();
            var placeholders = new string[batch.Length];
            for (var index = 0; index < batch.Length; index++)
            {
                placeholders[index] = $"$artifact{index}";
                command.Parameters.AddWithValue(placeholders[index], batch[index].ArtifactId.ToString("N"));
            }
            var list = string.Join(", ", placeholders);
            command.CommandText = $"""
                SELECT raw_artifact_id, capture_id, capture_sequence, 'Raw' FROM raw_captures WHERE raw_artifact_id IN ({list})
                UNION ALL
                SELECT artifact_id, capture_id, capture_sequence, role FROM processing_outputs WHERE artifact_id IN ({list});
                """;
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                resolved.TryAdd(
                    Guid.ParseExact(reader.GetString(0), "N"),
                    (Guid.ParseExact(reader.GetString(1), "N"), reader.GetInt64(2),
                        Enum.TryParse<FrameArtifactRole>(reader.GetString(3), out var role) ? role : null));
            }
        }
        var sources = new List<CameraAgentProductSource>(durable.Count);
        foreach (var source in durable)
        {
            sources.Add(resolved.TryGetValue(source.ArtifactId, out var match)
                ? new CameraAgentProductSource(source.Ordinal, source.ArtifactId, match.Role, match.CaptureId, match.CaptureSequence)
                : new CameraAgentProductSource(source.Ordinal, source.ArtifactId, null, null, null));
        }
        return (sources, truncated);
    }

    private static async Task<IReadOnlyList<CameraAgentProductPredecessor>> ReadProductPredecessorsAsync(
        SqliteConnection connection,
        CameraAgentProduct product,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT output.artifact_id, output.output_identity_sha256, output.committed_unix_ms, output.availability_state
            FROM processing_outputs output
            WHERE output.capture_id = $capture_id AND output.node_id = $node_id
              AND output.output_identity_sha256 <> $identity
              {ProductVisibilitySql}
            ORDER BY output.committed_unix_ms DESC, output.output_identity_sha256 DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$capture_id", product.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$node_id", product.NodeId);
        command.Parameters.AddWithValue("$identity", product.OutputIdentitySha256);
        command.Parameters.AddWithValue("$limit", MaximumProductPredecessors);
        var predecessors = new List<CameraAgentProductPredecessor>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            predecessors.Add(new CameraAgentProductPredecessor(
                Guid.ParseExact(reader.GetString(0), "N"),
                reader.GetString(1),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                reader.GetString(3)));
        }
        return predecessors;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "All appended SQL clauses are fixed statements selected by normalized filters; values remain parameterized.")]
    private static void AppendProductFilterClauses(StringBuilder sql, SqliteCommand command, NormalizedProductQuery query)
    {
        if (query.Role is { } role)
        {
            sql.AppendLine("AND output.role = $role");
            command.Parameters.AddWithValue("$role", role.ToString());
        }
        if (query.ProductKind is not null)
        {
            sql.AppendLine("AND output.product_kind = $product_kind");
            command.Parameters.AddWithValue("$product_kind", query.ProductKind);
        }
        if (query.Recipe is not null)
        {
            sql.AppendLine("AND output.recipe_identity_sha256 = $recipe_identity");
            command.Parameters.AddWithValue("$recipe_identity", query.Recipe);
        }
        if (!string.Equals(query.Availability, "Available", StringComparison.Ordinal))
        {
            // The page select already carries the literal partial-index term; this narrows within it.
            sql.AppendLine("AND output.availability_state = $availability");
            command.Parameters.AddWithValue("$availability", query.Availability);
        }
        if (query.FromUnixMilliseconds is { } from)
        {
            sql.AppendLine("AND output.committed_unix_ms >= $from");
            command.Parameters.AddWithValue("$from", from);
        }
        if (query.ToUnixMilliseconds is { } to)
        {
            sql.AppendLine("AND output.committed_unix_ms <= $to");
            command.Parameters.AddWithValue("$to", to);
        }
    }

    private static NormalizedProductQuery NormalizeProducts(CameraAgentProductQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = query.PageSize ?? DefaultPageSize;
        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new CameraAgentGalleryQueryException($"Page size must be between 1 and {MaximumPageSize}.");
        }
        var from = query.FromUtc?.ToUniversalTime().ToUnixTimeMilliseconds();
        var to = query.ToUtc?.ToUniversalTime().ToUnixTimeMilliseconds();
        if (from > to)
        {
            throw new CameraAgentGalleryQueryException("The time range is invalid.");
        }
        var productKind = string.IsNullOrWhiteSpace(query.ProductKind) ? null : query.ProductKind.Trim();
        if (productKind is not null && !ProductKinds.Contains(productKind))
        {
            throw new CameraAgentGalleryQueryException("The product kind filter is invalid.");
        }
        // Retained available products are the default view; missing and quarantined
        // outputs are an explicit filter over the small set retention has not yet expired.
        var availability = string.IsNullOrWhiteSpace(query.Availability) ? "Available" : query.Availability.Trim();
        if (!ProductAvailabilities.Contains(availability))
        {
            throw new CameraAgentGalleryQueryException("The availability filter is invalid.");
        }
        var recipe = string.IsNullOrWhiteSpace(query.Recipe) ? null : query.Recipe.Trim().ToUpperInvariant();
        if (recipe is not null && (recipe.Length != 64 || !recipe.All(Uri.IsHexDigit)))
        {
            throw new CameraAgentGalleryQueryException("The recipe filter must be a recipe identity.");
        }
        var filterBytes = JsonSerializer.SerializeToUtf8Bytes(new { query.Role, productKind, recipe, availability, from, to }, SerializerOptions);
        return new NormalizedProductQuery(
            pageSize,
            query.Cursor,
            query.Role,
            productKind,
            recipe,
            availability,
            from,
            to,
            Convert.ToHexString(SHA256.HashData(filterBytes)));
    }

    private static string EncodeProductCursor(ProductKey key, string filterHash)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new ProductCursor(ProductCursorVersion, filterHash, key.CommittedUnixMilliseconds, key.OutputIdentitySha256), SerializerOptions);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static ProductCursor? DecodeProductCursor(string? value, string filterHash)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (value.Length > 512)
        {
            throw new CameraAgentGalleryQueryException("The cursor is invalid.");
        }
        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            var cursor = JsonSerializer.Deserialize<ProductCursor>(Convert.FromBase64String(normalized), SerializerOptions);
            if (cursor is null || cursor.Version != ProductCursorVersion || cursor.OutputIdentitySha256 is not { Length: 64 } ||
                !cursor.OutputIdentitySha256.All(Uri.IsHexDigit) || !string.Equals(cursor.FilterHash, filterHash, StringComparison.Ordinal))
            {
                throw new CameraAgentGalleryQueryException("The cursor does not match the requested filters.");
            }
            return cursor;
        }
        catch (CameraAgentGalleryQueryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or NotSupportedException)
        {
            throw new CameraAgentGalleryQueryException("The cursor is invalid.");
        }
    }

    private sealed record ProductKey(string OutputIdentitySha256, long CommittedUnixMilliseconds, string ExecutionClass);

    private sealed record ProductCursor(int Version, string FilterHash, long CommittedUnixMilliseconds, string OutputIdentitySha256);

    private sealed record NormalizedProductQuery(
        int PageSize,
        string? Cursor,
        FrameArtifactRole? Role,
        string? ProductKind,
        string? Recipe,
        string Availability,
        long? FromUnixMilliseconds,
        long? ToUnixMilliseconds,
        string FilterHash);

    private async Task<IReadOnlyList<CameraAgentGalleryCapture>> ProjectAsync(
        IReadOnlyList<RawGalleryRow> rows,
        CancellationToken cancellationToken)
    {
        DurableGalleryProcessingProjection processing;
        var unavailable = false;
        IReadOnlySet<Guid> canonicalScenes;
        var canonicalSceneQueryUnavailable = false;
        try
        {
            canonicalScenes = await _processingStore.ReadCanonicalSceneCapturesAsync(
                rows.Select(static row => row.CaptureId).ToArray(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException or SqliteException)
        {
            canonicalScenes = new HashSet<Guid>();
            canonicalSceneQueryUnavailable = true;
        }
        try
        {
            processing = await _processingStore.ReadGalleryNodesAsync(
                rows.Select(static row => row.CaptureId).ToArray(),
                MaximumProcessingNodesPerCapture,
                MaximumArtifactsPerCapture - 1,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            processing = new([], new HashSet<Guid>(), new HashSet<Guid>());
            unavailable = true;
        }
        catch (FormatException)
        {
            processing = new([], new HashSet<Guid>(), new HashSet<Guid>());
            unavailable = true;
        }

        var nodesByCapture = processing.Nodes.ToLookup(static node => node.CaptureId);
        return rows.Select(row => ProjectCapture(
            row,
            nodesByCapture[row.CaptureId],
            processing.TruncatedNodeCaptures.Contains(row.CaptureId),
            processing.TruncatedOutputCaptures.Contains(row.CaptureId),
            unavailable,
            canonicalSceneQueryUnavailable ? "ProjectionUnavailable" :
                CanonicalSceneAvailability(row.CaptureId, nodesByCapture[row.CaptureId], canonicalScenes))).ToArray();
    }

    private static string CanonicalSceneAvailability(
        Guid captureId,
        IEnumerable<DurableGalleryProcessingNode> nodes,
        IReadOnlySet<Guid> availableCaptures)
    {
        if (availableCaptures.Contains(captureId)) return "Available";
        var state = nodes.SelectMany(static node => node.Outputs)
            .Where(static output => string.Equals(output.ProductSchemaVersion, "projected-scene-v1", StringComparison.Ordinal))
            .Select(static output => output.AvailabilityState)
            .FirstOrDefault();
        return state is "Quarantined" or "Missing" ? state : "Unavailable";
    }

    private static CameraAgentGalleryCapture ProjectCapture(
        RawGalleryRow row,
        IEnumerable<DurableGalleryProcessingNode> durableNodes,
        bool nodesTruncated,
        bool outputsTruncated,
        bool unavailable,
        string canonicalSceneAvailability)
    {
        var trustedManifest = TryReadTrustedManifest(row);
        var rawDescriptor = trustedManifest?.Descriptor;
        var computedOrigin = trustedManifest is null
            ? GalleryEvidenceOrigin.Unknown
            : GalleryEvidenceClassifier.Classify(trustedManifest);
        var origin = computedOrigin == row.EvidenceOrigin ? computedOrigin : GalleryEvidenceOrigin.Unknown;
        var artifacts = new List<CameraAgentGalleryArtifact>
        {
            rawDescriptor is null
                ? new CameraAgentGalleryArtifact(
                    row.RawArtifactId,
                    FrameArtifactRole.Raw,
                    null,
                    null,
                    null,
                    null,
                    row.PayloadSha256,
                    row.PayloadLength,
                    null,
                    [],
                    null)
                : ProjectArtifact(
                    rawDescriptor.Artifact,
                    rawDescriptor.Layout.ByteLength,
                    null,
                    null,
                    layout: rawDescriptor.Layout)
        };
        var nodes = new List<CameraAgentGalleryProcessingNode>();
        foreach (var node in durableNodes)
        {
            foreach (var output in node.Outputs)
            {
                artifacts.Add(ProjectArtifact(
                    output.Artifact,
                    output.Descriptor?.Layout.ByteLength ?? output.ProductManifest?.ByteLength,
                    output.RecipeIdentitySha256,
                    node.NodeId,
                    output.Algorithms,
                    output.ProductKind?.ToString(),
                    output.ProductSchemaVersion,
                    output.ContentIdentitySha256,
                    output.AvailabilityState,
                    output.AvailabilityReason,
                    (output.ProductManifest as DurableEncodedProductManifestV2)?.EncodedWidth,
                    (output.ProductManifest as DurableEncodedProductManifestV2)?.EncodedHeight,
                    output.Descriptor?.Layout,
                    (output.ProductManifest as DurableEncodedProductManifestV2)?.EncodedPixelFormat));
            }
            nodes.Add(new CameraAgentGalleryProcessingNode(
                node.NodeId,
                node.Required,
                node.Status.ToString(),
                node.RecipeName,
                Enum.TryParse<FrameArtifactRole>(node.OutputRole, out var role) ? role : null,
                node.OutputVariant,
                node.Outputs.Select(static output => output.ArtifactId).ToArray()));
        }

        return new CameraAgentGalleryCapture(
            row.CaptureId,
            row.AgentId,
            rawDescriptor?.Capture.RigId,
            row.CaptureSequence,
            DateTimeOffset.FromUnixTimeMilliseconds(row.ExposureUnixMilliseconds),
            DateTimeOffset.FromUnixTimeMilliseconds(row.DurableIngressUnixMilliseconds),
            row.RawState,
            origin,
            artifacts,
            nodes,
            ProcessingNodesTruncated: nodesTruncated,
            ArtifactsTruncated: outputsTruncated,
            ProcessingProjectionUnavailable: unavailable,
            CanonicalSceneAvailability: canonicalSceneAvailability);
    }

    private static CameraAgentGalleryArtifact ProjectArtifact(
        ArtifactDescriptor artifact,
        long? byteLength,
        string? recipeIdentity,
        string? processingNodeId,
        IReadOnlyList<ProcessingAlgorithmIdentity>? algorithms = null,
        string? productKind = null,
        string? productSchemaVersion = null,
        string? contentIdentitySha256 = null,
        string availability = "Available",
        string? availabilityReason = null,
        int? encodedWidth = null,
        int? encodedHeight = null,
        FrameLayoutDescriptor? layout = null,
        CameraPixelFormat? encodedPixelFormat = null)
    {
        recipeIdentity ??= ProcessingIdentity.CreateRecipeIdentity(artifact.Recipe).IdentitySha256;
        return new CameraAgentGalleryArtifact(
            artifact.ArtifactId,
            artifact.Role,
            artifact.SourceId,
            artifact.Variant,
            artifact.CreatedUtc,
            artifact.MediaType,
            artifact.ChecksumSha256,
            byteLength,
            new CameraAgentGalleryRecipe(
                artifact.Recipe.Name,
                artifact.Recipe.SemanticVersion,
                artifact.Recipe.ImplementationVersion,
                artifact.Recipe.OptionsSha256,
                recipeIdentity),
            artifact.SourceArtifactIds,
            processingNodeId,
            algorithms?.Select(static algorithm => new CameraAgentGalleryAlgorithm(
                algorithm.Name, algorithm.Version)).ToArray(),
            productKind,
            productSchemaVersion,
            contentIdentitySha256,
            availability,
            availabilityReason,
            encodedWidth,
            encodedHeight,
            layout?.PixelFormat ?? encodedPixelFormat,
            layout is not null && CameraAgentPreviewEligibilityPolicy.IsSupportedLayout(layout));
    }

    private static ArtifactManifestV2? TryReadTrustedManifest(RawGalleryRow row)
    {
        var parsed = CaptureContractJson.ParseManifest(row.ManifestJson);
        var manifest = parsed.Document?.Manifest;
        if (!parsed.IsValid || manifest is null ||
            !string.Equals(CaptureContractJson.ComputeManifestSha256(row.ManifestJson), row.ManifestSha256, StringComparison.Ordinal) ||
            !string.Equals(CaptureContractJson.ComputeDescriptorSha256(manifest.Descriptor), row.DescriptorSha256, StringComparison.Ordinal) ||
            manifest.Descriptor.Capture.CaptureId != row.CaptureId ||
            manifest.Descriptor.Capture.CaptureSequence != row.CaptureSequence ||
            !string.Equals(manifest.Descriptor.Capture.AgentId, row.AgentId, StringComparison.Ordinal) ||
            manifest.Descriptor.Artifact.ArtifactId != row.RawArtifactId ||
            manifest.Descriptor.Artifact.Role != FrameArtifactRole.Raw ||
            !string.Equals(manifest.Descriptor.Artifact.ChecksumSha256, row.PayloadSha256, StringComparison.Ordinal) ||
            manifest.Descriptor.Layout.ByteLength != row.PayloadLength ||
            manifest.Descriptor.Timing.ExposureStartedUtc.ToUnixTimeMilliseconds() != row.ExposureUnixMilliseconds ||
            manifest.Descriptor.Timing.DurableIngressUtc.ToUnixTimeMilliseconds() != row.DurableIngressUnixMilliseconds)
        {
            return null;
        }
        return manifest;
    }

    private static NormalizedQuery Normalize(CameraAgentGalleryQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = query.PageSize ?? DefaultPageSize;
        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new CameraAgentGalleryQueryException($"Page size must be between 1 and {MaximumPageSize}.");
        }
        var from = query.FromUtc?.ToUniversalTime().ToUnixTimeMilliseconds();
        var to = query.ToUtc?.ToUniversalTime().ToUnixTimeMilliseconds();
        if (from > to)
        {
            throw new CameraAgentGalleryQueryException("The time range is invalid.");
        }
        if (query.MinimumSequence is <= 0 || query.MaximumSequence is <= 0 ||
            query.MinimumSequence > query.MaximumSequence)
        {
            throw new CameraAgentGalleryQueryException("The sequence range is invalid.");
        }
        var rawState = NormalizeRawState(query.RawState);
        if (rawState is not null && !RawStates.Contains(rawState))
        {
            throw new CameraAgentGalleryQueryException("The raw state filter is invalid.");
        }
        string? processingStatus = null;
        if (!string.IsNullOrWhiteSpace(query.ProcessingStatus))
        {
            if (!Enum.TryParse<DurableProcessingNodeStatus>(query.ProcessingStatus.Trim(), true, out var status))
            {
                throw new CameraAgentGalleryQueryException("The processing status filter is invalid.");
            }
            processingStatus = status.ToString();
        }
        var recipe = string.IsNullOrWhiteSpace(query.Recipe) ? null : query.Recipe.Trim().ToUpperInvariant();
        if (recipe is { Length: > 256 })
        {
            throw new CameraAgentGalleryQueryException("The recipe filter is invalid.");
        }
        var filterBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            from,
            to,
            query.MinimumSequence,
            query.MaximumSequence,
            rawState,
            query.EvidenceOrigin,
            query.ProcessingRole,
            recipe,
            processingStatus
        }, SerializerOptions);
        var filterHash = Convert.ToHexString(SHA256.HashData(filterBytes));
        return new NormalizedQuery(
            pageSize,
            query.Cursor,
            from,
            to,
            query.MinimumSequence,
            query.MaximumSequence,
            rawState,
            query.EvidenceOrigin,
            query.ProcessingRole,
            recipe,
            processingStatus,
            filterHash);
    }

    private static string EncodeCursor(RawGalleryRow row, string filterHash)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new GalleryCursor(
            CursorVersion,
            filterHash,
            row.CaptureSequence,
            row.RawRowId), SerializerOptions);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string? NormalizeRawState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        foreach (var state in RawStates)
        {
            if (string.Equals(state, value.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return state;
            }
        }
        return value.Trim();
    }

    private static GalleryCursor? DecodeCursor(string? value, string filterHash)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        if (value.Length > 512)
        {
            throw new CameraAgentGalleryQueryException("The cursor is invalid.");
        }
        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + (4 - normalized.Length % 4) % 4, '=');
            var cursor = JsonSerializer.Deserialize<GalleryCursor>(Convert.FromBase64String(normalized), SerializerOptions);
            if (cursor is null || cursor.Version != CursorVersion || cursor.CaptureSequence < 1 ||
                cursor.RawRowId < 1 || cursor.FilterHash.Length != 64 || !cursor.FilterHash.All(Uri.IsHexDigit) ||
                !string.Equals(cursor.FilterHash, filterHash, StringComparison.Ordinal))
            {
                throw new CameraAgentGalleryQueryException("The cursor does not match the requested filters.");
            }
            return cursor;
        }
        catch (CameraAgentGalleryQueryException)
        {
            throw;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or NotSupportedException)
        {
            throw new CameraAgentGalleryQueryException("The cursor is invalid.");
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The interpolated value is a validated integer host option used only for SQLite PRAGMA configuration.")]
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

    private static async ValueTask<RawGalleryRow> ReadRawRowAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
        => new(
            reader.GetInt64(0),
            Guid.ParseExact(reader.GetString(1), "N"),
            Guid.ParseExact(reader.GetString(2), "N"),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetInt64(8),
            await reader.GetFieldValueAsync<byte[]>(9, cancellationToken).ConfigureAwait(false),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetString(12),
            Enum.TryParse<GalleryEvidenceOrigin>(reader.GetString(13), out var origin)
                ? origin
                : GalleryEvidenceOrigin.Unknown,
            reader.GetBoolean(14));

    private const string RawSelectSql = """
        SELECT raw.raw_capture_row_id, raw.capture_id, raw.raw_artifact_id, raw.agent_id,
               raw.capture_sequence, raw.descriptor_sha256, raw.manifest_sha256,
               raw.payload_sha256, raw.payload_length, raw.manifest_json,
               raw.exposure_started_unix_ms, raw.durable_ingress_unix_ms,
               raw.state, raw.evidence_origin, raw.retention_hold
        FROM raw_captures raw
        """;

    private sealed record NormalizedQuery(
        int PageSize,
        string? Cursor,
        long? FromUnixMilliseconds,
        long? ToUnixMilliseconds,
        long? MinimumSequence,
        long? MaximumSequence,
        string? RawState,
        GalleryEvidenceOrigin? EvidenceOrigin,
        FrameArtifactRole? ProcessingRole,
        string? Recipe,
        string? ProcessingStatus,
        string FilterHash);

    private sealed record GalleryCursor(
        int Version,
        string FilterHash,
        long CaptureSequence,
        long RawRowId);

    private sealed record RawGalleryRow(
        long RawRowId,
        Guid CaptureId,
        Guid RawArtifactId,
        string AgentId,
        long CaptureSequence,
        string DescriptorSha256,
        string ManifestSha256,
        string PayloadSha256,
        long PayloadLength,
        byte[] ManifestJson,
        long ExposureUnixMilliseconds,
        long DurableIngressUnixMilliseconds,
        string RawState,
        GalleryEvidenceOrigin EvidenceOrigin,
        bool RetentionHold);

    private sealed record DeliveryStatusLookup(
        string Availability,
        IReadOnlyDictionary<Guid, IReadOnlyList<CameraAgentGalleryArtifactDelivery>> Statuses);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
