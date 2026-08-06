-- SQLCMD variables: DatabaseName. Connect directly to DatabaseName.
-- Observation only: no thresholds, recommendations, ALTER INDEX, or UPDATE STATISTICS.
SET NOCOUNT ON;

DECLARE @databaseName sysname = N'$(ESCAPE_SQUOTE(DatabaseName))';
IF ISNULL(TRY_CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')), 0) <> 16
    THROW 51255, 'This toolkit supports SQL Server 2022 only.', 1;
IF DB_NAME() <> @databaseName OR DB_ID() <= 4 OR @databaseName = N'' OR @databaseName LIKE N'%[^0-9A-Za-z_-]%'
    THROW 51255, 'Connect directly to the validated non-system DatabaseName.', 1;
IF (SELECT [compatibility_level] FROM sys.databases WHERE [database_id] = DB_ID()) <> 160
    THROW 51255, 'The target database must use SQL Server 2022 compatibility level 160.', 1;

SELECT
    N'statistics-observation' AS [area],
    COUNT_BIG(*) AS [statistics_count],
    SUM(CONVERT(bigint, properties.[rows])) AS [observed_rows],
    SUM(CONVERT(bigint, properties.[modification_counter])) AS [modifications_since_update],
    MIN(properties.[last_updated]) AS [oldest_last_update_server_local],
    MAX(properties.[last_updated]) AS [newest_last_update_server_local]
FROM sys.stats AS stats
JOIN sys.tables AS tables ON tables.[object_id] = stats.[object_id] AND tables.[is_ms_shipped] = 0
OUTER APPLY sys.dm_db_stats_properties(stats.[object_id], stats.[stats_id]) AS properties;

;WITH physical_by_partition AS (
    SELECT [object_id], [index_id], [partition_number],
        SUM(CONVERT(bigint, [page_count])) AS [page_count],
        SUM(CONVERT(decimal(38, 4), [avg_fragmentation_in_percent]) * CONVERT(bigint, [page_count])) AS [weighted_fragmentation]
    FROM sys.dm_db_index_physical_stats(DB_ID(), NULL, NULL, NULL, N'LIMITED')
    WHERE [index_level] = 0 AND [alloc_unit_type_desc] = N'IN_ROW_DATA'
    GROUP BY [object_id], [index_id], [partition_number]),
physical_by_index AS (
    SELECT [object_id], [index_id], COUNT_BIG(*) AS [partition_count], SUM([page_count]) AS [page_count],
        SUM([weighted_fragmentation]) AS [weighted_fragmentation]
    FROM physical_by_partition
    GROUP BY [object_id], [index_id])
SELECT
    N'index-observation' AS [area],
    SUM(physical.[partition_count]) AS [partition_count],
    SUM(physical.[page_count]) AS [page_count],
    CONVERT(decimal(9, 4), COALESCE(SUM(physical.[weighted_fragmentation]) / NULLIF(SUM(physical.[page_count]), 0), 0)) AS [average_fragmentation_percent],
    SUM(CONVERT(bigint, COALESCE(usage_stats.[user_seeks], 0))) AS [user_seeks],
    SUM(CONVERT(bigint, COALESCE(usage_stats.[user_scans], 0))) AS [user_scans],
    SUM(CONVERT(bigint, COALESCE(usage_stats.[user_lookups], 0))) AS [user_lookups],
    SUM(CONVERT(bigint, COALESCE(usage_stats.[user_updates], 0))) AS [user_updates]
FROM physical_by_index AS physical
JOIN sys.indexes AS indexes ON indexes.[object_id] = physical.[object_id] AND indexes.[index_id] = physical.[index_id]
JOIN sys.tables AS tables ON tables.[object_id] = indexes.[object_id] AND tables.[is_ms_shipped] = 0
LEFT JOIN sys.dm_db_index_usage_stats AS usage_stats
  ON usage_stats.[database_id] = DB_ID() AND usage_stats.[object_id] = indexes.[object_id] AND usage_stats.[index_id] = indexes.[index_id];
