-- SQLCMD variables: DatabaseName. Run from master.
-- Application sessions are attributed. CPU, memory, waits, workers, grants, and tempdb are shared instance context.
SET NOCOUNT ON;

DECLARE @databaseName sysname = N'$(ESCAPE_SQUOTE(DatabaseName))';
DECLARE @databaseId int = DB_ID(@databaseName);
IF ISNULL(TRY_CONVERT(int, SERVERPROPERTY(N'ProductMajorVersion')), 0) <> 16
    THROW 51255, 'This toolkit supports SQL Server 2022 only.', 1;
IF DB_NAME() <> N'master' OR @databaseId IS NULL OR @databaseId <= 4 OR @databaseName = N'' OR @databaseName LIKE N'%[^0-9A-Za-z_-]%'
    THROW 51255, 'Run from master with a validated non-system DatabaseName.', 1;
IF (SELECT [compatibility_level] FROM sys.databases WHERE [database_id] = @databaseId) <> 160
    THROW 51255, 'The target database must use SQL Server 2022 compatibility level 160.', 1;

DECLARE @observedAt datetime2(0) = SYSUTCDATETIME();
SELECT
    N'metadata' AS [area], N'instance' AS [scope], N'all' AS [attribution],
    N'engine-major-version' AS [metric], CONVERT(decimal(38, 4), SERVERPROPERTY(N'ProductMajorVersion')) AS [value],
    N'version' AS [unit], @observedAt AS [observed_at_utc]
UNION ALL
SELECT N'metadata', N'instance', N'all', N'engine-edition', CONVERT(decimal(38, 4), SERVERPROPERTY(N'EngineEdition')), N'edition-code', @observedAt;

SELECT
    N'cpu' AS [area], N'instance-wide-shared' AS [scope], N'all' AS [attribution],
    CONVERT(int, [cpu_count]) AS [logical_cpu_count],
    CONVERT(int, [scheduler_count]) AS [scheduler_count],
    CONVERT(bigint, [process_kernel_time_ms]) AS [sql_process_kernel_time_ms],
    CONVERT(bigint, [process_user_time_ms]) AS [sql_process_user_time_ms],
    @observedAt AS [observed_at_utc]
FROM sys.dm_os_sys_info;

SELECT
    N'physical-memory' AS [area], N'instance-wide-shared' AS [scope], N'all' AS [attribution],
    CONVERT(bigint, [total_physical_memory_kb]) * 1024 AS [total_bytes],
    (CONVERT(bigint, [total_physical_memory_kb]) - CONVERT(bigint, [available_physical_memory_kb])) * 1024 AS [used_bytes],
    CONVERT(bigint, [available_physical_memory_kb]) * 1024 AS [free_bytes],
    CONVERT(bigint, [available_physical_memory_kb]) * 1024 AS [headroom_bytes],
    @observedAt AS [observed_at_utc]
FROM sys.dm_os_sys_memory;

SELECT
    N'sql-process-memory' AS [area], N'instance-wide-shared' AS [scope], N'all' AS [attribution],
    CONVERT(bigint, [physical_memory_in_use_kb]) * 1024 AS [physical_used_bytes],
    CONVERT(bigint, [virtual_address_space_committed_kb]) * 1024 AS [virtual_committed_bytes],
    CONVERT(bigint, [virtual_address_space_available_kb]) * 1024 AS [virtual_free_bytes],
    CONVERT(int, [memory_utilization_percentage]) AS [memory_utilization_percent],
    CONVERT(bigint, [page_fault_count]) AS [page_fault_count],
    @observedAt AS [observed_at_utc]
FROM sys.dm_os_process_memory;

SELECT
    N'sql-memory-configuration' AS [area], N'instance-wide-shared' AS [scope], N'all' AS [attribution],
    CONVERT(bigint, minimum_memory.[value_in_use]) * 1048576 AS [configured_min_bytes],
    CONVERT(bigint, maximum_memory.[value_in_use]) * 1048576 AS [configured_max_bytes],
    CONVERT(bigint, system_info.[committed_kb]) * 1024 AS [committed_bytes],
    CONVERT(bigint, system_info.[committed_target_kb]) * 1024 AS [target_bytes],
    CONVERT(bigint, CASE WHEN system_info.[committed_target_kb] > system_info.[committed_kb]
        THEN system_info.[committed_target_kb] - system_info.[committed_kb] ELSE 0 END) * 1024 AS [target_headroom_bytes],
    @observedAt AS [observed_at_utc]
