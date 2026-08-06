-- SQLCMD variables: DatabaseName, QueryStoreToken, OriginalDesiredState, OriginalActualState,
-- OriginalReadonlyReason, OriginalMaxStorageMb, OriginalRetentionDays, OriginalIntervalMinutes,
-- OriginalMaxPlansPerQuery, OriginalFlushSeconds, OriginalCaptureMode, OriginalCleanupMode,
-- OriginalWaitStatsMode, ConfiguredMaxStorageMb, ConfiguredRetentionDays,
-- ConfiguredIntervalMinutes, ConfiguredMaxPlansPerQuery.
-- Connect directly to DatabaseName. Successful rollback removes the exact ownership token.
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @databaseName sysname = N'$(ESCAPE_SQUOTE(DatabaseName))';
DECLARE @ownerToken varchar(32) = '$(ESCAPE_SQUOTE(QueryStoreToken))';
DECLARE @rollbackOwner varchar(41) = @ownerToken + ':rollback';
DECLARE @originalDesiredState nvarchar(30) = UPPER(N'$(ESCAPE_SQUOTE(OriginalDesiredState))');
DECLARE @originalActualState nvarchar(30) = UPPER(N'$(ESCAPE_SQUOTE(OriginalActualState))');
DECLARE @originalReadonlyReason int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(OriginalReadonlyReason))');
DECLARE @maxStorageMb int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(OriginalMaxStorageMb))');
DECLARE @retentionDays int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(OriginalRetentionDays))');
DECLARE @intervalMinutes int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(OriginalIntervalMinutes))');
DECLARE @maxPlansPerQuery int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(OriginalMaxPlansPerQuery))');
DECLARE @flushSeconds int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(OriginalFlushSeconds))');
DECLARE @captureMode nvarchar(10) = UPPER(N'$(ESCAPE_SQUOTE(OriginalCaptureMode))');
DECLARE @cleanupMode nvarchar(4) = UPPER(N'$(ESCAPE_SQUOTE(OriginalCleanupMode))');
DECLARE @waitStatsMode nvarchar(3) = UPPER(N'$(ESCAPE_SQUOTE(OriginalWaitStatsMode))');
DECLARE @configuredMaxStorageMb int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(ConfiguredMaxStorageMb))');
DECLARE @configuredRetentionDays int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(ConfiguredRetentionDays))');
DECLARE @configuredIntervalMinutes int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(ConfiguredIntervalMinutes))');
DECLARE @configuredMaxPlansPerQuery int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(ConfiguredMaxPlansPerQuery))');

IF ISNULL(TRY_CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')), 0) <> 16
    THROW 51255, 'This toolkit supports SQL Server 2022 only.', 1;
IF DB_NAME() <> @databaseName OR DB_ID() <= 4 OR @databaseName = N'' OR @databaseName LIKE N'%[^0-9A-Za-z_-]%'
    THROW 51255, 'Connect directly to the validated non-system DatabaseName.', 1;
IF (SELECT [compatibility_level] FROM sys.databases WHERE [database_id] = DB_ID()) <> 160
    THROW 51255, 'The target database must use SQL Server 2022 compatibility level 160.', 1;
IF LEN(@ownerToken) <> 32 OR @ownerToken COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9a-f]%'
    THROW 51255, 'QueryStoreToken must contain exactly 32 lowercase hexadecimal characters.', 1;
IF @originalDesiredState NOT IN (N'OFF', N'READ_ONLY', N'READ_WRITE')
   OR @originalActualState NOT IN (N'OFF', N'READ_ONLY', N'READ_WRITE') OR @originalReadonlyReason IS NULL
   OR @originalDesiredState <> @originalActualState OR @captureMode = N'CUSTOM'
   OR @maxStorageMb NOT BETWEEN 1 AND 2147483647 OR @retentionDays NOT BETWEEN 1 AND 367
   OR @intervalMinutes NOT IN (1, 5, 10, 15, 30, 60, 1440) OR @maxPlansPerQuery NOT BETWEEN 1 AND 200
   OR @flushSeconds NOT BETWEEN 60 AND 86400 OR @captureMode NOT IN (N'AUTO', N'ALL', N'NONE')
   OR @cleanupMode NOT IN (N'AUTO', N'OFF') OR @waitStatsMode NOT IN (N'ON', N'OFF')
    THROW 51255, 'Captured Query Store rollback values are invalid or incomplete.', 1;
