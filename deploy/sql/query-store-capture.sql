-- SQLCMD variable: DatabaseName. Connect directly to DatabaseName before configuration.
SET NOCOUNT ON;

DECLARE @databaseName sysname = N'$(ESCAPE_SQUOTE(DatabaseName))';
IF ISNULL(TRY_CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')), 0) <> 16
    THROW 51255, 'This toolkit supports SQL Server 2022 only.', 1;
IF DB_NAME() <> @databaseName OR DB_ID() <= 4 OR @databaseName = N'' OR @databaseName LIKE N'%[^0-9A-Za-z_-]%'
    THROW 51255, 'Connect directly to the validated non-system DatabaseName.', 1;
IF (SELECT [compatibility_level] FROM sys.databases WHERE [database_id] = DB_ID()) <> 160
    THROW 51255, 'The target database must use SQL Server 2022 compatibility level 160.', 1;
IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE [class] = 0 AND [name] = N'HvoSqlOps_QueryStoreOwner')
    THROW 51255, 'A Query Store toolkit owner already exists; resolve it before capture.', 1;

DECLARE @desiredState nvarchar(30);
DECLARE @actualState nvarchar(30);
DECLARE @readonlyReason int;
DECLARE @captureMode nvarchar(20);
SELECT
    @desiredState = CONVERT(nvarchar(30), [desired_state_desc]),
    @actualState = CONVERT(nvarchar(30), [actual_state_desc]),
    @readonlyReason = CONVERT(int, [readonly_reason]),
    @captureMode = CONVERT(nvarchar(20), [query_capture_mode_desc])
FROM sys.database_query_store_options;

IF @desiredState NOT IN (N'OFF', N'READ_ONLY', N'READ_WRITE')
   OR @actualState NOT IN (N'OFF', N'READ_ONLY', N'READ_WRITE')
   OR @desiredState <> @actualState
    THROW 51255, 'Query Store desired and actual states have an unexplained mismatch.', 1;
IF @captureMode = N'CUSTOM'
    THROW 51255, 'CUSTOM Query Store capture policy is not supported by reversible configuration.', 1;

SELECT
    N'query-store-baseline' AS [area],
    @desiredState AS [desired_state],
    @actualState AS [actual_state],
    @readonlyReason AS [readonly_reason],
    CONVERT(bigint, [max_storage_size_mb]) AS [maximum_storage_mb],
    CONVERT(int, [stale_query_threshold_days]) AS [retention_days],
    CONVERT(int, [interval_length_minutes]) AS [interval_minutes],
    CONVERT(int, [max_plans_per_query]) AS [maximum_plans_per_query],
    CONVERT(int, [flush_interval_seconds]) AS [flush_seconds],
    CONVERT(nvarchar(20), [query_capture_mode_desc]) AS [capture_mode],
    CONVERT(nvarchar(20), [size_based_cleanup_mode_desc]) AS [cleanup_mode],
    CONVERT(nvarchar(20), [wait_stats_capture_mode_desc]) AS [wait_stats_mode]
FROM sys.database_query_store_options;
