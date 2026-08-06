-- SQLCMD variables: DatabaseName, QueryStoreToken, WindowMinutes. Connect directly to DatabaseName.
-- Never projects query text, plans, hashes, object names, parameters, users, or identifiers.
SET NOCOUNT ON;

DECLARE @databaseName sysname = N'$(ESCAPE_SQUOTE(DatabaseName))';
DECLARE @ownerToken varchar(32) = '$(ESCAPE_SQUOTE(QueryStoreToken))';
DECLARE @windowMinutes int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(WindowMinutes))');
IF ISNULL(TRY_CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')), 0) <> 16
    THROW 51255, 'This toolkit supports SQL Server 2022 only.', 1;
IF DB_NAME() <> @databaseName OR DB_ID() <= 4 OR @databaseName = N'' OR @databaseName LIKE N'%[^0-9A-Za-z_-]%'
    THROW 51255, 'Connect directly to the validated non-system DatabaseName.', 1;
IF (SELECT [compatibility_level] FROM sys.databases WHERE [database_id] = DB_ID()) <> 160
    THROW 51255, 'The target database must use SQL Server 2022 compatibility level 160.', 1;
IF @windowMinutes IS NULL OR @windowMinutes NOT BETWEEN 1 AND 1440
    THROW 51255, 'WindowMinutes must be between 1 and 1440.', 1;
IF LEN(@ownerToken) <> 32 OR @ownerToken COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9a-f]%'
   OR NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE [class]=0 AND [name]=N'HvoSqlOps_QueryStoreOwner'
       AND CONVERT(varchar(32), [value]) COLLATE Latin1_General_100_BIN2=@ownerToken)
    THROW 51255, 'Query Store inspection does not own the active toolkit token.', 1;

;WITH window_runtime AS (
    SELECT runtime.[plan_id], runtime.[count_executions], runtime.[avg_duration], runtime.[avg_cpu_time],
        runtime.[avg_logical_io_reads], runtime.[avg_logical_io_writes], intervals.[start_time], intervals.[end_time]
    FROM sys.query_store_runtime_stats AS runtime
    JOIN sys.query_store_runtime_stats_interval AS intervals
      ON intervals.[runtime_stats_interval_id] = runtime.[runtime_stats_interval_id]
    WHERE intervals.[end_time] >= DATEADD(minute, -@windowMinutes, SYSUTCDATETIME())),
window_plan_counts AS (
    SELECT plans.[query_id], COUNT_BIG(DISTINCT plans.[plan_id]) AS [plans_per_query]
    FROM sys.query_store_plan AS plans
    JOIN window_runtime AS runtime ON runtime.[plan_id] = plans.[plan_id]
    GROUP BY plans.[query_id])
SELECT
    N'query-store-runtime' AS [area],
    CONVERT(datetime2(0), MIN(runtime.[start_time])) AS [window_start_utc],
    CONVERT(datetime2(0), MAX(runtime.[end_time])) AS [window_end_utc],
    COUNT_BIG(DISTINCT queries.[query_id]) AS [query_count],
    COUNT_BIG(DISTINCT plans.[plan_id]) AS [plan_count],
    COALESCE(SUM(CONVERT(bigint, runtime.[count_executions])), 0) AS [execution_count],
    COALESCE(SUM(CONVERT(decimal(38, 0), runtime.[avg_duration]) * runtime.[count_executions]), 0) AS [weighted_duration_microseconds],
    COALESCE(SUM(CONVERT(decimal(38, 0), runtime.[avg_cpu_time]) * runtime.[count_executions]), 0) AS [weighted_cpu_microseconds],
    COALESCE(SUM(CONVERT(decimal(38, 0), runtime.[avg_logical_io_reads]) * runtime.[count_executions]), 0) AS [weighted_logical_reads],
    COALESCE(SUM(CONVERT(decimal(38, 0), runtime.[avg_logical_io_writes]) * runtime.[count_executions]), 0) AS [weighted_logical_writes],
    COALESCE(MAX(plan_counts.[plans_per_query]), 0) AS [maximum_plan_variation]
FROM sys.query_store_query AS queries
JOIN sys.query_store_plan AS plans ON plans.[query_id] = queries.[query_id]
JOIN window_runtime AS runtime ON runtime.[plan_id] = plans.[plan_id]
JOIN window_plan_counts AS plan_counts ON plan_counts.[query_id] = queries.[query_id];