IF @configuredMaxStorageMb NOT BETWEEN 100 AND 10240 OR @configuredRetentionDays NOT BETWEEN 1 AND 30
   OR @configuredIntervalMinutes NOT BETWEEN 1 AND 60 OR @configuredMaxPlansPerQuery NOT BETWEEN 1 AND 200
    THROW 51255, 'Expected configured Query Store values are invalid.', 1;

DECLARE @lockResult int;
DECLARE @rollback nvarchar(max) = N'ALTER DATABASE CURRENT SET QUERY_STORE = ON
    (OPERATION_MODE=READ_WRITE, CLEANUP_POLICY=(STALE_QUERY_THRESHOLD_DAYS=' + CONVERT(nvarchar(11), @retentionDays) + N'),
     DATA_FLUSH_INTERVAL_SECONDS=' + CONVERT(nvarchar(11), @flushSeconds) + N', INTERVAL_LENGTH_MINUTES=' + CONVERT(nvarchar(11), @intervalMinutes) + N',
     MAX_STORAGE_SIZE_MB=' + CONVERT(nvarchar(11), @maxStorageMb) + N', SIZE_BASED_CLEANUP_MODE=' + @cleanupMode + N',
     QUERY_CAPTURE_MODE=' + @captureMode + N', MAX_PLANS_PER_QUERY=' + CONVERT(nvarchar(11), @maxPlansPerQuery) + N',
     WAIT_STATS_CAPTURE_MODE=' + @waitStatsMode + N');';
