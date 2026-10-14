using System.Diagnostics.CodeAnalysis;
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
            var rows = await ReadFinalEvaluationsAsync(connection,
                "WHERE state.observing_date BETWEEN $first_date AND $last_date AND state.scope = 'Final'",
                cancellationToken,
                ("$first_date", FormatDate(firstDate)),
                ("$last_date", FormatDate(lastDate))).ConfigureAwait(false);
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
            var others = await ReadOtherOutputsAsync(connection, detail, cancellationToken).ConfigureAwait(false);
            return new NightlyProductPresentation(
                detail, Deserialize<List<ProcessingAlgorithmIdentity>>(algorithmsJson), frames.Count, axis, others);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Lists daily final evaluations, newest report date first. Report dates are found from their daily evaluations
    /// through the date index and classified from the same summary the archive calendar reads; a status filter examines
    /// at most <see cref="NightlyProductProjectionContract.MaximumLibraryScannedDates"/> dates per page and then says how
    /// far back it searched rather than reading further.
    /// </summary>
    public async ValueTask<NightlyProductLibraryPage> ListLibraryAsync(
        NightlyProductLibraryQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.DateCount is < 1 or > NightlyProductProjectionContract.MaximumLibraryDates)
            throw new ArgumentOutOfRangeException(nameof(query), "The library page size is outside its declared bound.");
        if (query.ObservingDate is not null && query.Before is not null)
            throw new ArgumentOutOfRangeException(nameof(query), "A single observing day is not paged.");
        if (query.Before == DateOnly.MinValue)
        {
            return new NightlyProductLibraryPage([], null, null);
        }
        var firstDate = query.ObservingDate ?? DateOnly.MinValue;
        var lastDate = query.ObservingDate ?? query.Before?.AddDays(-1) ?? DateOnly.MaxValue;
        // Without a status filter every report date with a daily evaluation matches, so one date past the page is enough.
        var scan = query.State is null ? query.DateCount : NightlyProductProjectionContract.MaximumLibraryScannedDates;
        var kind = (object?)query.Kind?.ToString() ?? DBNull.Value;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            var candidates = await ReadLibraryDatesAsync(connection, firstDate, lastDate, kind, scan + 1, cancellationToken)
                .ConfigureAwait(false);
            var truncated = candidates.Count > scan;
            if (truncated) candidates.RemoveAt(scan);
            if (candidates.Count == 0)
            {
                return new NightlyProductLibraryPage([], null, null);
            }
            var (dateFilter, dateParameters) = InList("$date_", candidates.Select(FormatDate));
            var rows = await ReadFinalEvaluationsAsync(connection,
                $"WHERE state.observing_date IN ({dateFilter}) AND state.scope = 'Final' AND ($kind IS NULL OR state.kind = $kind)",
                cancellationToken, [.. dateParameters, ("$kind", kind)]).ConfigureAwait(false);
            var summaries = Summarize(rows);
            var matches = summaries.Where(summary => summary.DailyDisposition is not null &&
                (query.State is null || NightlyProductLibraryEntry.Classify(summary) == query.State)).ToList();
            var matchedDates = candidates.Where(date => matches.Any(summary => summary.ObservingDate == date)).ToList();
            var shownDates = matchedDates.Take(query.DateCount).ToHashSet();
            DateOnly? nextBefore = matchedDates.Count > query.DateCount ? matchedDates[query.DateCount - 1]
                : truncated ? candidates[^1]
                : null;
            DateOnly? searchedThrough = truncated && shownDates.Count < query.DateCount ? candidates[^1] : null;
            if (shownDates.Count == 0)
            {
                return new NightlyProductLibraryPage([], nextBefore, searchedThrough);
            }
            var entries = await ReadLibraryEntriesAsync(connection, summaries,
                [.. matches.Where(summary => shownDates.Contains(summary.ObservingDate))], kind, cancellationToken)
                .ConfigureAwait(false);
            return new NightlyProductLibraryPage(entries, nextBefore, searchedThrough);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The newest distinct report dates, within inclusive bounds, that hold a daily final evaluation.</summary>
    private static async ValueTask<List<DateOnly>> ReadLibraryDatesAsync(
        SqliteConnection connection,
        DateOnly firstDate,
        DateOnly lastDate,
        object kind,
        int limit,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT state.observing_date
            FROM nightly_windows AS state
            WHERE state.observing_date BETWEEN $first_date AND $last_date AND state.scope = 'Final'
              AND ($kind IS NULL OR state.kind = $kind)
              AND json_extract(state.occurrence_json, '$.sourceWindow.policy.kind') = $window_kind
            ORDER BY state.observing_date DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$first_date", FormatDate(firstDate));
        command.Parameters.AddWithValue("$last_date", FormatDate(lastDate));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$window_kind", nameof(LocalAutomationSourceWindowKind.SunriseDay));
        command.Parameters.AddWithValue("$limit", limit);
        var dates = new List<DateOnly>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            dates.Add(DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        return dates;
    }

    /// <summary>
    /// Joins each shown summary to its daily final evaluation, chosen as <see cref="NightlyProductDay.Daily"/> chooses
    /// it, newest report date first. A choice that disagrees with the summary is a torn read and fails.
    /// </summary>
    private static async ValueTask<List<NightlyProductLibraryEntry>> ReadLibraryEntriesAsync(
        SqliteConnection connection,
        List<NightlyProductDateSummary> summaries,
        List<NightlyProductDateSummary> shown,
        object kind,
        CancellationToken cancellationToken)
    {
        var (dateFilter, dateParameters) = InList("$date_",
            shown.Select(static summary => summary.ObservingDate).Distinct().Select(FormatDate));
        var windows = await ReadWindowsAsync(connection, $"""
            WHERE state.observing_date IN ({dateFilter}) AND state.scope = 'Final' AND ($kind IS NULL OR state.kind = $kind)
              AND json_extract(state.occurrence_json, '$.sourceWindow.policy.kind') = $window_kind
            """, cancellationToken,
            [.. dateParameters, ("$kind", kind), ("$window_kind", nameof(LocalAutomationSourceWindowKind.SunriseDay))])
            .ConfigureAwait(false);
        var products = (await ReadProductsAsync(connection,
                [.. windows.SelectMany(static window => window.ProductIds).Distinct()], cancellationToken).ConfigureAwait(false))
            .ToDictionary(static product => product.ProductId);
        var records = windows.Select(window => new NightlyProductWindowRecord(
            window.Status,
            [.. window.ProductIds.Select(id => products.TryGetValue(id, out var product)
                ? product
                : throw new InvalidDataException("A current nightly product is not published."))]))
            .ToList();
        return [.. shown.Select(summary =>
            {
                var daily = NightlyProductDay.Preferred(records.Where(record =>
                    record.Status.ObservingDate == summary.ObservingDate && record.Status.Kind == summary.Kind &&
                    string.Equals(record.ReportingPeriod.IdentitySha256, summary.ReportingPeriodSha256, StringComparison.Ordinal)));
                if (daily is null || daily.FinalProduct?.ProductId != summary.DailyProductId)
                    throw new InvalidDataException("A daily nightly evaluation disagrees with its summary.");
                var otherPeriod = summaries.Any(other => other.ObservingDate == summary.ObservingDate &&
                    other.Kind == summary.Kind &&
                    !string.Equals(other.ReportingPeriodSha256, summary.ReportingPeriodSha256, StringComparison.Ordinal));
                return new NightlyProductLibraryEntry(summary, daily, otherPeriod);
            })
            .OrderByDescending(static entry => entry.Summary.ObservingDate)
            .ThenBy(static entry => entry.Summary.Kind)
            .ThenBy(static entry => entry.Daily.ReportingPeriod.StartUtc)
            .ThenBy(static entry => entry.Summary.ReportingPeriodSha256, StringComparer.Ordinal)];
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant filter text is appended; values remain parameterized.")]
    private static async ValueTask<List<FinalEvaluation>> ReadFinalEvaluationsAsync(
        SqliteConnection connection,
        string filter,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        // The window kind and source period are read from the retained occurrence rather than inferred from the
        // span length or the report date; one report date can hold periods of different sites or time-zone rules.
        command.CommandText = $"""
            SELECT state.observing_date, state.kind, json_extract(state.occurrence_json, '$.sourceWindow.policy.kind'),
                   state.window_start_utc_ticks, state.disposition, state.reason_code, state.evaluated_unix_ms,
                   json_extract(state.occurrence_json, '$.sourceWindow.reportingPeriod.identitySha256'),
                   (SELECT pointer.product_id
                    FROM nightly_window_products AS pointer
                    JOIN nightly_products AS product ON product.product_id = pointer.product_id
                    WHERE pointer.occurrence_identity_sha256 = state.occurrence_identity_sha256
                      AND pointer.kind = state.kind AND pointer.scope = state.scope
                      AND pointer.window_start_utc_ticks = state.window_start_utc_ticks
                      AND product.scope = 'Final')
            FROM nightly_windows AS state
            {filter}
            LIMIT $limit;
            """;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        command.Parameters.AddWithValue("$limit", NightlyProductProjectionContract.MaximumSummarizedWindows + 1);
        var rows = new List<FinalEvaluation>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ||
                await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("A recorded nightly evaluation does not retain its source window.");
            rows.Add(new FinalEvaluation(
                DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                ParseEnum<NightlyProductKind>(reader.GetString(1)),
                ParseEnum<LocalAutomationSourceWindowKind>(reader.GetString(2)),
                reader.GetInt64(3),
                ParseEnum<NightlyProductWindowDisposition>(reader.GetString(4)),
                await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(5),
                reader.GetInt64(6),
                reader.GetString(7),
                await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false)
                    ? null : Guid.ParseExact(reader.GetString(8), "N")));
        }
        if (rows.Count > NightlyProductProjectionContract.MaximumSummarizedWindows)
            throw new InvalidDataException("The evaluation summary exceeds its declared bound; it cannot represent full coverage.");
        return rows;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only generated parameter names are appended; values remain parameterized.")]
    private static async ValueTask<List<NightlyProductSummary>> ReadProductsAsync(
        SqliteConnection connection,
        List<Guid> productIds,
        CancellationToken cancellationToken)
    {
        if (productIds.Count == 0)
        {
            return [];
        }
        var (idFilter, idParameters) = InList("$product_",
            productIds.Select(static id => id.ToString("N", CultureInfo.InvariantCulture)));
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ProductColumns}
            FROM nightly_products AS product
            WHERE product.product_id IN ({idFilter});
            """;
        foreach (var (name, value) in idParameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        var products = new List<NightlyProductSummary>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            products.Add(ReadSummary(reader));
        }
        return products;
    }

    /// <summary>Generated parameter names for an IN list, with their values; no value is ever written into the text.</summary>
    private static (string Filter, (string Name, object Value)[] Parameters) InList(string prefix, IEnumerable<string> values)
    {
        var parameters = values.Select((value, index) =>
            (string.Create(CultureInfo.InvariantCulture, $"{prefix}{index}"), (object)value)).ToArray();
        return (string.Join(", ", parameters.Select(static parameter => parameter.Item1)), parameters);
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
        [.. rows.GroupBy(static row => (row.Date, row.Kind, row.ReportingPeriodSha256))
            .OrderBy(static group => group.Key.Date).ThenBy(static group => group.Key.Kind)
            .ThenBy(static group => group.Key.ReportingPeriodSha256, StringComparer.Ordinal)
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
                    group.Key.ReportingPeriodSha256,
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
    /// The exposure starts of every distinct preview frame in a product's full lineage, oldest first. Nightly-product
    /// sources are followed to their own sources; published rows only reference earlier rows, so the walk terminates,
    /// and a frame reached through both a rollup and its parent counts once.
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
            SELECT DISTINCT source.artifact_id, source.observation_started_utc_ticks
            FROM nightly_product_sources AS source
            JOIN lineage ON source.product_id = lineage.product_id
            WHERE source.source_kind = 'PreviewFrame'
            ORDER BY source.observation_started_utc_ticks, source.artifact_id
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$product_id", productId.ToString("N", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$limit", NightlyProductProjectionContract.MaximumLineageFrames + 1);
        var frames = new List<DateTimeOffset>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            frames.Add(new DateTimeOffset(reader.GetInt64(1), TimeSpan.Zero));
        }
        if (frames.Count > NightlyProductProjectionContract.MaximumLineageFrames)
            throw new InvalidDataException("The product lineage exceeds its declared frame bound.");
        return frames;
    }

    /// <summary>
    /// Every other published output of the same retained source period, window and part, current or not. Nothing here
    /// records which output followed which.
    /// </summary>
    private static async ValueTask<List<NightlyProductSummary>> ReadOtherOutputsAsync(
        SqliteConnection connection,
        NightlyProductDetail detail,
        CancellationToken cancellationToken)
    {
        var summary = detail.Summary;
        var period = detail.Occurrence.SourceWindow?.ReportingPeriod.IdentitySha256
            ?? throw new InvalidDataException("A nightly product does not retain its source window.");
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {ProductColumns}
            FROM nightly_products AS product
            WHERE product.observing_date = $observing_date AND product.kind = $kind AND product.scope = $scope
              AND product.window_start_utc_ticks = $start AND product.window_end_utc_ticks = $end
              AND product.part_ordinal = $part_ordinal AND product.product_id <> $product_id
              AND json_extract(product.occurrence_json, '$.sourceWindow.reportingPeriod.identitySha256') = $period
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
        command.Parameters.AddWithValue("$period", period);
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
        string ReportingPeriodSha256,
        Guid? ProductId);
}
