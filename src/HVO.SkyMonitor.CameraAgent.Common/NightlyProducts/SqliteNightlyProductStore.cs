using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;

/// <summary>One product a generator run asks the store to publish.</summary>
internal sealed record NightlyProductPublication(
    NightlyProductKind Kind,
    NightlyProductScope Scope,
    DateOnly ObservingDate,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    int PartOrdinal,
    string RecipeName,
    ProcessingProduct Product,
    IReadOnlyList<NightlyProductSource> Sources,
    int RenditionJpegQuality)
{
    internal LocalAutomationOccurrence Occurrence { get; init; } = null!;
}

/// <summary>A published product restored as a verified recipe input, with the ordered lineage it was composed from.</summary>
internal sealed record NightlyStoredProduct(
    NightlyProductDetail Detail,
    FrameLayoutDescriptor Layout,
    ProcessingCompatibilityIdentity Compatibility,
    ReadOnlyMemory<byte> Payload);

/// <summary>The recorded evaluation of one window, its input fingerprint, and its current products in part order.</summary>
internal sealed record NightlyWindowState(
    NightlyProductWindowStatus Status,
    string FingerprintSha256,
    IReadOnlyList<Guid> ProductIds);

/// <summary>
/// The durable nightly product store. A product is published when its row commits: its payload, JPEG rendition, and
/// provenance document are written and synchronized first, and once the row exists neither the row, its lineage, nor
/// its files are rewritten. Windows record their latest evaluation and point at their current products, so a window
/// re-evaluated over different sources supersedes its earlier products without deleting them.
/// </summary>
internal sealed class SqliteNightlyProductStore : INightlyProductCatalog, IDisposable
{
    internal const string DirectoryName = ".nightly-products";
    internal const string FileName = "nightly-products.db";
    internal const string ProductDirectoryName = "nightly-products";
    internal const string RenditionMediaType = "image/jpeg";
    private const int ExpectedSchemaObjectCount = 11;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private const string SchemaSql = """
        CREATE TABLE nightly_products (
            product_id TEXT PRIMARY KEY CHECK (length(product_id) = 32),
            output_identity_sha256 TEXT NOT NULL UNIQUE CHECK (length(output_identity_sha256) = 64),
            occurrence_json TEXT NOT NULL,
            kind TEXT NOT NULL CHECK (kind IN ('Keogram', 'StarTrail')),
            scope TEXT NOT NULL CHECK (scope IN ('Segment', 'Rollup', 'Final')),
            observing_date TEXT NOT NULL CHECK (length(observing_date) = 10),
            window_start_utc_ticks INTEGER NOT NULL,
            window_end_utc_ticks INTEGER NOT NULL CHECK (window_end_utc_ticks > window_start_utc_ticks),
            part_ordinal INTEGER NOT NULL CHECK (part_ordinal >= 0),
            recipe_name TEXT NOT NULL CHECK (length(recipe_name) BETWEEN 1 AND 128),
            recipe_identity_sha256 TEXT NOT NULL CHECK (length(recipe_identity_sha256) = 64),
            recipe_json TEXT NOT NULL,
            algorithms_json TEXT NOT NULL,
            variant TEXT NOT NULL CHECK (length(variant) BETWEEN 1 AND 128),
            rig_profile_sha256 TEXT NOT NULL CHECK (length(rig_profile_sha256) BETWEEN 1 AND 128),
            compatibility_json TEXT NOT NULL,
            layout_json TEXT NOT NULL,
            pixel_format TEXT NOT NULL CHECK (pixel_format IN ('Mono8', 'Rgb24')),
            width INTEGER NOT NULL CHECK (width > 0),
            height INTEGER NOT NULL CHECK (height > 0),
            payload_relative_path TEXT NOT NULL UNIQUE,
            payload_sha256 TEXT NOT NULL CHECK (length(payload_sha256) = 64),
            payload_bytes INTEGER NOT NULL CHECK (payload_bytes > 0),
            rendition_relative_path TEXT NOT NULL UNIQUE,
            rendition_sha256 TEXT NOT NULL CHECK (length(rendition_sha256) = 64),
            rendition_bytes INTEGER NOT NULL CHECK (rendition_bytes > 0),
            provenance_relative_path TEXT NOT NULL UNIQUE,
            provenance_sha256 TEXT NOT NULL CHECK (length(provenance_sha256) = 64),
            provenance_bytes INTEGER NOT NULL CHECK (provenance_bytes BETWEEN 1 AND 8388608),
            source_count INTEGER NOT NULL CHECK (source_count > 0),
            first_observation_utc_ticks INTEGER NOT NULL,
            last_observation_utc_ticks INTEGER NOT NULL CHECK (last_observation_utc_ticks >= first_observation_utc_ticks),
            total_integration_ticks INTEGER NOT NULL CHECK (total_integration_ticks >= 0),
            created_unix_ms INTEGER NOT NULL
        ) STRICT;

        CREATE INDEX nightly_products_by_date ON nightly_products (observing_date, window_start_utc_ticks);

        CREATE TABLE nightly_product_sources (
            product_id TEXT NOT NULL REFERENCES nightly_products (product_id),
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            source_kind TEXT NOT NULL CHECK (source_kind IN ('PreviewFrame', 'NightlyProduct')),
            artifact_id TEXT NOT NULL CHECK (length(artifact_id) = 32),
            output_identity_sha256 TEXT NOT NULL CHECK (length(output_identity_sha256) = 64),
            capture_id TEXT CHECK (capture_id IS NULL OR length(capture_id) = 32),
            observation_started_utc_ticks INTEGER NOT NULL CHECK (observation_started_utc_ticks > 0),
            PRIMARY KEY (product_id, ordinal)
        ) STRICT;

        CREATE TABLE nightly_windows (
            occurrence_identity_sha256 TEXT NOT NULL CHECK (length(occurrence_identity_sha256) = 64),
            occurrence_json TEXT NOT NULL,            kind TEXT NOT NULL CHECK (kind IN ('Keogram', 'StarTrail')),
            scope TEXT NOT NULL CHECK (scope IN ('Segment', 'Final')),
            window_start_utc_ticks INTEGER NOT NULL,
            window_end_utc_ticks INTEGER NOT NULL CHECK (window_end_utc_ticks > window_start_utc_ticks),
            observing_date TEXT NOT NULL CHECK (length(observing_date) = 10),
            fingerprint_sha256 TEXT NOT NULL CHECK (length(fingerprint_sha256) = 64),
            disposition TEXT NOT NULL CHECK (disposition IN ('Produced', 'NoSources', 'Rejected')),
            reason_code TEXT CHECK (reason_code IS NULL OR length(reason_code) BETWEEN 1 AND 128),
            candidate_count INTEGER NOT NULL CHECK (candidate_count >= 0),
            admitted_count INTEGER NOT NULL CHECK (admitted_count >= 0 AND admitted_count <= candidate_count),
            exclusions_json TEXT NOT NULL,
            evaluated_unix_ms INTEGER NOT NULL,
            PRIMARY KEY (occurrence_identity_sha256, kind, scope, window_start_utc_ticks)
        ) STRICT;

        CREATE INDEX nightly_windows_by_date ON nightly_windows (observing_date, window_start_utc_ticks);

        CREATE TABLE nightly_window_products (
            occurrence_identity_sha256 TEXT NOT NULL,
            kind TEXT NOT NULL,
            scope TEXT NOT NULL,
            window_start_utc_ticks INTEGER NOT NULL,
            part_ordinal INTEGER NOT NULL CHECK (part_ordinal >= 0),
            product_id TEXT NOT NULL REFERENCES nightly_products (product_id),
            PRIMARY KEY (occurrence_identity_sha256, kind, scope, window_start_utc_ticks, part_ordinal),
            FOREIGN KEY (occurrence_identity_sha256, kind, scope, window_start_utc_ticks)
                REFERENCES nightly_windows (occurrence_identity_sha256, kind, scope, window_start_utc_ticks) ON DELETE CASCADE
        ) STRICT;

        CREATE INDEX nightly_window_products_by_product ON nightly_window_products (product_id);

        CREATE TRIGGER nightly_products_immutable_update BEFORE UPDATE ON nightly_products
        BEGIN SELECT RAISE(ABORT, 'Published nightly products are immutable.'); END;

        CREATE TRIGGER nightly_products_immutable_delete BEFORE DELETE ON nightly_products
        BEGIN SELECT RAISE(ABORT, 'Published nightly products are immutable.'); END;

        CREATE TRIGGER nightly_product_sources_immutable_update BEFORE UPDATE ON nightly_product_sources
        BEGIN SELECT RAISE(ABORT, 'Nightly product lineage is immutable.'); END;

        CREATE TRIGGER nightly_product_sources_immutable_delete BEFORE DELETE ON nightly_product_sources
        BEGIN SELECT RAISE(ABORT, 'Nightly product lineage is immutable.'); END;
        """;