DECLARE @safeStage int = 1;
BEGIN TRY
    BEGIN TRANSACTION;
    EXEC @lockResult = sys.sp_getapplock @Resource=N'HvoSqlOps.QueryStore.Owner', @LockMode=N'Exclusive',
        @LockOwner=N'Transaction', @LockTimeout=0;
    IF @lockResult < 0
        THROW 51255, 'Query Store toolkit ownership could not be serialized.', 1;
    IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE [class]=0 AND [name]=N'HvoSqlOps_QueryStoreOwner'
        AND CONVERT(varchar(32), [value]) COLLATE Latin1_General_100_BIN2=@ownerToken)
        THROW 51255, 'Query Store rollback does not own the active toolkit token.', 1;
    IF NOT EXISTS (
        SELECT 1 FROM sys.database_query_store_options
        WHERE [desired_state_desc]=N'READ_WRITE' AND [actual_state_desc]=N'READ_WRITE' AND [readonly_reason]=0
          AND [max_storage_size_mb]=@configuredMaxStorageMb AND [stale_query_threshold_days]=@configuredRetentionDays
          AND [interval_length_minutes]=@configuredIntervalMinutes AND [max_plans_per_query]=@configuredMaxPlansPerQuery
          AND [flush_interval_seconds]=900 AND [query_capture_mode_desc]=N'AUTO'
          AND [size_based_cleanup_mode_desc]=N'AUTO' AND [wait_stats_capture_mode_desc]=N'ON')
        THROW 51255, 'Query Store no longer matches the complete toolkit configured state.', 1;
    EXEC sys.sp_updateextendedproperty @name=N'HvoSqlOps_QueryStoreOwner', @value=@rollbackOwner;
    COMMIT TRANSACTION;

    SET @safeStage = 2;
    EXEC sys.sp_executesql @rollback;
    SET @safeStage = 3;
    IF NOT EXISTS (
    SELECT 1 FROM sys.database_query_store_options
    WHERE [desired_state_desc]=N'READ_WRITE' AND [actual_state_desc]=N'READ_WRITE' AND [readonly_reason]=0
      AND [max_storage_size_mb]=@maxStorageMb AND [stale_query_threshold_days]=@retentionDays
      AND [interval_length_minutes]=@intervalMinutes AND [max_plans_per_query]=@maxPlansPerQuery
      AND [flush_interval_seconds]=@flushSeconds AND [query_capture_mode_desc]=@captureMode
      AND [size_based_cleanup_mode_desc]=@cleanupMode AND [wait_stats_capture_mode_desc]=@waitStatsMode)
        THROW 51255, 'Query Store options were not restored while enabled.', 1;

    SET @safeStage = 4;
    IF @originalDesiredState = N'OFF'
        ALTER DATABASE CURRENT SET QUERY_STORE = OFF;
    ELSE IF @originalDesiredState = N'READ_ONLY'
        ALTER DATABASE CURRENT SET QUERY_STORE (OPERATION_MODE=READ_ONLY);

    SET @safeStage = 5;
    IF NOT EXISTS (
    SELECT 1 FROM sys.database_query_store_options
    WHERE [desired_state_desc]=@originalDesiredState AND [actual_state_desc]=@originalActualState
      AND [readonly_reason]=@originalReadonlyReason AND [max_storage_size_mb]=@maxStorageMb
      AND [stale_query_threshold_days]=@retentionDays AND [interval_length_minutes]=@intervalMinutes
      AND [max_plans_per_query]=@maxPlansPerQuery AND [flush_interval_seconds]=@flushSeconds
      AND [query_capture_mode_desc]=@captureMode AND [size_based_cleanup_mode_desc]=@cleanupMode
      AND [wait_stats_capture_mode_desc]=@waitStatsMode)
        THROW 51255, 'Query Store rollback did not restore every observable baseline value.', 1;

    SET @safeStage = 6;
    BEGIN TRANSACTION;
    EXEC @lockResult = sys.sp_getapplock @Resource=N'HvoSqlOps.QueryStore.Owner', @LockMode=N'Exclusive',
        @LockOwner=N'Transaction', @LockTimeout=0;
    IF @lockResult < 0 OR NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE [class]=0
        AND [name]=N'HvoSqlOps_QueryStoreOwner' AND CONVERT(varchar(41), [value]) COLLATE Latin1_General_100_BIN2=@rollbackOwner)
        THROW 51255, 'Query Store ownership changed before exact release.', 1;
    EXEC sys.sp_dropextendedproperty @name=N'HvoSqlOps_QueryStoreOwner';
    COMMIT TRANSACTION;

    SELECT N'query-store-rollback' AS [area], N'released' AS [ownership_state],
        CONVERT(nvarchar(30), [desired_state_desc]) AS [desired_state], CONVERT(nvarchar(30), [actual_state_desc]) AS [actual_state],
        CONVERT(int, [readonly_reason]) AS [readonly_reason], CONVERT(bigint, [max_storage_size_mb]) AS [maximum_storage_mb],
        CONVERT(int, [stale_query_threshold_days]) AS [retention_days], CONVERT(int, [interval_length_minutes]) AS [interval_minutes],
        CONVERT(int, [max_plans_per_query]) AS [maximum_plans_per_query], CONVERT(int, [flush_interval_seconds]) AS [flush_seconds],
        CONVERT(nvarchar(20), [query_capture_mode_desc]) AS [capture_mode],
        CONVERT(nvarchar(20), [size_based_cleanup_mode_desc]) AS [cleanup_mode],
        CONVERT(nvarchar(20), [wait_stats_capture_mode_desc]) AS [wait_stats_mode]
    FROM sys.database_query_store_options;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    DECLARE @safeErrorNumber int = 51400 + @safeStage;
    THROW @safeErrorNumber, 'Query Store rollback failed at the reported safe stage.', 1;
END CATCH;
