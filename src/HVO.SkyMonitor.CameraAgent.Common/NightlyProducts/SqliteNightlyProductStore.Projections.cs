using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;

/// <summary>
/// Read-only presentation projections over the published catalog. They read existing rows through the existing date
/// and key indexes, never write, and fail rather than truncate when a declared bound is exceeded.
/// </summary>
internal sealed partial class SqliteNightlyProductStore
{
    private static readonly JsonSerializerOptions RecipeOptionsSerializer = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async ValueTask<IReadOnlyList<NightlyProductDateSummary>> SummarizeDatesAsync(
        DateOnly firstDate,
        DateOnly lastDate,
        CancellationToken cancellationToken)
    {
        if (lastDate < firstDate ||
            lastDate.DayNumber - firstDate.DayNumber >= NightlyProductProjectionContract.MaximumSummarizedDates)
        {
            throw new ArgumentOutOfRangeException(nameof(lastDate), "The summarized date range is outside its declared bound.");
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            // The window kind is read from the retained occurrence rather than inferred from the span length.
            command.CommandText = """
                SELECT state.observing_date, state.kind, json_extract(state.occurrence_json, '$.sourceWindow.policy.kind'),
                       state.window_start_utc_ticks, state.disposition, state.reason_code, state.evaluated_unix_ms,
                       (SELECT pointer.product_id
                        FROM nightly_window_products AS pointer
                        JOIN nightly_products AS product ON product.product_id = pointer.product_id
                        WHERE pointer.occurrence_identity_sha256 = state.occurrence_identity_sha256
                          AND pointer.kind = state.kind AND pointer.scope = state.scope
                          AND pointer.window_start_utc_ticks = state.window_start_utc_ticks
                          AND product.scope = 'Final')
                FROM nightly_windows AS state
                WHERE state.observing_date BETWEEN $first_date AND $last_date AND state.scope = 'Final'
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$first_date", FormatDate(firstDate));
            command.Parameters.AddWithValue("$last_date", FormatDate(lastDate));
            command.Parameters.AddWithValue("$limit", NightlyProductProjectionContract.MaximumSummarizedWindows + 1);
            var rows = new List<FinalEvaluation>();
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false))
                    throw new InvalidDataException("A recorded nightly evaluation does not retain its source window kind.");
                rows.Add(new FinalEvaluation(
                    DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ParseEnum<NightlyProductKind>(reader.GetString(1)),
                    ParseEnum<LocalAutomationSourceWindowKind>(reader.GetString(2)),
                    reader.GetInt64(3),
                    ParseEnum<NightlyProductWindowDisposition>(reader.GetString(4)),
                    await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
                    reader.GetInt64(6),
                    await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false)
                        ? null : Guid.ParseExact(reader.GetString(7), "N")));
            }
            if (rows.Count > NightlyProductProjectionContract.MaximumSummarizedWindows)
                throw new InvalidDataException("The evaluation summary exceeds its declared bound; it cannot represent full coverage.");
            return Summarize(rows);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<NightlyProductDay> GetDayAsync(DateOnly observingDate, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            var windows = await ReadWindowsAsync(connection, "WHERE state.observing_date = $observing_date",
                cancellationToken, ("$observing_date", FormatDate(observingDate))).ConfigureAwait(false);
            var products = await ReadDateProductsAsync(connection, observingDate, cancellationToken).ConfigureAwait(false);
            var byId = products.ToDictionary(static product => product.ProductId);
            var records = windows.Select(window => new NightlyProductWindowRecord(
                window.Status,
                [.. window.ProductIds.Select(id => byId.TryGetValue(id, out var product)
                    ? product
                    : throw new InvalidDataException("A current nightly product is not published on its report date."))]))
                .ToList();
            return new NightlyProductDay(observingDate, records, products);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<NightlyProductPresentation?> GetPresentationAsync(
        Guid productId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            var detail = await ReadDetailAsync(connection, null, productId, cancellationToken).ConfigureAwait(false);
            if (detail is null)
            {
                return null;
            }
            var (recipeJson, algorithmsJson) = await ReadRecipeAsync(connection, productId, cancellationToken)
                .ConfigureAwait(false);
            var frames = await ReadLineageFramesAsync(connection, productId, cancellationToken).ConfigureAwait(false);
            var axis = detail.Summary.Kind == NightlyProductKind.Keogram ? CreateTimeAxis(detail, recipeJson, frames) : null;
            var others = await ReadOtherOutputsAsync(connection, detail.Summary, cancellationToken).ConfigureAwait(false);
            return new NightlyProductPresentation(
                detail, Deserialize<List<ProcessingAlgorithmIdentity>>(algorithmsJson), frames.Count, axis, others);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Recomputes the gaps of a planned keogram axis from its lineage frame times exactly as
    /// <see cref="PlannedKeogramComposer"/> bins them: a column is sampled when any frame falls in its half-open bin.
    /// </summary>
    internal static IReadOnlyList<KeogramGap> PlannedGaps(PlannedKeogramAxis axis, int width, IReadOnlyList<DateTimeOffset> frames)
    {
        var occupied = new bool[width];
        foreach (var frame in frames)
        {
            if (frame < axis.StartUtc || frame >= axis.EndUtc)
                throw new InvalidDataException("A keogram lineage frame lies outside its planned period.");
            occupied[(int)((frame - axis.StartUtc).Ticks / axis.ColumnDuration.Ticks)] = true;
        }
        var gaps = new List<KeogramGap>();
        for (var column = 0; column < width; column++)
        {
            if (occupied[column]) continue;
            var first = column;
            while (column < width && !occupied[column]) column++;
            var start = axis.StartUtc.AddTicks(first * axis.ColumnDuration.Ticks);
            var end = column == width ? axis.EndUtc : axis.StartUtc.AddTicks(column * axis.ColumnDuration.Ticks);
            gaps.Add(new KeogramGap(start, end, first, column - first));
        }
        return gaps;
    }

    private static List<NightlyProductDateSummary> Summarize(List<FinalEvaluation> rows) =>
        [.. rows.GroupBy(static row => (row.Date, row.Kind))
            .OrderBy(static group => group.Key.Date).ThenBy(static group => group.Key.Kind)
            .Select(static group =>
            {
                var daily = Preferred(group.Where(static row => row.WindowKind == LocalAutomationSourceWindowKind.SunriseDay));
                var hours = group.Where(static row => row.WindowKind == LocalAutomationSourceWindowKind.CompletedCivilHour)
                    .GroupBy(static row => row.StartTicks)
                    .Select(static hour => Preferred(hour)!)
                    .ToList();
                return new NightlyProductDateSummary(
                    group.Key.Date,
                    group.Key.Kind,
                    daily?.ProductId,
                    daily?.Disposition,
                    daily?.ReasonCode,
                    hours.Count(static hour => hour.ProductId is not null),
                    hours.Count(static hour => hour.ProductId is null));
            })];

    /// <summary>A produced evaluation first, then the latest evaluated; the product ID only makes the choice stable.</summary>
    private static FinalEvaluation? Preferred(IEnumerable<FinalEvaluation> evaluations) => evaluations
        .OrderByDescending(static row => row.ProductId is not null)
        .ThenByDescending(static row => row.EvaluatedUnixMs)
        .ThenBy(static row => row.ProductId)
        .FirstOrDefault();

    private static async ValueTask<(string Recipe, string Algorithms)> ReadRecipeAsync(
        SqliteConnection connection,
        Guid productId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT recipe_json, algorithms_json FROM nightly_products WHERE product_id = $product_id;";
        command.Parameters.AddWithValue("$product_id", productId.ToString("N", CultureInfo.InvariantCulture));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetString(1))
            : throw new InvalidDataException("A nightly product row disappeared during a read.");
    }

    /// <summary>
    /// The exposure starts of every preview frame in a product's full lineage, oldest first. Nightly-product sources
    /// are followed to their own sources; published rows only reference earlier rows, so the walk terminates.
    /// </summary>
    private static async ValueTask<List<DateTimeOffset>> ReadLineageFramesAsync(
        SqliteConnection connection,
        Guid productId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE lineage (product_id) AS (
                SELECT $product_id
                UNION
                SELECT source.artifact_id
                FROM nightly_product_sources AS source
                JOIN lineage ON source.product_id = lineage.product_id
                WHERE source.source_kind = 'NightlyProduct')
            SELECT source.observation_started_utc_ticks
            FROM nightly_product_sources AS source
            JOIN lineage ON source.product_id = lineage.product_id
            WHERE source.source_kind = 'PreviewFrame'
            ORDER BY source.observation_started_utc_ticks
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$product_id", productId.ToString("N", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", NightlyProductProjectionContract.MaximumLineageFrames + 1);
        var frames = new List<DateTimeOffset>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            frames.Add(new DateTimeOffset(reader.GetInt64(0), TimeSpan.Zero));
        }
        if (frames.Count > NightlyProductProjectionContract.MaximumLineageFrames)
            throw new InvalidDataException("The product lineage exceeds its declared frame bound.");
        return frames;
    }

    private static async ValueTask<List<NightlyProductSummary>> ReadOtherOutputsAsync(
        SqliteConnection connection,
        NightlyProductSummary summary,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ProductColumns}
            FROM nightly_products AS product
            WHERE product.observing_date = $observing_date AND product.kind = $kind AND product.scope = $scope
              AND product.window_start_utc_ticks = $start AND product.window_end_utc_ticks = $end
              AND product.part_ordinal = $part_ordinal AND product.product_id <> $product_id
            ORDER BY product.created_unix_ms, product.product_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$observing_date", FormatDate(summary.ObservingDate));
        command.Parameters.AddWithValue("$kind", summary.Kind.ToString());
        command.Parameters.AddWithValue("$scope", summary.Scope.ToString());
        command.Parameters.AddWithValue("$start", summary.WindowStartUtc.UtcTicks);
        command.Parameters.AddWithValue("$end", summary.WindowEndUtc.UtcTicks);
        command.Parameters.AddWithValue("$part_ordinal", summary.PartOrdinal);
        command.Parameters.AddWithValue("$product_id", summary.ProductId.ToString("N", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", NightlyProductContract.MaximumListedProducts + 1);
        var others = new List<NightlyProductSummary>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            others.Add(ReadSummary(reader));
        }
        if (others.Count > NightlyProductContract.MaximumListedProducts)
            throw new InvalidDataException("The product listing exceeds its declared bound; it cannot represent full coverage.");
        return others;
    }

    /// <summary>
    /// Rebuilds a keogram's time axis from its retained recipe options and lineage. A final carries its planned axis;
    /// a segment's actual axis is recomputed from its ordered source times under the options it was composed with, the
    /// same way assembly re-lays it. Any other keogram has no recorded axis.
    /// </summary>
    private static NightlyProductTimeAxis? CreateTimeAxis(
        NightlyProductDetail detail,
        string recipeJson,
        IReadOnlyList<DateTimeOffset> frames)
    {
        KeogramRecipeOptions options;
        try
        {
            options = Deserialize<RecipeIdentityDescriptor>(recipeJson).Options.GetProperty("parameters")
                .Deserialize<KeogramRecipeOptions>(RecipeOptionsSerializer)
                ?? throw new InvalidDataException("A keogram does not retain its recipe options.");
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidDataException("A keogram's retained recipe options cannot be read.", exception);
        }
        try
        {
            if (options.PlannedAxis is { } planned)
            {
                var width = planned.Width(options.MaximumColumnCount);
                var gaps = PlannedGaps(planned, width, frames);
                return new NightlyProductTimeAxis(true, planned.StartUtc, planned.EndUtc, width,
                    planned.ColumnDuration.TotalSeconds, width - gaps.Sum(static gap => gap.ColumnCount), gaps, []);
            }
            if (detail.Summary.Scope != NightlyProductScope.Segment)
            {
                return null;
            }
            var times = detail.Sources.Select(static source => source.ObservationStartedUtc).ToArray();
            var axis = KeogramComposer.ComputeTimeAxis(
                times, options.MaximumGapSeconds, options.MaximumGapColumnCount, options.MaximumColumnCount);
            var columns = KeogramComposer.ComputeFrameColumns(axis);
            return new NightlyProductTimeAxis(false, times[0], times[^1], axis.Width, axis.CadenceSeconds,
                columns.Length, axis.Gaps, [.. columns.Select((column, index) => new KeogramSegmentFrameV1(column, times[index]))]);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("A keogram's retained time axis is inconsistent with its lineage.", exception);
        }
    }

    private sealed record FinalEvaluation(
        DateOnly Date,
        NightlyProductKind Kind,
        LocalAutomationSourceWindowKind WindowKind,
        long StartTicks,
        NightlyProductWindowDisposition Disposition,
        string? ReasonCode,
        long EvaluatedUnixMs,
        Guid? ProductId);
}
