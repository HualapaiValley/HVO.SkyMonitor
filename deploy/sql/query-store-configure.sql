-- SQLCMD variables: DatabaseName, QueryStoreToken, OriginalDesiredState, OriginalActualState,
-- OriginalReadonlyReason, OriginalMaxStorageMb, OriginalRetentionDays, OriginalIntervalMinutes,
-- OriginalMaxPlansPerQuery, OriginalFlushSeconds, OriginalCaptureMode, OriginalCleanupMode,
-- OriginalWaitStatsMode, MaxStorageMb, RetentionDays, IntervalMinutes, MaxPlansPerQuery.
-- Connect directly to DatabaseName. The persistent token serializes the complete collection window.
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @databaseName sysname = N'$(ESCAPE_SQUOTE(DatabaseName))';
DECLARE @ownerToken varchar(32) = '$(ESCAPE_SQUOTE(QueryStoreToken))';
DECLARE @originalDesiredState nvarchar(30) = UPPER(N'$(ESCAPE_SQUOTE(OriginalDesiredState))');
DECLARE @originalActualState nvarchar(30) = UPPER(N'$(ESCAPE_SQUOTE(OriginalActualState))');
DECLARE @originalReadonlyReason int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(OriginalReadonlyReason))');
DECLARE @originalMaxStorageMb bigint = TRY_CONVERT(bigint, N'$(ESCAPE_SQUOTE(OriginalMaxStorageMb))');
DECLARE @originalRetentionDays int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(OriginalRetentionDays))');
DECLARE @originalIntervalMinutes int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(OriginalIntervalMinutes))');
DECLARE @originalMaxPlansPerQuery int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(OriginalMaxPlansPerQuery))');
DECLARE @originalFlushSeconds int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(OriginalFlushSeconds))');
DECLARE @originalCaptureMode nvarchar(20) = UPPER(N'$(ESCAPE_SQUOTE(OriginalCaptureMode))');
DECLARE @originalCleanupMode nvarchar(20) = UPPER(N'$(ESCAPE_SQUOTE(OriginalCleanupMode))');
DECLARE @originalWaitStatsMode nvarchar(20) = UPPER(N'$(ESCAPE_SQUOTE(OriginalWaitStatsMode))');
DECLARE @maxStorageMb int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(MaxStorageMb))');
DECLARE @retentionDays int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(RetentionDays))');
DECLARE @intervalMinutes int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(IntervalMinutes))');
DECLARE @maxPlansPerQuery int = TRY_CONVERT(int, N'$(ESCAPE_SQUOTE(MaxPlansPerQuery))');

