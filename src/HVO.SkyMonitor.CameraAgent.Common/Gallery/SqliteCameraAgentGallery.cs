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

internal sealed class SqliteCameraAgentGallery : ICameraAgentGallery
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

    public SqliteCameraAgentGallery(
        IOptions<CameraAgentHostOptions> options,
        SqliteCaptureProcessingStore processingStore,
        ICameraAgentStorageResolver? storageResolver = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _processingStore = processingStore ?? throw new ArgumentNullException(nameof(processingStore));
        _storageResolver = storageResolver;
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
            sql.AppendLine("AND EXISTS (SELECT 1 FROM processing_outputs output WHERE output.capture_id = raw.capture_id AND output.role = $processing_role)");
            command.Parameters.AddWithValue("$processing_role", processingRole.ToString());
        }
        if (query.Recipe is not null)
        {
            sql.AppendLine("""
                AND (
                    EXISTS (SELECT 1 FROM processing_nodes node
                            WHERE node.capture_id = raw.capture_id AND upper(node.recipe_name) = $recipe)
                    OR EXISTS (SELECT 1 FROM processing_outputs output
                               WHERE output.capture_id = raw.capture_id AND output.recipe_identity_sha256 = $recipe_identity)
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
                canonicalScenes.Contains(row.CaptureId) ? "Available" : "Unavailable")).ToArray();
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
                : ProjectArtifact(rawDescriptor.Artifact, rawDescriptor.Layout.ByteLength, null, null)
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
                    output.ContentIdentitySha256));
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
        string? contentIdentitySha256 = null)
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
            contentIdentitySha256);
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