FROM sys.dm_os_sys_info AS system_info
CROSS JOIN (SELECT [value_in_use] FROM sys.configurations WHERE [name] = N'min server memory (MB)') AS minimum_memory
CROSS JOIN (SELECT [value_in_use] FROM sys.configurations WHERE [name] = N'max server memory (MB)') AS maximum_memory;

;WITH transaction_sessions AS (
    SELECT DISTINCT [session_id]
    FROM sys.dm_tran_session_transactions),
grant_sessions AS (
    SELECT [session_id], COALESCE(SUM(CONVERT(bigint, [requested_memory_kb])), 0) AS [requested_memory_kb],
        COALESCE(SUM(CONVERT(bigint, [granted_memory_kb])), 0) AS [granted_memory_kb]
    FROM sys.dm_exec_query_memory_grants
    GROUP BY [session_id]),
lock_sessions AS (
    SELECT DISTINCT [request_session_id]
    FROM sys.dm_tran_locks)
SELECT
    N'sessions' AS [area], N'attributed' AS [scope],
    CASE sessions.[program_name]
        WHEN N'HVO.SkyMonitor.LogicHost.Runtime' THEN N'logichost-runtime'
        WHEN N'HVO.SkyMonitor.LogicHost.Migration' THEN N'logichost-migration'
        WHEN N'HVO.SkyMonitor.LogicHost.ObjectLock' THEN N'logichost-object-lock'
        ELSE N'other'
    END AS [attribution],
    COUNT_BIG(DISTINCT sessions.[session_id]) AS [session_count],
    COALESCE(SUM(CONVERT(bigint, CASE WHEN requests.[session_id] IS NULL THEN 0 ELSE 1 END)), 0) AS [active_request_count],
    COALESCE(SUM(CONVERT(bigint, CASE WHEN transactions.[session_id] IS NULL THEN 0 ELSE 1 END)), 0) AS [open_transaction_session_count],
    COALESCE(SUM(CONVERT(bigint, CASE WHEN requests.[blocking_session_id] > 0 THEN 1 ELSE 0 END)), 0) AS [blocked_request_count],
    COALESCE(SUM(CONVERT(bigint, COALESCE(grants.[requested_memory_kb], 0))), 0) AS [requested_grant_kb],
    COALESCE(SUM(CONVERT(bigint, COALESCE(grants.[granted_memory_kb], 0))), 0) AS [granted_memory_kb],
    COALESCE(SUM(CONVERT(bigint, CASE WHEN locks.[request_session_id] IS NULL THEN 0 ELSE 1 END)), 0) AS [lock_session_count],
    @observedAt AS [observed_at_utc]
FROM sys.dm_exec_sessions AS sessions
LEFT JOIN sys.dm_exec_requests AS requests ON requests.[session_id] = sessions.[session_id]
LEFT JOIN transaction_sessions AS transactions ON transactions.[session_id] = sessions.[session_id]
LEFT JOIN grant_sessions AS grants ON grants.[session_id] = sessions.[session_id]
LEFT JOIN lock_sessions AS locks ON locks.[request_session_id] = sessions.[session_id]
WHERE sessions.[is_user_process] = 1
GROUP BY CASE sessions.[program_name]
    WHEN N'HVO.SkyMonitor.LogicHost.Runtime' THEN N'logichost-runtime'
    WHEN N'HVO.SkyMonitor.LogicHost.Migration' THEN N'logichost-migration'
    WHEN N'HVO.SkyMonitor.LogicHost.ObjectLock' THEN N'logichost-object-lock'
    ELSE N'other' END;

SELECT TOP (20)
    N'waits' AS [area], N'instance-wide' AS [scope], N'all' AS [attribution],
    CONVERT(nvarchar(120), [wait_type]) AS [metric],
    CONVERT(bigint, [waiting_tasks_count]) AS [sample_count],
    CONVERT(bigint, [wait_time_ms]) AS [value_ms],
    CONVERT(bigint, [signal_wait_time_ms]) AS [signal_value_ms],
    @observedAt AS [observed_at_utc]
FROM sys.dm_os_wait_stats
ORDER BY [wait_time_ms] DESC, [wait_type];