IF ISNULL(TRY_CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')), 0) <> 16
    THROW 51255, 'This toolkit supports SQL Server 2022 only.', 1;
IF DB_NAME() <> @databaseName OR DB_ID() <= 4 OR @databaseName = N'' OR @databaseName LIKE N'%[^0-9A-Za-z_-]%'
    THROW 51255, 'Connect directly to the validated non-system DatabaseName.', 1;
IF (SELECT [compatibility_level] FROM sys.databases WHERE [database_id] = DB_ID()) <> 160
    THROW 51255, 'The target database must use SQL Server 2022 compatibility level 160.', 1;
IF LEN(@ownerToken) <> 32 OR @ownerToken COLLATE Latin1_General_100_BIN2 LIKE '%[^0-9a-f]%'
    THROW 51255, 'QueryStoreToken must contain exactly 32 lowercase hexadecimal characters.', 1;
IF @maxStorageMb IS NULL OR @retentionDays IS NULL OR @intervalMinutes IS NULL OR @maxPlansPerQuery IS NULL
   OR @maxStorageMb NOT BETWEEN 100 AND 10240 OR @retentionDays NOT BETWEEN 1 AND 30
   OR @intervalMinutes NOT BETWEEN 1 AND 60 OR @maxPlansPerQuery NOT BETWEEN 1 AND 200
    THROW 51255, 'Query Store bounds are invalid.', 1;
IF @originalDesiredState NOT IN (N'OFF', N'READ_ONLY', N'READ_WRITE')
   OR @originalActualState NOT IN (N'OFF', N'READ_ONLY', N'READ_WRITE') OR @originalReadonlyReason IS NULL
   OR @originalDesiredState <> @originalActualState OR @originalCaptureMode = N'CUSTOM'
   OR @originalMaxStorageMb NOT BETWEEN 1 AND 2147483647 OR @originalRetentionDays NOT BETWEEN 1 AND 367
   OR @originalIntervalMinutes NOT IN (1, 5, 10, 15, 30, 60, 1440)
   OR @originalMaxPlansPerQuery NOT BETWEEN 1 AND 200 OR @originalFlushSeconds NOT BETWEEN 60 AND 86400
   OR @originalCaptureMode NOT IN (N'AUTO', N'ALL', N'NONE')
   OR @originalCleanupMode NOT IN (N'AUTO', N'OFF') OR @originalWaitStatsMode NOT IN (N'ON', N'OFF')
    THROW 51255, 'The captured Query Store baseline is invalid or incomplete.', 1;

DECLARE @lockResult int;
DECLARE @safeStage int = 1;
DECLARE @alterAttempted bit = 0;
DECLARE @ownerAcquired bit = 0;
BEGIN TRY
    BEGIN TRANSACTION;
    EXEC @lockResult = sys.sp_getapplock @Resource=N'HvoSqlOps.QueryStore.Owner', @LockMode=N'Exclusive',
        @LockOwner=N'Transaction', @LockTimeout=0;
    IF @lockResult < 0
        THROW 51255, 'Query Store toolkit ownership could not be serialized.', 1;
    IF EXISTS (SELECT 1 FROM sys.extended_properties WHERE [class]=0 AND [name]=N'HvoSqlOps_QueryStoreOwner')
        THROW 51255, 'A Query Store toolkit owner already exists.', 1;
    EXEC sys.sp_addextendedproperty @name=N'HvoSqlOps_QueryStoreOwner', @value=@ownerToken;
    COMMIT TRANSACTION;
    SET @ownerAcquired = 1;

    SET @safeStage = 2;
    IF NOT EXISTS (
        SELECT 1 FROM sys.database_query_store_options
        WHERE [desired_state_desc]=@originalDesiredState AND [actual_state_desc]=@originalActualState
          AND [readonly_reason]=@originalReadonlyReason AND [max_storage_size_mb]=@originalMaxStorageMb
          AND [stale_query_threshold_days]=@originalRetentionDays AND [interval_length_minutes]=@originalIntervalMinutes
          AND [max_plans_per_query]=@originalMaxPlansPerQuery AND [flush_interval_seconds]=@originalFlushSeconds
          AND [query_capture_mode_desc]=@originalCaptureMode AND [size_based_cleanup_mode_desc]=@originalCleanupMode
          AND [wait_stats_capture_mode_desc]=@originalWaitStatsMode)
        THROW 51255, 'Query Store state changed after baseline capture.', 1;

    SET @safeStage = 3;
    SET @alterAttempted = 1;
    DECLARE @configure nvarchar(max) = N'ALTER DATABASE CURRENT SET QUERY_STORE = ON
        (OPERATION_MODE=READ_WRITE, CLEANUP_POLICY=(STALE_QUERY_THRESHOLD_DAYS=' + CONVERT(nvarchar(11), @retentionDays) + N'),
         DATA_FLUSH_INTERVAL_SECONDS=900, INTERVAL_LENGTH_MINUTES=' + CONVERT(nvarchar(11), @intervalMinutes) + N',
         MAX_STORAGE_SIZE_MB=' + CONVERT(nvarchar(11), @maxStorageMb) + N', SIZE_BASED_CLEANUP_MODE=AUTO,
         QUERY_CAPTURE_MODE=AUTO, MAX_PLANS_PER_QUERY=' + CONVERT(nvarchar(11), @maxPlansPerQuery) + N', WAIT_STATS_CAPTURE_MODE=ON);';
    EXEC sys.sp_executesql @configure;
    SET @safeStage = 4;
    IF NOT EXISTS (
        SELECT 1 FROM sys.database_query_store_options
        WHERE [desired_state_desc]=N'READ_WRITE' AND [actual_state_desc]=N'READ_WRITE' AND [readonly_reason]=0
          AND [max_storage_size_mb]=@maxStorageMb AND [stale_query_threshold_days]=@retentionDays
          AND [interval_length_minutes]=@intervalMinutes AND [max_plans_per_query]=@maxPlansPerQuery
          AND [flush_interval_seconds]=900 AND [query_capture_mode_desc]=N'AUTO'
          AND [size_based_cleanup_mode_desc]=N'AUTO' AND [wait_stats_capture_mode_desc]=N'ON')
        THROW 51255, 'Query Store did not reach the complete toolkit configured state.', 1;

    SELECT N'query-store' AS [area], N'acquired' AS [ownership_state],
        CONVERT(nvarchar(30), [desired_state_desc]) AS [desired_state], CONVERT(nvarchar(30), [actual_state_desc]) AS [actual_state],
        CONVERT(int, [readonly_reason]) AS [readonly_reason], CONVERT(bigint, [current_storage_size_mb]) AS [current_storage_mb],
        CONVERT(bigint, [max_storage_size_mb]) AS [maximum_storage_mb], CONVERT(int, [stale_query_threshold_days]) AS [retention_days],
        CONVERT(int, [interval_length_minutes]) AS [interval_minutes], CONVERT(int, [max_plans_per_query]) AS [maximum_plans_per_query]
    FROM sys.database_query_store_options;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    DECLARE @cleanupFailed bit = 0;
    IF @ownerAcquired = 1 AND @alterAttempted = 0
    BEGIN
        BEGIN TRY
            BEGIN TRANSACTION;
            EXEC @lockResult = sys.sp_getapplock @Resource=N'HvoSqlOps.QueryStore.Owner', @LockMode=N'Exclusive',
                @LockOwner=N'Transaction', @LockTimeout=0;
            IF @lockResult < 0 OR NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE [class]=0
                AND [name]=N'HvoSqlOps_QueryStoreOwner' AND CONVERT(varchar(32), [value]) COLLATE Latin1_General_100_BIN2=@ownerToken)
                THROW 51255, 'Query Store configure ownership cleanup could not be verified.', 1;
            EXEC sys.sp_dropextendedproperty @name=N'HvoSqlOps_QueryStoreOwner';
            COMMIT TRANSACTION;
        END TRY
        BEGIN CATCH
            IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
            SET @cleanupFailed = 1;
        END CATCH;
    END;
    IF @cleanupFailed = 1
        THROW 51309, 'Query Store configure failed and exact ownership cleanup also failed.', 1;
    DECLARE @safeErrorNumber int = 51300 + @safeStage;
    THROW @safeErrorNumber, 'Query Store configure failed at the reported safe stage.', 1;
END CATCH;