    private const string ProductColumns = """
        product.product_id, product.output_identity_sha256, product.kind, product.scope, product.observing_date,
        product.window_start_utc_ticks, product.window_end_utc_ticks, product.part_ordinal, product.recipe_name,
        product.recipe_identity_sha256, product.variant, product.rig_profile_sha256, product.compatibility_json,
        product.layout_json, product.pixel_format, product.width, product.height, product.payload_relative_path,
        product.payload_sha256, product.payload_bytes, product.rendition_relative_path, product.rendition_sha256,
        product.rendition_bytes, product.provenance_sha256, product.source_count, product.first_observation_utc_ticks,
        product.last_observation_utc_ticks, product.total_integration_ticks, product.created_unix_ms,
        EXISTS (SELECT 1 FROM nightly_window_products AS pointer WHERE pointer.product_id = product.product_id),
        product.occurrence_json
        """;

    private readonly CameraAgentHostOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _connectionString;
    private string? _databasePath;
    private bool _schemaReady;

    public SqliteNightlyProductStore(IOptions<CameraAgentHostOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Publishes a product, or returns the already-published product with the same output identity. Files are written
    /// and synchronized before the row commits; a file left by an interrupted earlier attempt has no row, so it is
    /// staging and is replaced rather than trusted.
    /// </summary>
    internal async ValueTask<NightlyProductDetail> PublishAsync(
        NightlyProductPublication publication,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var product = publication.Product;
        Validate(publication);
        var productId = ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            if (await ReadDetailAsync(connection, null, productId, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                if (!string.Equals(existing.OutputIdentitySha256, product.OutputIdentitySha256, StringComparison.Ordinal) ||
                    !string.Equals(existing.PayloadSha256, product.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "A published nightly product disagrees with a recomputation of the same output identity.");
                }
                return existing;
            }

            var layout = product.Layout!;
            var imageLayout = new ImageLayout(layout.Width, layout.Height, layout.PixelFormat, layout.StrideBytes);
            var rendition = JpegImageCodec.EncodeToJpeg(
                imageLayout, product.Payload, publication.RenditionJpegQuality, cancellationToken);
            var renditionSha256 = ProcessingIdentity.ComputePayloadSha256(rendition);
            var payloadSha256 = product.ChecksumSha256.ToUpperInvariant();
            var (first, last) = await ObservationSpanAsync(connection, publication.Sources, cancellationToken)
                .ConfigureAwait(false);
            var relativeDirectory = Path.Combine(
                ProductDirectoryName,
                publication.ObservingDate.Year.ToString("D4", CultureInfo.InvariantCulture),
                publication.ObservingDate.Month.ToString("D2", CultureInfo.InvariantCulture),
                publication.ObservingDate.Day.ToString("D2", CultureInfo.InvariantCulture),
                NightlyProductContract.TargetFor(publication.Kind));
            var stem = productId.ToString("N", CultureInfo.InvariantCulture);
            var payloadPath = Path.Combine(relativeDirectory, stem + ".bin");
            var renditionPath = Path.Combine(relativeDirectory, stem + ".jpg");
            var provenancePath = Path.Combine(relativeDirectory, stem + ".provenance.json");
            var provenance = CreateProvenance(
                publication, productId, payloadSha256, renditionSha256, rendition.LongLength, first, last);
            if (provenance.Length > 8 * 1024 * 1024) throw new InvalidDataException("Still product provenance exceeds its byte bound.");
            var provenanceSha256 = ProcessingIdentity.ComputePayloadSha256(provenance);

            var root = ResolveRoot();
            var directory = ResolveConfinedPath(root, relativeDirectory);
            Directory.CreateDirectory(directory);
            RawIngressFileStore.EnsureNoSymbolicLinks(root, directory);
            WriteDurably(root, ResolveConfinedPath(root, payloadPath), product.Payload.Span);
            WriteDurably(root, ResolveConfinedPath(root, renditionPath), rendition);
            WriteDurably(root, ResolveConfinedPath(root, provenancePath), provenance);
            RawIngressFileStore.SyncDirectoryHierarchy(root, directory);

            var createdUtc = Truncate(_timeProvider.GetUtcNow());
            using (var transaction = BeginImmediate(connection))
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO nightly_products (
                        product_id, output_identity_sha256, occurrence_json, kind, scope, observing_date, window_start_utc_ticks,
                        window_end_utc_ticks, part_ordinal, recipe_name, recipe_identity_sha256, recipe_json,
                        algorithms_json, variant, rig_profile_sha256, compatibility_json, layout_json, pixel_format,
                        width, height, payload_relative_path, payload_sha256, payload_bytes, rendition_relative_path,
                        rendition_sha256, rendition_bytes, provenance_relative_path, provenance_sha256, provenance_bytes, source_count,
                        first_observation_utc_ticks, last_observation_utc_ticks, total_integration_ticks, created_unix_ms)
                    VALUES (
                        $product_id, $output_identity, $occurrence_json, $kind, $scope, $observing_date, $window_start, $window_end,
                        $part_ordinal, $recipe_name, $recipe_identity, $recipe_json, $algorithms_json, $variant,
                        $rig, $compatibility_json, $layout_json, $pixel_format, $width, $height, $payload_path,
                        $payload_sha256, $payload_bytes, $rendition_path, $rendition_sha256, $rendition_bytes,
                        $provenance_path, $provenance_sha256, $provenance_bytes, $source_count, $first, $last, $integration, $created);
                    """, cancellationToken,
                    ("$product_id", stem),
                    ("$output_identity", product.OutputIdentitySha256),
                    ("$occurrence_json", Serialize(publication.Occurrence)),
                    ("$kind", publication.Kind.ToString()),
                    ("$scope", publication.Scope.ToString()),
                    ("$observing_date", FormatDate(publication.ObservingDate)),
                    ("$window_start", publication.WindowStartUtc.UtcTicks),
                    ("$window_end", publication.WindowEndUtc.UtcTicks),
                    ("$part_ordinal", publication.PartOrdinal),
                    ("$recipe_name", publication.RecipeName),
                    ("$recipe_identity", product.Recipe.IdentitySha256),
                    ("$recipe_json", Serialize(product.Recipe.Descriptor)),
                    ("$algorithms_json", Serialize(product.Algorithms)),
                    ("$variant", product.Variant),
                    ("$rig", product.Compatibility.Rig),
                    ("$compatibility_json", Serialize(product.Compatibility)),
                    ("$layout_json", Serialize(layout)),
                    ("$pixel_format", layout.PixelFormat.ToString()),
                    ("$width", layout.Width),
                    ("$height", layout.Height),
                    ("$payload_path", ToStoredPath(payloadPath)),
                    ("$payload_sha256", payloadSha256),
                    ("$payload_bytes", (long)product.Payload.Length),
                    ("$rendition_path", ToStoredPath(renditionPath)),
                    ("$rendition_sha256", renditionSha256),
                    ("$rendition_bytes", rendition.LongLength),
                    ("$provenance_path", ToStoredPath(provenancePath)),
                    ("$provenance_sha256", provenanceSha256),
                    ("$provenance_bytes", provenance.LongLength),
                    ("$source_count", publication.Sources.Count),
                    ("$first", first.UtcTicks),
                    ("$last", last.UtcTicks),
                    ("$integration", product.TotalIntegration.Ticks),
                    ("$created", createdUtc.ToUnixTimeMilliseconds())).ConfigureAwait(false);
                foreach (var source in publication.Sources)
                {
                    await ExecuteAsync(connection, transaction, """
                        INSERT INTO nightly_product_sources (
                            product_id, ordinal, source_kind, artifact_id, output_identity_sha256, capture_id,
                            observation_started_utc_ticks)
                        VALUES ($product_id, $ordinal, $source_kind, $artifact_id, $output_identity, $capture_id,
                                $observation_started);
                        """, cancellationToken,
                        ("$product_id", stem),
                        ("$ordinal", source.Ordinal),
                        ("$source_kind", source.SourceKind.ToString()),
                        ("$artifact_id", source.ArtifactId.ToString("N", CultureInfo.InvariantCulture)),
                        ("$output_identity", source.OutputIdentitySha256),
                        ("$capture_id", source.CaptureId is { } captureId
                            ? captureId.ToString("N", CultureInfo.InvariantCulture)
                            : DBNull.Value),
                        ("$observation_started", source.ObservationStartedUtc.UtcTicks))
                        .ConfigureAwait(false);
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            return await ReadDetailAsync(connection, null, productId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("A published nightly product could not be read back.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns the published product with an output identity, or null when none exists.</summary>
    internal async ValueTask<NightlyProductDetail?> FindByOutputIdentityAsync(
        string outputIdentitySha256,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputIdentitySha256);
        return await GetAsync(ProcessingIdentity.CreateArtifactId(outputIdentitySha256), cancellationToken)
            .ConfigureAwait(false) is { } detail &&
            string.Equals(detail.OutputIdentitySha256, outputIdentitySha256, StringComparison.Ordinal)
                ? detail
                : null;
    }

    /// <summary>Restores a published product's payload, verifying its length and checksum against the row.</summary>
    internal async ValueTask<NightlyStoredProduct> ReadStoredProductAsync(Guid productId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT payload_relative_path, layout_json, compatibility_json FROM nightly_products
                WHERE product_id = $product_id;
                """;
            command.Parameters.AddWithValue("$product_id", productId.ToString("N", CultureInfo.InvariantCulture));
            string payloadPath;
            FrameLayoutDescriptor layout;
            ProcessingCompatibilityIdentity compatibility;
            using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("A nightly product source is not published.");
                }
                payloadPath = reader.GetString(0);
                layout = Deserialize<FrameLayoutDescriptor>(reader.GetString(1));
                compatibility = Deserialize<ProcessingCompatibilityIdentity>(reader.GetString(2));
            }
            var detail = await ReadDetailAsync(connection, null, productId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("A nightly product source is not published.");
            var payload = await ReadVerifiedAsync(
                payloadPath, detail.PayloadBytes, detail.PayloadSha256, cancellationToken).ConfigureAwait(false);
            return new NightlyStoredProduct(detail, layout, compatibility, payload);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns the recorded state of one window, or null when it has never been evaluated.</summary>
    internal async ValueTask<NightlyWindowState?> ReadWindowAsync(
        NightlyProductKind kind,
        NightlyProductScope scope,
        DateTimeOffset windowStartUtc,
        CancellationToken cancellationToken,
        string? occurrenceIdentitySha256 = null)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            var windows = await ReadWindowsAsync(connection, """
                WHERE state.kind = $kind AND state.scope = $scope AND state.window_start_utc_ticks = $window_start
                  AND ($occurrence IS NULL OR state.occurrence_identity_sha256 = $occurrence)
                """, cancellationToken,
                ("$kind", kind.ToString()),
                ("$scope", scope.ToString()),
                ("$window_start", windowStartUtc.UtcTicks), ("$occurrence", (object?)occurrenceIdentitySha256 ?? DBNull.Value)).ConfigureAwait(false);
            if (windows.Count > 1) throw new InvalidDataException("A product window requires its occurrence identity.");
            return windows.Count == 0 ? null : windows[0];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records a window's evaluation and atomically replaces its current product pointers. Earlier products stay
    /// published and become superseded.
    /// </summary>
    internal async ValueTask RecordWindowAsync(
        NightlyProductWindowStatus status,
        string fingerprintSha256,
        IReadOnlyList<Guid> productIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(productIds);
        ValidateOccurrence(status.Occurrence, status.ObservingDate, status.WindowStartUtc, status.WindowEndUtc);
        if (status.Scope == NightlyProductScope.Rollup)
        {
            throw new ArgumentException("Only segment and night windows are recorded.", nameof(status));
        }
        if ((status.Disposition == NightlyProductWindowDisposition.Produced) != (productIds.Count > 0))
        {
            throw new ArgumentException("Only a produced window points at products.", nameof(productIds));
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var key = new (string, object)[]
            {
                ("$occurrence", status.Occurrence.IdentitySha256),
                ("$kind", status.Kind.ToString()),
                ("$scope", status.Scope.ToString()),
                ("$window_start", status.WindowStartUtc.UtcTicks)
            };
            await ExecuteAsync(connection, transaction, """
                DELETE FROM nightly_window_products
                WHERE occurrence_identity_sha256 = $occurrence AND kind = $kind AND scope = $scope AND window_start_utc_ticks = $window_start;
                """, cancellationToken, key).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO nightly_windows (
                    occurrence_identity_sha256, occurrence_json, kind, scope, window_start_utc_ticks, window_end_utc_ticks, observing_date, fingerprint_sha256,
                    disposition, reason_code, candidate_count, admitted_count, exclusions_json, evaluated_unix_ms)
                VALUES ($occurrence, $occurrence_json, $kind, $scope, $window_start, $window_end, $observing_date, $fingerprint, $disposition,
                        $reason_code, $candidate_count, $admitted_count, $exclusions_json, $evaluated)
                ON CONFLICT (occurrence_identity_sha256, kind, scope, window_start_utc_ticks) DO UPDATE SET
                    window_end_utc_ticks = excluded.window_end_utc_ticks,
                    observing_date = excluded.observing_date,
                    fingerprint_sha256 = excluded.fingerprint_sha256,
                    disposition = excluded.disposition,
                    reason_code = excluded.reason_code,
                    candidate_count = excluded.candidate_count,
                    admitted_count = excluded.admitted_count,
                    exclusions_json = excluded.exclusions_json,
                    evaluated_unix_ms = excluded.evaluated_unix_ms;
                """, cancellationToken,
                [.. key,
                    ("$occurrence_json", Serialize(status.Occurrence)),
                    ("$window_end", status.WindowEndUtc.UtcTicks),
                    ("$observing_date", FormatDate(status.ObservingDate)),
                    ("$fingerprint", fingerprintSha256),
                    ("$disposition", status.Disposition.ToString()),
                    ("$reason_code", (object?)status.ReasonCode ?? DBNull.Value),
                    ("$candidate_count", status.CandidateCount),
                    ("$admitted_count", status.AdmittedCount),
                    ("$exclusions_json", Serialize(new SortedDictionary<string, int>(
                        status.Exclusions.ToDictionary(), StringComparer.Ordinal))),
                    ("$evaluated", Truncate(status.EvaluatedUtc).ToUnixTimeMilliseconds())]).ConfigureAwait(false);
            for (var ordinal = 0; ordinal < productIds.Count; ordinal++)
            {
                var referenced = await ReadDetailAsync(connection, transaction, productIds[ordinal], cancellationToken).ConfigureAwait(false);
                if (referenced is null || referenced.Occurrence.IdentitySha256 != status.Occurrence.IdentitySha256 ||
                    referenced.Summary.Kind != status.Kind || referenced.Summary.WindowStartUtc != status.WindowStartUtc ||
                    referenced.Summary.WindowEndUtc != status.WindowEndUtc ||
                    (status.Scope == NightlyProductScope.Segment && referenced.Summary.Scope != NightlyProductScope.Segment))
                    throw new InvalidDataException("A current pointer must reference a product of the same retained occurrence and span.");
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO nightly_window_products (occurrence_identity_sha256, kind, scope, window_start_utc_ticks, part_ordinal, product_id)
                    VALUES ($occurrence, $kind, $scope, $window_start, $part_ordinal, $product_id);
                    """, cancellationToken,
                    [.. key,
                        ("$part_ordinal", ordinal),
                        ("$product_id", productIds[ordinal].ToString("N", CultureInfo.InvariantCulture))])
                    .ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<NightlyProductSummary>> ListAsync(
        DateOnly observingDate,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT {ProductColumns}
                FROM nightly_products AS product
                WHERE product.observing_date = $observing_date
                ORDER BY product.window_start_utc_ticks, product.kind, product.scope, product.part_ordinal,
                         product.created_unix_ms, product.product_id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$observing_date", FormatDate(observingDate));
            command.Parameters.AddWithValue("$limit", NightlyProductContract.MaximumListedProducts + 1);
            var summaries = new List<NightlyProductSummary>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                summaries.Add(ReadSummary(reader));
            }
            if (summaries.Count > NightlyProductContract.MaximumListedProducts)
                throw new InvalidDataException("The product listing exceeds its declared bound; it cannot represent full coverage.");
            return summaries;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<NightlyProductWindowStatus>> ListWindowsAsync(
        DateOnly observingDate,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            var windows = await ReadWindowsAsync(connection, "WHERE state.observing_date = $observing_date",
                cancellationToken, ("$observing_date", FormatDate(observingDate))).ConfigureAwait(false);
            return [.. windows.Select(static window => window.Status)];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<NightlyProductDetail?> GetAsync(Guid productId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            return await ReadDetailAsync(connection, null, productId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<NightlyProductRendition?> OpenRenditionAsync(
        Guid productId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT rendition_relative_path, rendition_bytes, rendition_sha256 FROM nightly_products
                WHERE product_id = $product_id;
                """;
            command.Parameters.AddWithValue("$product_id", productId.ToString("N", CultureInfo.InvariantCulture));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            var content = await ReadVerifiedAsync(
                reader.GetString(0), reader.GetInt64(1), reader.GetString(2), cancellationToken).ConfigureAwait(false);
            return new NightlyProductRendition(productId, RenditionMediaType, content);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<NightlyProductProvenance?> OpenProvenanceAsync(Guid productId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT provenance_relative_path, provenance_bytes, provenance_sha256 FROM nightly_products WHERE product_id = $id;";
            command.Parameters.AddWithValue("$id", productId.ToString("N", CultureInfo.InvariantCulture));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            var content = await ReadVerifiedAsync(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), cancellationToken)
                .ConfigureAwait(false);
            return new(productId, content);
        }
        finally { _gate.Release(); }
    }

    internal async ValueTask VerifyPublicationAsync(Guid productId, CancellationToken cancellationToken)
    {
        _ = await ReadStoredProductAsync(productId, cancellationToken).ConfigureAwait(false);
        if (await OpenRenditionAsync(productId, cancellationToken).ConfigureAwait(false) is null ||
            await OpenProvenanceAsync(productId, cancellationToken).ConfigureAwait(false) is null)
            throw new InvalidDataException("A reused product is no longer a verified publication.");
    }

    private void Validate(NightlyProductPublication publication)
    {
        ValidateOccurrence(publication.Occurrence, publication.ObservingDate, publication.WindowStartUtc, publication.WindowEndUtc);
        if (!publication.Occurrence.SourceWindow!.IsEligibleForFinal(_timeProvider.GetUtcNow()) ||
            !NightlyProductPreset.IsBound(publication.Product.Recipe, publication.Occurrence))
            throw new ArgumentException("A published still product must bind its eligible retained occurrence.", nameof(publication));
        var product = publication.Product;
        if (product.Role != FrameArtifactRole.Preview || product.Kind != ProcessingProductKind.PixelData ||
            product.Layout is not { PixelFormat: CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24 } ||
            product.Payload.IsEmpty ||
            !string.Equals(
                ProcessingIdentity.ComputePayloadSha256(product.Payload), product.ChecksumSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A nightly product must be a checksummed packed preview image.", nameof(publication));
        }
        if (publication.Sources.Count == 0 ||
            publication.Sources.Count != product.SourceArtifactIds.Count ||
            !publication.Sources.Select(static source => source.ArtifactId).SequenceEqual(product.SourceArtifactIds) ||
            publication.Sources.Where((source, index) => source.Ordinal != index).Any())
        {
            throw new ArgumentException(
                "Nightly product lineage must list exactly the product's sources in recipe order.", nameof(publication));
        }
        if (publication.WindowEndUtc <= publication.WindowStartUtc || publication.PartOrdinal < 0)
        {
            throw new ArgumentException("A nightly product window is invalid.", nameof(publication));
        }
    }

    private static void ValidateOccurrence(LocalAutomationOccurrence? occurrence, DateOnly date,
        DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (occurrence is null || !occurrence.IsValid() || occurrence.SourceWindow is not { } window ||
            occurrence.Definition.TaskKind != LocalAutomationTaskKind.StillImageGeneration ||
            date != window.ReportingPeriod.ReportDate || startUtc != window.StartUtc || endUtc != window.EndUtc)
            throw new InvalidDataException("A still product must retain its exact task, source span and starting-sunrise date.");
    }

    /// <summary>
    /// The observed span of a product. A preview source contributes its exposure start; a nightly-product source
    /// contributes the whole span of that published product, which must already exist with the stated identity, so a
    /// night ends at its last exposure rather than at the start of its last segment.
    /// </summary>
    private static async ValueTask<(DateTimeOffset First, DateTimeOffset Last)> ObservationSpanAsync(
        SqliteConnection connection,
        IReadOnlyList<NightlyProductSource> sources,
        CancellationToken cancellationToken)
    {
        var first = DateTimeOffset.MaxValue;
        var last = DateTimeOffset.MinValue;
        foreach (var source in sources)
        {
            var (start, end) = (source.ObservationStartedUtc, source.ObservationStartedUtc);
            if (source.SourceKind == NightlyProductSourceKind.NightlyProduct)
            {
                var referenced = await ReadDetailAsync(connection, null, source.ArtifactId, cancellationToken)
                    .ConfigureAwait(false);
                if (referenced is null ||
                    !string.Equals(referenced.OutputIdentitySha256, source.OutputIdentitySha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("A nightly product source is not a published nightly product.");
                }
                (start, end) = (referenced.Summary.FirstObservationUtc, referenced.Summary.LastObservationUtc);
            }
            first = start < first ? start : first;
            last = end > last ? end : last;
        }
        return (first, last);
    }

    /// <summary>
    /// The provenance document holds only facts fixed by the product's identity, so a recomputation writes the same
    /// bytes. Publication time lives in the row.
    /// </summary>
    private static byte[] CreateProvenance(
        NightlyProductPublication publication,
        Guid productId,
        string payloadSha256,
        string renditionSha256,
        long renditionBytes,
        DateTimeOffset first,
        DateTimeOffset last)
    {
        var product = publication.Product;
        var document = new
        {
            schemaVersion = NightlyProductContract.ProvenanceSchemaVersion,
            productId,
            outputIdentitySha256 = product.OutputIdentitySha256,
            kind = publication.Kind.ToString(),
            scope = publication.Scope.ToString(),
            observingDate = FormatDate(publication.ObservingDate),
            windowStartUtc = publication.WindowStartUtc.ToUniversalTime(),
            windowEndUtc = publication.WindowEndUtc.ToUniversalTime(),
            partOrdinal = publication.PartOrdinal,
            occurrence = publication.Occurrence,
            recipe = new { name = publication.RecipeName, identitySha256 = product.Recipe.IdentitySha256, descriptor = product.Recipe.Descriptor },
            algorithms = product.Algorithms,
            variant = product.Variant,
            compatibility = product.Compatibility,
            layout = product.Layout,
            payload = new { mediaType = product.MediaType, sha256 = payloadSha256, bytes = product.Payload.Length },
            rendition = new
            {
                mediaType = RenditionMediaType,
                quality = publication.RenditionJpegQuality,
                sha256 = renditionSha256,
                bytes = renditionBytes
            },
            totalIntegrationTicks = product.TotalIntegration.Ticks,
            firstObservationUtc = first.ToUniversalTime(),
            lastObservationUtc = last.ToUniversalTime(),
            sources = publication.Sources.Select(static source => new
            {
                ordinal = source.Ordinal,
                sourceKind = source.SourceKind.ToString(),
                artifactId = source.ArtifactId,
                outputIdentitySha256 = source.OutputIdentitySha256,
                captureId = source.CaptureId,
                observationStartedUtc = source.ObservationStartedUtc.ToUniversalTime()
            })
        };
        var element = CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(document, SerializerOptions));
        return Encoding.UTF8.GetBytes(element.GetRawText());
    }

    private static async ValueTask<NightlyProductDetail?> ReadDetailAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid productId,
        CancellationToken cancellationToken)
    {
        var id = productId.ToString("N", CultureInfo.InvariantCulture);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {ProductColumns}
            FROM nightly_products AS product
            WHERE product.product_id = $product_id;
            """;
        command.Parameters.AddWithValue("$product_id", id);
        NightlyProductSummary summary;
        string outputIdentity, recipeName, recipeIdentity, variant, rig, payloadSha, renditionSha, provenanceSha;
        long payloadBytes, renditionBytes;
        LocalAutomationOccurrence occurrence;
        using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            summary = ReadSummary(reader);
            outputIdentity = reader.GetString(1);
            recipeName = reader.GetString(8);
            recipeIdentity = reader.GetString(9);
            variant = reader.GetString(10);
            rig = reader.GetString(11);
            payloadSha = reader.GetString(18);
            payloadBytes = reader.GetInt64(19);
            renditionSha = reader.GetString(21);
            renditionBytes = reader.GetInt64(22);
            provenanceSha = reader.GetString(23);
            occurrence = Deserialize<LocalAutomationOccurrence>(reader.GetString(30));
            ValidateOccurrence(occurrence, summary.ObservingDate, summary.WindowStartUtc, summary.WindowEndUtc);
        }

        using var sourcesCommand = connection.CreateCommand();
        sourcesCommand.Transaction = transaction;
        sourcesCommand.CommandText = """
            SELECT ordinal, source_kind, artifact_id, output_identity_sha256, capture_id, observation_started_utc_ticks
            FROM nightly_product_sources WHERE product_id = $product_id ORDER BY ordinal;
            """;
        sourcesCommand.Parameters.AddWithValue("$product_id", id);
        var sources = new List<NightlyProductSource>(summary.SourceCount);
        using (var reader = await sourcesCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                sources.Add(new NightlyProductSource(
                    reader.GetInt32(0),
                    ParseEnum<NightlyProductSourceKind>(reader.GetString(1)),
                    Guid.ParseExact(reader.GetString(2), "N"),
                    reader.GetString(3),
                    await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : Guid.ParseExact(reader.GetString(4), "N"),
                    new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero)));
            }
        }
        if (sources.Count != summary.SourceCount || sources.Where((source, index) => source.Ordinal != index).Any())
        {
            throw new InvalidDataException("Nightly product lineage is incomplete.");
        }
        return new NightlyProductDetail(
            summary, outputIdentity, recipeIdentity, recipeName, variant, rig, payloadSha, payloadBytes,
            renditionSha, renditionBytes, provenanceSha, sources)
        { Occurrence = occurrence };
    }

    private static NightlyProductSummary ReadSummary(SqliteDataReader reader) => new(
        Guid.ParseExact(reader.GetString(0), "N"),
        ParseEnum<NightlyProductKind>(reader.GetString(2)),
        ParseEnum<NightlyProductScope>(reader.GetString(3)),
        DateOnly.ParseExact(reader.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture),
        new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
        new DateTimeOffset(reader.GetInt64(6), TimeSpan.Zero),
        reader.GetInt32(7),
        reader.GetInt64(29) != 0,
        reader.GetInt32(15),
        reader.GetInt32(16),
        ParseEnum<CameraPixelFormat>(reader.GetString(14)),
        reader.GetInt32(24),
        new DateTimeOffset(reader.GetInt64(25), TimeSpan.Zero),
        new DateTimeOffset(reader.GetInt64(26), TimeSpan.Zero),
        TimeSpan.FromTicks(reader.GetInt64(27)),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(28)));

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant filter text is appended; values remain parameterized.")]
    private static async ValueTask<List<NightlyWindowState>> ReadWindowsAsync(
        SqliteConnection connection,
        string filter,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT state.kind, state.scope, state.window_start_utc_ticks, state.window_end_utc_ticks,
                   state.observing_date, state.fingerprint_sha256, state.disposition, state.reason_code,
                   state.candidate_count, state.admitted_count, state.exclusions_json, state.evaluated_unix_ms,
                   (SELECT group_concat(pointer.part_ordinal || ':' || pointer.product_id, ',')
                    FROM nightly_window_products AS pointer
                    WHERE pointer.kind = state.kind AND pointer.scope = state.scope
                      AND pointer.window_start_utc_ticks = state.window_start_utc_ticks
                      AND pointer.occurrence_identity_sha256 = state.occurrence_identity_sha256),
                   state.occurrence_json
            FROM nightly_windows AS state
            {filter}
            ORDER BY state.window_start_utc_ticks, state.kind, state.scope
            LIMIT 1025;
            """;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        var windows = new List<NightlyWindowState>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var status = new NightlyProductWindowStatus(
                ParseEnum<NightlyProductKind>(reader.GetString(0)),
                ParseEnum<NightlyProductScope>(reader.GetString(1)),
                DateOnly.ParseExact(reader.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
                new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero),
                ParseEnum<NightlyProductWindowDisposition>(reader.GetString(6)),
                await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(7),
                reader.GetInt32(8),
                reader.GetInt32(9),
                new SortedDictionary<string, int>(
                    Deserialize<Dictionary<string, int>>(reader.GetString(10)), StringComparer.Ordinal),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(11)))
            { Occurrence = Deserialize<LocalAutomationOccurrence>(reader.GetString(13)) };
            ValidateOccurrence(status.Occurrence, status.ObservingDate, status.WindowStartUtc, status.WindowEndUtc);
            // Pointers are ordered by part explicitly; group_concat order is not part of SQLite's contract.
            var products = await reader.IsDBNullAsync(12, cancellationToken).ConfigureAwait(false)
                ? []
                : reader.GetString(12).Split(',')
                    .Select(static pointer => pointer.Split(':'))
                    .OrderBy(static pointer => int.Parse(pointer[0], NumberStyles.None, CultureInfo.InvariantCulture))
                    .Select(static pointer => Guid.ParseExact(pointer[1], "N"))
                    .ToArray();
            windows.Add(new NightlyWindowState(status, reader.GetString(5), products));
        }
        if (windows.Count > NightlyProductContract.MaximumListedProducts)
            throw new InvalidDataException("The window listing exceeds its declared bound; it cannot represent full coverage.");
        return windows;
    }

    private async ValueTask<byte[]> ReadVerifiedAsync(
        string storedRelativePath,
        long expectedBytes,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var root = ResolveRoot();
        var path = ResolveConfinedPath(root, FromStoredPath(storedRelativePath));
        RawIngressFileStore.EnsureNoSymbolicLinks(root, path);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != expectedBytes || expectedBytes > int.MaxValue)
        {
            throw new InvalidDataException("A published nightly product file is missing or has the wrong length.");
        }
        var content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                Convert.ToHexString(SHA256.HashData(content)), expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A published nightly product file failed checksum verification.");
        }
        return content;
    }

    [SuppressMessage("Reliability", "CA1849:Call async methods when in an async method",
        Justification = "Flush(true) is the only API that forces the file to stable storage before the move.")]
    private static void WriteDurably(string root, string path, ReadOnlySpan<byte> content)
    {
        var temporary = string.Concat(path, ".", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), ".tmp");
        try
        {
            using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            RawIngressFileStore.EnsureNoSymbolicLinks(root, temporary);
            if (new FileInfo(path).LinkTarget is not null)
            {
                throw new IOException("A nightly product file must not be a symbolic link.");
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Creates or verifies the schema once per process. A database written by a newer schema, or one whose objects
    /// were altered, is refused rather than migrated in place.
    /// </summary>
    private async ValueTask EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (_schemaReady)
        {
            return;
        }
        using var transaction = BeginImmediate(connection);
        var version = await ScalarLongAsync(connection, transaction, "PRAGMA user_version;", cancellationToken)
            .ConfigureAwait(false);
        if (version > NightlyProductContract.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Nightly product schema {version} is newer than supported schema "
                + $"{NightlyProductContract.CurrentSchemaVersion}.");
        }
        if (version == 0)
        {
            var objectCount = await ScalarLongAsync(
                connection, transaction, "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';",
                cancellationToken).ConfigureAwait(false);
            if (objectCount != 0)
            {
                throw new InvalidDataException(
                    "Nightly product storage exists without a schema version; archive the database before starting "
                    + "this CameraAgent.");
            }
            await ExecuteAsync(connection, transaction, SchemaSql, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection, transaction, $"PRAGMA user_version = {NightlyProductContract.CurrentSchemaVersion};",
                cancellationToken).ConfigureAwait(false);
        }
        else if (version != NightlyProductContract.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Nightly product schema {version} is unsupported.");
        }
        var schemaObjects = await ScalarLongAsync(
            connection, transaction, "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';",
            cancellationToken).ConfigureAwait(false);
        if (schemaObjects != ExpectedSchemaObjectCount)
        {
            throw new InvalidDataException("Nightly product SQLite schema is incomplete or drifted.");
        }
        var integrity = await ScalarStringAsync(connection, transaction, "PRAGMA integrity_check;", cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(integrity, "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Nightly product SQLite storage failed its integrity check.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _schemaReady = true;
    }

    [SuppressMessage("Reliability", "CA1849:Call async methods when in an async method",
        Justification = "Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.")]
    private static SqliteTransaction BeginImmediate(SqliteConnection connection)
        => connection.BeginTransaction(deferred: false);

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant PRAGMA statements with a validated numeric bound are executed.")]
    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var path = ResolveDatabasePath();
        EnsureDatabaseFilesArePhysical(path);
        var connection = new SqliteConnection(ResolveConnectionString());
        await Sqlite.SqliteConnectionConfigurationGate.OpenAndConfigureAsync(
            connection,
            async (configuredConnection, token) =>
        {
            using var command = configuredConnection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout = {_options.RawIngressSqliteBusyTimeoutSeconds * 1000};";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            command.CommandText = "PRAGMA synchronous = FULL;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            command.CommandText = "PRAGMA foreign_keys = ON;";
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            command.CommandText = "PRAGMA journal_mode = WAL;";
            var mode = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Nightly product SQLite storage could not enable write-ahead logging.");
            }
        }, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static void EnsureDatabaseFilesArePhysical(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            if (new FileInfo(candidate).LinkTarget is not null)
            {
                throw new InvalidDataException("Nightly product SQLite storage must not be a symbolic link.");
            }
        }
    }

    private string ResolveConnectionString()
    {
        _connectionString ??= new SqliteConnectionStringBuilder
        {
            DataSource = ResolveDatabasePath(),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = _options.RawIngressSqliteBusyTimeoutSeconds
        }.ToString();
        return _connectionString;
    }

    private string ResolveDatabasePath()
    {
        if (_databasePath is not null)
        {
            return _databasePath;
        }
        var root = ResolveRoot();
        var directory = ResolveConfinedPath(root, DirectoryName);
        Directory.CreateDirectory(directory);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, directory);
        _databasePath = ResolveConfinedPath(root, Path.Combine(DirectoryName, FileName));
        return _databasePath;
    }

    private string ResolveRoot()
    {
        var root = Path.GetFullPath(_options.RawIngressRoot);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ResolveConfinedPath(string root, string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var prefix = string.Concat(Path.TrimEndingDirectorySeparator(root), Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.StartsWith(prefix, comparison)
            ? path
            : throw new InvalidOperationException("A nightly product path escapes the CameraAgent data root.");
    }

    private static string ToStoredPath(string relativePath) => relativePath.Replace(Path.DirectorySeparatorChar, '/');

    private static string FromStoredPath(string storedPath) => storedPath.Replace('/', Path.DirectorySeparatorChar);

    private static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, SerializerOptions);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, SerializerOptions)
        ?? throw new InvalidDataException("Nightly product storage contains an empty JSON value.");

    private static T ParseEnum<T>(string value) where T : struct, Enum
        => Enum.TryParse<T>(value, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : throw new InvalidDataException($"Nightly product storage contains an unknown {typeof(T).Name} value.");

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant schema and statement text is passed; values remain parameterized.")]
    private static async ValueTask<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant statement text is passed.")]
    private static async ValueTask<long> ScalarLongAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant statement text is passed.")]
    private static async ValueTask<string?> ScalarStringAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }
}