SELECT
    N'workers' AS [area], N'instance-wide' AS [scope], N'all' AS [attribution],
    COALESCE(SUM(CONVERT(bigint, [current_workers_count])), 0) AS [current_workers],
    COALESCE(SUM(CONVERT(bigint, [active_workers_count])), 0) AS [active_workers],
    COALESCE(SUM(CONVERT(bigint, [runnable_tasks_count])), 0) AS [runnable_tasks],
    COALESCE(SUM(CONVERT(bigint, [current_tasks_count])), 0) AS [current_tasks],
    CONVERT(bigint, MAX(system_info.[max_workers_count])) AS [effective_max_workers],
    CONVERT(bigint, MAX(worker_configuration.[value_in_use])) AS [configured_max_worker_threads],
    CASE WHEN CONVERT(bigint, MAX(system_info.[max_workers_count])) > COALESCE(SUM(CONVERT(bigint, [current_workers_count])), 0)
        THEN CONVERT(bigint, MAX(system_info.[max_workers_count])) - COALESCE(SUM(CONVERT(bigint, [current_workers_count])), 0) ELSE 0 END AS [worker_headroom],
    @observedAt AS [observed_at_utc]
FROM sys.dm_os_schedulers
CROSS JOIN sys.dm_os_sys_info AS system_info
CROSS JOIN (SELECT [value_in_use] FROM sys.configurations WHERE [name] = N'max worker threads') AS worker_configuration
WHERE [status] = N'VISIBLE ONLINE';

SELECT
    N'memory-grants' AS [area], N'instance-wide' AS [scope], N'all' AS [attribution],
    COUNT_BIG(*) AS [grant_count],
    COALESCE(SUM(CONVERT(bigint, COALESCE([requested_memory_kb], 0))), 0) AS [requested_kb],
    COALESCE(SUM(CONVERT(bigint, COALESCE([granted_memory_kb], 0))), 0) AS [granted_kb],
    COALESCE(SUM(CONVERT(bigint, CASE WHEN [grant_time] IS NULL THEN 1 ELSE 0 END)), 0) AS [waiting_grant_count],
    @observedAt AS [observed_at_utc]
FROM sys.dm_exec_query_memory_grants;

SELECT
    N'query-spills' AS [area], N'instance-wide-cache' AS [scope], N'all' AS [attribution],
    COALESCE(SUM(CONVERT(bigint, [total_spills])), 0) AS [spill_pages],
    @observedAt AS [observed_at_utc]
FROM sys.dm_exec_query_stats;

SELECT
    N'tempdb' AS [area], N'instance-wide' AS [scope], N'all' AS [attribution],
    COALESCE(SUM(CONVERT(bigint, files.[size]) * 8192), 0) AS [allocated_file_bytes],
    COALESCE(SUM((CONVERT(bigint, files.[size]) - CONVERT(bigint, COALESCE(space.[unallocated_extent_page_count], 0))) * 8192), 0) AS [used_file_bytes],
    COALESCE(SUM(CONVERT(bigint, COALESCE(space.[unallocated_extent_page_count], 0)) * 8192), 0) AS [free_file_bytes],
    COALESCE(SUM(CONVERT(bigint, COALESCE(space.[unallocated_extent_page_count], 0)) * 8192), 0) AS [headroom_bytes],
    COALESCE(SUM(CONVERT(bigint, COALESCE(space.[version_store_reserved_page_count], 0)) * 8192), 0) AS [version_store_bytes],
    @observedAt AS [observed_at_utc]
FROM tempdb.sys.database_files AS files
LEFT JOIN tempdb.sys.dm_db_file_space_usage AS space ON space.[file_id] = files.[file_id]
WHERE files.[type] = 0;

DECLARE @storageSql nvarchar(max) = N'USE ' + QUOTENAME(@databaseName) + N';
SELECT
    N''database-storage'' AS [area], N''target-database'' AS [scope], N''all'' AS [attribution],
    CASE files.[type] WHEN 0 THEN N''data'' ELSE N''log'' END AS [file_kind],
    COALESCE(SUM(CONVERT(bigint, files.[size]) * 8192), 0) AS [allocated_bytes],
    COALESCE(SUM(CONVERT(bigint, io.[num_of_bytes_read])), 0) AS [bytes_read],
    COALESCE(SUM(CONVERT(bigint, io.[num_of_bytes_written])), 0) AS [bytes_written],
    COALESCE(SUM(CONVERT(bigint, io.[num_of_reads])), 0) AS [read_count],
    COALESCE(SUM(CONVERT(bigint, io.[num_of_writes])), 0) AS [write_count],
    COALESCE(SUM(CONVERT(bigint, io.[io_stall_read_ms])), 0) AS [read_stall_ms],
    COALESCE(SUM(CONVERT(bigint, io.[io_stall_write_ms])), 0) AS [write_stall_ms],
    COALESCE(SUM(CONVERT(bigint, CASE WHEN files.[is_percent_growth] = 1 THEN 1 ELSE 0 END)), 0) AS [percent_growth_file_count],
    COALESCE(SUM(CONVERT(bigint, CASE WHEN files.[growth] = 0 THEN 1 ELSE 0 END)), 0) AS [fixed_size_file_count],
    @sampledAt AS [observed_at_utc]
FROM sys.database_files AS files
JOIN sys.dm_io_virtual_file_stats(DB_ID(), NULL) AS io ON io.[file_id] = files.[file_id]
GROUP BY files.[type];';
BEGIN TRY
    EXEC sys.sp_executesql @storageSql, N'@sampledAt datetime2(0)', @sampledAt=@observedAt;
END TRY
BEGIN CATCH
    THROW 51255, 'Target-database storage capacity could not be collected.', 1;
END CATCH;

DECLARE @volumeSql nvarchar(max) = N'USE ' + QUOTENAME(@databaseName) + N';
;WITH volumes AS (
    SELECT volume.[volume_mount_point], MAX(CONVERT(bigint, volume.[total_bytes])) AS [total_bytes],
        MAX(CONVERT(bigint, volume.[available_bytes])) AS [available_bytes]
    FROM sys.database_files AS files
    CROSS APPLY sys.dm_os_volume_stats(DB_ID(), files.[file_id]) AS volume
    GROUP BY volume.[volume_mount_point])
SELECT N''storage-volumes'' AS [area], N''target-database-volumes-shared'' AS [scope], N''all'' AS [attribution],
    COUNT_BIG(*) AS [volume_count], COALESCE(SUM([total_bytes]), 0) AS [total_bytes],
    COALESCE(SUM([total_bytes] - [available_bytes]), 0) AS [used_bytes], COALESCE(SUM([available_bytes]), 0) AS [free_bytes],
    COALESCE(SUM([available_bytes]), 0) AS [headroom_bytes], @sampledAt AS [observed_at_utc]
FROM volumes;';
BEGIN TRY
    EXEC sys.sp_executesql @volumeSql, N'@sampledAt datetime2(0)', @sampledAt=@observedAt;
END TRY
BEGIN CATCH
    THROW 51255, 'Target-database volume capacity could not be collected.', 1;
END CATCH;

SELECT
    N'database-log' AS [area], N'target-database' AS [scope], N'all' AS [attribution],
    CONVERT(bigint, [total_log_size_mb]) * 1048576 AS [allocated_bytes],
    CONVERT(bigint, [active_log_size_mb]) * 1048576 AS [used_bytes],
    (CONVERT(bigint, [total_log_size_mb]) - CONVERT(bigint, [active_log_size_mb])) * 1048576 AS [free_bytes],
    (CONVERT(bigint, [total_log_size_mb]) - CONVERT(bigint, [active_log_size_mb])) * 1048576 AS [headroom_bytes],
    CONVERT(bigint, [log_since_last_log_backup_mb]) * 1048576 AS [bytes_since_log_backup],
    CONVERT(nvarchar(60), [log_truncation_holdup_reason]) AS [reuse_wait],
    @observedAt AS [observed_at_utc]
FROM sys.dm_db_log_stats(@databaseId);

DECLARE @defaultTrace nvarchar(4000) = (SELECT [path] FROM sys.traces WHERE [is_default] = 1);
IF @defaultTrace IS NULL
    SELECT N'autogrowth' AS [area], N'instance-history' AS [scope], N'all' AS [attribution],
        N'unavailable' AS [state], CONVERT(bigint, 0) AS [event_count],
        CONVERT(datetime2(0), NULL) AS [first_observed_server_local], CONVERT(datetime2(0), NULL) AS [last_observed_server_local];
ELSE
    SELECT N'autogrowth' AS [area], N'instance-history' AS [scope], N'all' AS [attribution],
        N'available' AS [state], COUNT_BIG(*) AS [event_count],
        CONVERT(datetime2(0), MIN([StartTime])) AS [first_observed_server_local],
        CONVERT(datetime2(0), MAX([StartTime])) AS [last_observed_server_local]
    FROM sys.fn_trace_gettable(@defaultTrace, DEFAULT)
    WHERE [DatabaseID] = @databaseId AND [EventClass] IN (92, 93);
